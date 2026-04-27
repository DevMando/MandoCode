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
    /// <param name="promptTextVdom">
    /// Optional VDOM-aware text prompt (App.razor's WizardPromptTextAsync). When
    /// provided, the endpoint step routes through RazorConsole's TextInput component,
    /// which captures keystrokes reliably alongside the VDOM render loop. Without
    /// this delegate, falls back to Spectre's TextPrompt — fine for CLI invocation,
    /// but users have reported dropped keystrokes when Spectre's prompt runs while
    /// the VDOM is live.
    /// </param>
    public static async Task<MandoCodeConfig> RunAsync(
        MandoCodeConfig? existingConfig = null,
        Func<string, string, Func<string, string?>?, string?, Task<string>>? promptTextVdom = null)
    {
        var config = existingConfig ?? MandoCodeConfig.CreateDefault();

        AnsiConsole.Clear();
        DisplayWizardHeader();

        // Step 1: Ollama Endpoint
        config.OllamaEndpoint = await ConfigureOllamaEndpoint(config.OllamaEndpoint, promptTextVdom);

        // Step 2: Model Selection
        config = await ConfigureModel(config);

        // Step 3: Temperature
        config.Temperature = ConfigureTemperature(config.Temperature);

        // Step 4: Max Tokens
        config.MaxTokens = ConfigureMaxTokens(config.MaxTokens);

        // Step 5: Request Timeout
        config.RequestTimeoutMinutes = ConfigureRequestTimeout(config.RequestTimeoutMinutes);

        // Step 6: Ignore Directories
        config.IgnoreDirectories = ConfigureIgnoreDirectories(config.IgnoreDirectories);

        // Step 7: Save Configuration
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

    private static async Task<string> ConfigureOllamaEndpoint(
        string currentEndpoint,
        Func<string, string, Func<string, string?>?, string?, Task<string>>? promptTextVdom = null)
    {
        AnsiConsole.Write(new Rule("[yellow]1. Ollama Connection[/]").LeftJustified());
        AnsiConsole.WriteLine();

        // Prefer the VDOM TextInput when wired in — Spectre's TextPrompt drops
        // keystrokes when the VDOM render loop is active. Pre-fill the input with
        // the current endpoint so the user can press Enter to keep it or edit in
        // place rather than retyping.
        string endpoint;
        if (promptTextVdom != null)
        {
            var entered = await promptTextVdom("Ollama endpoint URL (press Enter to keep current):", currentEndpoint, v =>
            {
                if (string.IsNullOrWhiteSpace(v)) return null; // empty → falls back to default
                return Uri.TryCreate(v, UriKind.Absolute, out _) ? null : "Invalid URL format";
            }, currentEndpoint);
            endpoint = string.IsNullOrWhiteSpace(entered) ? currentEndpoint : entered.Trim();
        }
        else
        {
            endpoint = AnsiConsole.Prompt(
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
        }

        // Test connection
        await AnsiConsole.Status()
            .Spinner(LoadingMessages.GetRandomSpinner())
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
                    AnsiConsole.MarkupLine("[dim]  ollama pull minimax-m2.7:cloud[/]");
                    AnsiConsole.MarkupLine("[dim]  ollama pull qwen2.5-coder:14b[/]");
                    config.ModelName = AnsiConsole.Ask<string>("[cyan]Enter model name:[/]", "minimax-m2.7:cloud");
                }
                break;

            case "Enter model name manually":
                config.ModelName = AnsiConsole.Ask<string>("[cyan]Enter model name:[/]", config.ModelName ?? "minimax-m2.7:cloud");
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

        AnsiConsole.MarkupLine("[dim]Temperature controls how predictable vs. creative the model's replies are.[/]");
        AnsiConsole.MarkupLine("[dim]At 0.0 the model picks the single most likely next word every time —[/]");
        AnsiConsole.MarkupLine("[dim]identical prompts give identical answers. Higher values let it sample[/]");
        AnsiConsole.MarkupLine("[dim]from less-likely options, so replies become more varied (and noisier).[/]");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[dim]  0.0-0.3  Very focused — best for code generation, refactors, exact formats[/]");
        AnsiConsole.MarkupLine("[dim]  0.4-0.7  Balanced — recommended for general use, technical writing, Q&A[/]");
        AnsiConsole.MarkupLine("[dim]  0.8-1.0  Creative — brainstorming, naming ideas, prose. Can hallucinate more.[/]");
        AnsiConsole.WriteLine();

        var temperature = AnsiConsole.Prompt(
            new TextPrompt<double>("[cyan]Temperature (0.0-1.0):[/]")
                .DefaultValue(currentTemperature)
                .ValidationErrorMessage("[red]Please enter a number between 0.0 and 1.0[/]")
                .Validate(temp =>
                {
                    if (MandoCodeConfig.IsValidTemperature(temp))
                        return ValidationResult.Success();
                    return ValidationResult.Error($"[red]Temperature must be between {MandoCodeConfig.MinTemperature} and {MandoCodeConfig.MaxTemperature}[/]");
                })
        );

        AnsiConsole.MarkupLine($"[green]✓ Temperature set to: {temperature}[/]");
        AnsiConsole.WriteLine();
        return temperature;
    }

    public static int ConfigureMaxTokens(int currentMaxTokens)
    {
        AnsiConsole.Write(new Rule("[yellow]4. Maximum Response Tokens[/]").LeftJustified());
        AnsiConsole.WriteLine();

        AnsiConsole.MarkupLine("[dim]This is the upper bound on how long a single reply can be — counted in tokens[/]");
        AnsiConsole.MarkupLine("[dim](roughly 4 characters or ¾ of a word each). If a reply hits the limit, it gets[/]");
        AnsiConsole.MarkupLine("[dim]cut off mid-sentence and you'll see a warning.[/]");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[dim]Bigger isn't always better:[/]");
        AnsiConsole.MarkupLine("[dim]  • Local models slow down a lot at very high limits.[/]");
        AnsiConsole.MarkupLine("[dim]  • The number must fit in your model's [italic]context window[/] (e.g. qwen3.5 = 256k).[/]");
        AnsiConsole.MarkupLine("[dim]  • Setting it lower than you need is fine — the model only uses what it needs.[/]");
        AnsiConsole.WriteLine();

        // Spectre's SelectionPrompt always highlights the FIRST item — there's no
        // "default selection" API. To honor the user's existing setting (or the
        // baseline 32k for fresh installs), put the preferred value first and let
        // the rest follow in ascending order.
        // Capping the picker at 200k. The 256k tier was misleading — many cloud models
        // advertise 256k but have a practical limit below that once system prompts,
        // tool definitions, and response budget are accounted for. 200k aligns with
        // the largest real-world reliable ceiling. Labels are neutral (no specific
        // model names) so they don't go stale as cloud-model lineups change.
        var allTokens = new[] { 2048, 4096, 8192, 16384, 32768, 65536, 131072, 204800 };
        var preferred = allTokens.Contains(currentMaxTokens) ? currentMaxTokens : 32768;
        var ordered = new[] { preferred }.Concat(allTokens.Where(t => t != preferred)).ToArray();

        var maxTokens = AnsiConsole.Prompt(
            new SelectionPrompt<int>()
                .Title("[cyan]Max response tokens:[/]")
                .AddChoices(ordered)
                .UseConverter(tokens =>
                {
                    var marker = tokens == preferred ? "  ← current" : "";
                    return tokens switch
                    {
                        2048   => $"2k     Quick Q&A, short answers{marker}",
                        4096   => $"4k     General coding assistance{marker}",
                        8192   => $"8k     Multi-component generation, detailed code{marker}",
                        16384  => $"16k    Full-page scaffolding, large file rewrites{marker}",
                        32768  => $"32k    Large multi-file refactors (default){marker}",
                        65536  => $"64k    Very long outputs, larger codebases{marker}",
                        131072 => $"128k   Maximum for many cloud models{marker}",
                        204800 => $"200k   Very long sessions, top of the reliable range{marker}",
                        _      => tokens.ToString()
                    };
                })
        );

        AnsiConsole.MarkupLine($"[green]✓ Max tokens set to: {FormatK(maxTokens)}[/]");
        AnsiConsole.WriteLine();
        return maxTokens;
    }

    private static string FormatK(int tokens) => tokens >= 1024 ? $"{tokens / 1024}k" : tokens.ToString();

    private static int ConfigureRequestTimeout(int currentTimeout)
    {
        AnsiConsole.Write(new Rule("[yellow]5. Per-Request Timeout[/]").LeftJustified());
        AnsiConsole.WriteLine();

        AnsiConsole.MarkupLine("[dim]How long a single chat or plan step can run before it's cut off.[/]");
        AnsiConsole.MarkupLine("[dim]Agentic work with many tool calls may need 15+ minutes.[/]");
        AnsiConsole.MarkupLine("[dim]You can always cancel mid-request with Ctrl+C.[/]");
        AnsiConsole.WriteLine();

        var timeout = AnsiConsole.Prompt(
            new TextPrompt<int>($"[cyan]Timeout in minutes ({MandoCodeConfig.MinRequestTimeoutMinutes}-{MandoCodeConfig.MaxRequestTimeoutMinutes}):[/]")
                .DefaultValue(currentTimeout)
                .Validate(value =>
                {
                    if (MandoCodeConfig.IsValidRequestTimeout(value))
                        return ValidationResult.Success();
                    return ValidationResult.Error($"[red]Timeout must be between {MandoCodeConfig.MinRequestTimeoutMinutes} and {MandoCodeConfig.MaxRequestTimeoutMinutes} minutes[/]");
                })
        );

        AnsiConsole.MarkupLine($"[green]✓ Request timeout set to: {timeout} min[/]");
        AnsiConsole.WriteLine();
        return timeout;
    }

    private static List<string> ConfigureIgnoreDirectories(List<string> currentIgnoreDirectories)
    {
        AnsiConsole.Write(new Rule("[yellow]6. Ignore Directories[/]").LeftJustified());
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
                    currentIgnoreDirectories = new List<string>(MandoCodeConfig.DefaultIgnoreDirectories);
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
        var probe = await OllamaSetupHelper.ProbeAsync(endpoint);
        return probe.Ok;
    }

    private static Task<List<string>> GetAvailableOllamaModels(string endpoint)
        => OllamaSetupHelper.ListModelsAsync(endpoint);

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
        table.AddRow("Max Tokens", FormatK(config.MaxTokens));
        table.AddRow("Request Timeout", $"{config.RequestTimeoutMinutes} min");
        table.AddRow("Ignore Dirs", string.Join(", ", config.IgnoreDirectories.Take(5)) +
                                     (config.IgnoreDirectories.Count > 5 ? "..." : ""));

        AnsiConsole.Write(table);
    }
}
