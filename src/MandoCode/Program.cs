using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MandoCode.Models;
using MandoCode.Services;
using Spectre.Console;

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

        // Display welcome banner
        DisplayBanner();

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

        // Get project root from args or use current directory
        var projectRoot = args.Length > 0 ? args[0] : Environment.CurrentDirectory;

        AnsiConsole.MarkupLine($"[dim]Project Root: {projectRoot}[/]");
        AnsiConsole.MarkupLine($"[dim]Ollama Endpoint: {config.OllamaEndpoint}[/]");
        AnsiConsole.MarkupLine($"[dim]Model: {config.GetEffectiveModelName()}[/]");
        AnsiConsole.WriteLine();

        // Check if Ollama is accessible
        if (!await CheckOllamaConnection(config.OllamaEndpoint))
        {
            AnsiConsole.MarkupLine("[red]Error: Could not connect to Ollama![/]");
            AnsiConsole.MarkupLine("[yellow]Please ensure:[/]");
            AnsiConsole.MarkupLine("  1. Ollama is installed: https://ollama.ai");
            AnsiConsole.MarkupLine("  2. Ollama is running: ollama serve");
            AnsiConsole.MarkupLine($"  3. Model is installed: ollama pull {config.GetEffectiveModelName()}");
            AnsiConsole.MarkupLine("\n[dim]Or run: mandocode config --help[/]");
            return;
        }

        // Initialize AI service
        var aiService = new AIService(projectRoot, config);

        AnsiConsole.MarkupLine("[green]✓ MandoCode is ready![/]");
        AnsiConsole.WriteLine();

        DisplayHelp();

        // Main interaction loop
        while (true)
        {
            AnsiConsole.Markup("[cyan]You:[/] ");
            var input = Console.ReadLine();

            if (string.IsNullOrWhiteSpace(input))
                continue;

            var command = input.Trim().ToLower();

            // Handle special commands
            if (command == "exit" || command == "quit")
            {
                AnsiConsole.MarkupLine("[yellow]Goodbye![/]");
                break;
            }

            if (command == "clear")
            {
                aiService.ClearHistory();
                Console.Clear();
                DisplayBanner();
                AnsiConsole.MarkupLine("[green]Conversation cleared.[/]");
                continue;
            }

            if (command == "help")
            {
                DisplayHelp();
                continue;
            }

            if (command == "config")
            {
                AnsiConsole.WriteLine();
                var choice = AnsiConsole.Prompt(
                    new SelectionPrompt<string>()
                        .Title("[cyan]Configuration Options:[/]")
                        .AddChoices(new[]
                        {
                            "Run configuration wizard",
                            "View current configuration",
                            "Cancel"
                        })
                );

                switch (choice)
                {
                    case "Run configuration wizard":
                        config = await ConfigurationWizard.RunAsync(config);
                        aiService = new AIService(projectRoot, config);
                        AnsiConsole.Clear();
                        DisplayBanner();
                        AnsiConsole.MarkupLine("[green]✓ Configuration updated and applied![/]\n");
                        break;

                    case "View current configuration":
                        AnsiConsole.WriteLine();
                        config.Display();
                        AnsiConsole.WriteLine();
                        break;
                }

                continue;
            }

            // Process AI request
            AnsiConsole.WriteLine();
            AnsiConsole.Status()
                .Spinner(Spinner.Known.Moon)
                .Start("[yellow]Thinking...[/]", ctx =>
                {
                    var response = aiService.ChatAsync(input).GetAwaiter().GetResult();

                    ctx.Status("[green]Done![/]");
                    AnsiConsole.WriteLine();

                    var panel = new Panel(response)
                    {
                        Header = new PanelHeader("[green]MandoCode[/]"),
                        Border = BoxBorder.Rounded,
                        BorderStyle = new Style(Color.Green)
                    };

                    AnsiConsole.Write(panel);
                    AnsiConsole.WriteLine();
                });
        }
    }

    static void DisplayBanner()
    {
        var gradient = new Rule("[bold cyan]MandoCode[/]")
        {
            Style = Style.Parse("cyan"),
            Justification = Justify.Left
        };

        AnsiConsole.Write(gradient);
        AnsiConsole.MarkupLine("[dim]Local AI Coding Assistant[/]");
        AnsiConsole.MarkupLine("[dim]Powered by DeepSeek Coder V2, Semantic Kernel & Ollama[/]");
        AnsiConsole.WriteLine();
    }

    static void DisplayHelp()
    {
        var table = new Table()
        {
            Border = TableBorder.Rounded
        };

        table.AddColumn("[cyan]Command[/]");
        table.AddColumn("[cyan]Description[/]");

        table.AddRow("help", "Show this help message");
        table.AddRow("config", "Open configuration menu");
        table.AddRow("clear", "Clear conversation history");
        table.AddRow("exit, quit", "Exit MandoCode");
        table.AddRow("[dim]anything else[/]", "[dim]Chat with the AI assistant[/]");

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();

        AnsiConsole.MarkupLine("[yellow]Examples:[/]");
        AnsiConsole.MarkupLine("  • [dim]What files are in this project?[/]");
        AnsiConsole.MarkupLine("  • [dim]Show me the contents of Program.cs[/]");
        AnsiConsole.MarkupLine("  • [dim]Refactor the Main method to use async/await[/]");
        AnsiConsole.MarkupLine("  • [dim]What's the current git status?[/]");
        AnsiConsole.WriteLine();
    }

    static async Task<bool> CheckOllamaConnection(string endpoint)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            var response = await client.GetAsync($"{endpoint}/api/tags");
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
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
                AnsiConsole.MarkupLine($"[green]✓ Created default configuration at: {MandoCodeConfig.GetDefaultConfigPath()}[/]");
                newConfig.Display();
                break;

            case "set":
                if (args.Length < 3)
                {
                    AnsiConsole.MarkupLine("[red]Usage: mandocode config set <key> <value>[/]");
                    return;
                }
                SetConfigValue(args[1], args[2]);
                break;

            case "path":
                AnsiConsole.MarkupLine($"Config file: [cyan]{MandoCodeConfig.GetDefaultConfigPath()}[/]");
                break;

            default:
                AnsiConsole.MarkupLine($"[red]Unknown config command: {command}[/]");
                DisplayConfigHelp();
                break;
        }
    }

    static void DisplayConfigHelp()
    {
        AnsiConsole.MarkupLine("[bold cyan]MandoCode Configuration[/]\n");

        var table = new Table()
        {
            Border = TableBorder.Rounded
        };

        table.AddColumn("[cyan]Command[/]");
        table.AddColumn("[cyan]Description[/]");

        table.AddRow("config show", "Display current configuration");
        table.AddRow("config init", "Create default configuration file");
        table.AddRow("config set <key> <value>", "Set a configuration value");
        table.AddRow("config path", "Show configuration file location");

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();

        AnsiConsole.MarkupLine("[yellow]Available Keys:[/]");
        AnsiConsole.MarkupLine("  • [cyan]endpoint[/]        - Ollama endpoint URL");
        AnsiConsole.MarkupLine("  • [cyan]model[/]           - Model name to use");
        AnsiConsole.MarkupLine("  • [cyan]modelPath[/]       - Path to local model file");
        AnsiConsole.MarkupLine("  • [cyan]temperature[/]     - Temperature (0.0-1.0)");
        AnsiConsole.MarkupLine("  • [cyan]maxTokens[/]       - Maximum response tokens");
        AnsiConsole.WriteLine();

        AnsiConsole.MarkupLine("[yellow]Examples:[/]");
        AnsiConsole.MarkupLine("  mandocode config show");
        AnsiConsole.MarkupLine("  mandocode config set endpoint http://localhost:11434");
        AnsiConsole.MarkupLine("  mandocode config set model codellama:7b");
        AnsiConsole.MarkupLine("  mandocode config set temperature 0.5");
    }

    static void SetConfigValue(string key, string value)
    {
        var config = MandoCodeConfig.Load();

        switch (key.ToLower())
        {
            case "endpoint":
            case "ollamaendpoint":
                config.OllamaEndpoint = value;
                AnsiConsole.MarkupLine($"[green]✓ Set Ollama endpoint to: {value}[/]");
                break;

            case "model":
            case "modelname":
                config.ModelName = value;
                AnsiConsole.MarkupLine($"[green]✓ Set model to: {value}[/]");
                break;

            case "modelpath":
                config.ModelPath = value;
                AnsiConsole.MarkupLine($"[green]✓ Set model path to: {value}[/]");
                break;

            case "temperature":
                if (double.TryParse(value, out var temp) && temp >= 0 && temp <= 1)
                {
                    config.Temperature = temp;
                    AnsiConsole.MarkupLine($"[green]✓ Set temperature to: {temp}[/]");
                }
                else
                {
                    AnsiConsole.MarkupLine("[red]Error: Temperature must be a number between 0.0 and 1.0[/]");
                    return;
                }
                break;

            case "maxtokens":
                if (int.TryParse(value, out var tokens) && tokens > 0)
                {
                    config.MaxTokens = tokens;
                    AnsiConsole.MarkupLine($"[green]✓ Set max tokens to: {tokens}[/]");
                }
                else
                {
                    AnsiConsole.MarkupLine("[red]Error: Max tokens must be a positive integer[/]");
                    return;
                }
                break;

            default:
                AnsiConsole.MarkupLine($"[red]Unknown configuration key: {key}[/]");
                AnsiConsole.MarkupLine("[yellow]Run 'mandocode config --help' for available keys[/]");
                return;
        }

        config.Save();
        AnsiConsole.MarkupLine($"[dim]Configuration saved to: {MandoCodeConfig.GetDefaultConfigPath()}[/]");
    }
}
