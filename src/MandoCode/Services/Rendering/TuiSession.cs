using System.Text;
using System.Text.RegularExpressions;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace MandoCode.Services;

/// <summary>Ordered, session-local output and transient agent state for the component TUI.</summary>
public sealed class TuiSession(TimeProvider? timeProvider = null)
{
    private readonly object _sync = new();
    private readonly List<TuiEntry> _entries = new();
    private readonly Dictionary<long, IRenderable> _registered = new();
    private static long _nextId;
    private long _revision;
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private DateTimeOffset _startedAt;
    private DateTimeOffset _verbChangedAt;
    private Spinner _spinner = Spinner.Known.Dots;
    private string _loadingMessage = "";
    private TimeSpan _elapsed;
    private bool _running;
    private string _activity = "";
    private string _preview = "";
    public long Revision => Interlocked.Read(ref _revision);
    public TuiSnapshot Snapshot()
    {
        lock (_sync) return new(_entries.ToArray(), _running, _activity, _preview) { Spinner = _spinner, LoadingMessage = _loadingMessage, Elapsed = _elapsed };
    }
    public void Append(IRenderable content)
    {
        if (content is MandoCode.Translators.AnsiPassthroughRenderable ansi) content = AnsiTranscriptText.Parse(ansi.Content);
        lock (_sync) { _entries.Add(new(Interlocked.Increment(ref _nextId), content)); Interlocked.Increment(ref _revision); }
    }
    public void AppendUserPrompt(string prompt)
    {
        lock (_sync)
        {
            _entries.Add(new(Interlocked.Increment(ref _nextId), new Text("> " + prompt, new Style(new Color(255, 200, 80)))) { UserPrompt = prompt });
            Interlocked.Increment(ref _revision);
        }
    }
    public void AppendSpaced(IRenderable content)
    {
        lock (_sync)
        {
            _entries.Add(new(Interlocked.Increment(ref _nextId), content) { SpaceAfter = true });
            Interlocked.Increment(ref _revision);
        }
    }
    public IRenderable? Find(long id)
    {
        lock (_sync) return _registered.GetValueOrDefault(id) ?? _entries.FirstOrDefault(entry => entry.Id == id)?.Content;
    }
    public long Register(IRenderable content)
    {
        lock (_sync) { var id = Interlocked.Increment(ref _nextId); _registered[id] = content; return id; }
    }
    public void Unregister(long id) { lock (_sync) _registered.Remove(id); }
    public void Clear()
    {
        lock (_sync) { _entries.Clear(); Interlocked.Increment(ref _revision); }
    }
    public void SetRunning(bool running, string? activity = null)
    {
        lock (_sync)
        {
            _running = running;
            _activity = running ? activity ?? "Thinking…" : "";
            if (running)
            {
                _spinner = MandoCode.Models.LoadingMessages.GetRandomSpinner();
                _loadingMessage = MandoCode.Models.LoadingMessages.GetRandom();
                _startedAt = _verbChangedAt = _clock.GetUtcNow();
                _elapsed = TimeSpan.Zero;
            }
            else
            {
                _preview = "";
                _loadingMessage = "";
                _elapsed = TimeSpan.Zero;
            }
            Interlocked.Increment(ref _revision);
        }
    }
    public void RefreshStatus()
    {
        lock (_sync)
        {
            if (!_running) return;
            var now = _clock.GetUtcNow();
            var elapsed = TimeSpan.FromSeconds(Math.Max(0, (long)(now - _startedAt).TotalSeconds));
            var changed = elapsed != _elapsed;
            _elapsed = elapsed;
            if (now - _verbChangedAt >= TimeSpan.FromSeconds(15))
            {
                _loadingMessage = MandoCode.Models.LoadingMessages.GetRandom();
                _verbChangedAt = now;
                changed = true;
            }
            if (changed) Interlocked.Increment(ref _revision);
        }
    }
    public void SetActivity(string? activity)
    {
        lock (_sync) { _activity = activity ?? ""; Interlocked.Increment(ref _revision); }
    }
    public void SetPreview(string? preview)
    {
        lock (_sync) { _preview = preview ?? ""; Interlocked.Increment(ref _revision); }
    }
}
public sealed record TuiEntry(long Id, IRenderable Content)
{
    public string? UserPrompt { get; init; }
    public bool SpaceAfter { get; init; }
}
public sealed record TuiSnapshot(IReadOnlyList<TuiEntry> Entries, bool Running, string Activity, string Preview)
{
    public Spinner Spinner { get; init; } = Spinner.Known.Dots;
    public string LoadingMessage { get; init; } = "";
    public TimeSpan Elapsed { get; init; }
}

/// <summary>Converts legacy plain stdout to transcript lines, never replaying terminal control codes.</summary>
public sealed class TuiTranscriptWriter(TuiSession session) : TextWriter
{
    private readonly object _sync = new();
    private readonly StringBuilder _line = new();
    private static readonly Regex Controls = new(@"\x1b(?:\[[0-?]*[ -/]*[@-~]|\][^\x07\x1b]*(?:\x07|\x1b\\)|[@-_])", RegexOptions.Compiled, TimeSpan.FromSeconds(1));
    public override Encoding Encoding => Encoding.UTF8;
    public override void Write(char value) => Write(value.ToString());
    public override void Write(string? value)
    {
        if (value is null) return;
        lock (_sync)
        {
            foreach (var ch in value)
            {
                if (ch == '\n') FlushLine();
                else _line.Append(ch);
            }
        }
    }
    public override void Write(char[] buffer, int index, int count) => Write(new string(buffer, index, count));
    public override void Flush() { lock (_sync) FlushLine(); }
    private void FlushLine()
    {
        var text = Controls.Replace(_line.ToString(), "").Replace("\r", "").Replace("\b", "");
        _line.Clear();
        if (!string.IsNullOrWhiteSpace(text)) session.Append(new Text(text));
    }
}
