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
        _systemPrompt = GetSystemPrompt();

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
        builder.Plugins.AddFromObject(new GitPlugin(projectRoot), "Git");

        _kernel = builder.Build();
        _chatService = _kernel.GetRequiredService<IChatCompletionService>();

        // Initialize chat history with system prompt
        _chatHistory = new ChatHistory(_systemPrompt);
    }

    /// <summary>
    /// Sends a message to the AI and gets a response.
    /// </summary>
    public async Task<string> ChatAsync(string userMessage, bool autoInvoke = true)
    {
        _chatHistory.AddUserMessage(userMessage);

        try
        {
            OllamaPromptExecutionSettings settings = new()
            {
                Temperature = (float)_config.Temperature,
                FunctionChoiceBehavior = autoInvoke
                    ? FunctionChoiceBehavior.Auto(autoInvoke: true, options: new() { AllowConcurrentInvocation = true })
                    : FunctionChoiceBehavior.None()
            };

            // Get the response with automatic function calling
            var response = await _chatService.GetChatMessageContentAsync(
                _chatHistory,
                settings,
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
                       $"MandoCode requires a model with function calling support to use FileSystem and Git plugins.\n\n" +
                       $"Recommended models with tool support:\n" +
                       $"  • ollama pull qwen2.5-coder:7b\n" +
                       $"  • ollama pull mistral\n" +
                       $"  • ollama pull llama3.1\n\n" +
                       $"Then update your configuration:\n" +
                       $"  Type 'config' and select 'Run configuration wizard'\n" +
                       $"  Or run: dotnet run -- config set model qwen2.5-coder:7b";
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

    private string GetSystemPrompt()
    {
        return @"You are MandoCode, a local AI coding assistant powered by Ollama.

Your capabilities:
- You have access to filesystem operations via the FileSystem plugin
- You have access to Git operations via the Git plugin
- You can read, write, and search files in the current project
- You can analyze code across multiple languages (C#, JavaScript, TypeScript, Python, etc.)

CRITICAL: Always respond in natural language to the user. Never output raw JSON or function call syntax.
When you need to use a tool:
1. Call the appropriate function
2. Wait for the result
3. Use that result to formulate a helpful, conversational response to the user

Important guidelines:
1. ALWAYS respond in complete sentences, never raw JSON
2. When showing file paths to the user, ALWAYS include the ABSOLUTE PATH from the WriteFile result
3. When proposing changes:
   - Explain what you're changing and why
   - Show a clear diff or summary of changes
   - Keep edits minimal unless requested otherwise
4. Work across multi-language codebases intelligently
5. Use Git functions to check status and view diffs before committing
6. Be thorough but concise in your responses
7. If you're unsure about a file's location, list the project files first

Examples of good responses:
- ""I've created name.txt at: C:\Users\DevMando\Desktop\MandoCode\name.txt""
- ""Here are all the files in your project: [list]""
- ""The file is located at absolute path: C:\path\to\file.txt""

CRITICAL: When you create or modify a file, the WriteFile function returns both relative and absolute paths.
ALWAYS extract and show the user the absolute path from the function result.

You are running completely offline with no token costs. Your goal is to help developers write better code efficiently.

Remember: You are a LOCAL assistant. All operations happen on the user's machine. Be safe and respectful of their codebase.";
    }
}
