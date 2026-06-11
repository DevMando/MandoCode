/**
 *  Author: DevMando
 *  Date: 2026-06-10
 *  Description: FallbackFunctionCallExecutor.cs - Detects and executes function calls that
 *               local models emit as JSON text instead of proper tool calls. Extracted from
 *               AIService so chat orchestration and text-parsing fallback evolve (and test)
 *               independently.
 *  File: FallbackFunctionCallExecutor.cs
 */

using Microsoft.SemanticKernel;
using MandoCode.Models;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MandoCode.Services;

/// <summary>
/// Processes model text responses to detect and execute function calls that were output as
/// plain text. Some local models (especially smaller qwen and mistral variants) emit function
/// calls as JSON in the response body instead of using the tool-call protocol. Only active
/// when <see cref="MandoCodeConfig.EnableFallbackFunctionParsing"/> is on.
/// </summary>
public class FallbackFunctionCallExecutor
{
    private readonly Action<FunctionCall>? _onFunctionInvoked;
    private readonly Action<FunctionExecutionResult>? _onFunctionCompleted;

    /// <param name="onFunctionInvoked">Raised before a fallback-parsed function runs, so the UI shows it like a real tool call.</param>
    /// <param name="onFunctionCompleted">Raised after a fallback-parsed function finishes (success or failure).</param>
    public FallbackFunctionCallExecutor(
        Action<FunctionCall>? onFunctionInvoked = null,
        Action<FunctionExecutionResult>? onFunctionCompleted = null)
    {
        _onFunctionInvoked = onFunctionInvoked;
        _onFunctionCompleted = onFunctionCompleted;
    }

    /// <summary>
    /// Scans <paramref name="response"/> for text-based function calls, executes any found
    /// against <paramref name="kernel"/>, and returns the response with the call JSON replaced
    /// by a "Function Results" section. Returns the response unchanged when nothing matches.
    /// </summary>
    public async Task<string> ProcessAsync(string response, Kernel kernel, string modelName)
    {
        var functionCalls = ExtractFunctionCallsFromText(response);

        if (functionCalls.Count == 0)
        {
            return response;
        }

        System.Diagnostics.Debug.WriteLine(
            $"[FallbackParsing] Detected {functionCalls.Count} text-based function call(s) in response. Model: {modelName}");

        var resultBuilder = new System.Text.StringBuilder();
        var functionsExecuted = new List<string>();

        foreach (var (functionName, parametersJson) in functionCalls)
        {
            try
            {
                // Normalize function name (remove FileSystem_ prefix if present)
                var normalizedName = functionName.Replace("FileSystem_", "").Replace("FileSystem.", "");

                // Map common function name variations
                normalizedName = NormalizeFunctionName(normalizedName);

                System.Diagnostics.Debug.WriteLine(
                    $"[FallbackParsing] Invoking function: {normalizedName} (original: {functionName})");

                var functionResult = await InvokeFunctionByNameAsync(kernel, normalizedName, parametersJson);

                if (functionResult != null)
                {
                    functionsExecuted.Add($"{normalizedName}: {functionResult}");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[FallbackParsing] Error executing function {functionName}: {ex.Message}");
                functionsExecuted.Add($"Error executing function: {ex.Message}");
            }
        }

        if (functionsExecuted.Any())
        {
            // Clean up the response by removing JSON function call text
            var cleanedResponse = RemoveFunctionCallJson(response);

            // Remove common preamble text
            cleanedResponse = Regex.Replace(cleanedResponse, @"Here is the JSON for the function call:?\s*", "", RegexOptions.IgnoreCase);
            cleanedResponse = Regex.Replace(cleanedResponse, @"I will (call|use) the [`']?[\w_]+[`']? function[^.]*\.\s*", "", RegexOptions.IgnoreCase);
            cleanedResponse = Regex.Replace(cleanedResponse, @"To \w+ the \w+[^,]*,\s*I will use the [`']?[\w_]+[`']? function[^.]*\.\s*", "", RegexOptions.IgnoreCase);
            cleanedResponse = cleanedResponse.Trim();

            if (!string.IsNullOrWhiteSpace(cleanedResponse))
            {
                resultBuilder.AppendLine(cleanedResponse);
                resultBuilder.AppendLine();
            }

            resultBuilder.AppendLine("--- Function Results ---");
            foreach (var result in functionsExecuted)
            {
                resultBuilder.AppendLine(result);
            }

            return resultBuilder.ToString();
        }

        return response;
    }

    /// <summary>
    /// Extracts function calls from text by finding JSON objects with "name" and "parameters" fields.
    /// Handles nested braces properly and supports multiple JSON formats:
    /// - {"name": "func", "parameters": {...}}
    /// - {"function_call": {"name": "func", "arguments": {...}}}
    /// - {"tool_calls": [{"function": {"name": "func", "arguments": {...}}}]}
    /// Public static for direct unit testing without standing up a kernel.
    /// </summary>
    public static List<(string FunctionName, string ParametersJson)> ExtractFunctionCallsFromText(string text)
    {
        var results = new List<(string, string)>();

        // Pattern 1: Standard {"name": "func", "parameters": {...}}
        var namePattern = @"\{\s*""name""\s*:\s*""([^""]+)""\s*,\s*""(?:parameters|arguments)""\s*:\s*";
        var matches = Regex.Matches(text, namePattern, RegexOptions.IgnoreCase);

        foreach (Match match in matches)
        {
            var functionName = match.Groups[1].Value;
            var startIndex = match.Index + match.Length;

            // Extract the parameters JSON by counting braces
            var parametersJson = ExtractJsonObject(text, startIndex);

            if (!string.IsNullOrEmpty(parametersJson))
            {
                results.Add((functionName, parametersJson));
            }
        }

        // Pattern 2: {"function_call": {"name": "func", "arguments": {...}}}
        if (results.Count == 0)
        {
            var fcPattern = @"""function_call""\s*:\s*\{\s*""name""\s*:\s*""([^""]+)""\s*,\s*""arguments""\s*:\s*";
            matches = Regex.Matches(text, fcPattern, RegexOptions.IgnoreCase);

            foreach (Match match in matches)
            {
                var functionName = match.Groups[1].Value;
                var startIndex = match.Index + match.Length;
                var parametersJson = ExtractJsonObject(text, startIndex);

                if (!string.IsNullOrEmpty(parametersJson))
                {
                    results.Add((functionName, parametersJson));
                }
            }
        }

        // Pattern 3: {"tool_calls": [{"function": {"name": "func", "arguments": {...}}}]}
        if (results.Count == 0)
        {
            var tcPattern = @"""function""\s*:\s*\{\s*""name""\s*:\s*""([^""]+)""\s*,\s*""arguments""\s*:\s*";
            matches = Regex.Matches(text, tcPattern, RegexOptions.IgnoreCase);

            foreach (Match match in matches)
            {
                var functionName = match.Groups[1].Value;
                var startIndex = match.Index + match.Length;

                // Arguments might be a string (escaped JSON) or an object
                var argsStart = text.IndexOf('"', startIndex);
                if (argsStart == startIndex || argsStart == startIndex + 1)
                {
                    // Arguments is a string, need to extract and unescape
                    var argsEnd = FindStringEnd(text, argsStart);
                    if (argsEnd > argsStart)
                    {
                        var argsStr = text.Substring(argsStart + 1, argsEnd - argsStart - 1);
                        // Unescape the JSON string
                        argsStr = argsStr.Replace("\\\"", "\"").Replace("\\\\", "\\");
                        results.Add((functionName, argsStr));
                    }
                }
                else
                {
                    var parametersJson = ExtractJsonObject(text, startIndex);
                    if (!string.IsNullOrEmpty(parametersJson))
                    {
                        results.Add((functionName, parametersJson));
                    }
                }
            }
        }

        return results;
    }

    /// <summary>
    /// Finds the end of a JSON string starting at the given position.
    /// </summary>
    private static int FindStringEnd(string text, int startIndex)
    {
        if (startIndex >= text.Length || text[startIndex] != '"')
            return -1;

        for (int i = startIndex + 1; i < text.Length; i++)
        {
            if (text[i] == '\\' && i + 1 < text.Length)
            {
                i++; // Skip escaped character
                continue;
            }
            if (text[i] == '"')
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>
    /// Finds the bounds (start, end exclusive) of a JSON object starting at the given index.
    /// Handles nested braces and string escaping. Returns null if no valid object found.
    /// </summary>
    private static (int Start, int End)? FindJsonObjectBounds(string text, int startIndex)
    {
        if (startIndex >= text.Length || text[startIndex] != '{')
            return null;

        var depth = 0;
        var inString = false;
        var escapeNext = false;

        for (int i = startIndex; i < text.Length; i++)
        {
            var c = text[i];

            if (escapeNext) { escapeNext = false; continue; }
            if (c == '\\' && inString) { escapeNext = true; continue; }
            if (c == '"') { inString = !inString; continue; }

            if (!inString)
            {
                if (c == '{') depth++;
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0)
                        return (startIndex, i + 1);
                }
            }
        }

        return null;
    }

    private static string ExtractJsonObject(string text, int startIndex)
    {
        var bounds = FindJsonObjectBounds(text, startIndex);
        return bounds.HasValue ? text[bounds.Value.Start..bounds.Value.End] : string.Empty;
    }

    /// <summary>
    /// Removes function call JSON from the response text.
    /// Public static for direct unit testing.
    /// </summary>
    public static string RemoveFunctionCallJson(string text)
    {
        var result = text;
        var namePattern = @"\{\s*""name""\s*:\s*""[^""]+""";
        var matches = Regex.Matches(text, namePattern);

        var jsonRanges = new List<(int Start, int End)>();

        foreach (Match match in matches)
        {
            var bounds = FindJsonObjectBounds(text, match.Index);
            if (bounds.HasValue)
            {
                jsonRanges.Add(bounds.Value);
            }
        }

        // Remove ranges in reverse order to maintain indices
        foreach (var (start, end) in jsonRanges.OrderByDescending(r => r.Start))
        {
            result = result.Remove(start, end - start);
        }

        return result;
    }

    // Alias dictionary for common alternative function names
    private static readonly Dictionary<string, string> FunctionAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        // Common short aliases
        { "mkdir", "create_folder" },
        { "make_directory", "create_folder" },
        { "create_directory", "create_folder" },
        { "read_file", "read_file_contents" },
        { "remove_file", "delete_file" },
        { "rm", "delete_file" },
        { "unlink", "delete_file" },
        { "list_files", "list_files_match_glob_pattern" },
        { "list_files_glob", "list_files_match_glob_pattern" },
        { "list_project_files", "list_all_project_files" },
        { "search_in_files", "search_text_in_files" },
        { "find_in_files", "search_text_in_files" },
    };

    /// <summary>
    /// Normalizes function names to match the actual plugin function names.
    /// Uses convention-based conversion (PascalCase/camelCase to snake_case) and alias lookup.
    /// Public static for direct unit testing.
    /// </summary>
    public static string NormalizeFunctionName(string functionName)
    {
        // First check aliases for common alternative names
        if (FunctionAliases.TryGetValue(functionName, out var aliasedName))
        {
            return aliasedName;
        }

        // Convert to snake_case if it appears to be PascalCase or camelCase
        var snakeCased = ToSnakeCase(functionName);

        // Check aliases again with the converted name
        if (FunctionAliases.TryGetValue(snakeCased, out aliasedName))
        {
            return aliasedName;
        }

        return snakeCased;
    }

    /// <summary>
    /// Converts PascalCase or camelCase to snake_case.
    /// </summary>
    public static string ToSnakeCase(string input)
    {
        if (string.IsNullOrEmpty(input))
            return input;

        // If already snake_case (contains underscore), just lowercase it
        if (input.Contains('_'))
            return input.ToLowerInvariant();

        // Convert PascalCase/camelCase to snake_case
        var result = new System.Text.StringBuilder();
        for (int i = 0; i < input.Length; i++)
        {
            var c = input[i];
            if (char.IsUpper(c))
            {
                // Add underscore before uppercase letters (except at start)
                if (i > 0)
                    result.Append('_');
                result.Append(char.ToLowerInvariant(c));
            }
            else
            {
                result.Append(c);
            }
        }

        return result.ToString();
    }

    /// <summary>
    /// Invokes a FileSystem plugin function by name with JSON parameters.
    /// </summary>
    private async Task<string?> InvokeFunctionByNameAsync(Kernel kernel, string functionName, string parametersJson)
    {
        try
        {
            // Get the FileSystem plugin
            if (!kernel.Plugins.TryGetPlugin("FileSystem", out var plugin))
            {
                return null;
            }

            // Find the function
            if (!plugin.TryGetFunction(functionName, out var function))
            {
                // Try case-insensitive search
                function = plugin.FirstOrDefault(f =>
                    f.Name.Equals(functionName, StringComparison.OrdinalIgnoreCase));

                if (function == null)
                {
                    return $"Function '{functionName}' not found in FileSystem plugin";
                }
            }

            // Parse parameters
            var parameters = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(parametersJson);
            if (parameters == null)
            {
                return "Failed to parse function parameters";
            }

            // Build kernel arguments
            var arguments = new KernelArguments();
            foreach (var param in parameters)
            {
                // Convert parameter name to match function parameter names
                var paramName = NormalizeParameterName(param.Key);
                var paramValue = param.Value.ValueKind == JsonValueKind.String
                    ? param.Value.GetString()
                    : param.Value.ToString();

                arguments[paramName] = paramValue;
            }

            // Raise function invoked event
            _onFunctionInvoked?.Invoke(new FunctionCall
            {
                FunctionName = functionName,
                Description = $"Executing {functionName} (fallback)",
                Arguments = parameters.ToDictionary(p => p.Key, p => (object?)p.Value.ToString())
            });

            // Invoke the function
            var result = await function.InvokeAsync(kernel, arguments);
            var resultString = result.GetValue<string>() ?? result.ToString() ?? "Function completed";

            // Raise function completed event
            _onFunctionCompleted?.Invoke(new FunctionExecutionResult
            {
                FunctionName = functionName,
                Result = resultString.Length > 200 ? resultString[..200] + "..." : resultString,
                Success = true
            });

            return resultString;
        }
        catch (Exception ex)
        {
            _onFunctionCompleted?.Invoke(new FunctionExecutionResult
            {
                FunctionName = functionName,
                Result = ex.Message,
                Success = false
            });

            return $"Error: {ex.Message}";
        }
    }

    // Static parameter name mappings to avoid allocation per call
    private static readonly Dictionary<string, string> ParamMappings =
        new(StringComparer.OrdinalIgnoreCase)
    {
        { "pattern", "pattern" },
        { "glob_pattern", "pattern" },
        { "globPattern", "pattern" },
        { "relativePath", "relativePath" },
        { "relative_path", "relativePath" },
        { "path", "relativePath" },
        { "filePath", "relativePath" },
        { "file_path", "relativePath" },
        { "content", "content" },
        { "file_content", "content" },
        { "fileContent", "content" },
        { "searchPattern", "searchPattern" },
        { "search_pattern", "searchPattern" },
        { "query", "searchPattern" },
    };

    /// <summary>
    /// Normalizes parameter names to match function parameter names.
    /// Public static for direct unit testing.
    /// </summary>
    public static string NormalizeParameterName(string paramName)
    {
        return ParamMappings.TryGetValue(paramName, out var normalizedName)
            ? normalizedName
            : paramName;
    }
}
