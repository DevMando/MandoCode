using ArdinCode.Models;
using ArdinCode.Plugins;
using Spectre.Console;
using System.Text.Json;

namespace ArdinCode.Services;

/// <summary>
/// Interactive TUI wizard for configuring ArdinCode.
/// </summary>
public class ConfigurationWizard
{
    // App-standard selection treatment — same black-on-deepskyblue1 as the approval
    // prompts and command autocomplete, so selection reads the same everywhere.
    private static readonly Style SelectionHighlight = new(foreground: Color.Black, background: Color.DeepSkyBlue1);

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
    public static async Task<ArdinCodeConfig> RunAsync(
        ArdinCodeConfig? existingConfig = null,
        Func<string, string, Func<string, string?>?, string?, Task<string>>? promptTextVdom = null)
    {
        var config = existingConfig ?? ArdinCodeConfig.CreateDefault();

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

        // Step 7: Web Search (optional Tavily key — skippable, works without one)
        await ConfigureWebSearchAsync(config);

        // Step 8: Save Configuration
        if (ConfirmSave())
        {
            config.Save();
            AnsiConsole.MarkupLine("\n[green]✓ Configuration saved successfully![/]");
            AnsiConsole.MarkupLine($"[dim]Location: {ArdinCodeConfig.GetDefaultConfigPath()}[/]");
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
                new Markup("[bold deepskyblue1]ArdinCode Configuration Wizard[/]\n[dim]Let's set up your AI coding assistant[/]"),
                VerticalAlignment.Middle))
        {
            Border = BoxBorder.Double,
            BorderStyle = new Style(Color.DeepSkyBlue1)
        };

        AnsiConsole.Write(panel);
        AnsiConsole.WriteLine();
    }

    private static async Task<string> ConfigureOllamaEndpoint(
        string currentEndpoint,
        Func<string, string, Func<string, string?>?, string?, Task<string>>? promptTextVdom = null)
    {
        AnsiConsole.Write(new Rule("[rgb(255,200,80)]1. Ollama Connection[/]").LeftJustified());
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
                new TextPrompt<string>("[deepskyblue1]Ollama endpoint URL:[/]")
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

    private static async Task<ArdinCodeConfig> ConfigureModel(ArdinCodeConfig config)
    {
        AnsiConsole.Write(new Rule("[rgb(255,200,80)]2. Model Selection[/]").LeftJustified());
        AnsiConsole.WriteLine();

        var modelChoice = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[deepskyblue1]How would you like to configure your model?[/]")
                .HighlightStyle(SelectionHighlight)
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
                            .Title("[deepskyblue1]Select a model:[/]")
                            .HighlightStyle(SelectionHighlight)
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
                    config.ModelName = AnsiConsole.Ask<string>("[deepskyblue1]Enter model name:[/]", "minimax-m2.7:cloud");
                }
                break;

            case "Enter model name manually":
                config.ModelName = AnsiConsole.Ask<string>("[deepskyblue1]Enter model name:[/]", config.ModelName ?? "minimax-m2.7:cloud");
                config.ModelPath = null;
                AnsiConsole.MarkupLine($"[green]✓ Model set to: {config.ModelName}[/]");
                break;

            case "Specify local model path (GGUF)":
                config.ModelPath = AnsiConsole.Ask<string>("[deepskyblue1]Enter path to model file:[/]");
                var inferredName = Path.GetFileNameWithoutExtension(config.ModelPath);
                config.ModelName = AnsiConsole.Ask<string>("[deepskyblue1]Model name:[/]", inferredName);
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
        AnsiConsole.Write(new Rule("[rgb(255,200,80)]3. Temperature (Creativity)[/]").LeftJustified());
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
            new TextPrompt<double>("[deepskyblue1]Temperature (0.0-1.0):[/]")
                .DefaultValue(currentTemperature)
                .ValidationErrorMessage("[red]Please enter a number between 0.0 and 1.0[/]")
                .Validate(temp =>
                {
                    if (ArdinCodeConfig.IsValidTemperature(temp))
                        return ValidationResult.Success();
                    return ValidationResult.Error($"[red]Temperature must be between {ArdinCodeConfig.MinTemperature} and {ArdinCodeConfig.MaxTemperature}[/]");
                })
        );

        AnsiConsole.MarkupLine($"[green]✓ Temperature set to: {temperature}[/]");
        AnsiConsole.WriteLine();
        return temperature;
    }

    public static int ConfigureMaxTokens(int currentMaxTokens)
    {
        AnsiConsole.Write(new Rule("[rgb(255,200,80)]4. Maximum Response Tokens[/]").LeftJustified());
        AnsiConsole.WriteLine();

        AnsiConsole.MarkupLine("[dim]This is the upper bound on how long a single reply can be — counted in tokens[/]");
        AnsiConsole.MarkupLine("[dim](roughly 4 characters or ¾ of a word each). If a reply hits the limit, it gets[/]");
        AnsiConsole.MarkupLine("[dim]cut off mid-sentence and you'll see a warning.[/]");
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[dim]This is NOT the context window — it only caps a single reply. Long tasks[/]");
        AnsiConsole.MarkupLine("[dim]happen as many tool calls, so most replies never come close to the cap.[/]");
        AnsiConsole.MarkupLine("[dim]On local models a reply can never exceed the context window anyway.[/]");
        AnsiConsole.WriteLine();

        // Spectre's SelectionPrompt always highlights the FIRST item — there's no
        // "default selection" API. To honor the user's existing setting (or the
        // baseline 32k for fresh installs), put the preferred value first and let
        // the rest follow in ascending order.
        // Tiers top out at 64k: these are RESPONSE-length caps, not context windows.
        // The old 128k/200k tiers were context-window numbers that made no sense as
        // single-reply lengths — no agent reply is 200k tokens, and agentic work is
        // chunked across tool calls by design.
        var allTokens = new[] { 2048, 4096, 8192, 16384, 32768, 65536 };
        var preferred = allTokens.Contains(currentMaxTokens) ? currentMaxTokens : 32768;
        var ordered = new[] { preferred }.Concat(allTokens.Where(t => t != preferred)).ToArray();

        var maxTokens = AnsiConsole.Prompt(
            new SelectionPrompt<int>()
                .Title("[deepskyblue1]Max response tokens:[/]")
                .HighlightStyle(SelectionHighlight)
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
                        65536  => $"64k    Huge single-file generation on cloud models{marker}",
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
        AnsiConsole.Write(new Rule("[rgb(255,200,80)]5. Per-Request Timeout[/]").LeftJustified());
        AnsiConsole.WriteLine();

        AnsiConsole.MarkupLine("[dim]How long a single chat or plan step can run before it's cut off.[/]");
        AnsiConsole.MarkupLine("[dim]Agentic work with many tool calls may need 15+ minutes.[/]");
        AnsiConsole.MarkupLine("[dim]You can always cancel mid-request with Ctrl+C.[/]");
        AnsiConsole.WriteLine();

        var timeout = AnsiConsole.Prompt(
            new TextPrompt<int>($"[deepskyblue1]Timeout in minutes ({ArdinCodeConfig.MinRequestTimeoutMinutes}-{ArdinCodeConfig.MaxRequestTimeoutMinutes}):[/]")
                .DefaultValue(currentTimeout)
                .Validate(value =>
                {
                    if (ArdinCodeConfig.IsValidRequestTimeout(value))
                        return ValidationResult.Success();
                    return ValidationResult.Error($"[red]Timeout must be between {ArdinCodeConfig.MinRequestTimeoutMinutes} and {ArdinCodeConfig.MaxRequestTimeoutMinutes} minutes[/]");
                })
        );

        AnsiConsole.MarkupLine($"[green]✓ Request timeout set to: {timeout} min[/]");
        AnsiConsole.WriteLine();
        return timeout;
    }

    private static List<string> ConfigureIgnoreDirectories(List<string> currentIgnoreDirectories)
    {
        AnsiConsole.Write(new Rule("[rgb(255,200,80)]6. Ignore Directories[/]").LeftJustified());
        AnsiConsole.WriteLine();

        AnsiConsole.MarkupLine("[dim]Current ignore list:[/]");
        foreach (var dir in currentIgnoreDirectories)
        {
            AnsiConsole.MarkupLine($"[dim]  • {dir}[/]");
        }
        AnsiConsole.WriteLine();

        var modify = AnsiConsole.Confirm("[deepskyblue1]Modify ignore directories?[/]", false);

        if (modify)
        {
            var choice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("[deepskyblue1]What would you like to do?[/]")
                    .HighlightStyle(SelectionHighlight)
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
                    var newDir = AnsiConsole.Ask<string>("[deepskyblue1]Directory name to ignore:[/]");
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
                    currentIgnoreDirectories = new List<string>(ArdinCodeConfig.DefaultIgnoreDirectories);
                    AnsiConsole.MarkupLine("[green]✓ Reset to defaults[/]");
                    break;
            }
        }

        AnsiConsole.WriteLine();
        return currentIgnoreDirectories;
    }

    /// <summary>
    /// Optional Tavily key for web search. Deliberately skippable with a default of
    /// "no": search works keyless via DuckDuckGo, and a user who hasn't hit DuckDuckGo's
    /// rate-limiting yet has no reason to want a key — the in-context teaching happens
    /// at the moment a search actually gets blocked (see WebSearchPlugin).
    /// </summary>
    private static async Task ConfigureWebSearchAsync(ArdinCodeConfig config)
    {
        AnsiConsole.Write(new Rule("[rgb(255,200,80)]7. Web Search[/]").LeftJustified());
        AnsiConsole.WriteLine();

        AnsiConsole.MarkupLine("[dim]Web search works out of the box via DuckDuckGo — no key needed. But DuckDuckGo's[/]");
        AnsiConsole.MarkupLine("[dim]free endpoint rate-limits and temporarily blocks IPs, so searches can randomly fail.[/]");
        AnsiConsole.MarkupLine("[dim]For reliable, AI-optimized results you can add a Tavily API key — free tier of about[/]");
        AnsiConsole.MarkupLine("[dim]1,000 searches/month at https://app.tavily.com. The key is stored locally in[/]");
        AnsiConsole.MarkupLine("[dim]config.json and only ever sent to Tavily; DuckDuckGo stays as the fallback.[/]");
        AnsiConsole.WriteLine();

        if (!string.IsNullOrWhiteSpace(config.TavilyApiKey))
        {
            var choice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title($"[deepskyblue1]Tavily key configured ({ArdinCodeConfig.MaskApiKey(config.TavilyApiKey)}). What would you like to do?[/]")
                    .HighlightStyle(SelectionHighlight)
                    .AddChoices("Keep current key", "Replace key", "Remove key"));

            if (choice == "Keep current key")
            {
                AnsiConsole.WriteLine();
                return;
            }
            if (choice == "Remove key")
            {
                config.TavilyApiKey = null;
                AnsiConsole.MarkupLine("[rgb(255,200,80)]✓ Tavily key removed — web search falls back to DuckDuckGo[/]");
                AnsiConsole.WriteLine();
                return;
            }
        }
        else if (!AnsiConsole.Confirm("[deepskyblue1]Add a Tavily API key now?[/]", false))
        {
            AnsiConsole.MarkupLine("[dim]Skipped — DuckDuckGo will be used. Add a key anytime: /config set tavilyKey <key>[/]");
            AnsiConsole.WriteLine();
            return;
        }

        var key = AnsiConsole.Prompt(
            new TextPrompt<string>("[deepskyblue1]Tavily API key (starts with tvly-; Enter to skip):[/]")
                .Secret('*')
                .AllowEmpty());

        if (string.IsNullOrWhiteSpace(key))
        {
            AnsiConsole.MarkupLine("[dim]Skipped — no key entered. Add one anytime: /config set tavilyKey <key>[/]");
            AnsiConsole.WriteLine();
            return;
        }

        config.TavilyApiKey = key.Trim();

        // Verify immediately — instant feedback at configuration time is the single
        // biggest trust win for an optional key. The key stays set even if the probe
        // fails (the user may be offline); the message says so.
        string verification = "";
        await AnsiConsole.Status()
            .Spinner(LoadingMessages.GetRandomSpinner())
            .StartAsync("[yellow]Verifying key with Tavily...[/]", async _ =>
            {
                verification = await WebSearchPlugin.ValidateTavilyKeyAsync(key.Trim());
            });
        AnsiConsole.WriteLine(verification);
        AnsiConsole.WriteLine();
    }

    private static bool ConfirmSave()
    {
        AnsiConsole.Write(new Rule("[rgb(255,200,80)]8. Save Configuration[/]").LeftJustified());
        AnsiConsole.WriteLine();

        return AnsiConsole.Confirm("[deepskyblue1]Save this configuration?[/]", true);
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
    public static void DisplayConfigSummary(ArdinCodeConfig config)
    {
        var table = new Table()
        {
            Border = TableBorder.Rounded,
            BorderStyle = new Style(Color.DeepSkyBlue1)
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
