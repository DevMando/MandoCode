using System.Reflection;
using MandoCode.Components;
using MandoCode.Models;
using MandoCode.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using RazorConsole.Core;
using HtmlAgilityPack;
using RazorConsole.Core.Layout;
using RazorConsole.Core.Rendering;
using RazorConsole.Core.Vdom;
using Spectre.Console;
using Xunit;

namespace MandoCode.Tests;

public sealed class IntegrationSettingsKeyboardTests
{
    [Theory]
    [InlineData(100, 0)]
    [InlineData(42, 0)]
    [InlineData(100, 1)]
    [InlineData(42, 1)]
    public async Task TableButtons_ActOnTheirOwnItem_AndKeepSelectionAfterSorting(int width, int tab)
    {
        var temp = Path.Combine(Path.GetTempPath(), "mandocode-table-test-" + Guid.NewGuid().ToString("N"));
        var config = new MandoCodeConfig { AllowPersistence = false, UserSkillsDirectory = temp, EnableMcp = false,
            McpServers = new() { ["alpha"] = new() { Command = "unused" }, ["beta"] = new() { Command = "unused" } } };
        var registrations = new ServiceCollection().AddLogging();
        registrations.AddRazorConsoleServices();
        Program.RegisterAgentServices(registrations, config, Path.GetTempPath());
        await using var services = registrations.BuildServiceProvider();
        var coordinator = services.GetRequiredService<CliIntegrationCoordinator>();
        var alpha = tab == 0 ? "alpha" : coordinator.Skills.Save(null, "Alpha", "First description", "First instructions");
        var beta = tab == 0 ? "beta" : coordinator.Skills.Save(null, "Beta", "Second description", "Second instructions");
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        try
        {
            await renderer.Dispatcher.InvokeAsync(async () =>
            {
                IntegrationSettingsPanel panel = null!;
                var scope = new ExplorerFocusScope { Active = true };
                var rendered = await renderer.RenderComponentAsync<Host>(ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    ["Config"] = config, ["Coordinator"] = coordinator, ["Scope"] = scope, ["Width"] = width, ["Kind"] = tab,
                    ["Capture"] = (Action<IntegrationSettingsPanel>)(value => panel = value)
                }));
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                T Value<T>(string name) => (T)typeof(IntegrationSettingsPanel).GetField(name, flags)!.GetValue(panel)!;
                async Task Key(string key) { await (Task)typeof(IntegrationSettingsPanel).GetMethod("Key", flags)!.Invoke(panel, new object[] { new KeyboardEventArgs { Key = key } })!; typeof(ComponentBase).GetMethod("StateHasChanged", flags)!.Invoke(panel, null); }
                {
                    Assert.True((bool)typeof(IntegrationSettingsPanel).GetProperty("SearchSelected", flags)!.GetValue(panel)!);
                    Assert.Contains(tab == 0 ? "Search MCP Servers" : "Search Skills", rendered.ToHtmlString());
                    await (Task<bool>)typeof(IntegrationSettingsPanel).GetMethod("SearchKey", flags)!.Invoke(panel, new object[] { new KeyboardEventArgs { Key = "ArrowDown" } })!;
                }
                await (Task)typeof(IntegrationSettingsPanel).GetMethod("Key", flags)!.Invoke(panel, new object[] { new KeyboardEventArgs { Key = "ArrowRight", CtrlKey = true } })!; Assert.Equal(tab, Value<int>("_kind")); Assert.DoesNotContain(tab == 0 ? "New Skill" : "MCP Servers", rendered.ToHtmlString()); var document = new HtmlDocument(); document.LoadHtml(rendered.ToHtmlString());
                var deletes = document.DocumentNode.SelectNodes("//*[@data-integration-action='Remove']");
                Assert.InRange(deletes.Count, 1, 2);
                Assert.Equal(alpha, deletes[0].GetAttributeValue("data-integration-item", ""));
                if (width >= 70) Assert.Equal(2, deletes.Count);
                if (tab == 0 || width >= 70) Assert.Contains(tab == 0 ? "Type / Status" : "Actions", rendered.ToHtmlString());
                if (tab == 1) Assert.DoesNotContain(">Description<", rendered.ToHtmlString());
                var painted = Paint(services, document, width, 24);
                Assert.Contains("Delete", painted);
                Assert.Contains("Refresh", painted);
                Assert.Contains("Close", painted);
                Assert.Contains(tab == 0 ? "Global MCP settings" : "Global skills", painted);
                Assert.DoesNotContain("Shared user settings", painted);
                Assert.DoesNotContain("Per-agent access", painted);
                Assert.True(painted.IndexOf(tab == 0 ? "+MCP Server" : "+Skill", StringComparison.Ordinal) < painted.IndexOf(tab == 0 ? "Type / Status" : "Alpha", StringComparison.Ordinal), painted);
                Assert.True(painted.IndexOf("Search", StringComparison.Ordinal) < painted.IndexOf(tab == 0 ? "Type / Status" : "Alpha", StringComparison.Ordinal), painted);
                if (tab == 1) Assert.Contains("First description", painted);
                Assert.All(painted.Split('\n'), line => Assert.True(RazorConsole.Core.Input.TextSelectionState.CellWidth(line.TrimEnd('\r')) <= width, line));
                if (width >= 70)
                {
                    var line = painted.Split('\n').Single(text => text.Contains(tab == 0 ? "alpha" : "Alpha", StringComparison.Ordinal) && text.Contains("Delete", StringComparison.Ordinal));
                    Assert.True(line.IndexOf("Active", StringComparison.Ordinal) < line.IndexOf("Edit", StringComparison.Ordinal), line);
                    Assert.True(line.IndexOf("Edit", StringComparison.Ordinal) < line.IndexOf("Delete", StringComparison.Ordinal), line);
                }
                await Key("ArrowDown");
                Assert.Equal(beta, Value<string>("_rowKey"));
                document.LoadHtml(rendered.ToHtmlString());
                Assert.Contains(document.DocumentNode.SelectNodes("//*[@data-integration-action='Remove']"), node => node.GetAttributeValue("data-integration-item", "") == beta);
                await Key("ArrowUp");
                await scope.ToggleAsync(); await Key("Enter");
                Assert.Equal(alpha, Value<string>("_rowKey"));
                Assert.Equal(1, Value<int>("_row")); // Disabled rows move after enabled rows, focus stays on Alpha.
                if (tab == 0) Assert.True(coordinator.Servers["alpha"].Disabled);
                else Assert.False(coordinator.Skills.List().Single(skill => skill.Folder == alpha).Enabled);
                var statusDocument = new HtmlDocument(); statusDocument.LoadHtml(rendered.ToHtmlString());
                Assert.Contains("Disabled", Paint(services, statusDocument, width, 24));
                await Key("ArrowUp"); await Key("ArrowRight"); await Key("Enter");
                Assert.Equal(tab == 0 ? "beta" : "Beta", Value<string>("_name"));
                Assert.Equal(tab == 0 ? "mcp" : "skill", Value<string>("_mode"));
                var inlineBuffer = (RazorConsole.Core.Input.TextSelectionState)typeof(IntegrationSettingsPanel).GetProperty("InlineBuffer", flags)!.GetValue(panel)!;
                Assert.Equal(tab == 0 ? "beta" : "Beta", inlineBuffer.Text);
                inlineBuffer.SetText("Edited name");
                typeof(IntegrationSettingsPanel).GetMethod("InlineChanged", flags)!.Invoke(panel, null);
                Assert.Equal("Edited name", Value<string>("_name"));
                Assert.Null(Value<object?>("_input"));
                Assert.True(await (Task<bool>)typeof(IntegrationSettingsPanel).GetMethod("InlineKey", flags)!.Invoke(panel, new object[] { new KeyboardEventArgs { Key = "Enter" } })!);
                Assert.Equal(0, Value<int>("_selected"));
                Assert.False(await (Task<bool>)typeof(IntegrationSettingsPanel).GetMethod("InlineKey", flags)!.Invoke(panel, new object[] { new KeyboardEventArgs { Key = "ArrowLeft" } })!);
                await Key("Enter");
                var formDocument = new HtmlDocument(); formDocument.LoadHtml(rendered.ToHtmlString());
                var formFrame = Paint(services, formDocument, width, 24);
                Assert.Contains("Edited name", formFrame);
                Assert.All(formFrame.Split('\n'), line => Assert.True(RazorConsole.Core.Input.TextSelectionState.CellWidth(line.TrimEnd('\r')) <= width, line));
                var fields = (int)typeof(IntegrationSettingsPanel).GetProperty("OptionCount", flags)!.GetValue(panel)!;
                var actions = (string[])typeof(IntegrationSettingsPanel).GetProperty("Actions", flags)!.GetValue(panel)!;
                Assert.DoesNotContain("Details", actions);
                typeof(IntegrationSettingsPanel).GetField("_selected", flags)!.SetValue(panel, fields);
                await Key("ArrowRight"); Assert.Equal(fields + 1, Value<int>("_selected"));
                await Key("ArrowLeft"); Assert.Equal(fields, Value<int>("_selected"));
                await Key("ArrowLeft"); Assert.Equal(fields + actions.Length - 1, Value<int>("_selected"));
                await Key("ArrowRight"); Assert.Equal(fields, Value<int>("_selected"));
                if (tab == 0)
                {
                    await (Task)typeof(IntegrationSettingsPanel).GetMethod("PerformAction", flags)!.Invoke(panel, new object[] { "Cancel" })!;
                    Assert.Equal("list", Value<string>("_mode"));
                    Assert.Null(Value<string?>("_confirmAction"));
                    Assert.True(coordinator.Servers.ContainsKey("beta"));
                    Assert.False(coordinator.Servers.ContainsKey("Edited name"));
                }
                else { await Key("Escape"); await Key("ArrowDown"); await Key("Enter"); }
                await Key("ArrowRight"); await Key("ArrowRight"); await Key("ArrowRight"); await Key("Enter");
                Assert.StartsWith("delete ", Value<string>("_confirmAction"));
                await Key("Enter"); // Cancel is selected by default; return to this item's Delete button.
                Assert.Equal(3, Value<int>("_selected"));
                Assert.Equal(beta, Value<string>("_rowKey"));
                await Key("Enter"); await Key("ArrowDown"); await Key("Enter");
                if (tab == 0) { Assert.False(coordinator.Servers.ContainsKey("beta")); Assert.True(coordinator.Servers.ContainsKey("alpha")); }
                else { Assert.Equal(alpha, Assert.Single(coordinator.Skills.List()).Folder); Assert.True(Directory.Exists(alpha)); }
                if (tab == 0)
                {
                    var perform = typeof(IntegrationSettingsPanel).GetMethod("PerformAction", flags)!;
                    await (Task)perform.Invoke(panel, new object[] { "Add Server" })!;
                    var draft = (RazorConsole.Core.Input.TextSelectionState)typeof(IntegrationSettingsPanel).GetProperty("InlineBuffer", flags)!.GetValue(panel)!;
                    draft.SetText("unsaved-server");
                    typeof(IntegrationSettingsPanel).GetMethod("InlineChanged", flags)!.Invoke(panel, null);
                    await (Task)perform.Invoke(panel, new object[] { "Cancel" })!;
                    Assert.Equal("list", Value<string>("_mode"));
                    Assert.Null(Value<string?>("_confirmAction"));
                    Assert.Equal("", Value<string>("_name"));
                    Assert.False(coordinator.Servers.ContainsKey("unsaved-server"));
                    typeof(IntegrationSettingsPanel).GetMethod("SelectSearch", flags)!.Invoke(panel, null);
                    var search = Value<RazorConsole.Core.Input.TextSelectionState>("_searchBuffer");
                    search.SetText("no-match");
                    typeof(IntegrationSettingsPanel).GetMethod("SearchChanged", flags)!.Invoke(panel, null);
                    Assert.Empty((string[])typeof(IntegrationSettingsPanel).GetProperty("Rows", flags)!.GetValue(panel)!);
                    Assert.True((bool)typeof(IntegrationSettingsPanel).GetProperty("SearchSelected", flags)!.GetValue(panel)!);
                    Assert.Null(Value<object?>("_input"));
                    search.SetText("");
                    typeof(IntegrationSettingsPanel).GetMethod("SearchChanged", flags)!.Invoke(panel, null);
                    Assert.Single((string[])typeof(IntegrationSettingsPanel).GetProperty("Rows", flags)!.GetValue(panel)!);
                    Assert.True(await (Task<bool>)typeof(IntegrationSettingsPanel).GetMethod("SearchKey", flags)!.Invoke(panel, new object[] { new KeyboardEventArgs { Key = "ArrowDown" } })!);
                    Assert.Equal(0, Value<int>("_selected"));
                }
                else
                {
                    var perform = typeof(IntegrationSettingsPanel).GetMethod("PerformAction", flags)!;
                    foreach (var mode in new[] { "Write Manually", "Create with AI" })
                    {
                        await (Task)perform.Invoke(panel, new object[] { "New Skill" })!;
                        await (Task)perform.Invoke(panel, new object[] { mode })!;
                        var draft = (RazorConsole.Core.Input.TextSelectionState)typeof(IntegrationSettingsPanel).GetProperty("InlineBuffer", flags)!.GetValue(panel)!;
                        draft.SetText("Unsaved skill");
                        typeof(IntegrationSettingsPanel).GetMethod("InlineChanged", flags)!.Invoke(panel, null);
                        await (Task)perform.Invoke(panel, new object[] { "Cancel" })!;
                        Assert.Equal("list", Value<string>("_mode"));
                        Assert.Null(Value<string?>("_confirmAction"));
                        Assert.Equal("", Value<string>("_name"));
                        Assert.Equal("", Value<string>("_intent"));
                        Assert.Equal(alpha, Assert.Single(coordinator.Skills.List()).Folder);
                    }
                }
            });
        }
        finally { if (Directory.Exists(temp)) Directory.Delete(temp, true); }
    }

    [Fact]
    public async Task TabsStayInManager_AndEditorsAndDiscardConfirmationPreserveDrafts()
    {
        var temp = Path.Combine(Path.GetTempPath(), "mandocode-manager-test-" + Guid.NewGuid().ToString("N"));
        var config = new MandoCodeConfig { AllowPersistence = false, UserSkillsDirectory = temp, ShowTips = true };
        var registrations = new ServiceCollection().AddLogging();
        registrations.AddRazorConsoleServices();
        Program.RegisterAgentServices(registrations, config, Path.GetTempPath());
        await using var services = registrations.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var scope = new ExplorerFocusScope { Active = true };
        try
        {
            await renderer.Dispatcher.InvokeAsync(async () =>
            {
                IntegrationSettingsPanel panel = null!;
                var closed = false;
                var rendered = await renderer.RenderComponentAsync<Host>(ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    ["Config"] = config, ["Coordinator"] = services.GetRequiredService<CliIntegrationCoordinator>(), ["Scope"] = scope,
                    ["Capture"] = (Action<IntegrationSettingsPanel>)(value => panel = value), ["Close"] = (Action)(() => closed = true), ["Kind"] = 1
                }));
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                T Value<T>(string name) => (T)typeof(IntegrationSettingsPanel).GetField(name, flags)!.GetValue(panel)!;
                async Task Key(string key) { await (Task)typeof(IntegrationSettingsPanel).GetMethod("Key", flags)!.Invoke(panel, new object[] { new KeyboardEventArgs { Key = key } })!; typeof(ComponentBase).GetMethod("StateHasChanged", flags)!.Invoke(panel, null); }
                Assert.NotNull(scope.NavigateTab);
                await scope.ToggleAsync(); Assert.Equal(1, Value<int>("_selected"));
                await scope.ToggleAsync(true); Assert.Equal(0, Value<int>("_selected"));
                Assert.Equal(1, Value<int>("_kind")); Assert.DoesNotContain("MCP Servers", rendered.ToHtmlString());
                await (Task)typeof(IntegrationSettingsPanel).GetMethod("PerformAction", flags)!.Invoke(panel, new object[] { "New Skill" })!; await Key("Unidentified"); Assert.Equal("skill-choice", Value<string>("_mode"));
                Assert.Contains("Write Manually", rendered.ToHtmlString());
                Assert.Contains("Create with AI", rendered.ToHtmlString());
                var choiceDocument = new HtmlDocument(); choiceDocument.LoadHtml(rendered.ToHtmlString());
                var choiceLines = Paint(services, choiceDocument, 80, 40).Split('\n');
                Assert.InRange(Array.FindIndex(choiceLines, line => line.Contains("Write Manually", StringComparison.Ordinal)), 1, 10);
                await Key("ArrowRight"); await Key("Enter"); Assert.Equal("skill-ai", Value<string>("_mode"));
                Assert.Contains("What should this skill do?", rendered.ToHtmlString());
                var intentBuffer = (RazorConsole.Core.Input.TextSelectionState)typeof(IntegrationSettingsPanel).GetProperty("InlineBuffer", flags)!.GetValue(panel)!;
                intentBuffer.SetText("Review code for bugs");
                typeof(IntegrationSettingsPanel).GetMethod("InlineChanged", flags)!.Invoke(panel, null);
                Assert.True(await (Task<bool>)typeof(IntegrationSettingsPanel).GetMethod("InlineKey", flags)!.Invoke(panel, new object[] { new KeyboardEventArgs { Key = "Enter" } })!);
                Assert.Null(Value<object?>("_input"));
                Assert.Equal("Review code for bugs", Value<string>("_intent"));
                typeof(IntegrationSettingsPanel).GetField("_modelReturnMode", flags)!.SetValue(panel, "skill-ai");
                typeof(IntegrationSettingsPanel).GetField("_modelReturnSelection", flags)!.SetValue(panel, 1);
                typeof(IntegrationSettingsPanel).GetField("_mode", flags)!.SetValue(panel, "models");
                typeof(IntegrationSettingsPanel).GetField("_modelChoices", flags)!.SetValue(panel, new[] { "chosen:cloud" });
                await Key("Enter");
                Assert.Equal("skill-ai", Value<string>("_mode"));
                Assert.Equal(1, Value<int>("_selected"));
                Assert.Equal("Review code for bugs", Value<string>("_intent"));
                await Key("Escape"); await Key("ArrowDown"); await Key("Enter");
                await (Task)typeof(IntegrationSettingsPanel).GetMethod("PerformAction", flags)!.Invoke(panel, new object[] { "New Skill" })!;
                await Key("Enter"); Assert.Equal("skill", Value<string>("_mode"));
                await Key("Enter"); Assert.Null(Value<object?>("_input"));
                var nameBuffer = (RazorConsole.Core.Input.TextSelectionState)typeof(IntegrationSettingsPanel).GetProperty("InlineBuffer", flags)!.GetValue(panel)!;
                nameBuffer.SetText("My skill");
                typeof(IntegrationSettingsPanel).GetMethod("InlineChanged", flags)!.Invoke(panel, null);
                Assert.Equal("My skill", Value<string>("_name"));
                await Key("Escape"); Assert.Equal("discard draft", Value<string>("_confirmAction"));
                await Key("Enter"); Assert.Null(Value<string?>("_confirmAction"));
                Assert.Equal("My skill", Value<string>("_name"));
                Assert.Contains("Save", rendered.ToHtmlString());
                var perform = typeof(IntegrationSettingsPanel).GetMethod("PerformAction", flags)!;
                var manualFields = (string[])typeof(IntegrationSettingsPanel).GetProperty("SkillFields", flags)!.GetValue(panel)!;
                Assert.Equal(new[] { "Name", "Description", "Instructions" }, manualFields);
                Assert.Equal(new[] { "Save", "Cancel", "Refine with AI" }, (string[])typeof(IntegrationSettingsPanel).GetProperty("Actions", flags)!.GetValue(panel)!);
                await (Task)perform.Invoke(panel, new object[] { "Refine with AI" })!;
                Assert.Equal("skill-refine", Value<string>("_mode"));
                await (Task)perform.Invoke(panel, new object[] { "Back to Form" })!;
                Assert.Equal("skill", Value<string>("_mode"));
                Assert.Equal("My skill", Value<string>("_name"));
                typeof(IntegrationSettingsPanel).GetField("_mode", flags)!.SetValue(panel, "models");
                typeof(IntegrationSettingsPanel).GetField("_modelReturnMode", flags)!.SetValue(panel, "skill");
                typeof(IntegrationSettingsPanel).GetField("_modelReturnSelection", flags)!.SetValue(panel, 3);
                typeof(IntegrationSettingsPanel).GetField("_modelChoices", flags)!.SetValue(panel, new[] { "selected:cloud", "other:cloud" });
                await Key("ArrowDown"); await Key("Enter");
                Assert.Equal("other:cloud", Value<string>("_model"));
                Assert.Equal("My skill", Value<string>("_name"));
                Assert.Equal("skill", Value<string>("_mode"));
                await Key("Escape"); await Key("ArrowDown"); await Key("Enter");
                Assert.Equal("list", Value<string>("_mode"));
                Assert.Empty(services.GetRequiredService<CliIntegrationCoordinator>().Skills.List());
                await (Task)perform.Invoke(panel, new object[] { "Install from Git / ZIP / Folder" })!;
                Assert.Equal("skill-install", Value<string>("_mode"));
                await (Task)perform.Invoke(panel, new object[] { "Choose Folder" })!;
                Assert.True(Value<bool>("_installBrowser"));
                await scope.ToggleAsync();
                Assert.True(Value<bool>("_installBrowser"));
                await (Task)typeof(IntegrationSettingsPanel).GetMethod("CompleteFolderInstall", flags)!.Invoke(panel, new object?[] { null })!;
                Assert.False(Value<bool>("_installBrowser"));
                Assert.Empty(services.GetRequiredService<CliIntegrationCoordinator>().Skills.List());
                await Key("Escape");
                typeof(IntegrationSettingsPanel).GetField("_tools", flags)!.SetValue(panel, new[] { new McpToolsViewer.ToolDescription("search_docs", "Find documentation [sample] <without hiding details>.") });
                typeof(IntegrationSettingsPanel).GetField("_toolsServer", flags)!.SetValue(panel, "Example");
                typeof(ComponentBase).GetMethod("StateHasChanged", flags)!.Invoke(panel, null);
                var viewer = new McpToolsViewer();
                var renderLine = typeof(McpToolsViewer).GetMethod("RenderLine", flags)!;
                var writer = new StringWriter();
                var console = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(writer), Ansi = AnsiSupport.No, ColorSystem = ColorSystemSupport.NoColors });
                console.Write((Text)renderLine.Invoke(viewer, new object[] { "search_docs", true })!);
                console.Write((Text)renderLine.Invoke(viewer, new object[] { "Find documentation [sample]", false })!);
                var toolsPainted = writer.ToString();
                Assert.Contains("search_docs", toolsPainted);
                Assert.Contains("Find documentation [sample]", toolsPainted);
                Assert.DoesNotContain("[bold]", toolsPainted);
                Assert.DoesNotContain("[[sample]]", toolsPainted);
                Assert.Contains("1 tools", rendered.ToHtmlString());
                var selectionBeforeToolsTab = Value<int>("_selected");
                await scope.ToggleAsync();
                Assert.Equal(selectionBeforeToolsTab, Value<int>("_selected"));
                typeof(IntegrationSettingsPanel).GetField("_tools", flags)!.SetValue(panel, null);
                await Key("Escape"); Assert.True(closed);
            });
        }
        finally { if (Directory.Exists(temp)) Directory.Delete(temp, true); }
    }

    private static string Paint(IServiceProvider services, HtmlDocument document, int width, int height)
    {
        VNode? Convert(HtmlNode node)
        {
            if (node.NodeType == HtmlNodeType.Text) return VNode.CreateText(System.Net.WebUtility.HtmlDecode(node.InnerText));
            if (node.NodeType != HtmlNodeType.Element) return null;
            var result = VNode.CreateElement(node.Name);
            foreach (var attribute in node.Attributes) result.SetAttribute(attribute.Name, System.Net.WebUtility.HtmlDecode(attribute.Value));
            foreach (var child in node.ChildNodes) if (Convert(child) is { } nested) result.AddChild(nested);
            return result;
        }
        var root = VNode.CreateRegion();
        foreach (var child in document.DocumentNode.ChildNodes) if (Convert(child) is { } node) root.AddChild(node);
        var layout = new LayoutEngine().Layout(services.GetRequiredService<WidgetTranslationContext>().Translate(root), new BoxConstraints(width, width, height, height));
        var writer = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(writer), Ansi = AnsiSupport.No, ColorSystem = ColorSystemSupport.NoColors });
        console.Profile.Width = width;
        console.Write(layout.PaintToRenderable());
        return writer.ToString();
    }

    public sealed class Host : ComponentBase
    {
        [Parameter] public MandoCodeConfig Config { get; set; } = default!;
        [Parameter] public CliIntegrationCoordinator Coordinator { get; set; } = default!;
        [Parameter] public ExplorerFocusScope Scope { get; set; } = default!;
        [Parameter] public Action<IntegrationSettingsPanel>? Capture { get; set; }
        [Parameter] public Action? Close { get; set; }
        [Parameter] public int Width { get; set; } = 80;
        [Parameter] public int Kind { get; set; }
        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<CascadingValue<ExplorerFocusScope>>(0);
            builder.AddAttribute(1, "Value", Scope);
            builder.AddAttribute(2, "ChildContent", (RenderFragment)(content =>
            {
                content.OpenComponent<IntegrationSettingsPanel>(0);
                content.AddAttribute(1, "Config", Config);
                content.AddAttribute(2, "Coordinator", Coordinator);
                content.AddAttribute(3, "Width", Width);
                content.AddAttribute(4, "Height", 24);
                content.AddAttribute(5, "OnClose", EventCallback.Factory.Create(this, () => Close?.Invoke()));
                content.AddAttribute(7, "Kind", Kind);
                content.AddComponentReferenceCapture(6, value => Capture?.Invoke((IntegrationSettingsPanel)value));
                content.CloseComponent();
            }));
            builder.CloseComponent();
        }
    }
}
