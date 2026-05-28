using Xunit;
using MandoCode.Services;

namespace MandoCode.Tests;

/// <summary>
/// Focused on the context-overflow detection and non-retry behavior.
/// Prior behavior retried overflow errors up to MaxRetryAttempts times before the
/// recovery catch got to fire, wasting 3 round-trips per real provider rejection.
/// </summary>
public class RetryPolicyTests
{
    [Theory]
    [InlineData("context window exceeds limit", true)]
    [InlineData("context length exceeded", true)]
    [InlineData("context_length_exceeded", true)]
    [InlineData("prompt is too long", true)]
    [InlineData("maximum context reached", true)]
    // Transport-level rejections — Ollama's Go HTTP server, nginx, common proxies.
    // Recover the same way (compact + retry), so they're classified as overflow.
    [InlineData("http: request body too large", true)]
    [InlineData("Request Entity Too Large", true)]
    [InlineData("payload too large", true)]
    [InlineData("server returned 413", true)]
    // Patterns that SHOULD NOT match — previous version caught these by mistake.
    [InlineData("rate limit exceeded", false)]
    [InlineData("token limit reached", false)]
    [InlineData("exceeds limit", false)]
    [InlineData("connection reset", false)]
    [InlineData("", false)]
    public void IsContextOverflowError_MatchesOnlyContextPatterns(string message, bool expected)
    {
        var ex = new Exception(message);
        Assert.Equal(expected, RetryPolicy.IsContextOverflowError(ex));
    }

    [Fact]
    public void IsContextOverflowError_HttpRequestException_413_Matches()
    {
        // Even with an opaque message, the 413 status code alone identifies a payload-too-big
        // rejection — recovers via the same compact-and-retry path as a model context overflow.
        var ex = new HttpRequestException("upstream rejected", null, System.Net.HttpStatusCode.RequestEntityTooLarge);
        Assert.True(RetryPolicy.IsContextOverflowError(ex));
    }

    [Fact]
    public void IsContextOverflowError_HttpRequestException_503_DoesNotMatch()
    {
        // Regression guard: generic transport failures stay transient (retried by IsTransientError),
        // they must not be misclassified as overflow.
        var ex = new HttpRequestException("upstream unavailable", null, System.Net.HttpStatusCode.ServiceUnavailable);
        Assert.False(RetryPolicy.IsContextOverflowError(ex));
    }

    [Fact]
    public void IsContextOverflowError_ChecksInnerExceptions()
    {
        var inner = new Exception("context window exceeds limit");
        var outer = new Exception("wrapped", inner);
        Assert.True(RetryPolicy.IsContextOverflowError(outer));
    }

    [Fact]
    public void IsContextOverflowError_NullReturnsFalse()
    {
        Assert.False(RetryPolicy.IsContextOverflowError(null));
    }

    [Fact]
    public async Task ExecuteWithRetry_OverflowException_BailsImmediately()
    {
        // Overflow errors are not transient — the retry policy should NOT retry them.
        // If it did, we'd see attempts = MaxRetryAttempts + 1 before the exception escapes.
        int attempts = 0;
        var ex = await Assert.ThrowsAsync<Exception>(async () =>
        {
            await RetryPolicy.ExecuteWithRetryAsync(async () =>
            {
                attempts++;
                await Task.Yield();
                throw new Exception("context window exceeds limit");
            }, maxRetries: 3, operationName: "test");
        });

        Assert.Equal(1, attempts);
        Assert.Contains("context window", ex.Message);
    }

    [Fact]
    public async Task ExecuteWithRetry_HttpError_StillRetries()
    {
        // Sanity: genuinely transient errors still retry (regression check).
        int attempts = 0;
        await Assert.ThrowsAsync<HttpRequestException>(async () =>
        {
            await RetryPolicy.ExecuteWithRetryAsync(async () =>
            {
                attempts++;
                await Task.Yield();
                throw new HttpRequestException("503");
            }, maxRetries: 2, operationName: "test");
        });

        Assert.Equal(3, attempts); // 1 initial + 2 retries
    }
}
