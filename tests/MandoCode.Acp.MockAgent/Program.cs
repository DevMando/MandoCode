using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

// Usage: MandoCode.Acp.MockAgent <scenario> [extra args echoed back in initialize._meta.args]
//
// Scenarios:
//   echo               answers prompts with "echo: <text>"
//   auth-required      session/new fails with -32000 (not signed in)
//   permission         a prompt asks the client for permission and reports the outcome
//   slow-cancel        streams ticks until session/cancel, then ends the turn with "cancelled"
//   ignore-cancel      streams ticks forever and never answers session/cancel
//   crash-mid-turn     streams one chunk, writes to stderr and exits with code 3
//   unknown-updates    sends unknown update kinds / content types / fields between normal chunks
//   fs-roundtrip       prompt text is an absolute path: reads it, writes "<path>.out" in upper case
//   exit-immediately   exits with code 7 before reading anything
//   stdout-garbage     writes non-protocol lines to stdout
//   stderr-flood       writes ~1.7 MB to stderr before serving
//   hang-on-shutdown   ignores stdin EOF and keeps a child process alive
//   silent-with-child  starts a child process, never answers anything and ignores stdin EOF
//   auto-mode          session/new reports current mode "auto" (modes: default, auto); session/set_mode works
//   set-mode-fails     like auto-mode, but session/set_mode answers with an error
//   only-auto-mode     the only offered mode is "auto" (there is no "default")
//   sleep              does nothing (used as the child of hang-on-shutdown)
var scenario = args.Length > 0 ? args[0] : "echo";
return await new MockAgent(scenario, args).RunAsync();

internal sealed class MockAgent
{
    private const string SessionId = "mock-session-1";

    private readonly string _scenario;
    private readonly string[] _args;
    private readonly Stream _stdout = Console.OpenStandardOutput();
    private readonly object _writeGate = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonObject>> _pendingClientCalls = new();
    private readonly TaskCompletionSource _cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _nextCallId;
    private string _mode = "default";

    public MockAgent(string scenario, string[] args)
    {
        _scenario = scenario;
        _args = args;
    }

    public async Task<int> RunAsync()
    {
        switch (_scenario)
        {
            case "exit-immediately":
                Console.Error.WriteLine("mock-agent: fatal: cannot start");
                return 7;

            case "sleep":
                await Task.Delay(Timeout.Infinite);
                return 0;

            case "stdout-garbage":
                for (var i = 0; i < 20; i++)
                    WriteRaw($"[mock] this is a log line, not JSON-RPC ({i})");
                await Task.Delay(Timeout.Infinite);
                return 0;

            case "stderr-flood":
                // Far more than a pipe buffer: a transport that does not drain stderr would deadlock.
                for (var i = 0; i < 20000; i++)
                    Console.Error.WriteLine($"flood line {i} " + new string('x', 80));
                break;

            case "hang-on-shutdown":
                StartChild();
                break;

            case "silent-with-child":
                StartChild();
                Console.Error.WriteLine("mock-agent: booting, loading models...");
                await Task.Delay(Timeout.Infinite);
                return 0;
        }

        using var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        while (await stdin.ReadLineAsync() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            JsonNode? message;
            try
            {
                message = JsonNode.Parse(line);
            }
            catch (System.Text.Json.JsonException)
            {
                continue;
            }

            if (message is not JsonObject json)
                continue;

            Dispatch(json);
        }

        // stdin reached EOF: a well-behaved agent exits. This scenario deliberately does not.
        if (_scenario == "hang-on-shutdown")
            await Task.Delay(Timeout.Infinite);

        return 0;
    }

    private void Dispatch(JsonObject message)
    {
        var method = message["method"]?.GetValue<string>();

        if (method is null)
        {
            // A response to something this agent asked the client.
            var key = message["id"]?.ToJsonString();
            if (key is not null && _pendingClientCalls.TryRemove(key, out var pending))
                pending.TrySetResult(message);
            return;
        }

        var id = message["id"];
        switch (method)
        {
            case "initialize":
                Reply(id, new JsonObject
                {
                    ["protocolVersion"] = 1,
                    ["agentInfo"] = new JsonObject { ["name"] = "mock-" + _scenario, ["version"] = "0.0.1" },
                    ["agentCapabilities"] = new JsonObject
                    {
                        ["loadSession"] = false,
                        ["promptCapabilities"] = new JsonObject { ["image"] = false, ["audio"] = false, ["embeddedContext"] = true },
                    },
                    ["authMethods"] = _scenario == "auth-required"
                        ? new JsonArray(new JsonObject { ["id"] = "mock-login", ["name"] = "Mock login", ["description"] = "run mock login" })
                        : new JsonArray(),
                    // Lets tests observe how the process was launched and what the client advertised.
                    ["_meta"] = new JsonObject
                    {
                        ["pid"] = Environment.ProcessId,
                        ["cwd"] = Directory.GetCurrentDirectory(),
                        ["args"] = new JsonArray(_args.Skip(1).Select(a => (JsonNode?)a).ToArray()),
                        ["clientCapabilities"] = message["params"]?["clientCapabilities"]?.DeepClone(),
                        ["env"] = new JsonObject
                        {
                            ["ANTHROPIC_API_KEY"] = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY"),
                            ["MOCK_FOO"] = Environment.GetEnvironmentVariable("MOCK_FOO"),
                            ["MOCK_INHERITED"] = Environment.GetEnvironmentVariable("MOCK_INHERITED"),
                        },
                    },
                });
                break;

            case "session/new":
                if (_scenario == "auth-required")
                {
                    Error(id, -32000, "Authentication required");
                }
                else
                {
                    var startsInAuto = _scenario is "auto-mode" or "set-mode-fails" or "only-auto-mode";
                    _mode = startsInAuto ? "auto" : "default";
                    var offered = new JsonArray();
                    if (_scenario != "only-auto-mode")
                        offered.Add(new JsonObject { ["id"] = "default", ["name"] = "Default" });
                    if (startsInAuto)
                        offered.Add(new JsonObject { ["id"] = "auto", ["name"] = "Auto" });
                    Reply(id, new JsonObject
                    {
                        ["sessionId"] = SessionId,
                        ["modes"] = new JsonObject { ["currentModeId"] = _mode, ["availableModes"] = offered },
                    });
                }

                break;

            case "session/set_mode":
                if (_scenario == "set-mode-fails")
                {
                    Error(id, -32602, "Mode change is not allowed");
                }
                else
                {
                    _mode = message["params"]?["modeId"]?.GetValue<string>() ?? _mode;
                    Reply(id, new JsonObject());
                }

                break;

            case "session/prompt":
                var text = message["params"]?["prompt"]?[0]?["text"]?.GetValue<string>() ?? "";
                _ = Task.Run(() => RunPromptAsync(id, text));
                break;

            case "session/cancel":
                _cancelled.TrySetResult();
                break;

            default:
                if (id is not null)
                    Error(id, -32601, "Method not found: " + method);
                break;
        }
    }

    private async Task RunPromptAsync(JsonNode? id, string text)
    {
        switch (_scenario)
        {
            case "permission":
            {
                var response = await CallClientAsync("session/request_permission", new JsonObject
                {
                    ["sessionId"] = SessionId,
                    ["toolCall"] = new JsonObject { ["toolCallId"] = "call-1", ["title"] = "Edit file", ["kind"] = "edit" },
                    ["options"] = new JsonArray(
                        new JsonObject { ["optionId"] = "allow", ["name"] = "Allow", ["kind"] = "allow_once" },
                        new JsonObject { ["optionId"] = "reject", ["name"] = "Reject", ["kind"] = "reject_once" }),
                });

                var outcome = response["result"]?["outcome"];
                var kind = outcome?["outcome"]?.GetValue<string>();
                var described = kind == "selected" ? "selected:" + outcome?["optionId"]?.GetValue<string>() : kind ?? "error";
                Chunk("permission: " + described);
                Reply(id, new JsonObject { ["stopReason"] = kind == "cancelled" ? "cancelled" : "end_turn" });
                return;
            }

            case "slow-cancel":
                for (var i = 0; i < 400; i++)
                {
                    Chunk("tick " + i);
                    if (await Task.WhenAny(_cancelled.Task, Task.Delay(30)) == _cancelled.Task)
                    {
                        Chunk("stopping after cancel");
                        Reply(id, new JsonObject { ["stopReason"] = "cancelled" });
                        return;
                    }
                }

                Reply(id, new JsonObject { ["stopReason"] = "end_turn" });
                return;

            case "ignore-cancel":
                for (var i = 0; ; i++)
                {
                    Chunk("tick " + i);
                    await Task.Delay(30);
                }

            case "crash-mid-turn":
                Chunk("about to crash");
                Console.Error.WriteLine("mock-agent: panic: unrecoverable");
                Environment.Exit(3);
                return;

            case "unknown-updates":
                Update(new JsonObject { ["sessionUpdate"] = "usage_update", ["used"] = 12, ["size"] = 200000 });
                Update(new JsonObject { ["sessionUpdate"] = "hologram_update", ["payload"] = new JsonObject { ["x"] = 1 } });
                Update(new JsonObject
                {
                    ["sessionUpdate"] = "agent_message_chunk",
                    ["futureField"] = "ignored",
                    ["content"] = new JsonObject { ["type"] = "hologram", ["depth"] = 3 },
                });
                Update(new JsonObject
                {
                    ["sessionUpdate"] = "agent_message_chunk",
                    ["futureField"] = new JsonArray(1, 2, 3),
                    ["content"] = new JsonObject { ["type"] = "text", ["text"] = "after unknowns", ["extra"] = true },
                });
                Reply(id, new JsonObject { ["stopReason"] = "a_future_stop_reason", ["extra"] = 1 });
                return;

            case "fs-roundtrip":
            {
                var read = await CallClientAsync("fs/read_text_file", new JsonObject { ["sessionId"] = SessionId, ["path"] = text });
                if (read["error"] is JsonNode readError)
                {
                    Chunk("read failed: " + readError["message"]?.GetValue<string>());
                    Reply(id, new JsonObject { ["stopReason"] = "end_turn" });
                    return;
                }

                var content = read["result"]?["content"]?.GetValue<string>() ?? "";
                var write = await CallClientAsync("fs/write_text_file", new JsonObject
                {
                    ["sessionId"] = SessionId,
                    ["path"] = text + ".out",
                    ["content"] = content.ToUpperInvariant(),
                });
                Chunk(write["error"] is null ? $"read {content.Length} chars, wrote copy" : "write failed: " + write["error"]?["message"]?.GetValue<string>());
                Reply(id, new JsonObject { ["stopReason"] = "end_turn" });
                return;
            }

            default:
                Chunk(text == "/mode" ? "mode: " + _mode : "echo: " + text);
                Reply(id, new JsonObject { ["stopReason"] = "end_turn" });
                return;
        }
    }

    private Task<JsonObject> CallClientAsync(string method, JsonNode parameters)
    {
        var id = "agent-" + Interlocked.Increment(ref _nextCallId);
        var pending = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingClientCalls[JsonValue.Create(id)!.ToJsonString()] = pending;
        Write(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters });
        return pending.Task;
    }

    private void Chunk(string text) =>
        Update(new JsonObject
        {
            ["sessionUpdate"] = "agent_message_chunk",
            ["content"] = new JsonObject { ["type"] = "text", ["text"] = text },
        });

    private void Update(JsonObject update) =>
        Write(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = "session/update",
            ["params"] = new JsonObject { ["sessionId"] = SessionId, ["update"] = update },
        });

    private static void StartChild()
    {
        var child = Process.Start(new ProcessStartInfo(Environment.ProcessPath!)
        {
            ArgumentList = { typeof(MockAgent).Assembly.Location, "sleep" },
            UseShellExecute = false,
        });
        Console.Error.WriteLine("child-pid: " + child!.Id);
    }

    private void Reply(JsonNode? id, JsonNode result) =>
        Write(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id?.DeepClone(), ["result"] = result });

    private void Error(JsonNode? id, int code, string message) =>
        Write(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id?.DeepClone(),
            ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
        });

    private void Write(JsonNode message) => WriteRaw(message.ToJsonString());

    private void WriteRaw(string line)
    {
        var bytes = Encoding.UTF8.GetBytes(line + "\n");
        lock (_writeGate)
        {
            _stdout.Write(bytes, 0, bytes.Length);
            _stdout.Flush();
        }
    }
}
