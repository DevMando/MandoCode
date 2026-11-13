using System.Text;
using MandoCode.Services;
using RazorConsole.Core;
using RazorConsole.Core.Rendering;
using Spectre.Console;
using Xunit;

namespace MandoCode.Tests;

public sealed class FrameBufferedOutputTests
{
    [Fact]
    public void FragmentedFrameIsDeliveredTogetherDespiteIntermediateFlushes()
    {
        var terminal = new CountingWriter();
        using var writer = new FrameBufferedOutput.FrameWriter(terminal);
        var frame = FrameBufferedOutput.FrameWriter.Begin + "\u001b[Hrow one\u001b[2;1Hrow two" + FrameBufferedOutput.FrameWriter.End;
        foreach (var ch in frame[..^1]) { writer.Write(ch); writer.Flush(); }
        Assert.Empty(terminal.Writes);
        writer.Write(frame[^1]);
        Assert.Equal(frame, Assert.Single(terminal.Writes));
        writer.Write("outside frame");
        Assert.Equal("outside frame", terminal.Writes[1]);
    }

    [Fact]
    public void ShutdownCompletesAnInterruptedFrameWithoutClosingTerminal()
    {
        var terminal = new CountingWriter();
        using (var writer = new FrameBufferedOutput.FrameWriter(terminal))
            writer.Write(FrameBufferedOutput.FrameWriter.Begin + "partial content");
        Assert.Equal(FrameBufferedOutput.FrameWriter.Begin + "partial content" + FrameBufferedOutput.FrameWriter.End, Assert.Single(terminal.Writes));
        terminal.Write("still usable");
        Assert.Equal(2, terminal.Writes.Count);
    }

    [Fact]
    public void LargeFramesDoNotBufferWithoutLimit()
    {
        var terminal = new CountingWriter();
        using var writer = new FrameBufferedOutput.FrameWriter(terminal);
        writer.Write(FrameBufferedOutput.FrameWriter.Begin);
        writer.Write(new string('x', 1024 * 1024));
        Assert.Single(terminal.Writes);
        writer.Write(FrameBufferedOutput.FrameWriter.End);
        Assert.Equal(FrameBufferedOutput.FrameWriter.Begin + new string('x', 1024 * 1024) + FrameBufferedOutput.FrameWriter.End, string.Concat(terminal.Writes));
    }

    [Fact]
    public void ActualLiveCanvasDeliversLargeToBlankTransitionInOneWrite()
    {
        var terminal = new CountingWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(terminal), Ansi = AnsiSupport.Yes });
        console.Profile.Width = 100;
        console.Profile.Height = 40;
        console.Profile.Capabilities.Ansi = true;
        var original = console.Profile.Out;
        var canvasType = typeof(ConsoleAppOptions).Assembly.GetType("RazorConsole.Core.LiveDisplayCanvas")!;
        using var canvas = (IDisposable)Activator.CreateInstance(canvasType, new ConsoleLiveDisplayOptions
        {
            EnableSynchronizedUpdates = true,
            UseAlternateScreenBuffer = true
        }, console)!;
        var update = canvasType.GetMethod("UpdateTarget")!;
        var occupied = new Text(string.Join('\n', Enumerable.Range(0, 38).Select(i => $"Occupied pane row {i}")));
        update.Invoke(canvas, [occupied]);
        terminal.Writes.Clear();
        update.Invoke(canvas, [new Text("")]);
        Assert.True(terminal.Writes.Count > 1);
        var unbufferedFrame = string.Concat(terminal.Writes);
        update.Invoke(canvas, [occupied]);
        using (FrameBufferedOutput.Attach(console))
        {
            terminal.Writes.Clear();
            update.Invoke(canvas, [new Text("")]);
            var frame = Assert.Single(terminal.Writes);
            Assert.StartsWith(FrameBufferedOutput.FrameWriter.Begin, frame);
            Assert.EndsWith(FrameBufferedOutput.FrameWriter.End, frame);
            Assert.Contains("\u001b[", frame);
            Assert.Equal(unbufferedFrame, frame);
        }
        Assert.Same(original, console.Profile.Out);
    }

    private sealed class CountingWriter : StringWriter
    {
        public List<string> Writes { get; } = [];
        public override void Write(string? value)
        {
            if (!string.IsNullOrEmpty(value)) Writes.Add(value);
            base.Write(value);
        }
        public override Encoding Encoding => Encoding.UTF8;
    }
}
