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
//   config-options     session/new offers configOptions (mode, model, grouped effort) and an available_commands_update
//                      with compact; session/set_config_option works (unknown ids/values are refused)
//   sessions           config-options plus loadSession and sessionCapabilities list/resume/close. Sessions are numbered
//                      mock-session-N and kept in the file named by MOCK_SESSION_STORE (shared across processes) when set
//   load-only          like sessions, but without sessionCapabilities (only session/load)
//   sleep              does nothing (used as the child of hang-on-shutdown)
//
// In config-options / sessions / load-only a prompt "__state" answers with the sessions this process saw closed, and
// a prompt "/compact" answers "compacted" (so tests can see what was sent).
var scenario = args.Length > 0 ? args[0] : "echo";
return await new MockAgent(scenario, args).RunAsync();

internal sealed class MockAgent
{
    private string _sessionId = "mock-session-1";

    private readonly string _scenario;
    private readonly string[] _args;
    private readonly Stream _stdout = Console.OpenStandardOutput();
    private readonly object _writeGate = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonObject>> _pendingClientCalls = new();
    private readonly TaskCompletionSource _cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _nextCallId;
    private string _mode = "default";

    // config-options / sessions / load-only
    private bool Config => _scenario is "config-options" or "sessions" or "load-only";
    private bool Sessions => _scenario is "sessions" or "load-only";
    private readonly Dictionary<string, string> _values = new() { ["mode"] = "default", ["model"] = "model-a", ["effort"] = "medium" };
    private readonly List<string> _closed = [];
    private readonly object _storeGate = new();
    private static readonly string? StorePath = Environment.GetEnvironmentVariable("MOCK_SESSION_STORE");
    private readonly Dictionary<string, MockSession> _local = new();

    private sealed class MockSession
    {
        public string Cwd { get; set; } = "";
        public string Title { get; set; } = "";
        public List<string[]> History { get; set; } = [];
    }

    private Dictionary<string, MockSession> LoadStore()
    {
        lock (_storeGate)
        {
            if (StorePath is null || !File.Exists(StorePath))
                return new Dictionary<string, MockSession>(_local);
            return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, MockSession>>(File.ReadAllText(StorePath)) ?? new();
        }
    }

    private void SaveStore(Dictionary<string, MockSession> store)
    {
        lock (_storeGate)
        {
            _local.Clear();
            foreach (var pair in store)
                _local[pair.Key] = pair.Value;
            if (StorePath is not null)
                File.WriteAllText(StorePath, System.Text.Json.JsonSerializer.Serialize(store));
        }
    }

    private JsonArray ConfigOptions() => new(
        new JsonObject
        {
            ["id"] = "mode", ["name"] = "Mode", ["category"] = "mode", ["type"] = "select",
            ["currentValue"] = _values["mode"],
            ["options"] = new JsonArray(
                new JsonObject { ["value"] = "default", ["name"] = "Default" },
                new JsonObject { ["value"] = "plan", ["name"] = "Plan" }),
        },
        new JsonObject
        {
            ["id"] = "model", ["name"] = "Model", ["category"] = "model", ["type"] = "select",
            ["currentValue"] = _values["model"],
            ["options"] = new JsonArray(
                new JsonObject { ["value"] = "model-a", ["name"] = "Model A", ["description"] = "The first one" },
                new JsonObject { ["value"] = "model-b", ["name"] = "Model B" }),
        },
        new JsonObject
        {
            ["id"] = "effort", ["name"] = "Effort", ["category"] = "thought_level", ["type"] = "select",
            ["currentValue"] = _values["effort"],
            ["options"] = new JsonArray(
                new JsonObject
                {
                    ["group"] = "levels", ["name"] = "Levels",
                    ["options"] = new JsonArray(
                        new JsonObject { ["value"] = "low", ["name"] = "Low" },
                        new JsonObject { ["value"] = "medium", ["name"] = "Medium" }),
                }),
        });

    private void ResetValues()
    {
        _values["mode"] = "default";
        _values["model"] = "model-a";
        _values["effort"] = "medium";
    }

    private void SendCommands() =>
        Update(new JsonObject
        {
            ["sessionUpdate"] = "available_commands_update",
            ["availableCommands"] = new JsonArray(
                new JsonObject { ["name"] = "compact", ["description"] = "Summarize the conversation" },
                new JsonObject { ["name"] = "review", ["description"] = "Review the changes" }),
        });

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
                        ["loadSession"] = Sessions,
                        ["promptCapabilities"] = new JsonObject { ["image"] = false, ["audio"] = false, ["embeddedContext"] = true },
                        ["sessionCapabilities"] = _scenario == "sessions"
                            ? new JsonObject { ["list"] = new JsonObject(), ["resume"] = new JsonObject(), ["close"] = new JsonObject() }
                            : null,
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
                    if (Sessions)
                    {
                        var store = LoadStore();
                        _sessionId = "mock-session-" + (store.Count + 1);
                        store[_sessionId] = new MockSession { Cwd = message["params"]?["cwd"]?.GetValue<string>() ?? "", Title = "Session " + (store.Count + 1) };
                        SaveStore(store);
                    }

                    if (Config)
                        ResetValues();
                    var created = new JsonObject
                    {
                        ["sessionId"] = _sessionId,
                        ["modes"] = new JsonObject { ["currentModeId"] = _mode, ["availableModes"] = offered },
                    };
                    if (Config)
                        created["configOptions"] = ConfigOptions();
                    Reply(id, created);
                    if (Config)
                        SendCommands();
                }

                break;

            case "session/set_config_option" when Config:
            {
                var configId = message["params"]?["configId"]?.GetValue<string>() ?? "";
                var value = message["params"]?["value"]?.GetValue<string>() ?? "";
                var allowed = ConfigOptions().Cast<JsonObject>().FirstOrDefault(o => o["id"]!.GetValue<string>() == configId);
                var known = allowed?["options"]!.AsArray().SelectMany(o => o!["options"] is JsonArray inner ? inner.Select(x => x!["value"]!.GetValue<string>()) : [o["value"]!.GetValue<string>()]) ?? [];
                if (allowed is null || !known.Contains(value))
                    Error(id, -32602, $"Unknown option or value: {configId}={value}");
                else
                {
                    _values[configId] = value;
                    Reply(id, new JsonObject { ["configOptions"] = ConfigOptions() });
                }

                break;
            }

            case "session/list" when _scenario == "sessions":
            {
                var cwd = message["params"]?["cwd"]?.GetValue<string>();
                var sessions = new JsonArray();
                foreach (var (key, value) in LoadStore().Where(p => cwd is null || p.Value.Cwd == cwd))
                    sessions.Add(new JsonObject { ["sessionId"] = key, ["cwd"] = value.Cwd, ["title"] = value.Title, ["updatedAt"] = "2026-11-12T10:00:00Z" });
                Reply(id, new JsonObject { ["sessions"] = sessions });
                break;
            }

            case "session/resume" when _scenario == "sessions":
            case "session/load" when Sessions:
            {
                var wanted = message["params"]?["sessionId"]?.GetValue<string>() ?? "";
                var cwd = message["params"]?["cwd"]?.GetValue<string>() ?? "";
                var store = LoadStore();
                if (!store.TryGetValue(wanted, out var known) || known.Cwd != cwd)
                {
                    Error(id, -32002, "Session not found: " + wanted);
                    break;
                }

                _sessionId = wanted;
                if (method == "session/load")
                {
                    foreach (var turn in known.History)
                        Update(new JsonObject
                        {
                            ["sessionUpdate"] = turn[0] == "user" ? "user_message_chunk" : "agent_message_chunk",
                            ["content"] = new JsonObject { ["type"] = "text", ["text"] = turn[1] },
                        });
                }

                Reply(id, new JsonObject { ["configOptions"] = ConfigOptions() });
                break;
            }

            case "session/close" when _scenario == "sessions":
                _closed.Add(message["params"]?["sessionId"]?.GetValue<string>() ?? "");
                Reply(id, new JsonObject());
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
                if (Config && text is "__state" or "/compact")
                {
                    var answer = text == "/compact"
                        ? "compacted"
                        : $"closed=[{string.Join(",", _closed)}] session={_sessionId} model={_values["model"]} mode={_values["mode"]}";
                    _ = Task.Run(() =>
                    {
                        Chunk(answer);
                        Reply(id, new JsonObject { ["stopReason"] = "end_turn" });
                    });
                    break;
                }

                if (Sessions)
                {
                    var store = LoadStore();
                    if (store.TryGetValue(_sessionId, out var current))
                    {
                        current.History.Add(["user", text]);
                        current.History.Add(["assistant", "echo: " + text]);
                        SaveStore(store);
                    }
                }

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
                    ["sessionId"] = _sessionId,
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
                var read = await CallClientAsync("fs/read_text_file", new JsonObject { ["sessionId"] = _sessionId, ["path"] = text });
                if (read["error"] is JsonNode readError)
                {
                    Chunk("read failed: " + readError["message"]?.GetValue<string>());
                    Reply(id, new JsonObject { ["stopReason"] = "end_turn" });
                    return;
                }

                var content = read["result"]?["content"]?.GetValue<string>() ?? "";
                var write = await CallClientAsync("fs/write_text_file", new JsonObject
                {
                    ["sessionId"] = _sessionId,
                    ["path"] = text + ".out",
                    ["content"] = content.ToUpperInvariant(),
                });
                Chunk(write["error"] is null ? $"read {content.Length} chars, wrote copy" : "write failed: " + write["error"]?["message"]?.GetValue<string>());
                Reply(id, new JsonObject { ["stopReason"] = "end_turn" });
                return;
            }

            case "ext-notification":
                Write(new JsonObject
                {
                    ["jsonrpc"] = "2.0",
                    ["method"] = "_auth/status_update",
                    ["params"] = new JsonObject { ["account"] = "someone@example.com", ["state"] = "signed-in" },
                });
                Chunk("after extension");
                Reply(id, new JsonObject { ["stopReason"] = "end_turn" });
                return;

            case "tools-plan-diff":
            {
                Chunk("Looking at the file.");
                Update(new JsonObject
                {
                    ["sessionUpdate"] = "tool_call",
                    ["toolCallId"] = "t1",
                    ["title"] = "Read config",
                    ["kind"] = "read",
                    ["status"] = "pending",
                    ["locations"] = new JsonArray(new JsonObject { ["path"] = "/work/config.json", ["line"] = 3 }),
                });
                Update(new JsonObject { ["sessionUpdate"] = "tool_call_update", ["toolCallId"] = "t1", ["status"] = "in_progress" });
                Update(new JsonObject { ["sessionUpdate"] = "tool_call_update", ["toolCallId"] = "t1", ["status"] = "completed" });
                Update(new JsonObject
                {
                    ["sessionUpdate"] = "plan",
                    ["entries"] = new JsonArray(
                        new JsonObject { ["content"] = "Read config", ["priority"] = "high", ["status"] = "completed" },
                        new JsonObject { ["content"] = "Edit config", ["priority"] = "medium", ["status"] = "in_progress" }),
                });
                Update(new JsonObject
                {
                    ["sessionUpdate"] = "plan",
                    ["entries"] = new JsonArray(
                        new JsonObject { ["content"] = "Edit config", ["priority"] = "medium", ["status"] = "completed" }),
                });
                Update(new JsonObject
                {
                    ["sessionUpdate"] = "tool_call",
                    ["toolCallId"] = "t2",
                    ["title"] = "Edit config",
                    ["kind"] = "edit",
                    ["content"] = new JsonArray(
                        new JsonObject { ["type"] = "diff", ["path"] = "/work/config.json", ["oldText"] = "a\nb\n", ["newText"] = "a\nc\n" },
                        new JsonObject { ["type"] = "hologram", ["depth"] = 3 },
                        new JsonObject { ["type"] = "terminal", ["terminalId"] = "term-1" }),
                });
                Update(new JsonObject
                {
                    ["sessionUpdate"] = "tool_call_update",
                    ["toolCallId"] = "t2",
                    ["status"] = "failed",
                    ["content"] = new JsonArray(new JsonObject
                    {
                        ["type"] = "content",
                        ["content"] = new JsonObject { ["type"] = "text", ["text"] = "disk full\nsecond line" },
                    }),
                });
                Chunk("Done.");
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
            ["params"] = new JsonObject { ["sessionId"] = _sessionId, ["update"] = update },
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
