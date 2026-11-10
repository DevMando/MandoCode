using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

// Usage: MandoCode.Acp.MockAgent <scenario> [extra args echoed back in initialize._meta.args]
var scenario = args.Length > 0 ? args[0] : "echo";
return await new MockAgent(scenario, args).RunAsync();

internal sealed class MockAgent
{
    private readonly string _scenario;
    private readonly string[] _args;
    private readonly Stream _stdout = Console.OpenStandardOutput();
    private readonly object _writeGate = new();

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

            if (message is not JsonObject request)
                continue;

            Handle(request);
        }

        // stdin reached EOF: a well-behaved agent exits. This scenario deliberately does not.
        if (_scenario == "hang-on-shutdown")
            await Task.Delay(Timeout.Infinite);

        return 0;
    }

    private void Handle(JsonObject request)
    {
        var method = request["method"]?.GetValue<string>();
        var id = request["id"];

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
                    ["authMethods"] = new JsonArray(),
                    // Lets tests observe how the process was launched without a real prompt round trip.
                    ["_meta"] = new JsonObject
                    {
                        ["pid"] = Environment.ProcessId,
                        ["cwd"] = Directory.GetCurrentDirectory(),
                        ["args"] = new JsonArray(_args.Skip(1).Select(a => (JsonNode?)a).ToArray()),
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
                Reply(id, new JsonObject { ["sessionId"] = "mock-session-1" });
                break;

            case "session/prompt":
                var text = request["params"]?["prompt"]?[0]?["text"]?.GetValue<string>() ?? "";
                Notify("session/update", new JsonObject
                {
                    ["sessionId"] = "mock-session-1",
                    ["update"] = new JsonObject
                    {
                        ["sessionUpdate"] = "agent_message_chunk",
                        ["content"] = new JsonObject { ["type"] = "text", ["text"] = "echo: " + text },
                    },
                });
                Reply(id, new JsonObject { ["stopReason"] = "end_turn" });
                break;

            default:
                if (id is not null)
                    Error(id, -32601, "Method not found: " + method);
                break;
        }
    }

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

    private void Notify(string method, JsonNode parameters) =>
        Write(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method, ["params"] = parameters });

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
