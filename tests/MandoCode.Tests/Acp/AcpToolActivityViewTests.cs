using System.Reflection;
using MandoCode.Acp.Protocol;
using MandoCode.Components;
using MandoCode.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MandoCode.Tests.Acp;

[Trait("Category", "Component")]
public class AcpToolActivityViewTests
{
    [Fact]
    public async Task DetailsExpandAndStayOpenAsTheSameCallFinishes()
    {
        await using var services = AcpTestHost.Services();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var session = new TuiSession();
        var store = new MandoCode.Services.Acp.AcpToolActivityStore(session.UpdateAcpTool);
        store.Apply(new ToolCall { ToolCallId = "1", Title = "Inspect metadata", Kind = "execute", Status = "in_progress" });
        var entry = Assert.Single(session.Snapshot().Entries);
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            Host host = null!;
            var rendered = await renderer.RenderComponentAsync<Host>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(Host.Entry)] = entry,
                [nameof(Host.Capture)] = (Action<Host>)(value => host = value)
            }));
            Assert.Contains("Running", rendered.ToHtmlString());
            Assert.DoesNotContain("data-canvas", rendered.ToHtmlString());
            host.Toggle();
            Assert.Contains("data-canvas", rendered.ToHtmlString());
            store.Apply(new ToolCallUpdate { ToolCallId = "1", Status = "completed" });
            host.Refresh();
            var html = rendered.ToHtmlString();
            Assert.Contains("Completed", html);
            Assert.DoesNotContain("Running", html);
            Assert.Contains("data-canvas", html);
            Assert.Single(session.Snapshot().Entries);
            host.Toggle();
            Assert.DoesNotContain("data-canvas", rendered.ToHtmlString());
        });
    }

    public sealed class Host : ComponentBase
    {
        [Parameter] public TuiEntry Entry { get; set; } = default!;
        [Parameter] public Action<Host> Capture { get; set; } = default!;
        private AcpToolActivityView _view = default!;
        protected override void OnInitialized() => Capture(this);
        public void Refresh() => StateHasChanged();
        public void Toggle()
        {
            typeof(AcpToolActivityView).GetMethod("Toggle", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(_view, null);
            Refresh();
        }
        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<AcpToolActivityView>(0);
            builder.AddAttribute(1, "Entry", Entry);
            builder.AddAttribute(2, "Activity", Entry.AcpTool);
            builder.AddComponentReferenceCapture(3, value => _view = (AcpToolActivityView)value);
            builder.CloseComponent();
        }
    }
}
