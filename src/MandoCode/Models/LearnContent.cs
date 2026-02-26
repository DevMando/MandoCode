using Spectre.Console;

namespace MandoCode.Models;

/// <summary>
/// Static educational content for the /learn command.
/// Renders LLM education and setup guidance using Spectre.Console widgets.
/// </summary>
public static class LearnContent
{
    /// <summary>
    /// Displays the full educational guide about local LLMs and setup instructions.
    /// </summary>
    public static void Display()
    {
        AnsiConsole.WriteLine();

        // Section 1: What are Open-Weight LLMs?
        AnsiConsole.Write(new Rule("[cyan]What are Open-Weight LLMs?[/]").LeftJustified());
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Panel(
            new Rows(
                new Markup("Large language models with [bold]publicly available weights[/] that run on your own machine."),
                new Markup("Unlike cloud AI (ChatGPT, Claude), they are [green]free[/], [green]private[/], and [green]offline[/]."),
                new Markup(""),
                new Markup("Open-weight models give you full control — no API keys, no usage limits,"),
                new Markup("no data leaving your computer. Your code stays on your machine.")
            ))
        {
            Border = BoxBorder.Rounded,
            BorderStyle = new Style(Color.Cyan)
        });
        AnsiConsole.WriteLine();

        // Section 2: Understanding Model Sizes & Hardware
        AnsiConsole.Write(new Rule("[cyan]Understanding Model Sizes & Hardware[/]").LeftJustified());
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Panel(
            new Rows(
                new Markup("[bold]Parameters[/] = the model's \"brain size\". More parameters = smarter but heavier."),
                new Markup("[bold]Quantization[/] (Q4, Q8) shrinks models at slight quality cost."),
                new Markup(""),
                new Markup("  [yellow]~4B params[/]   -> [dim]~3 GB VRAM[/]    Runs on most laptops"),
                new Markup("  [yellow]~7-8B params[/] -> [dim]~5-6 GB VRAM[/]  Good sweet spot"),
                new Markup("  [yellow]~14B params[/]  -> [dim]~10-12 GB VRAM[/] Needs a dedicated GPU"),
                new Markup("  [yellow]~30B+ params[/] -> [dim]~20 GB+ VRAM[/]  High-end GPUs only")
            ))
        {
            Border = BoxBorder.Rounded,
            BorderStyle = new Style(Color.Cyan)
        });
        AnsiConsole.WriteLine();

        // Section 3: Cloud vs Local Models
        AnsiConsole.Write(new Rule("[cyan]Cloud vs Local Models[/]").LeftJustified());
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Panel(
            new Rows(
                new Markup("Ollama supports [bold]two types[/] of models:"),
                new Markup(""),
                new Markup("  [green]Cloud models[/]  Run remotely — [bold]no GPU needed[/] on your machine."),
                new Markup("                 Examples: kimi-k2.5:cloud, minimax-m2.5:cloud, qwen3-coder:480b-cloud"),
                new Markup(""),
                new Markup("  [yellow]Local models[/]  Run on your hardware — fully offline and private."),
                new Markup("                 Requires enough VRAM for the model size (see above).")
            ))
        {
            Border = BoxBorder.Rounded,
            BorderStyle = new Style(Color.Cyan)
        });
        AnsiConsole.WriteLine();

        // Section 4: Recommended Models
        AnsiConsole.Write(new Rule("[cyan]Recommended Models[/]").LeftJustified());
        AnsiConsole.WriteLine();

        var table = new Table()
        {
            Border = TableBorder.Rounded
        };
        table.AddColumn(new TableColumn("[bold]Type[/]").Centered());
        table.AddColumn(new TableColumn("[bold]Model[/]"));
        table.AddColumn(new TableColumn("[bold]Notes[/]"));

        table.AddRow("[green]Cloud[/]", "kimi-k2.5:cloud", "No GPU required");
        table.AddRow("[green]Cloud[/]", "minimax-m2.5:cloud", "No GPU required");
        table.AddRow("[green]Cloud[/]", "qwen3-coder:480b-cloud", "No GPU required, code-focused");
        table.AddRow("[yellow]Local[/]", "[bold]qwen3:8b[/]", "Recommended — good balance of speed & quality");
        table.AddRow("[yellow]Local[/]", "qwen2.5-coder:7b", "Code-focused, ~5-6 GB VRAM");
        table.AddRow("[yellow]Local[/]", "mistral", "General purpose, ~5 GB VRAM");
        table.AddRow("[yellow]Local[/]", "llama3.1", "Meta's model, ~5-6 GB VRAM");

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();

        // Section 5: Getting Started
        AnsiConsole.Write(new Rule("[cyan]Getting Started[/]").LeftJustified());
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Panel(
            new Rows(
                new Markup("[bold]Step 1:[/] Install Ollama from [link]https://ollama.com[/]"),
                new Markup("[bold]Step 2:[/] Open a terminal and run [cyan]ollama serve[/]"),
                new Markup("[bold]Step 3:[/] Pull a model: [cyan]ollama pull qwen3:8b[/]"),
                new Markup("[bold]Step 4:[/] Come back to MandoCode and run [cyan]/config[/] to configure")
            ))
        {
            Border = BoxBorder.Rounded,
            BorderStyle = new Style(Color.Green),
            Header = new PanelHeader("[green] Quick Start [/]")
        });
        AnsiConsole.WriteLine();
    }
}
