using MandoCode.Models;
using MandoCode.Services;
using Microsoft.Extensions.AI;
using Xunit;

namespace MandoCode.Tests;

public class PlanQualityTests
{
    [Fact]
    public void RepositoryDistinguishesEmptyMissingAndUnsupportedFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Assert.Contains("Confirmed empty directory", PlanRepositoryContext.Capture(root, "game"));
            File.WriteAllText(Path.Combine(root, "asset.bin"), "asset");
            Assert.Contains("Directory is not empty", PlanRepositoryContext.Capture(root, "game"));
            var missing = PlanRepositoryContext.Capture(Path.Combine(root, "missing"), "game");
            Assert.Contains("directory errors", missing);
            Assert.DoesNotContain("Confirmed empty directory", missing);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void WebProjectIncludesManifestAndEntryPointWithoutArbitraryJsonSecrets()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "package.json"), "{\"scripts\":{\"test\":\"node test.js\"}}");
            File.WriteAllText(Path.Combine(root, "index.html"), "<canvas></canvas>");
            File.WriteAllText(Path.Combine(root, "style.css"), "canvas { color: red; }");
            File.WriteAllText(Path.Combine(root, "credentials.json"), "do-not-read");
            File.WriteAllText(Path.Combine(root, "settings.json"), "private-settings");
            var snapshot = PlanRepositoryContext.Capture(root, "game");
            Assert.Contains("node test.js", snapshot);
            Assert.Contains("<canvas>", snapshot);
            Assert.Contains("style.css", snapshot);
            Assert.DoesNotContain("do-not-read", snapshot);
            Assert.DoesNotContain("private-settings", snapshot);
        }
        finally { Directory.Delete(root, true); }
    }

    private static GeneratedPlan Candidate() => new("Build game", [new("Discover", "Inspect existing framework", ["Identify framework"])]);

    [Fact]
    public async Task ReviewCorrectsProposalOnceWithoutToolsAndPreservesGoal()
    {
        using var client = new Client("""
            {"accept":false,"reason":"Empty folder cannot contain existing framework",
             "correctedSteps":[{"description":"Create runnable project","instruction":"Create a minimal game entry point",
             "acceptanceCriteria":["Entry point exists","Run command is documented"]}]}
            """);
        var result = await PlanQualityReview.ReviewAsync(client, Candidate(), "Build game", "Confirmed empty directory", null, 1000);
        Assert.Equal("Build game", result.Goal);
        Assert.Equal("Create runnable project", result.Steps.Single().description);
        Assert.Equal(1, client.Calls);
        Assert.Null(client.Options!.Tools);
        Assert.Contains("Confirmed empty directory", client.Input);
    }

    [Fact]
    public async Task AcceptedPlanIsPreservedAndRevisionBoundaryIsSupplied()
    {
        using var client = new Client("""{"accept":true,"reason":"Feasible","correctedSteps":null}""");
        var plan = Candidate();
        Assert.Same(plan, await PlanQualityReview.ReviewAsync(client, plan, "Build game", "Existing project", "Only unfinished work", 1000));
        Assert.Contains("Only unfinished work", client.Input);
    }

    [Theory]
    [InlineData("not JSON")]
    [InlineData("{}")]
    [InlineData("{\"accept\":false,\"reason\":\"Need a choice\",\"correctedSteps\":null}")]
    [InlineData("{\"accept\":false,\"reason\":\"Bad plan\",\"correctedSteps\":[{\"description\":\"Setup\",\"instruction\":\"Create files\"}]}")]
    public async Task InvalidOrUnresolvedReviewDoesNotReleasePlanOrLoop(string response)
    {
        using var client = new Client(response);
        await Assert.ThrowsAsync<InvalidOperationException>(() => PlanQualityReview.ReviewAsync(client, Candidate(), "Build game", "", null, 1000));
        Assert.Equal(1, client.Calls);
    }

    private sealed class Client(string response) : IChatClient
    {
        public int Calls { get; private set; }
        public ChatOptions? Options { get; private set; }
        public string Input { get; private set; } = "";
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            Options = options;
            Input = string.Join("\n", messages.Select(m => m.Text));
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, response)));
        }
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
