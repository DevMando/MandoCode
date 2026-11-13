using System.Text.Json;
using MandoCode.Acp;
using MandoCode.Acp.Protocol;
using MandoCode.Services.Acp;
using Xunit;

namespace MandoCode.Tests.Acp;

public class AcpModeCommandTests
{
    [Fact]
    public void ConnectionCardShowsCurrentModeAndHowToChangeIt()
    {
        var rendered = Plain.Of(AcpConnectionCard.Build(Configured()));
        Assert.Contains("Auto review", rendered);
        Assert.Contains("Review actions automatically", rendered);
        Assert.Contains("/agent-acp-mode", rendered);
        Assert.Contains("/agent-acp stop", rendered);
        Assert.Contains("context is not shared", rendered);
    }

    [Fact]
    public void ConnectionCardDoesNotInventAnUnreportedMode()
    {
        var runtime = new FakeRuntime { Modes = new SessionModeState
        {
            AvailableModes = [new SessionMode { Id = "default", Name = "Standard permissions" }]
        }};
        var rendered = Plain.Of(AcpConnectionCard.Build(runtime));
        Assert.Contains("Standard permissions", rendered);
        Assert.DoesNotContain("Auto review", rendered);
    }
    private static FakeRuntime Configured() => new()
    {
        ConfigOptions = [JsonSerializer.Deserialize("""
            {"id":"permissions","name":"Mode","category":"mode","type":"select","currentValue":"agent",
             "options":[{"value":"read-only","name":"Read-only","description":"Ask before edits"},
                        {"value":"agent","name":"Auto review","description":"Review actions automatically"}]}
            """, AcpJsonContext.Default.SessionConfigOption)!]
    };

    private sealed class Picker : IAcpChoicePrompt
    {
        public int Initial = -1;
        public IReadOnlyList<AcpChoice> Choices = [];
        public int? Pick { get; init; }
        public Task<string?> ChooseAsync(string heading, IReadOnlyList<AcpChoice> choices, int initialIndex = 0)
        {
            Initial = initialIndex;
            Choices = choices;
            return Task.FromResult(Pick is { } index ? choices[index].Label : null);
        }
    }

    [Fact]
    public async Task PickerStartsOnCurrentModeAndSendsTheAdvertisedConfigId()
    {
        var runtime = Configured();
        var picker = new Picker { Pick = 0 };
        await AcpPaneCommands.ModeAsync(runtime, "", picker, _ => { }, default);
        Assert.Equal(1, picker.Initial);
        Assert.Contains("Ask before edits", picker.Choices[0].Label);
        Assert.Equal(("permissions", "read-only"), Assert.Single(runtime.ConfigChanges));
        Assert.Empty(runtime.ModeChanges);
        Assert.Equal(0, runtime.NewConversations);
    }

    [Fact]
    public async Task FallbackUsesSessionModesWhenNoConfigOptionExists()
    {
        var runtime = new FakeRuntime { Modes = new SessionModeState
        {
            AvailableModes = [new SessionMode { Id = "plan", Name = "Plan" }]
        }};
        await AcpPaneCommands.ModeAsync(runtime, "Plan", new Picker(), _ => { }, default);
        Assert.Equal("plan", Assert.Single(runtime.ModeChanges));
        Assert.Empty(runtime.ConfigChanges);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(2)]
    public async Task DismissOrCancelDoesNotChangePermissions(int? selection)
    {
        var runtime = Configured();
        await AcpPaneCommands.ModeAsync(runtime, "", new Picker { Pick = selection }, _ => { }, default);
        Assert.Empty(runtime.ConfigChanges);
    }

    [Theory]
    [InlineData("agent")]
    [InlineData("does-not-exist")]
    public async Task CurrentOrUnknownModeDoesNotSendAChange(string value)
    {
        var runtime = Configured();
        await AcpPaneCommands.ModeAsync(runtime, value, new Picker(), _ => { }, default);
        Assert.Empty(runtime.ConfigChanges);
    }

    [Fact]
    public async Task UnsupportedAgentGetsAnExplanation()
    {
        var messages = new List<string>();
        await AcpPaneCommands.ModeAsync(new FakeRuntime(), "", new Picker(), messages.Add, default);
        Assert.Contains("does not expose modes", Assert.Single(messages));
    }

    [Fact]
    public async Task AgentRefusalDoesNotReportSuccess()
    {
        var runtime = Configured();
        runtime.ConfigChangeFails = new InvalidOperationException("refused");
        var messages = new List<string>();
        await AcpPaneCommands.ModeAsync(runtime, "read-only", new Picker(), messages.Add, default);
        Assert.Contains("did not accept", Assert.Single(messages));
        Assert.Empty(runtime.ConfigChanges);
    }
}
