using System.Reflection;
using HtmlAgilityPack;
using MandoCode.Components;
using MandoCode.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RazorConsole.Core;
using Spectre.Console;
using Xunit;

namespace MandoCode.Tests;

public class ApprovalSelectTests
{
    [Theory]
    [InlineData("ArrowDown", "Enter")]
    [InlineData("DownArrow", "Return")]
    public async Task ApprovalMenu_IsFocusable_AndNavigationSubmitsTheSelectedChoice(string down, string enter)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddRazorConsoleServices();
        using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        var coordinator = new ApprovalSelectCoordinator();
        var choices = new[]
        {
            new ApprovalSelectCoordinator.Option("Write file", Color.Green),
            new ApprovalSelectCoordinator.Option("Provide instructions", Color.Yellow),
            new ApprovalSelectCoordinator.Option("Reject", Color.Red)
        };
        var pending = coordinator.RequestAsync(choices);
        var options = choices.Select(o => new ApprovalSelect.Option(o.Text, o.Color)).ToArray();
        ApprovalSelect menu = default!;
        var scrolled = new List<ApprovalSelect.ScrollKey>();
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            RenderFragment fragment = b =>
            {
                b.OpenComponent<ApprovalSelect>(0);
                b.AddAttribute(1, "Options", options);
                b.AddAttribute(2, "OnSubmit", EventCallback.Factory.Create<string>(this, coordinator.Submit));
                b.AddAttribute(3, "OnScroll", EventCallback.Factory.Create<ApprovalSelect.ScrollKey>(this, key => scrolled.Add(key)));
                b.AddComponentReferenceCapture(4, instance => menu = (ApprovalSelect)instance);
                b.CloseComponent();
            };
            var root = await renderer.RenderComponentAsync<FragmentHost>(ParameterView.FromDictionary(
                new Dictionary<string, object?> { ["ChildContent"] = fragment }));
            var document = new HtmlDocument();
            document.LoadHtml(root.ToHtmlString());
            var target = document.DocumentNode.SelectSingleNode("//div[@data-focusable='true']");
            Assert.NotNull(target);
            Assert.Equal("0", target.GetAttributeValue("data-focus-order", ""));
            var focusKey = target.GetAttributeValue("data-focus-key", "");
            Assert.StartsWith("approval-", focusKey);
            Assert.False(pending.IsCompleted);
            var handler = typeof(ApprovalSelect).GetMethod("HandleKeyDown", BindingFlags.Instance | BindingFlags.NonPublic)!;
            async Task Key(string key) => await (Task)handler.Invoke(menu, new object[] { new KeyboardEventArgs { Key = key } })!;
            await Key(down == "ArrowDown" ? "ArrowUp" : "UpArrow");
            Assert.Contains("&gt; Reject", root.ToHtmlString());
            await Key(down);
            Assert.Contains("&gt; Write file", root.ToHtmlString());
            await Key(down);
            Assert.Contains("&gt; Provide instructions", root.ToHtmlString());
            Assert.Contains(focusKey, root.ToHtmlString());
            await Key("PageDown");
            Assert.Equal(new[] { ApprovalSelect.ScrollKey.PageDown }, scrolled);
            Assert.Contains("&gt; Provide instructions", root.ToHtmlString());
            await Key(enter);
            Assert.Equal("Provide instructions", await pending);
            Assert.False(coordinator.IsActive);
        });
    }

    public sealed class FragmentHost : ComponentBase
    {
        [Parameter] public RenderFragment? ChildContent { get; set; }
        protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder builder) =>
            builder.AddContent(0, ChildContent);
    }
}
