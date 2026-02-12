using MandoCode.Models;
using Spectre.Console;
using System.Text.Json;

namespace MandoCode.Services;

/// <summary>
/// Interactive TUI wizard for configuring MandoCode.
/// </summary>
public class ConfigurationWizard
{
    /// <summary>
    /// Runs the interactive configuration wizard.
    /// </summary>
    public static async Task<MandoCodeConfig> RunAsync(MandoCodeConfig? existingConfig = null)
    {
        var config = existingConfig ?? MandoCodeConfig.CreateDefault();

        AnsiConsole.Clear();
        DisplayWizardHeader();

        // Step 1: Ollama Endpoint
        config.OllamaEndpoint = await ConfigureOllamaEndpoint(config.OllamaEndpoint);

        // Step 2: Model Selection
        config = await ConfigureModel(config);

        // Step 3: Temperature
        config.Temperature = ConfigureTemperature(config.Temperature);

        // Step 4: Max Tokens
        config.MaxTokens = ConfigureMaxTokens(config.MaxTokens);

        // Step 5: Ignore Directories
        config.IgnoreDirectories = ConfigureIgnoreDirectories(config.IgnoreDirectories);

        // Step 6: Save Configuration
        if (ConfirmSave())
        {
            config.Save();
            AnsiConsole.MarkupLine("\n[green]✓ Configuration saved successfully![/]");
            AnsiConsole.MarkupLine($"[dim]Location: {MandoCodeConfig.GetDefaultConfigPath()}[/]");
        }
        else
        {
            AnsiConsole.MarkupLine("\n[yellow]Configuration not saved. Changes will only apply to this session.[/]");
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[dim]Press any key to continue...[/]");
        Console.ReadKey(true);

        return config;
    }

    private static void DisplayWizardHeader()
    {
        var panel = new Panel(
            Align.Center(
                new Markup("[bold cyan]MandoCode Configuration Wizard[/]\n[dim]Let's set up your AI coding assistant[/]"),
                VerticalAlignment.Middle))
        {
            Border = BoxBorder.Double,
            BorderStyle = new Style(Color.Cyan)
        };

        AnsiConsole.Write(panel);
        AnsiConsole.WriteLine();
    }

    private static async Task<string> ConfigureOllamaEndpoint(string currentEndpoint)
    {
        AnsiConsole.Write(new Rule("[yellow]1. Ollama Connection[/]").LeftJustified());
        AnsiConsole.WriteLine();

        var endpoint = AnsiConsole.Prompt(
            new TextPrompt<string>("[cyan]Ollama endpoint URL:[/]")
                .DefaultValue(currentEndpoint)
                .ValidationErrorMessage("[red]Please enter a valid URL[/]")
                .Validate(url =>
                {
                    if (Uri.TryCreate(url, UriKind.Absolute, out _))
                        return ValidationResult.Success();
                    return ValidationResult.Error("[red]Invalid URL format[/]");
                })
        );

        // Test connection
        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync("[yellow]Testing connection to Ollama...[/]", async ctx =>
            {
                var isConnected = await TestOllamaConnection(endpoint);
                if (isConnected)
                {
                    AnsiConsole.MarkupLine("[green]✓ Connected successfully![/]");
                }
                else
                {
                    AnsiConsole.MarkupLine("[red]✗ Could not connect to Ollama[/]");
                    AnsiConsole.MarkupLine("[yellow]Make sure Ollama is running: ollama serve[/]");
                }
            });

        AnsiConsole.WriteLine();
        return endpoint;
    }

    private static async Task<MandoCodeConfig> ConfigureModel(MandoCodeConfig config)
    {
        AnsiConsole.Write(new Rule("[yellow]2. Model Selection[/]").LeftJustified());
        AnsiConsole.WriteLine();

        var modelChoice = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[cyan]How would you like to configure your model?[/]")
                .AddChoices(new[]
                {
                    "Select from available Ollama models",
                    "Enter model name manually",
                    "Specify local model path (GGUF)",
                    "Keep current setting"
                })
        );

        switch (modelChoice)
        {
            case "Select from available Ollama models":
                var availableModels = await GetAvailableOllamaModels(config.OllamaEndpoint);
                if (availableModels.Any())
                {
                    var selectedModel = AnsiConsole.Prompt(
                        new SelectionPrompt<string>()
                            .Title("[cyan]Select a model:[/]")
                            .PageSize(10)
                            .AddChoices(availableModels)
                    );
                    config.ModelName = selectedModel;
                    config.ModelPath = null;
                    AnsiConsole.MarkupLine($"[green]✓ Selected model: {selectedModel}[/]");
                }
                else
                {
                    AnsiConsole.MarkupLine("[yellow]No models found. You may need to pull a model first:[/]");
                    AnsiConsole.MarkupLine("[dim]  ollama pull qwen2.5-coder:14b[/]");
                    AnsiConsole.MarkupLine("[dim]  ollama pull qwen2.5-coder:7b[/]");
                    config.ModelName = AnsiConsole.Ask<string>("[cyan]Enter model name:[/]", "qwen2.5-coder:14b");
                }
                break;

            case "Enter model name manually":
                config.ModelName = AnsiConsole.Ask<string>("[cyan]Enter model name:[/]", config.ModelName ?? "qwen2.5-coder:14b");
                config.ModelPath = null;
                AnsiConsole.MarkupLine($"[green]✓ Model set to: {config.ModelName}[/]");
                break;

            case "Specify local model path (GGUF)":
                config.ModelPath = AnsiConsole.Ask<string>("[cyan]Enter path to model file:[/]");
                var inferredName = Path.GetFileNameWithoutExtension(config.ModelPath);
                config.ModelName = AnsiConsole.Ask<string>("[cyan]Model name:[/]", inferredName);
                AnsiConsole.MarkupLine($"[green]✓ Model path: {config.ModelPath}[/]");
                AnsiConsole.MarkupLine($"[green]✓ Model name: {config.ModelName}[/]");
                break;

            case "Keep current setting":
                AnsiConsole.MarkupLine($"[dim]Keeping: {config.GetEffectiveModelName()}[/]");
                break;
        }

        AnsiConsole.WriteLine();
        return config;
    }

    private static double ConfigureTemperature(double currentTemperature)
    {
        AnsiConsole.Write(new Rule("[yellow]3. Temperature (Creativity)[/]").LeftJustified());
        AnsiConsole.WriteLine();

        AnsiConsole.MarkupLine("[dim]Temperature controls response creativity:[/]");
        AnsiConsole.MarkupLine("[dim]  0.0-0.3: Very focused (code generation)[/]");
        AnsiConsole.MarkupLine("[dim]  0.4-0.7: Balanced (recommended)[/]");
        AnsiConsole.MarkupLine("[dim]  0.8-1.0: Creative (brainstorming)[/]");
        AnsiConsole.WriteLine();

        var temperature = AnsiConsole.Prompt(
            new TextPrompt<double>("[cyan]Temperature (0.0-1.0):[/]")
                .DefaultValue(currentTemperature)
                .ValidationErrorMessage("[red]Please enter a number between 0.0 and 1.0[/]")
                .Validate(temp =>
                {
                    if (temp >= 0 && temp <= 1)
                        return ValidationResult.Success();
                    return ValidationResult.Error("[red]Temperature must be between 0.0 and 1.0[/]");
                })
        );

        AnsiConsole.MarkupLine($"[green]✓ Temperature set to: {temperature}[/]");
        AnsiConsole.WriteLine();
        return temperature;
    }

    private static int ConfigureMaxTokens(int currentMaxTokens)
    {
        AnsiConsole.Write(new Rule("[yellow]4. Maximum Response Tokens[/]").LeftJustified());
        AnsiConsole.WriteLine();

        AnsiConsole.MarkupLine("[dim]Controls the maximum length of AI responses.[/]");
        AnsiConsole.MarkupLine("[dim]  2048: Fast, shorter responses[/]");
        AnsiConsole.MarkupLine("[dim]  4096: Balanced (recommended)[/]");
        AnsiConsole.MarkupLine("[dim]  8192: Longer, more detailed responses[/]");
        AnsiConsole.WriteLine();

        var maxTokens = AnsiConsole.Prompt(
            new TextPrompt<int>("[cyan]Max tokens:[/]")
                .DefaultValue(currentMaxTokens)
                .ValidationErrorMessage("[red]Please enter a positive number[/]")
                .Validate(tokens =>
                {
                    if (tokens > 0)
                        return ValidationResult.Success();
                    return ValidationResult.Error("[red]Max tokens must be positive[/]");
                })
        );

        AnsiConsole.MarkupLine($"[green]✓ Max tokens set to: {maxTokens}[/]");
        AnsiConsole.WriteLine();
        return maxTokens;
    }

    private static List<string> ConfigureIgnoreDirectories(List<string> currentIgnoreDirectories)
    {
        AnsiConsole.Write(new Rule("[yellow]5. Ignore Directories[/]").LeftJustified());
        AnsiConsole.WriteLine();

        AnsiConsole.MarkupLine("[dim]Current ignore list:[/]");
        foreach (var dir in currentIgnoreDirectories)
        {
            AnsiConsole.MarkupLine($"[dim]  • {dir}[/]");
        }
        AnsiConsole.WriteLine();

        var modify = AnsiConsole.Confirm("[cyan]Modify ignore directories?[/]", false);

        if (modify)
        {
            var choice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("[cyan]What would you like to do?[/]")
                    .AddChoices(new[]
                    {
                        "Add directory to ignore list",
                        "Reset to defaults",
                        "Keep current list"
                    })
            );

            switch (choice)
            {
                case "Add directory to ignore list":
                    var newDir = AnsiConsole.Ask<string>("[cyan]Directory name to ignore:[/]");
                    if (!currentIgnoreDirectories.Contains(newDir))
                    {
                        currentIgnoreDirectories.Add(newDir);
                        AnsiConsole.MarkupLine($"[green]✓ Added: {newDir}[/]");
                    }
                    else
                    {
                        AnsiConsole.MarkupLine($"[yellow]Already in list: {newDir}[/]");
                    }
                    break;

                case "Reset to defaults":
                    currentIgnoreDirectories = new List<string>
                    {
                        ".git", "node_modules", "bin", "obj", ".vs", ".vscode",
                        "packages", "dist", "build", "__pycache__", ".idea"
                    };
                    AnsiConsole.MarkupLine("[green]✓ Reset to defaults[/]");
                    break;
            }
        }

        AnsiConsole.WriteLine();
        return currentIgnoreDirectories;
    }

    private static bool ConfirmSave()
    {
        AnsiConsole.Write(new Rule("[yellow]6. Save Configuration[/]").LeftJustified());
        AnsiConsole.WriteLine();

        return AnsiConsole.Confirm("[cyan]Save this configuration?[/]", true);
    }

    private static async Task<bool> TestOllamaConnection(string endpoint)
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

    private static async Task<List<string>> GetAvailableOllamaModels(string endpoint)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var response = await client.GetAsync($"{endpoint}/api/tags");

            if (!response.IsSuccessStatusCode)
                return new List<string>();

            var content = await response.Content.ReadAsStringAsync();
            var json = JsonDocument.Parse(content);

            var models = new List<string>();
            if (json.RootElement.TryGetProperty("models", out var modelsArray))
            {
                foreach (var model in modelsArray.EnumerateArray())
                {
                    if (model.TryGetProperty("name", out var name))
                    {
                        models.Add(name.GetString() ?? "");
                    }
                }
            }

            return models.Where(m => !string.IsNullOrEmpty(m)).ToList();
        }
        catch
        {
            return new List<string>();
        }
    }

    /// <summary>
    /// Shows a quick configuration summary.
    /// </summary>
    public static void DisplayConfigSummary(MandoCodeConfig config)
    {
        var table = new Table()
        {
            Border = TableBorder.Rounded,
            BorderStyle = new Style(Color.Cyan)
        };

        table.AddColumn("[bold]Setting[/]");
        table.AddColumn("[bold]Value[/]");

        table.AddRow("Ollama Endpoint", config.OllamaEndpoint);
        table.AddRow("Model", config.GetEffectiveModelName());
        if (!string.IsNullOrEmpty(config.ModelPath))
        {
            table.AddRow("Model Path", config.ModelPath);
        }
        table.AddRow("Temperature", config.Temperature.ToString("F2"));
        table.AddRow("Max Tokens", config.MaxTokens.ToString());
        table.AddRow("Ignore Dirs", string.Join(", ", config.IgnoreDirectories.Take(5)) +
                                     (config.IgnoreDirectories.Count > 5 ? "..." : ""));

        AnsiConsole.Write(table);
    }
}
