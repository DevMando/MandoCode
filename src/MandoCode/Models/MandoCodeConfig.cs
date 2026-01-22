using System.Text.Json;
using System.Text.Json.Serialization;

namespace MandoCode.Models;

/// <summary>
/// Configuration for MandoCode.
/// </summary>
public class MandoCodeConfig
{
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
                var config = JsonSerializer.Deserialize<MandoCodeConfig>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    WriteIndented = true
                });
                return config ?? new MandoCodeConfig();
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

        var directory = Path.GetDirectoryName(configPath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions
        {
            WriteIndented = true
        });

        File.WriteAllText(configPath, json);
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
        return "qwen2.5-coder:14b";
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
            ModelName = "qwen2.5-coder:14b",
            Temperature = 0.7,
            MaxTokens = 4096,
            IgnoreDirectories = new List<string>
            {
                ".git", "node_modules", "bin", "obj", ".vs", ".vscode",
                "packages", "dist", "build", "__pycache__", ".idea"
            },
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
        Console.WriteLine($"  Model Name: {GetEffectiveModelName()}");
        if (!string.IsNullOrWhiteSpace(ModelPath))
        {
            Console.WriteLine($"  Model Path: {ModelPath}");
        }
        Console.WriteLine($"  Temperature: {Temperature}");
        Console.WriteLine($"  Max Tokens: {MaxTokens}");
        if (IgnoreDirectories.Any())
        {
            Console.WriteLine($"  Ignore Directories: {string.Join(", ", IgnoreDirectories)}");
        }
        Console.WriteLine($"  Task Planning: {(EnableTaskPlanning ? "Enabled" : "Disabled")}");
        Console.WriteLine($"  Fallback Function Parsing: {(EnableFallbackFunctionParsing ? "Enabled" : "Disabled")}");
        Console.WriteLine($"  Deduplication Window: {FunctionDeduplicationWindowSeconds}s");
        Console.WriteLine($"  Max Retry Attempts: {MaxRetryAttempts}");
        Console.WriteLine($"  Config File: {GetDefaultConfigPath()}");
    }
}
