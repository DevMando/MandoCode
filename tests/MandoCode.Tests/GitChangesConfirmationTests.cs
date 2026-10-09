using System.Reflection;
using MandoCode.Components;
using MandoCode.Services;
using Microsoft.AspNetCore.Components.Web;
using Xunit;

namespace MandoCode.Tests;

[Trait("Category", "Component")]
public sealed class GitChangesConfirmationTests
{
    [Fact]
    public async Task EnterOnDefaultConfirmationCancelsWithoutDiscarding()
    {
        var panel = new AgentGitChanges();
        var type = panel.GetType();
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var file = new GitChangedFile("important.txt", " M", null, 1, 1, false);
        type.GetField("_file", flags)!.SetValue(panel, file);
        type.GetField("_confirm", flags)!.SetValue(panel, true);
        await (Task)type.GetMethod("Key", flags)!.Invoke(panel, [new KeyboardEventArgs { Key = "Enter" }])!;
        Assert.False((bool)type.GetField("_confirm", flags)!.GetValue(panel)!);
        Assert.Same(file, type.GetField("_file", flags)!.GetValue(panel));
    }
}
