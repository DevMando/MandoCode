using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Components.Web;
using MandoCode.Models;
using MandoCode.Services;
using Spectre.Console;
using System.Text.RegularExpressions;

namespace MandoCode.Components;

public partial class App
{
    private void HandleRabbitCommand()
    {
        // Katakana-style characters for the digital rain
        var rainChars = "ﾊﾐﾋｰｳｼﾅﾓﾆｻﾜﾂｵﾘｱﾎﾃﾏｹﾒｴｶｷﾑﾕﾗｾﾈｽﾀﾇﾍ012345789:・.\"=*+-<>¦╌ꜝ";
        var rng = Random.Shared;
        var width = Math.Min(Console.WindowWidth, 120);
        var height = Math.Min(Console.WindowHeight - 2, 24);

        // Each column has a drop position (head of the rain streak)
        var drops = new int[width];
        for (int i = 0; i < width; i++)
            drops[i] = rng.Next(-height, 0); // stagger start positions

        Console.CursorVisible = false;
        TuiConsole.Clear();

        // Phase 1: Digital rain (~14 frames)
        for (int frame = 0; frame < 14; frame++)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("\u001b[H"); // cursor home

            for (int row = 0; row < height; row++)
            {
                for (int col = 0; col < width; col++)
                {
                    int dropPos = drops[col];
                    if (row == dropPos)
                    {
                        // Bright head of the streak
                        sb.Append($"\u001b[97m{rainChars[rng.Next(rainChars.Length)]}\u001b[0m");
                    }
                    else if (row < dropPos && row > dropPos - 6)
                    {
                        // Green trail
                        var fade = dropPos - row;
                        var intensity = fade <= 2 ? "92" : fade <= 4 ? "32" : "2;32";
                        sb.Append($"\u001b[{intensity}m{rainChars[rng.Next(rainChars.Length)]}\u001b[0m");
                    }
                    else
                    {
                        sb.Append(' ');
                    }
                }
                if (row < height - 1) sb.Append('\n');
            }
            Console.Write(sb.ToString());

            // Advance drops
            for (int i = 0; i < width; i++)
            {
                drops[i] += rng.Next(1, 3);
                if (drops[i] > height + 6)
                    drops[i] = rng.Next(-4, 0);
            }

            Thread.Sleep(80);
        }

        // Phase 2: Fade to black, then reveal the rabbit
        TuiConsole.Clear();
        Thread.Sleep(300);

        var centerY = height / 2 - 1;
        var rabbit = "🐇";
        var quote = "Follow the white rabbit.";
        var rabbitX = Math.Max(0, (width - 2) / 2);
        var quoteX = Math.Max(0, (width - quote.Length) / 2);

        // Position and print rabbit
        Console.SetCursorPosition(rabbitX, centerY);
        Console.Write(rabbit);

        Thread.Sleep(400);

        // Print quote in green below rabbit
        Console.SetCursorPosition(quoteX, centerY + 2);
        Console.Write($"\u001b[32m{quote}\u001b[0m");

        Thread.Sleep(1500);

        // Clear and return
        TuiConsole.Clear();
        Console.CursorVisible = true;
    }

    private static readonly string[] _matrixTriggers =
    {
        "what is the matrix",
        "red pill",
        "follow the white rabbit",
        "there is no spoon"
    };

    private static bool ContainsMatrixTrigger(string input)
    {
        var lower = input.ToLowerInvariant();
        foreach (var trigger in _matrixTriggers)
        {
            if (lower.Contains(trigger))
                return true;
        }
        return false;
    }

    private static void PlayMatrixMoment(string input)
    {
        var lower = input.ToLowerInvariant();
        TuiConsole.WriteLine();

        if (lower.Contains("what is the matrix"))
        {
            TuiConsole.MarkupLine("[green]Unfortunately, no one can be told what the Matrix is. You have to see it for yourself.[/]");
            Thread.Sleep(1500);
        }
        else if (lower.Contains("there is no spoon"))
        {
            TuiConsole.MarkupLine("[green]Then you'll see that it is not the spoon that bends, it is only yourself.[/]");
            Thread.Sleep(1500);
        }
        else if (lower.Contains("red pill"))
        {
            TuiConsole.MarkupLine("[green]Remember, all I'm offering is the truth. Nothing more.[/]");
            Thread.Sleep(1500);
        }
        else if (lower.Contains("follow the white rabbit"))
        {
            // Small rabbit hopping across the screen
            var width = Math.Min(Console.WindowWidth - 4, 60);
            Console.CursorVisible = false;
            for (int i = 0; i < width; i += 3)
            {
                Console.SetCursorPosition(i, Console.CursorTop);
                Console.Write("  🐇");
                Thread.Sleep(80);
                Console.SetCursorPosition(i, Console.CursorTop);
                Console.Write("    ");
            }
            Console.SetCursorPosition(0, Console.CursorTop);
            Console.CursorVisible = true;
        }

        TuiConsole.WriteLine();
    }

    private void HandleMusicCommand(string action)
    {
        // Stop visualizer before rendering status panels to avoid flicker
        StopMusicVisualizer();

        // Parse volume from "music-vol 70" style commands
        if (action.StartsWith("music-vol"))
        {
            var parts = action.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && int.TryParse(parts[1], out var vol))
            {
                vol = Math.Clamp(vol, 0, 100);
                MusicPlayer.SetVolume(vol / 100f);
                MusicPlayerUI.RenderStatus(MusicPlayer);
            }
            else
            {
                Console.WriteLine();
                Console.WriteLine("  Usage: /music-vol [0-100]");
                Console.WriteLine("  Example: /music-vol 70");
                Console.WriteLine();
            }
            // Restart visualizer after panel render
            StartMusicVisualizer();
            return;
        }

        switch (action)
        {
            case "play":
                MusicPlayer.Play();
                MusicPlayerUI.RenderStatus(MusicPlayer);
                break;

            case "stop":
                MusicPlayer.Stop();
                MusicPlayerUI.RenderStatus(MusicPlayer);
                break;

            case "pause":
                MusicPlayer.TogglePause();
                MusicPlayerUI.RenderStatus(MusicPlayer);
                break;

            case "next":
                MusicPlayer.NextTrack();
                MusicPlayerUI.RenderStatus(MusicPlayer);
                break;

            case "list":
                MusicPlayerUI.RenderTrackList(MusicPlayer);
                break;
        }

        // Restart visualizer — it will auto-check if music is still active
        StartMusicVisualizer();
    }

    private void HandleMusicPlaySelection()
    {
        StopMusicVisualizer();

        var genres = MusicPlayer.GetAvailableGenres();

        if (genres.Count == 0)
        {
            TuiConsole.WriteLine();
            TuiConsole.MarkupLine("[yellow]No genres found.[/] Add mp3 files to [deepskyblue1]Audio/<genre>/[/] folders.");
            TuiConsole.WriteLine();
            StartMusicVisualizer();
            return;
        }

        if (genres.Count == 1)
        {
            MusicPlayer.Play(genres[0]);
            MusicPlayerUI.RenderStatus(MusicPlayer);
            StartMusicVisualizer();
            return;
        }

        // Build choices: title-cased genre names + Shuffle All
        var choices = genres
            .Select(g => char.ToUpper(g[0]) + g[1..])
            .ToList();
        choices.Insert(0, "Shuffle All");

        TuiConsole.WriteLine();
        var selected = TuiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("[deepskyblue1]Select a genre:[/]")
                .HighlightStyle(SelectionHighlight)
                .AddChoices(choices)
        );

        if (selected == "Shuffle All")
        {
            MusicPlayer.Play(null);
        }
        else
        {
            MusicPlayer.Play(selected.ToLowerInvariant());
        }

        MusicPlayerUI.RenderStatus(MusicPlayer);
        StartMusicVisualizer();
    }

}
