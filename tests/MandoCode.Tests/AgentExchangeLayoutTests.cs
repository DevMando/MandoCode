using System.Reflection;
using MandoCode.Components;
using MandoCode.Models;
using MandoCode.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RazorConsole.Core;
using RazorConsole.Core.Rendering;
using Spectre.Console;
using Xunit;

namespace MandoCode.Tests;

[Trait("Category", "Component")]
public sealed class AgentExchangeLayoutTests
{
    private static ServiceProvider Services()
    {
        var registrations = new ServiceCollection().AddLogging();
        registrations.AddRazorConsoleServices();
        registrations.AddSingleton<ITerminalViewport>(new Viewport());
        Program.RegisterAgentServices(registrations, new MandoCodeConfig { AllowPersistence = false }, Path.GetTempPath());
        return registrations.BuildServiceProvider();
    }
    private sealed class Viewport : ITerminalViewport
    {
        public int Width => 100;
        public int Height => 30;
        public event Action? OnResized { add { } remove { } }
    }

    [Fact]
    public async Task SectionCollapsesDetailsButKeepsUserAnswerAndExpansionOnCompletion()
    {
        await using var services = Services();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var session = new TuiSession();
        session.AppendAgentExchange("🤖 Topaz · Question\nFind the answer", Color.Purple, "q1", "Waiting for reply");
        session.AppendSpaced(new Text("Fusion's answer to the user"));
        var group = session.Snapshot().Entries[0].AgentActivity!;
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            ParameterView Parameters() => ParameterView.FromDictionary(new Dictionary<string, object?> { ["Entries"] = session.Snapshot().Entries });
            var root = await renderer.RenderComponentAsync<ConversationView>(Parameters());
            var requestId = session.Snapshot().Entries[1].Id;
            var answerId = session.Snapshot().Entries[2].Id;
            Assert.Contains("Waiting for reply", root.ToHtmlString());
            Assert.DoesNotContain($"data-transcript-entry=\"{requestId}\"", root.ToHtmlString());
            Assert.Contains($"data-transcript-entry=\"{answerId}\"", root.ToHtmlString());
            group.Expanded = true;
            session.AppendAgentDetail("q1", "Topaz", new Text("Raw reply"), "Completed");
            root = await renderer.RenderComponentAsync<ConversationView>(Parameters());
            Assert.True(group.Expanded);
            Assert.Contains("Completed", root.ToHtmlString());
            Assert.Contains($"data-transcript-entry=\"{requestId}\"", root.ToHtmlString());
            Assert.Equal(1, session.Snapshot().Entries.Count(e => e.AgentActivity is not null));
        });
    }

    [Fact]
    public void PeerTranscriptScopeIncludesToolsAndEndsBeforeUnrelatedConversation()
    {
        var session = new TuiSession();
        session.AppendAgentExchange("🤖 Fusion · Task\nRequest", Color.Purple, "j1", "Working");
        var group = session.Snapshot().Entries[0].AgentActivity!;
        using (session.CaptureAgentExchange("j1", "Fusion"))
        {
            Assert.Equal("j1", session.CurrentAgentExchangeFor("Fusion"));
            Assert.Null(session.CurrentAgentExchangeFor("Other"));
            session.Append(new Text("Progress"));
            using (session.CaptureToolOutput(true)) session.Append(new Text("Tool output"));
            session.AppendSpaced(new Text("Final peer answer"));
        }
        session.CompleteToolTurn();
        Assert.All(session.Snapshot().Entries.Skip(1), e => Assert.Same(group, e.AgentOutput));
        session.AppendSpaced(new Text("User conversation continues"));
        Assert.Null(session.Snapshot().Entries[^1].AgentOutput);
        Assert.Null(session.CurrentAgentExchangeFor("Fusion"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PromptArrowsScrollWithoutMenusIncludingWhileBusy(bool disabled)
    {
        await using var services = Services();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            PromptInput prompt = null!;
            var scrolls = new List<string>();
            RenderFragment footer = b =>
            {
                b.OpenComponent<PromptInput>(0);
                b.AddAttribute(1, "Disabled", disabled);
                b.AddAttribute(2, "OnScroll", EventCallback.Factory.Create<string>(this, key => scrolls.Add(key)));
                b.AddComponentReferenceCapture(3, value => prompt = (PromptInput)value);
                b.CloseComponent();
            };
            await renderer.RenderComponentAsync<ChatShell>(ParameterView.FromDictionary(new Dictionary<string, object?> { ["Footer"] = footer }));
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            async Task Key(string key) => Assert.True(await (Task<bool>)typeof(PromptInput).GetMethod("PreviewKey", flags)!.Invoke(prompt, [new KeyboardEventArgs { Key = key }])!);
            await Key("ArrowUp"); await Key("DownArrow");
            Assert.Equal(new[] { "ArrowUp", "DownArrow" }, scrolls);
            if (!disabled)
            {
                prompt.SetValue(":cool");
                typeof(PromptInput).GetMethod("HandleChanged", flags)!.Invoke(prompt, null);
                await Key("ArrowUp");
                Assert.Equal(2, scrolls.Count);
            }
        });
    }
}
