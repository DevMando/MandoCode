using Spectre.Console;

namespace ArdinCode.Models;

/// <summary>
/// Static educational content for the /learn command.
/// Renders LLM education and configuration guidance using Spectre.Console widgets.
/// </summary>
public static class LearnContent
{
    /// <summary>
    /// Displays the full educational guide about AI Providers and configuration.
    /// </summary>
    public static void Display()
    {
        AnsiConsole.WriteLine();

        // Section 1: What are OpenAI-Compatible Providers?
        AnsiConsole.Write(new Rule("[deepskyblue1]What are OpenAI-Compatible Providers?[/]").LeftJustified());
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Panel(
            new Rows(
                new Markup("ArdinCode connects to any AI Provider that exposes an [bold]OpenAI-compatible API[/]."),
                new Markup("This includes cloud providers (like OpenAI, Anthropic via proxies, AvalAI, etc.)"),
                new Markup("as well as local model servers (like LM Studio, llama.cpp, LocalAI)."),
                new Markup(""),
                new Markup("To configure, you only need to provide the [deepskyblue1]API Endpoint URL[/] and an [deepskyblue1]API Key[/].")
            ))
        {
            Border = BoxBorder.Rounded,
            BorderStyle = new Style(Color.DeepSkyBlue1)
        });
        AnsiConsole.WriteLine();

        // Section 2: AI Provider Settings
        AnsiConsole.Write(new Rule("[deepskyblue1]AI Provider Settings[/]").LeftJustified());
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Panel(
            new Rows(
                new Markup("[bold]API Endpoint[/] -> The base URL of the API (e.g. `https://api.avalai.ir/v1` or `http://localhost:1234/v1`)."),
                new Markup("[bold]API Key[/]      -> Your authentication token (left empty for local servers that don't require one)."),
                new Markup("[bold]Model Name[/]   -> The identifier of the model you want to use (e.g. `gpt-4o-mini`, `qwen2.5-coder`)."),
                new Markup(""),
                new Markup("Tune settings anytime using: [deepskyblue1]/config[/] or inline using [deepskyblue1]/config set <key> <value>[/].")
            ))
        {
            Border = BoxBorder.Rounded,
            BorderStyle = new Style(Color.DeepSkyBlue1)
        });
        AnsiConsole.WriteLine();

        // Section 3: Getting Started
        AnsiConsole.Write(new Rule("[deepskyblue1]Getting Started[/]").LeftJustified());
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Panel(
            new Rows(
                new Markup("[bold]Step 1:[/] Run [deepskyblue1]/setup[/] to launch the interactive configuration wizard."),
                new Markup("[bold]Step 2:[/] Enter the API Endpoint URL and your API Key (if required)."),
                new Markup("[bold]Step 3:[/] Select your preferred model from the fetched list (or type the name manually)."),
                new Markup("[bold]Step 4:[/] You're ready! Start asking questions, refactoring, or building projects.")
            ))
        {
            Border = BoxBorder.Rounded,
            BorderStyle = new Style(Color.Green),
            Header = new PanelHeader("[green] Quick Start [/]")
        });
        AnsiConsole.WriteLine();
    }
}
