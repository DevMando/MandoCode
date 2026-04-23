using System.Text.Json;
using System.Text.Json.Serialization;
using MandoCode.Services;

namespace MandoCode.Models;

// Shared serializer options to avoid repeated allocations
internal static class ConfigJsonOptions
{
    internal static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    internal static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true
    };
}

/// <summary>
/// Configuration for MandoCode.
/// </summary>
public class MandoCodeConfig
{
    /// <summary>
    /// Canonical list of directories to ignore during file operations.
    /// Referenced by FileSystemPlugin, FileAutocompleteProvider, and CreateDefault().
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultIgnoreDirectories = new[]
    {
        ".git", "node_modules", "bin", "obj", ".vs", ".vscode",
        "packages", "dist", "build", "__pycache__", ".idea", ".claude"
    };

    // ── Validation ranges (single source of truth) ──
    public const double MinTemperature = 0.0;
    public const double MaxTemperature = 1.0;
    public const int MinMaxTokens = 256;
    public const int MaxMaxTokens = 131072;
    public const int MinRequestTimeoutMinutes = 1;
    public const int MaxRequestTimeoutMinutes = 60;
    public const long MinToolResultCharBudget = 50_000;
    public const long MaxToolResultCharBudget = 4_000_000;
    public const int MinMaxAutoContinuations = 0;
    public const int MaxMaxAutoContinuations = 10;

    public static bool IsValidTemperature(double value) => value >= MinTemperature && value <= MaxTemperature;
    public static bool IsValidMaxTokens(int value) => value >= MinMaxTokens && value <= MaxMaxTokens;
    public static bool IsValidRequestTimeout(int value) => value >= MinRequestTimeoutMinutes && value <= MaxRequestTimeoutMinutes;
    public static bool IsValidToolResultCharBudget(long value) => value >= MinToolResultCharBudget && value <= MaxToolResultCharBudget;
    public static bool IsValidMaxAutoContinuations(int value) => value >= MinMaxAutoContinuations && value <= MaxMaxAutoContinuations;

    /// <summary>
    /// Ollama endpoint URL.
    /// </summary>
    [JsonPropertyName("ollamaEndpoint")]
    public string OllamaEndpoint { get; set; } = "http://localhost:11434";

    /// <summary>
    /// Model name to use. If not specified, will be inferred from the model path.
    /// </summary>
    [JsonPropertyName("modelName")]
    public string? ModelName { get; set; }

    /// <summary>
    /// Optional: Direct path to a local model file (GGUF, etc.)
    /// If specified, this will be used instead of pulling from Ollama registry.
    /// </summary>
    [JsonPropertyName("modelPath")]
    public string? ModelPath { get; set; }

    /// <summary>
    /// Temperature for model responses (0.0 - 1.0).
    /// Lower = more focused, Higher = more creative.
    /// </summary>
    [JsonPropertyName("temperature")]
    public double Temperature { get; set; } = 0.7;

    /// <summary>
    /// Maximum tokens for model responses.
    /// </summary>
    [JsonPropertyName("maxTokens")]
    public int MaxTokens { get; set; } = 4096;

    /// <summary>
    /// Per-request timeout in minutes. Covers direct chats and each plan step.
    /// Agentic work with many tool calls can take longer than a few minutes — raise this
    /// if the model gets cut off mid-task. Cancel anytime with Ctrl+C.
    /// </summary>
    [JsonPropertyName("requestTimeoutMinutes")]
    public int RequestTimeoutMinutes { get; set; } = 15;

    /// <summary>
    /// Total character budget for tool-call results within a single chat turn or plan step.
    /// When exceeded, further tool calls are refused to prevent context-window overflow,
    /// and (if auto-continuation is on) the step restarts with a fresh scope.
    /// ~4 chars per token, so 100,000 chars ≈ 25k tokens of tool results — fits safely
    /// inside even small provider context windows (32k+). Raise this if you're on a
    /// large-context cloud model and want fewer continuations.
    /// </summary>
    [JsonPropertyName("toolResultCharBudget")]
    public long ToolResultCharBudget { get; set; } = 100_000;

    /// <summary>
    /// When true and the tool-result budget is exhausted, the assistant's progress summary
    /// is treated as implicit compaction and the turn auto-continues with a fresh scope —
    /// no "press enter to keep going" step.
    /// </summary>
    [JsonPropertyName("enableAutoContinuation")]
    public bool EnableAutoContinuation { get; set; } = true;

    /// <summary>
    /// Hard cap on how many times a single user request can auto-continue after budget
    /// exhaustion. Prevents runaway loops if the model never converges.
    /// </summary>
    [JsonPropertyName("maxAutoContinuations")]
    public int MaxAutoContinuations { get; set; } = 3;

    /// <summary>
    /// Additional directories to ignore when scanning files.
    /// </summary>
    [JsonPropertyName("ignoreDirectories")]
    public List<string> IgnoreDirectories { get; set; } = new();

    /// <summary>
    /// Enable task planning for complex requests.
    /// When enabled, complex requests are broken down into multiple steps.
    /// </summary>
    [JsonPropertyName("enableTaskPlanning")]
    public bool EnableTaskPlanning { get; set; } = true;

    /// <summary>
    /// Enable fallback parsing for function calls output as JSON text.
    /// Some local models output function calls as text instead of proper tool calls.
    /// </summary>
    [JsonPropertyName("enableFallbackFunctionParsing")]
    public bool EnableFallbackFunctionParsing { get; set; } = true;

    /// <summary>
    /// Default deduplication window in seconds for function calls.
    /// Prevents duplicate function invocations within this time window.
    /// </summary>
    [JsonPropertyName("functionDeduplicationWindowSeconds")]
    public int FunctionDeduplicationWindowSeconds { get; set; } = 5;

    /// <summary>
    /// Maximum number of retry attempts for transient errors.
    /// </summary>
    [JsonPropertyName("maxRetryAttempts")]
    public int MaxRetryAttempts { get; set; } = 2;

    /// <summary>
    /// Enable diff approval prompts before file writes.
    /// When enabled, the user sees a diff and must approve changes before they are written.
    /// </summary>
    [JsonPropertyName("enableDiffApprovals")]
    public bool EnableDiffApprovals { get; set; } = true;

    /// <summary>
    /// Enable token tracking and display.
    /// Shows session token totals and per-operation token costs.
    /// </summary>
    [JsonPropertyName("enableTokenTracking")]
    public bool EnableTokenTracking { get; set; } = true;

    /// <summary>
    /// Enable terminal theme detection and ANSI palette customization.
    /// When enabled, MandoCode detects light/dark theme and applies a curated color palette.
    /// </summary>
    [JsonPropertyName("enableThemeCustomization")]
    public bool EnableThemeCustomization { get; set; } = true;

    /// <summary>
    /// Enable web search and page fetching capabilities.
    /// When enabled, the AI can search DuckDuckGo and fetch web pages.
    /// </summary>
    [JsonPropertyName("enableWebSearch")]
    public bool EnableWebSearch { get; set; } = true;

    /// <summary>
    /// Music player preferences (volume, genre, autoplay).
    /// </summary>
    [JsonPropertyName("music")]
    public MusicConfig Music { get; set; } = new();

    /// <summary>
    /// Loads configuration from file, or creates a default one if it doesn't exist.
    /// </summary>
    public static MandoCodeConfig Load(string? configPath = null)
    {
        configPath ??= GetDefaultConfigPath();

        if (File.Exists(configPath))
        {
            try
            {
                var json = File.ReadAllText(configPath);
                var config = JsonSerializer.Deserialize<MandoCodeConfig>(json, ConfigJsonOptions.ReadOptions);
                if (config != null)
                {
                    config.ValidateAndClamp();
                    return config;
                }
                return new MandoCodeConfig();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Warning: Failed to load config from {configPath}: {ex.Message}");
                Console.WriteLine("Using default configuration.");
                return new MandoCodeConfig();
            }
        }

        return new MandoCodeConfig();
    }

    /// <summary>
    /// Saves configuration to file.
    /// </summary>
    public void Save(string? configPath = null)
    {
        configPath ??= GetDefaultConfigPath();

        try
        {
            var directory = Path.GetDirectoryName(configPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(this, ConfigJsonOptions.WriteOptions);
            File.WriteAllText(configPath, json);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Warning: Failed to save config to {configPath}: {ex.Message}");
        }
    }

    /// <summary>
    /// Validates and clamps numeric config values to safe ranges.
    /// Called after deserialization to guard against corrupted or hand-edited config files.
    /// </summary>
    public void ValidateAndClamp()
    {
        Temperature = Math.Clamp(Temperature, MinTemperature, MaxTemperature);
        MaxTokens = Math.Clamp(MaxTokens, MinMaxTokens, MaxMaxTokens);
        FunctionDeduplicationWindowSeconds = Math.Clamp(FunctionDeduplicationWindowSeconds, 0, 60);
        MaxRetryAttempts = Math.Clamp(MaxRetryAttempts, 0, 10);
        Music.Volume = Math.Clamp(Music.Volume, 0f, 1f);

        if (string.IsNullOrWhiteSpace(OllamaEndpoint))
            OllamaEndpoint = "http://localhost:11434";
    }

    /// <summary>
    /// Gets the effective model name (from ModelName or inferred from ModelPath).
    /// </summary>
    public string GetEffectiveModelName()
    {
        // Prefer explicit ModelName if provided
        if (!string.IsNullOrWhiteSpace(ModelName))
            return ModelName;

        // Fall back to extracting name from ModelPath
        if (!string.IsNullOrWhiteSpace(ModelPath))
        {
            var fileName = Path.GetFileNameWithoutExtension(ModelPath);
            return fileName;
        }

        // Final fallback to default model with tool support
        return "minimax-m2.5:cloud";
    }

    /// <summary>
    /// Gets the default config file path.
    /// </summary>
    public static string GetDefaultConfigPath()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(userProfile, ".mandocode", "config.json");
    }

    /// <summary>
    /// Checks if this is the first run (no config file exists).
    /// </summary>
    public static bool IsFirstRun()
    {
        return !File.Exists(GetDefaultConfigPath());
    }

    /// <summary>
    /// Creates a default configuration file.
    /// </summary>
    public static MandoCodeConfig CreateDefault()
    {
        return new MandoCodeConfig
        {
            OllamaEndpoint = "http://localhost:11434",
            ModelName = "minimax-m2.5:cloud",
            Temperature = 0.7,
            MaxTokens = 4096,
            RequestTimeoutMinutes = 15,
            ToolResultCharBudget = 100_000,
            EnableAutoContinuation = true,
            MaxAutoContinuations = 3,
            IgnoreDirectories = new List<string>(DefaultIgnoreDirectories),
            EnableTaskPlanning = true
        };
    }

    /// <summary>
    /// Displays the current configuration.
    /// </summary>
    public void Display()
    {
        Console.WriteLine("Current Configuration:");
        Console.WriteLine($"  Ollama Endpoint: {OllamaEndpoint}");
        var modelBase = GetEffectiveModelName().Split(':')[0];
        var modelLink = FileLinkHelper.Hyperlink($"https://ollama.com/library/{modelBase}", GetEffectiveModelName());
        Console.WriteLine($"  Model Name: {modelLink}");
        if (!string.IsNullOrWhiteSpace(ModelPath))
        {
            Console.WriteLine($"  Model Path: {ModelPath}");
        }
        Console.WriteLine($"  Temperature: {Temperature}");
        Console.WriteLine($"  Max Tokens: {MaxTokens}");
        Console.WriteLine($"  Request Timeout: {RequestTimeoutMinutes} min");
        Console.WriteLine($"  Tool Result Budget: {ToolResultCharBudget:N0} chars (~{ToolResultCharBudget / 4:N0} tokens)");
        Console.WriteLine($"  Auto-Continuation: {(EnableAutoContinuation ? $"Enabled (max {MaxAutoContinuations})" : "Disabled")}");
        if (IgnoreDirectories.Any())
        {
            Console.WriteLine($"  Ignore Directories: {string.Join(", ", IgnoreDirectories)}");
        }
        Console.WriteLine($"  Task Planning: {(EnableTaskPlanning ? "Enabled" : "Disabled")}");
        Console.WriteLine($"  Diff Approvals: {(EnableDiffApprovals ? "Enabled" : "Disabled")}");
        Console.WriteLine($"  Token Tracking: {(EnableTokenTracking ? "Enabled" : "Disabled")}");
        Console.WriteLine($"  Fallback Function Parsing: {(EnableFallbackFunctionParsing ? "Enabled" : "Disabled")}");
        Console.WriteLine($"  Deduplication Window: {FunctionDeduplicationWindowSeconds}s");
        Console.WriteLine($"  Max Retry Attempts: {MaxRetryAttempts}");
        Console.WriteLine($"  Theme Customization: {(EnableThemeCustomization ? "Enabled" : "Disabled")}");
        Console.WriteLine($"  Web Search: {(EnableWebSearch ? "Enabled" : "Disabled")}");
        Console.WriteLine($"  Music Volume: {(int)(Music.Volume * 100)}%  Genre: {Music.Genre}");
        Console.WriteLine($"  Config File: {GetDefaultConfigPath()}");
    }
}
