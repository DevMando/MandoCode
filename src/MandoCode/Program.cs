using MandoCode.Components;
using MandoCode.Models;
using MandoCode.Services;
using MandoCode.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RazorConsole.Core;
using System.Text;

namespace MandoCode;

class Program
{
    static async Task Main(string[] args)
    {
        // Set console encoding to UTF-8 for Unicode characters (spinners, emojis, etc.)
        Console.OutputEncoding = Encoding.UTF8;
        Console.InputEncoding = Encoding.UTF8;

        // Handle config commands first
        if (args.Length > 0 && args[0].ToLower() == "config")
        {
            HandleConfigCommand(args.Skip(1).ToArray());
            return;
        }

        // Get project root from args or use current directory
        var projectRoot = args.Length > 0 ? args[0] : Environment.CurrentDirectory;

        var hostBuilder = Host.CreateDefaultBuilder(args)
            .UseRazorConsole<App>();

        hostBuilder.ConfigureServices(services =>
        {
            // Load configuration
            var config = MandoCodeConfig.Load();

            // Override with environment variables if set
            var envEndpoint = Environment.GetEnvironmentVariable("OLLAMA_ENDPOINT");
            if (!string.IsNullOrEmpty(envEndpoint))
            {
                config.OllamaEndpoint = envEndpoint;
            }

            var envModel = Environment.GetEnvironmentVariable("OLLAMA_MODEL");
            if (!string.IsNullOrEmpty(envModel))
            {
                config.ModelName = envModel;
            }

            // Register configuration as singleton
            services.AddSingleton(config);

            // Register AIService as singleton
            services.AddSingleton(provider =>
            {
                var cfg = provider.GetRequiredService<MandoCodeConfig>();
                return new AIService(projectRoot, cfg);
            });

            // Register TaskPlannerService as singleton
            services.AddSingleton(provider =>
            {
                var aiService = provider.GetRequiredService<AIService>();
                var cfg = provider.GetRequiredService<MandoCodeConfig>();
                return new TaskPlannerService(aiService, cfg);
            });

            // Register FileAutocompleteProvider as singleton
            services.AddSingleton(provider =>
            {
                var cfg = provider.GetRequiredService<MandoCodeConfig>();
                var ignoreDirs = new HashSet<string>
                {
                    ".git", "node_modules", "bin", "obj", ".vs", ".vscode",
                    "packages", "dist", "build", "__pycache__", ".idea", ".claude"
                };
                foreach (var dir in cfg.IgnoreDirectories) ignoreDirs.Add(dir);
                return new FileAutocompleteProvider(projectRoot, ignoreDirs);
            });

            // Configure console options
            services.Configure<ConsoleAppOptions>(options =>
            {
                options.AutoClearConsole = false;
            });
        });

        var host = hostBuilder.Build();
        await host.RunAsync();
    }

    static void HandleConfigCommand(string[] args)
    {
        if (args.Length == 0 || args[0] == "--help" || args[0] == "-h")
        {
            DisplayConfigHelp();
            return;
        }

        var command = args[0].ToLower();

        switch (command)
        {
            case "show":
            case "view":
                var config = MandoCodeConfig.Load();
                config.Display();
                break;

            case "init":
            case "create":
                var newConfig = MandoCodeConfig.CreateDefault();
                newConfig.Save();
                Console.WriteLine($"✓ Created default configuration at: {MandoCodeConfig.GetDefaultConfigPath()}");
                newConfig.Display();
                break;

            case "set":
                if (args.Length < 3)
                {
                    Console.WriteLine("Usage: mandocode config set <key> <value>");
                    return;
                }
                SetConfigValue(args[1], args[2]);
                break;

            case "path":
                Console.WriteLine($"Config file: {MandoCodeConfig.GetDefaultConfigPath()}");
                break;

            default:
                Console.WriteLine($"Unknown config command: {command}");
                DisplayConfigHelp();
                break;
        }
    }

    static void DisplayConfigHelp()
    {
        Console.WriteLine("MandoCode Configuration\n");
        Console.WriteLine("Commands:");
        Console.WriteLine("  config show              - Display current configuration");
        Console.WriteLine("  config init              - Create default configuration file");
        Console.WriteLine("  config set <key> <value> - Set a configuration value");
        Console.WriteLine("  config path              - Show configuration file location");
        Console.WriteLine();
        Console.WriteLine("Available Keys:");
        Console.WriteLine("  • endpoint        - Ollama endpoint URL");
        Console.WriteLine("  • model           - Model name to use");
        Console.WriteLine("  • modelPath       - Path to local model file");
        Console.WriteLine("  • temperature     - Temperature (0.0-1.0)");
        Console.WriteLine("  • maxTokens       - Maximum response tokens");
        Console.WriteLine("  • taskPlanning    - Enable/disable task planning (true/false)");
        Console.WriteLine();
        Console.WriteLine("Examples:");
        Console.WriteLine("  mandocode config show");
        Console.WriteLine("  mandocode config set endpoint http://localhost:11434");
        Console.WriteLine("  mandocode config set model minimax-m2.5:cloud");
        Console.WriteLine("  mandocode config set temperature 0.5");
    }

    static void SetConfigValue(string key, string value)
    {
        var config = MandoCodeConfig.Load();

        switch (key.ToLower())
        {
            case "endpoint":
            case "ollamaendpoint":
                config.OllamaEndpoint = value;
                Console.WriteLine($"✓ Set Ollama endpoint to: {value}");
                break;

            case "model":
            case "modelname":
                config.ModelName = value;
                Console.WriteLine($"✓ Set model to: {value}");
                break;

            case "modelpath":
                config.ModelPath = value;
                Console.WriteLine($"✓ Set model path to: {value}");
                break;

            case "temperature":
                if (double.TryParse(value, out var temp) && temp >= 0 && temp <= 1)
                {
                    config.Temperature = temp;
                    Console.WriteLine($"✓ Set temperature to: {temp}");
                }
                else
                {
                    Console.WriteLine("Error: Temperature must be a number between 0.0 and 1.0");
                    return;
                }
                break;

            case "maxtokens":
                if (int.TryParse(value, out var tokens) && tokens > 0)
                {
                    config.MaxTokens = tokens;
                    Console.WriteLine($"✓ Set max tokens to: {tokens}");
                }
                else
                {
                    Console.WriteLine("Error: Max tokens must be a positive integer");
                    return;
                }
                break;

            case "taskplanning":
            case "enabletaskplanning":
                if (bool.TryParse(value, out var enablePlanning))
                {
                    config.EnableTaskPlanning = enablePlanning;
                    Console.WriteLine($"✓ Task planning {(enablePlanning ? "enabled" : "disabled")}");
                }
                else
                {
                    Console.WriteLine("Error: Value must be 'true' or 'false'");
                    return;
                }
                break;

            default:
                Console.WriteLine($"Unknown configuration key: {key}");
                Console.WriteLine("Run 'mandocode config --help' for available keys");
                return;
        }

        config.Save();
        Console.WriteLine($"Configuration saved to: {MandoCodeConfig.GetDefaultConfigPath()}");
    }
}
