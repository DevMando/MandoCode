using MandoCode.Models;
using Spectre.Console;

namespace MandoCode.Services;

public static class CliReferenceCommands
{
    public static void ShowHelp(MandoCodeConfig config)
    {
        TuiConsole.MarkupLine("[deepskyblue1]Available commands[/]");
        foreach (var entry in SlashCommands.All.Where(entry => SlashCommands.IsAvailable(entry.Key, config)))
            TuiConsole.MarkupLine($"[deepskyblue1]{Markup.Escape(entry.Key)}[/]  {Markup.Escape(entry.Value)}");
    }

    public static void ShowKeybindings()
    {
        foreach (var group in Keybindings.All.GroupBy(binding => binding.Group))
        {
            var table = new Table { Border = TableBorder.Rounded, Title = new TableTitle(group.Key) };
            table.AddColumn("Keys");
            table.AddColumn("Action");
            foreach (var binding in group) table.AddRow(Markup.Escape(binding.Keys), Markup.Escape(binding.Action));
            TuiConsole.Write(table);
            TuiConsole.WriteLine();
        }
        TuiConsole.WriteLine("Alt is the default modifier. Windows/Command variants work only when the terminal forwards them.");
    }

    public static async Task SaveTranscriptAsync(TuiSession? session, string agent, string project, string model, string argument)
    {
        try
        {
            if (session is null) throw new IOException("The conversation is not available to save.");
            var path = await TranscriptExport.SaveAsync(session, agent, project, model, argument);
            TuiConsole.MarkupLine($"[green]Transcript saved to {Markup.Escape(path)}[/]");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            TuiConsole.MarkupLine($"[yellow]{Markup.Escape("Couldn't save transcript: " + ex.Message)}[/]");
        }
    }
}
