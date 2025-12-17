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
    private readonly Kernel _kernel;
    private readonly IChatCompletionService _chatService;
    private readonly ChatHistory _chatHistory;
    private readonly string _systemPrompt;
    private readonly MandoCodeConfig _config;

    public AIService(string projectRoot, MandoCodeConfig config)
    {
        _config = config;
        _systemPrompt = SystemPrompts.MandoCodeAssistant;

        // Build the kernel with Ollama as the AI service
        var builder = Kernel.CreateBuilder();

        builder.AddOllamaChatCompletion(
            modelId: config.GetEffectiveModelName(),
            endpoint: new Uri(config.OllamaEndpoint)
        );

        // Add plugins with custom ignore directories if configured
        var fileSystemPlugin = new FileSystemPlugin(projectRoot);
        if (config.IgnoreDirectories.Any())
        {
            fileSystemPlugin.AddIgnoreDirectories(config.IgnoreDirectories);
        }

        builder.Plugins.AddFromObject(fileSystemPlugin, "FileSystem");

        _kernel = builder.Build();
        _chatService = _kernel.GetRequiredService<IChatCompletionService>();

        // Initialize chat history with system prompt
        _chatHistory = new ChatHistory(_systemPrompt);
    }

    /// <summary>
    /// Sends a message to the AI and gets a response.
    /// </summary>
    public async Task<string> ChatAsync(string userMessage)
    {
        _chatHistory.AddUserMessage(userMessage);

        try
        {
            // Create settings for this request
            OllamaPromptExecutionSettings settings = new()
            {
                Temperature = (float)_config.Temperature,
                FunctionChoiceBehavior = FunctionChoiceBehavior.Auto(autoInvoke: true, options: new() { AllowConcurrentInvocation = true })
            };

            // Manual function calling loop to handle Ollama connector issues
            const int maxIterations = 10;
            int iteration = 0;

            while (iteration < maxIterations)
            {
                iteration++;

                // Get the response with function calling enabled
                var response = await _chatService.GetChatMessageContentAsync(
                    _chatHistory,
                    settings,
                    _kernel
                );

                // Add the assistant's response to history
                _chatHistory.Add(response);

                // Check if there are any function calls to process
                var functionCalls = response.Items.OfType<FunctionCallContent>().ToList();

                if (!functionCalls.Any())
                {
                    // No function calls, return the final response
                    return response.Content ?? "No response from AI.";
                }

                // Process each function call
                foreach (var functionCall in functionCalls)
                {
                    try
                    {
                        // Log function execution for debugging
                        Console.WriteLine($"[Function Call] {functionCall.PluginName}.{functionCall.FunctionName}");

                        // Get the function from the kernel
                        var function = _kernel.Plugins.GetFunction(functionCall.PluginName, functionCall.FunctionName);

                        // Invoke the function
                        var result = await function.InvokeAsync(_kernel, functionCall.Arguments);

                        // Log success
                        Console.WriteLine($"[Function Success] {functionCall.FunctionName} executed");

                        // Add the function result to chat history
                        _chatHistory.Add(new ChatMessageContent(
                            AuthorRole.Tool,
                            [
                                new FunctionResultContent(
                                    functionCall,
                                    result.ToString()
                                )
                            ]
                        ));
                    }
                    catch (Exception ex)
                    {
                        // Log error
                        Console.WriteLine($"[Function Error] {functionCall.FunctionName}: {ex.Message}");

                        // Add error result to history
                        _chatHistory.Add(new ChatMessageContent(
                            AuthorRole.Tool,
                            [
                                new FunctionResultContent(
                                    functionCall,
                                    $"Error executing function: {ex.Message}"
                                )
                            ]
                        ));
                    }
                }

                // Continue the loop to get the next response from the model
            }

            // If we've hit max iterations, return the last response
            return "Maximum function call iterations reached. Please try rephrasing your request.";
        }
        catch (Exception ex)
        {
            // Check if the error is about tool support
            if (ex.Message.Contains("does not support tools") || ex.Message.Contains("does not support functions"))
            {
                return $"Error: The model '{_config.GetEffectiveModelName()}' does not support function calling (tools).\n\n" +
                       $"MandoCode requires a model with function calling support to use FileSystem plugins.\n\n" +
                       $"Recommended models with tool support:\n" +
                       $"  • ollama pull qwen2.5:14b\n" +
                       $"  • ollama pull qwen2.5:7b\n" +
                       $"  • ollama pull mistral\n" +
                       $"  • ollama pull llama3.1\n\n" +
                       $"Then update your configuration:\n" +
                       $"  Type 'config' and select 'Run configuration wizard'\n" +
                       $"  Or run: dotnet run -- config set model qwen2.5:14b";
            }

            return $"Error communicating with AI: {ex.Message}\n\nMake sure Ollama is running and the model '{_config.GetEffectiveModelName()}' is installed.\nRun: ollama pull {_config.GetEffectiveModelName()}";
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
