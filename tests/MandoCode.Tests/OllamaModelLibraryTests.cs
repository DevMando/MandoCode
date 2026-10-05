using System.Net;
using System.Text;
using MandoCode.Services;
using Xunit;

namespace MandoCode.Tests;

public class OllamaModelLibraryTests
{
    [Fact]
    public void CatalogIgnoresNavigationAndDecodesDescription()
    {
        var models = OllamaModelLibrary.ParseModels("<a href='/library/example'><h2>example</h2><p>Code &amp; tools</p><span>tools</span></a><a href='/library/example'>duplicate</a><a href='/library/../bad'><h2>bad</h2></a>");
        var model = Assert.Single(models);
        Assert.Equal("example", model.Name);
        Assert.Equal("Code & tools", model.Description);
        Assert.Equal("tools", model.Capabilities);
    }
    [Fact]
    public void TagsDeduplicateDesktopAndMobileAndKeepDownloadSize()
    {
        var tags = OllamaModelLibrary.ParseTags("<a href='/library/example:8b'>example:8b</a><a href='/library/example:8b'>example:8b • 5.2GB</a><a href='/library/other:8b'>other</a>", "example");
        var tag = Assert.Single(tags);
        Assert.Equal("example:8b", tag.Name);
        Assert.Equal("5.2GB", tag.Size);
    }
    [Fact]
    public async Task PullUsesConfiguredServerAndStreamsProgress()
    {
        var handler = new Handler("{\"status\":\"pulling\",\"completed\":50,\"total\":100}\n{\"status\":\"success\"}\n");
        using var http = new HttpClient(handler);
        var progress = new List<OllamaModelLibrary.Progress>();
        await new OllamaModelLibrary(http).Pull("http://server:1234", "example:8b", progress.Add, default);
        Assert.Equal("http://server:1234/api/pull", handler.Url);
        Assert.Contains("example:8b", handler.Body);
        Assert.Equal(50, progress[0].Completed);
        Assert.Equal("success", progress[1].Status);
    }
    [Theory]
    [InlineData("{\"error\":\"model not found\"}\n", "model not found")]
    [InlineData("{\"status\":\"pulling\"}\n", "before Ollama confirmed success")]
    public async Task PullDoesNotReportFalseSuccess(string stream, string message)
    {
        using var http = new HttpClient(new Handler(stream));
        var error = await Assert.ThrowsAnyAsync<Exception>(() => new OllamaModelLibrary(http).Pull("http://server", "example", _ => { }, default));
        Assert.Contains(message, error.Message);
    }
    [Fact]
    public void NameSearchIsCaseInsensitiveAndSortKeepsPublishedOrderUnlessRequested()
    {
        OllamaModelLibrary.Model[] models = [new("zeta", "alpha in description", ""), new("Alpha-two", "", ""), new("alpha-one", "", "")];
        var matches = MandoCode.Components.OllamaPullPanel.FilterModels(models, " ALPHA ", false);
        Assert.Equal(new[] { "Alpha-two", "alpha-one" }, matches.Select(m => m.Name));
        Assert.Equal(new[] { "alpha-one", "Alpha-two" }, MandoCode.Components.OllamaPullPanel.FilterModels(models, "alpha", true).Select(m => m.Name));
        Assert.Equal("zeta", MandoCode.Components.OllamaPullPanel.FilterModels(models, "", false)[0].Name);
    }
    [Fact]
    public async Task NewestUsesThePublishedLibraryOrder()
    {
        var handler = new Handler("<a href='/library/zeta'><h2>zeta</h2></a><a href='/library/alpha'><h2>alpha</h2></a>");
        using var http = new HttpClient(handler);
        var models = await new OllamaModelLibrary(http).Models(default, newest: true);
        Assert.Equal("https://ollama.com/library?sort=newest", handler.Url);
        Assert.Equal(new[] { "zeta", "alpha" }, models.Select(m => m.Name));
    }
    [Fact]
    public void CloudFilterCombinesWithSearchAndIgnoresUnrelatedCapabilityWords()
    {
        OllamaModelLibrary.Model[] models = [new("local", "cloud mentioned here", "tools"), new("cloud-two", "", "tools · cloud"), new("cloud-one", "", "cloud · vision"), new("cloudish", "", "cloudish")];
        Assert.Equal(new[] { "cloud-one", "cloud-two" }, MandoCode.Components.OllamaPullPanel.FilterModels(models, "cloud", true, cloudOnly: true).Select(m => m.Name));
        Assert.Equal(new[] { "cloud-two" }, MandoCode.Components.OllamaPullPanel.FilterModels(models, "two", false, cloudOnly: true).Select(m => m.Name));
    }
    private sealed class Handler(string stream) : HttpMessageHandler
    {
        public string? Url, Body;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Url = request.RequestUri!.ToString(); Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new(HttpStatusCode.OK) { Content = new StringContent(stream, Encoding.UTF8, "application/x-ndjson") };
        }
    }
}
