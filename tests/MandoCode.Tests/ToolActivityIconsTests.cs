using MandoCode.Services;
using Xunit;

namespace MandoCode.Tests;

public class ToolActivityIconsTests
{
    [Theory]
    [InlineData("search_web", "🔎")]
    [InlineData("fetch_webpage", "🌐")]
    [InlineData("FileSystem_read_file_contents", "📄")]
    [InlineData("grep_files", "📄")]
    [InlineData("write_file", "✏️")]
    [InlineData("edit_file", "✏️")]
    [InlineData("delete_folder", "🗑️")]
    [InlineData("execute_command", "⚡")]
    [InlineData("unknown_tool", "⚡")]
    public void UsesToolIdentity(string tool, string icon) => Assert.Equal(icon, ToolActivityIcons.For(tool));

    [Fact]
    public void ExternalToolsUseIntegrationIconEvenWithBuiltinName()
        => Assert.Equal("🔌", ToolActivityIcons.For("search_web", isMcp: true));
}
