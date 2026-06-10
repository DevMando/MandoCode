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
        AnsiConsole.Write(new Rule("[deepskyblue1]What are Open-Weight LLMs?[/]").LeftJustified());
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Panel(
            new Rows(
                new Markup("Large language models with [bold]publicly available weights[/] that run on your own machine."),
                new Markup("Unlike cloud AI (ChatGPT, Claude), they are [green]free[/], [green]private[/], and [green]offline[/]."),
                new Markup(""),
                new Markup("Open-weight models give you full control — no API keys, no usage limits."),
                new Markup("Run locally or use Ollama cloud. Your code, your choice.")
            ))
        {
            Border = BoxBorder.Rounded,
            BorderStyle = new Style(Color.DeepSkyBlue1)
        });
        AnsiConsole.WriteLine();

        // Section 2: Understanding Model Sizes & Hardware
        AnsiConsole.Write(new Rule("[deepskyblue1]Understanding Model Sizes & Hardware[/]").LeftJustified());
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
            BorderStyle = new Style(Color.DeepSkyBlue1)
        });
        AnsiConsole.WriteLine();

        // Section 2.5: The Context Window
        AnsiConsole.Write(new Rule("[deepskyblue1]The Context Window (the model's desk)[/]").LeftJustified());
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Panel(
            new Rows(
                new Markup("The [bold]context window[/] is how much conversation + code the model can see at once."),
                new Markup("Think of it as the model's desk: a bigger desk holds more of your project in view,"),
                new Markup("but takes more GPU memory (the [dim]KV cache[/] grows with the window)."),
                new Markup(""),
                new Markup("  • When the desk fills up, the [bold]oldest content silently falls off[/] — the model"),
                new Markup("    \"forgets\" instructions and files it saw earlier. More space = fewer surprises."),
                new Markup("  • Rough cost: [yellow]each 8k tokens of window ≈ 0.5-1.5 GB VRAM[/], varies by model."),
                new Markup("  • MandoCode sizes it automatically when you pick a model tier in /setup —"),
                new Markup("    but this only applies when MandoCode starts the Ollama daemon itself."),
                new Markup("  • [bold]Ollama desktop app users:[/] the app controls this directly — drag"),
                new Markup("    [deepskyblue1]Settings → Context length[/] (it defaults to a small 4k!)."),
                new Markup("  • Tune MandoCode's value with: [deepskyblue1]mandocode --config set contextLength 16384[/]"),
                new Markup("  • [green]Cloud models[/] manage this on Ollama's servers — nothing to configure."),
                new Markup("  • Advanced: [dim]OLLAMA_KV_CACHE_TYPE=q8_0[/] compresses the cache, roughly doubling"),
                new Markup("    the window your VRAM can hold at a tiny quality cost.")
            ))
        {
            Border = BoxBorder.Rounded,
            BorderStyle = new Style(Color.DeepSkyBlue1)
        });
        AnsiConsole.WriteLine();

        // Section 3: Cloud vs Local Models
        AnsiConsole.Write(new Rule("[deepskyblue1]Cloud vs Local Models[/]").LeftJustified());
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Panel(
            new Rows(
                new Markup("Ollama supports [bold]two types[/] of models:"),
                new Markup(""),
                new Markup("  [green]Cloud models[/]  Run remotely — [bold]no GPU needed[/] on your machine."),
                new Markup("                 Examples: kimi-k2.5:cloud, minimax-m2.7:cloud, qwen3-coder:480b-cloud"),
                new Markup(""),
                new Markup("  [yellow]Local models[/]  Run on your hardware — fully offline and private."),
                new Markup("                 Requires enough VRAM for the model size (see above).")
            ))
        {
            Border = BoxBorder.Rounded,
            BorderStyle = new Style(Color.DeepSkyBlue1)
        });
        AnsiConsole.WriteLine();

        // Section 4: Recommended Models
        AnsiConsole.Write(new Rule("[deepskyblue1]Recommended Models[/]").LeftJustified());
        AnsiConsole.WriteLine();

        var table = new Table()
        {
            Border = TableBorder.Rounded
        };
        table.AddColumn(new TableColumn("[bold]Type[/]").Centered());
        table.AddColumn(new TableColumn("[bold]Model[/]"));
        table.AddColumn(new TableColumn("[bold]Notes[/]"));

        table.AddRow("[green]Cloud[/]", "kimi-k2.5:cloud", "No GPU required");
        table.AddRow("[green]Cloud[/]", "minimax-m2.7:cloud", "No GPU required");
        table.AddRow("[green]Cloud[/]", "qwen3-coder:480b-cloud", "No GPU required, code-focused");
        table.AddRow("[yellow]Local[/]", "[bold]qwen3:8b[/]", "Recommended — good balance of speed & quality");
        table.AddRow("[yellow]Local[/]", "qwen2.5-coder:7b", "Code-focused, ~5-6 GB VRAM");
        table.AddRow("[yellow]Local[/]", "mistral", "General purpose, ~5 GB VRAM");
        table.AddRow("[yellow]Local[/]", "llama3.1", "Meta's model, ~5-6 GB VRAM");

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();

        // Section 5: Getting Started
        AnsiConsole.Write(new Rule("[deepskyblue1]Getting Started[/]").LeftJustified());
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Panel(
            new Rows(
                new Markup("[bold]Step 1:[/] Install Ollama from [link]https://ollama.com[/]"),
                new Markup("[bold]Step 2:[/] Open a terminal and run [deepskyblue1]ollama serve[/]"),
                new Markup("[bold]Step 3:[/] Pull a model: [deepskyblue1]ollama pull qwen3:8b[/]"),
                new Markup("[bold]Step 4:[/] Come back to MandoCode — run [deepskyblue1]/setup[/] for a guided walkthrough or [deepskyblue1]/config[/] to pick the model directly")
            ))
        {
            Border = BoxBorder.Rounded,
            BorderStyle = new Style(Color.Green),
            Header = new PanelHeader("[green] Quick Start [/]")
        });
        AnsiConsole.WriteLine();
    }
}
