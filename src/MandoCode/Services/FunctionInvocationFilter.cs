using Microsoft.SemanticKernel;
using MandoCode.Models;
using System.Collections.Concurrent;

namespace MandoCode.Services;

/// <summary>
/// Filter that intercepts function invocations to emit events for the UI.
/// </summary>
public class FunctionInvocationFilter : IFunctionInvocationFilter
{
    private readonly ConcurrentQueue<StreamEvent> _events = new();

    public ConcurrentQueue<StreamEvent> Events => _events;

    public async Task OnFunctionInvocationAsync(FunctionInvocationContext context, Func<FunctionInvocationContext, Task> next)
    {
        // Emit function call event before invocation
        var functionName = $"{context.Function.PluginName}_{context.Function.Name}";
        var description = GetFunctionDescription(context.Function.PluginName, context.Function.Name, context.Arguments);

        _events.Enqueue(new FunctionCall
        {
            FunctionName = functionName,
            Description = description,
            Arguments = context.Arguments.ToDictionary(kvp => kvp.Key, kvp => kvp.Value)
        });

        // Invoke the function
        try
        {
            await next(context);

            // Emit success result
            _events.Enqueue(new Models.FunctionResult
            {
                FunctionName = functionName,
                Result = context.Result?.ToString() ?? string.Empty,
                Success = true
            });
        }
        catch (Exception ex)
        {
            // Emit error result
            _events.Enqueue(new Models.FunctionResult
            {
                FunctionName = functionName,
                Result = $"Error: {ex.Message}",
                Success = false
            });
            throw;
        }
    }

    private string GetFunctionDescription(string? pluginName, string? functionName, IReadOnlyDictionary<string, object?> arguments)
    {
        if (pluginName == "FileSystem")
        {
            switch (functionName)
            {
                case "write_file":
                    if (arguments.TryGetValue("relativePath", out var writePath))
                        return $"Writing to {writePath}";
                    return "Writing file";

                case "read_file":
                    if (arguments.TryGetValue("relativePath", out var readPath))
                        return $"Reading {readPath}";
                    return "Reading file";

                case "list_all_project_files":
                    return "Listing all project files";

                case "search_files":
                    if (arguments.TryGetValue("searchTerm", out var searchTerm))
                        return $"Searching for '{searchTerm}'";
                    return "Searching files";

                default:
                    return $"{functionName}";
            }
        }

        return $"{pluginName ?? "Unknown"}.{functionName ?? "Unknown"}";
    }

    public void ClearEvents()
    {
        _events.Clear();
    }
}
