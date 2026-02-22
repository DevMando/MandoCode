/**
 *  Author: DevMando
 *  Date: 2025-12-10
 *  Description: AIService.cs - Manages AI interactions using Semantic Kernel with Ollama.
 *  File: AIService.cs
 */

using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.Ollama;
using MandoCode.Models;
using MandoCode.Plugins;

namespace MandoCode.Services;


/// <summary>
/// Manages AI interactions using Semantic Kernel with Ollama.
/// </summary>
public class AIService
{
    private Kernel _kernel;
    private IChatCompletionService _chatService;
    private readonly ChatHistory _chatHistory;
    private readonly string _systemPrompt;
    private MandoCodeConfig _config;
    private OllamaPromptExecutionSettings _settings;
    private readonly string _projectRoot;

    public AIService(string projectRoot, MandoCodeConfig config)
    {
        _projectRoot = projectRoot;
        _config = config;
        _systemPrompt = SystemPrompts.MandoCodeAssistant;

        BuildKernel();

        // Initialize chat history with system prompt
        _chatHistory = new ChatHistory(_systemPrompt);
    }

    /// <summary>
    /// Reinitializes the AI service with a new configuration.
    /// Rebuilds the kernel with the updated model and settings.
    /// </summary>
    public void Reinitialize(MandoCodeConfig config)
    {
        _config = config;
        BuildKernel();
        ClearHistory();
    }

    private void BuildKernel()
    {
        _settings = new()
        {
            Temperature = (float)_config.Temperature,
            FunctionChoiceBehavior = FunctionChoiceBehavior.Auto(autoInvoke: true, options: new() { AllowConcurrentInvocation = true })
        };

        var builder = Kernel.CreateBuilder();

        builder.AddOllamaChatCompletion(
            modelId: _config.GetEffectiveModelName(),
            endpoint: new Uri(_config.OllamaEndpoint)
        );

        var fileSystemPlugin = new FileSystemPlugin(_projectRoot);
        if (_config.IgnoreDirectories.Any())
        {
            fileSystemPlugin.AddIgnoreDirectories(_config.IgnoreDirectories);
        }

        builder.Plugins.AddFromObject(fileSystemPlugin, "FileSystem");

        _kernel = builder.Build();
        _chatService = _kernel.GetRequiredService<IChatCompletionService>();
    }

    /// <summary>
    /// Sends a message to the AI and gets a response.
    /// </summary>
    public async Task<string> ChatAsync(string userMessage)
    {
        _chatHistory.AddUserMessage(userMessage);

        try
        {
            // Get the response with automatic function calling
            var response = await _chatService.GetChatMessageContentAsync(
                _chatHistory,
                _settings,
                _kernel
            );

            // Add the final response to history
            if (!string.IsNullOrEmpty(response.Content))
            {
                _chatHistory.AddAssistantMessage(response.Content);
            }

            return response.Content ?? "No response from AI.";
        }
        catch (Exception ex)
        {
            // Check if the error is about tool support
            if (ex.Message.Contains("does not support tools") || ex.Message.Contains("does not support functions"))
            {
                return $"Error: The model '{_config.GetEffectiveModelName()}' does not support function calling (tools).\n\n" +
                       $"MandoCode requires a model with function calling support to use FileSystem plugins.\n\n" +
                       $"Recommended models with tool support:\n" +
                       $"  • ollama pull minimax-m2.5:cloud\n" +
                       $"  • ollama pull qwen2.5-coder:14b\n" +
                       $"  • ollama pull mistral\n" +
                       $"  • ollama pull llama3.1\n\n" +
                       $"Then update your configuration:\n" +
                       $"  Type 'config' and select 'Run configuration wizard'\n" +
                       $"  Or run: dotnet run -- config set model minimax-m2.5:cloud";
            }

            return $"Error communicating with AI: {ex.Message}\n\nMake sure Ollama is running and the model '{_config.GetEffectiveModelName()}' is installed.\nRun: ollama pull {_config.GetEffectiveModelName()}";
        }
    }

    /// <summary>
    /// Sends a message to the AI and streams the response chunk by chunk.
    /// </summary>
    public async IAsyncEnumerable<string> ChatStreamAsync(string userMessage)
    {
        _chatHistory.AddUserMessage(userMessage);
        var fullResponse = string.Empty;
        var hasError = false;
        var errorMessage = string.Empty;

        IAsyncEnumerable<StreamingChatMessageContent>? streamingResponse = null;

        // Attempt to get the streaming response
        try
        {
            streamingResponse = _chatService.GetStreamingChatMessageContentsAsync(
                _chatHistory,
                _settings,
                _kernel
            );
        }
        catch (Exception ex)
        {
            hasError = true;
            // Check if the error is about tool support
            if (ex.Message.Contains("does not support tools") || ex.Message.Contains("does not support functions"))
            {
                errorMessage = $"Error: The model '{_config.GetEffectiveModelName()}' does not support function calling (tools).\n\n" +
                       $"MandoCode requires a model with function calling support to use FileSystem plugins.\n\n" +
                       $"Recommended models with tool support:\n" +
                       $"  • ollama pull minimax-m2.5:cloud\n" +
                       $"  • ollama pull qwen2.5-coder:14b\n" +
                       $"  • ollama pull mistral\n" +
                       $"  • ollama pull llama3.1\n\n" +
                       $"Then update your configuration:\n" +
                       $"  Type 'config' and select 'Run configuration wizard'\n" +
                       $"  Or run: dotnet run -- config set model minimax-m2.5:cloud";
            }
            else
            {
                errorMessage = $"Error communicating with AI: {ex.Message}\n\nMake sure Ollama is running and the model '{_config.GetEffectiveModelName()}' is installed.\nRun: ollama pull {_config.GetEffectiveModelName()}";
            }
        }

        if (hasError)
        {
            yield return errorMessage;
            yield break;
        }

        // Stream the response chunks
        if (streamingResponse != null)
        {
            await foreach (var chunk in streamingResponse)
            {
                if (!string.IsNullOrEmpty(chunk.Content))
                {
                    fullResponse += chunk.Content;
                    yield return chunk.Content;
                }
            }

            // Add the complete response to history
            if (!string.IsNullOrEmpty(fullResponse))
            {
                _chatHistory.AddAssistantMessage(fullResponse);
            }
        }
    }

    /// <summary>
    /// Clears the chat history and starts a new conversation.
    /// </summary>
    public void ClearHistory()
    {
        _chatHistory.Clear();
        _chatHistory.AddSystemMessage(_systemPrompt);
    }

    /// <summary>
    /// Gets the current chat history.
    /// </summary>
    public IReadOnlyList<ChatMessageContent> GetHistory()
    {
        return _chatHistory.ToList().AsReadOnly();
    }
}
