using System.Reflection;
using MandoCode.Components;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Xunit;

namespace MandoCode.Tests;

public class DirectoryBrowserTests
{
    [Fact]
    public async Task BrowseDoesNotCommit_ConfirmAndCancelAreDistinct()
    {
        var root = Path.Combine(Path.GetTempPath(), "mandocode-browser-" + Guid.NewGuid());
        Directory.CreateDirectory(Path.Combine(root, "child"));
        try
        {
            var browser = new DirectoryBrowser();
            var callbacks = new List<string?>();
            ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(DirectoryBrowser.OnComplete)] = EventCallback.Factory.Create<string?>(callbacks, (string? path) => callbacks.Add(path))
            }).SetParameterProperties(browser);
            await Call(browser, "Navigate", root);
            await Call(browser, "Navigate", Path.Combine(root, "child"));
            Assert.Empty(callbacks);
            await Call(browser, "Key", new KeyboardEventArgs { Key = "Enter" });
            Assert.Equal(Path.Combine(root, "child"), Assert.Single(callbacks));
            callbacks.Clear();
            await Call(browser, "Key", new KeyboardEventArgs { Key = "Escape" });
            Assert.Null(Assert.Single(callbacks));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ParentNavigationAndInvalidDirectoryKeepBrowserUsable()
    {
        var root = Path.Combine(Path.GetTempPath(), "mandocode-browser-" + Guid.NewGuid());
        Directory.CreateDirectory(Path.Combine(root, "child"));
        try
        {
            var browser = new DirectoryBrowser();
            await Call(browser, "Navigate", Path.Combine(root, "child"));
            await Call(browser, "Key", new KeyboardEventArgs { Key = "ArrowLeft" });
            Assert.Equal(root, Field<string>(browser, "_directory"));
            await Call(browser, "Navigate", Path.Combine(root, "missing"));
            Assert.Equal(root, Field<string>(browser, "_directory"));
            Assert.NotEmpty(Field<string>(browser, "_error"));
            await Call(browser, "Navigate", Path.Combine(root, "child"));
            Assert.Equal(Path.Combine(root, "child"), Field<string>(browser, "_directory"));
        }
        finally { Directory.Delete(root, true); }
    }

    private static Task Call(DirectoryBrowser browser, string name, object argument) =>
        (Task)typeof(DirectoryBrowser).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(browser, [argument])!;
    private static T Field<T>(DirectoryBrowser browser, string name) =>
        (T)typeof(DirectoryBrowser).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(browser)!;
}
