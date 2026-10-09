using System.Net;
using System.Text;
using MandoCode.Models;
using MandoCode.Services;
using Xunit;

namespace MandoCode.Tests;

[Trait("Category", "Integration")]
public class ConfigurationWizardPromptTests
{
    [Theory]
    [InlineData("qwen3:4b")]
    [InlineData("glm-5.2:cloud")]
    public async Task WholeWizard_UsesHostInput_AndReturnsWithoutReadingConsole(string model)
    {
        using var server = new OllamaServer();
        var config = new MandoCodeConfig
        {
            OllamaEndpoint = server.Url, ModelName = model, Temperature = 0.5,
            MaxTokens = 8192, ContextLength = 8192, ContextLengthSetByUser = true,
            RequestTimeoutMinutes = 10, TavilyApiKey = "tvly-test",
            IgnoreDirectories = new List<string> { "custom-folder" }
        };
        var titles = new List<string>();
        var prompts = new ConfigurationPrompts
        {
            Select = (title, options) =>
            {
                titles.Add(title);
                var choice = title switch
                {
                    "How would you like to configure your model?" => "Select from available Ollama models",
                    "Modify ignore directories?" => "Yes",
                    "What would you like to do?" => "Reset to defaults",
                    "Save this configuration?" => "No",
                    _ when title.StartsWith("Tavily key configured") => "Remove key",
                    _ => options[0]
                };
                Assert.Contains(choice, options);
                return Task.FromResult(choice);
            },
            PickModel = models =>
            {
                Assert.Contains(model, models);
                return Task.FromResult<string?>(model);
            },
            Text = (title, initial, validate, secret) =>
            {
                Assert.False(secret);
                if (title.StartsWith("Temperature"))
                {
                    Assert.NotNull(validate!("NaN"));
                    Assert.NotNull(validate("2"));
                }
                if (title.StartsWith("Timeout"))
                    Assert.NotNull(validate!("0"));
                Assert.Null(validate!(initial!));
                return Task.FromResult(initial!);
            }
        };
        var result = await ConfigurationWizard.RunAsync(config, prompts: prompts);
        Assert.Same(config, result);
        Assert.Equal(8192, result.MaxTokens);
        Assert.Equal(10, result.RequestTimeoutMinutes);
        Assert.Equal(0.5, result.Temperature);
        Assert.Null(result.TavilyApiKey);
        Assert.DoesNotContain("custom-folder", result.IgnoreDirectories);
        Assert.Equal("Configuration complete", titles[^1]);
        Assert.Equal(!MandoCodeConfig.IsCloudModel(model), titles.Contains("Context window:"));
        Assert.Equal(8192, result.ContextLength);
        Assert.True(result.ContextLengthSetByUser);
    }

    [Fact]
    public async Task SecretText_IsMaskedByHost_AndMayBeSkipped()
    {
        var prompts = new ConfigurationPrompts
        {
            Text = (_, initial, validate, secret) =>
            {
                Assert.True(secret);
                Assert.Null(initial);
                Assert.Null(validate!(""));
                return Task.FromResult("");
            }
        };
        Assert.Equal("", await prompts.TextAsync("API key", secret: true, allowEmpty: true));
    }

    [Fact]
    public async Task ContextSelection_MapsDisplayLabelBackToNumericValue()
    {
        var config = new MandoCodeConfig { ModelName = "qwen3:4b", ContextLength = 16384, ContextLengthSetByUser = true };
        var prompts = new ConfigurationPrompts
        {
            Select = (_, options) => Task.FromResult(options.Single(label => label.StartsWith("32k")))
        };
        await ConfigurationWizard.ConfigureContextWindowAsync(config, prompts);
        Assert.Equal(32768, config.ContextLength);
        Assert.True(config.ContextLengthSetByUser);
    }

    private sealed class OllamaServer : IDisposable
    {
        private readonly HttpListener _listener;
        public string Url { get; }
        public OllamaServer()
        {
            for (var attempt = 0; ; attempt++)
            {
                Url = $"http://127.0.0.1:{Random.Shared.Next(20000, 60000)}";
                _listener = new HttpListener();
                _listener.Prefixes.Add(Url + "/");
                try { _listener.Start(); break; }
                catch (HttpListenerException) when (attempt < 9) { _listener.Close(); }
            }
            _ = Serve();
        }
        private async Task Serve()
        {
            while (_listener.IsListening)
            {
                try
                {
                    var request = await _listener.GetContextAsync();
                    var bytes = Encoding.UTF8.GetBytes("{\"models\":[{\"name\":\"qwen3:4b\"},{\"name\":\"glm-5.2:cloud\"}]}");
                    request.Response.ContentLength64 = bytes.Length;
                    await request.Response.OutputStream.WriteAsync(bytes);
                    request.Response.Close();
                }
                catch (Exception) when (!_listener.IsListening) { return; }
            }
        }
        public void Dispose() => _listener.Close();
    }
}
