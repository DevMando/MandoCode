using MandoCode.Services;
using Microsoft.Extensions.AI;
using Xunit;
using System.Net;
using System.Text;
using System.Text.Json;
using OllamaSharp;

namespace MandoCode.Tests;

public sealed class ContextSnapshotTests
{
    private static ContextSnapshot Snapshot(string name = "Feature work") => new(Guid.NewGuid(), DateTimeOffset.UtcNow,
        name, "origin", "summarizer", "The user asked to build a feature. The next step is testing.", 12, "project");

    [Fact]
    public void SnapshotsSurviveRestartAndSeparateSessionsDoNotOverwriteEachOther()
    {
        var directory = Path.Combine(Path.GetTempPath(), "snapshots-" + Guid.NewGuid());
        var path = Path.Combine(directory, "snapshots.json");
        try
        {
            var first = new SnapshotStore(path); var second = new SnapshotStore(path);
            var a = Snapshot(); var b = Snapshot("Other agent");
            first.Add(a); second.Add(b);
            var reopened = new SnapshotStore(path);
            Assert.Equal(2, reopened.Items.Count);
            Assert.Contains(a, reopened.Items); Assert.Contains(b, reopened.Items);
            first.Remove(a.Id);
            Assert.Equal(b, Assert.Single(new SnapshotStore(path).Items));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void CorruptStoreIsKeptIntactInsteadOfOverwritten()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "broken json");
            var store = new SnapshotStore(path);
            Assert.NotNull(store.LoadError);
            Assert.Throws<IOException>(() => store.Add(Snapshot()));
            Assert.Equal("broken json", File.ReadAllText(path));
            Assert.Empty(store.Items);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void FailedWriteDoesNotAddAnUnsavedSnapshotToMemory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "snapshots-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            var store = new SnapshotStore(directory);
            var error = Record.Exception(() => store.Add(Snapshot()));
            Assert.True(error is IOException or UnauthorizedAccessException);
            Assert.Empty(store.Items);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void ImportsStackOncePerSnapshotAndRemainQueuedUntilAcknowledged()
    {
        var context = new SnapshotContext(); var first = Snapshot(); var second = Snapshot("Other topic");
        Assert.True(context.Queue(first)); Assert.False(context.Queue(first)); Assert.True(context.Queue(second));
        var request = context.WithImports("Continue please");
        Assert.Contains(first.Name, request); Assert.Contains(second.Name, request);
        Assert.EndsWith("Current request:\nContinue please", request);
        Assert.Equal(2, context.QueuedCount);
        Assert.Equal(request, context.WithImports("Continue please"));
        context.ClearImports();
        Assert.Equal("Continue please", context.WithImports("Continue please"));
    }

    [Fact]
    public void TranscriptPreservesLongTextAndToolResultsWithoutImageBytes()
    {
        var result = new string('x', 20000);
        var messages = new[]
        {
            new ChatMessage(ChatRole.User, "Inspect the attached image"),
            new ChatMessage(ChatRole.Assistant, [new TextContent("Reading a file"), new FunctionCallContent("call", "read_file", new Dictionary<string, object?> { ["path"] = "source.cs" })]),
            new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call", result)]),
            new ChatMessage(ChatRole.User, [new DataContent(new byte[] { 1, 2, 3 }, "image/png")])
        };
        var formatted = SnapshotContext.Format(messages);
        Assert.Contains(result, formatted); Assert.Contains("read_file", formatted); Assert.Contains("source.cs", formatted);
        Assert.Contains("Reading a file", formatted); Assert.Contains("[Image or file attached]", formatted);
        Assert.DoesNotContain("AQID", formatted);
    }

    [Fact]
    public void LargeSingleLinesAreBoundedAndEntireHistoryIsCovered()
    {
        var history = new string('x', 200000) + "\nMost recent instruction";
        var chunks = SnapshotEnhancer.Chunk(history);
        Assert.All(chunks, chunk => Assert.InRange(chunk.Length, 1, 6000));
        Assert.Equal(200000, string.Concat(chunks).Count(c => c == 'x'));
        Assert.Contains("Most recent instruction", chunks[^1]);
    }

    [Fact]
    public void NamesAreCleanAndUniqueWithoutCaseSensitiveDuplicates()
    {
        Assert.Equal("Feature Work", SnapshotNaming.Clean("\"Feature Work.\"\nExtra commentary"));
        Assert.Equal("Feature Work (3)", SnapshotNaming.MakeUnique("Feature Work", ["feature work", "Feature Work (2)"]));
    }

    [Fact]
    public async Task SummarizerUsesIsolatedToolFreeRequestsAndCombinesAllSegments()
    {
        var calls = new List<string>();
        using var http = new HttpClient(new Handler(async (request, ct) =>
        {
            var body = await request.Content!.ReadAsStringAsync(ct); calls.Add(body);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new
            {
                model = "summary-model", created_at = "2026-01-01T00:00:00Z", done = true,
                message = new { role = "assistant", content = "Summary " + calls.Count }
            }), Encoding.UTF8, "application/json") };
        })) { BaseAddress = new Uri("http://localhost:11434") };
        using IChatClient chat = new OllamaApiClient(http, "summary-model");
        Assert.Equal("Summary 3", await SnapshotEnhancer.SummarizeAsync(chat, new string('x', 6500)));
        Assert.Equal(3, calls.Count);
        foreach (var body in calls)
        {
            using var json = JsonDocument.Parse(body);
            Assert.Equal("summary-model", json.RootElement.GetProperty("model").GetString());
            Assert.Equal(2, json.RootElement.GetProperty("messages").GetArrayLength());
            Assert.False(json.RootElement.TryGetProperty("tools", out var tools) && tools.ValueKind == JsonValueKind.Array && tools.GetArrayLength() > 0);
        }
        Assert.Contains("Summary 1", calls[2]); Assert.Contains("Summary 2", calls[2]);
    }

    [Fact]
    public async Task CancellingGenerationStopsModelRequests()
    {
        using var http = new HttpClient(new Handler(async (_, ct) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        })) { BaseAddress = new Uri("http://localhost:11434") };
        using IChatClient chat = new OllamaApiClient(http, "summary-model");
        using var stop = new CancellationTokenSource();
        var generate = SnapshotEnhancer.SummarizeAsync(chat, "Conversation", stop.Token);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => generate);
    }

    [Fact]
    public async Task ThinkingOnlyResponseRetriesWithMoreTokensAndSavesOnlyFinalText()
    {
        var budgets = new List<int>();
        using var http = new HttpClient(new Handler(async (request, ct) =>
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            budgets.Add(json.RootElement.GetProperty("options").GetProperty("num_predict").GetInt32());
            var retried = budgets.Count > 1;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new
            {
                model = "thinking-model", created_at = "2026-01-01T00:00:00Z", done = true,
                done_reason = retried ? "stop" : "length",
                message = new { role = "assistant", content = retried ? "The user is building context snapshots." : "",
                    thinking = "Private reasoning that must never become the saved summary." }
            }), Encoding.UTF8, "application/json") };
        })) { BaseAddress = new Uri("http://localhost:11434") };
        using IChatClient chat = new OllamaApiClient(http, "thinking-model");
        Assert.Equal("The user is building context snapshots.", await SnapshotEnhancer.SummarizeAsync(chat, "Conversation"));
        Assert.Equal(new[] { 4096, 16384 }, budgets);
    }

    [Fact]
    public async Task PersistentlyEmptyResponsesStopAfterOneRetryWithActionableError()
    {
        var calls = 0;
        using var http = new HttpClient(new Handler((_, _) =>
        {
            calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new
            {
                model = "empty-model", created_at = "2026-01-01T00:00:00Z", done = true,
                message = new { role = "assistant", content = "" }
            }), Encoding.UTF8, "application/json") });
        })) { BaseAddress = new Uri("http://localhost:11434") };
        using IChatClient chat = new OllamaApiClient(http, "empty-model");
        var error = await Assert.ThrowsAsync<IOException>(() => SnapshotEnhancer.SummarizeAsync(chat, "Conversation"));
        Assert.Equal(2, calls); Assert.Contains("Try another summarizer model", error.Message);
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
