using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using MandoCode.Models;
using MandoCode.Services;
using Xunit;

namespace MandoCode.Tests;

public class OllamaModelAvailabilityTests
{
    [Theory]
    [InlineData("Response status code does not indicate success: 410 (Gone).", true)]
    [InlineData("upstream returned 410 Gone", true)]
    [InlineData("model-410 not found", false)]
    [InlineData("connection gone", false)]
    [InlineData("Response status code does not indicate success: 404 (Not Found).", false)]
    [InlineData("Response status code does not indicate success: 401 (Unauthorized).", false)]
    public void ProviderMessages_RequireTheGoneStatus(string message, bool expected)
        => Assert.Equal(expected, OllamaModelAvailability.IsUnavailable(new Exception(message)));

    [Fact]
    public void TypedAndWrappedErrors_PreserveStatusDetection()
    {
        var gone = new HttpRequestException("provider failure", null, HttpStatusCode.Gone);
        Assert.True(OllamaModelAvailability.IsUnavailable(gone));
        Assert.True(OllamaModelAvailability.IsUnavailable(new InvalidOperationException("wrapped", gone)));
        Assert.True(OllamaModelAvailability.IsUnavailable(new AggregateException(new Exception("other"), gone)));
        Assert.False(OllamaModelAvailability.IsUnavailable(new HttpRequestException("410 (Gone)", null, HttpStatusCode.ServiceUnavailable)));
    }

    [Fact]
    public async Task Gone_IsNotRetried()
    {
        var attempts = 0;
        await Assert.ThrowsAsync<HttpRequestException>(() => RetryPolicy.ExecuteWithRetryAsync(() =>
        {
            attempts++;
            throw new HttpRequestException("provider failure", null, HttpStatusCode.Gone);
        }, maxRetries: 3));
        Assert.Equal(1, attempts);
    }

    [Theory]
    [InlineData("FormatHttpFailure")]
    [InlineData("FormatErrorMessage")]
    public void SharedAiFormatter_NamesUnavailableModelAndRecovery(string method)
    {
        // Formatting is independent of network/client initialization.
        var ai = (AIService)RuntimeHelpers.GetUninitializedObject(typeof(AIService));
        typeof(AIService).GetField("_config", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(ai, new MandoCodeConfig { ModelName = "retired-model:cloud", ModelPath = null });
        Exception error = new HttpRequestException("Response status code does not indicate success: 410 (Gone).", null, HttpStatusCode.Gone);
        if (method == "FormatErrorMessage") error = new InvalidOperationException("wrapped provider error", error);
        var text = (string)typeof(AIService).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(ai, new object[] { error })!;
        Assert.Contains("retired-model:cloud", text);
        Assert.Contains("no longer available on Ollama", text);
        Assert.Contains("/model", text);
        Assert.DoesNotContain("ollama serve", text);
        Assert.DoesNotContain("ollama pull", text);
        Assert.DoesNotContain("/setup", text);
    }
}