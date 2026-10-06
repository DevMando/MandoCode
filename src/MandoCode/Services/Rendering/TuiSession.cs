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
    private readonly Dictionary<long, IRenderable> _entryContent = new();
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
    private ToolActivity? _toolActivity;
    private readonly AsyncLocal<ToolActivity?> _capturedTools = new();
    private readonly Dictionary<string, AgentExchange> _exchanges = new();
    private readonly AsyncLocal<AgentExchange?> _capturedExchange = new();
    private readonly AsyncLocal<string?> _exchangePeer = new();
    public string? CurrentAgentExchangeFor(string name) => string.Equals(name, _exchangePeer.Value, StringComparison.OrdinalIgnoreCase) ? _capturedExchange.Value?.Id : null;
    public IDisposable CaptureAgentExchange(string id, string? peer = null)
    {
        lock (_sync)
        {
            var previous = _capturedExchange.Value;
            var previousPeer = _exchangePeer.Value;
            _capturedExchange.Value = _exchanges.GetValueOrDefault(id);
            _exchangePeer.Value = peer;
            return new OutputScope(() => { _capturedExchange.Value = previous; _exchangePeer.Value = previousPeer; });
        }
    }
    public void AppendAgentDetail(string id, string title, IRenderable content, string? status = null, bool problem = false)
    {
        lock (_sync)
        {
            if (!_exchanges.TryGetValue(id, out var exchange))
            {
                exchange = new() { Id = id, Title = title, CreatedAt = _clock.GetUtcNow() };
                _exchanges[id] = exchange;
                AddEntry(new(Interlocked.Increment(ref _nextId), new Text("")) { AgentActivity = exchange, AgentOutput = _capturedExchange.Value });
            }
            if (status is not null) exchange.Status = status;
            exchange.UpdatedAt = _clock.GetUtcNow();
            AddEntry(new(Interlocked.Increment(ref _nextId), content) { AgentOutput = exchange, SpaceAfter = true });
            if (problem) AddEntry(new(Interlocked.Increment(ref _nextId), new Text(title + " · " + status, new Style(Color.Yellow))) { SpaceAfter = true });
            Interlocked.Increment(ref _revision);
        }
    }
    public void BeginToolTurn() { lock (_sync) _toolActivity = null; }
    public IDisposable CaptureToolOutput(bool invoked, bool success = true)
    {
        lock (_sync)
        {
            if (_toolActivity is null)
            {
                _toolActivity = new();
                AddEntry(new(Interlocked.Increment(ref _nextId), new Text("")) { ToolActivity = _toolActivity, AgentOutput = _capturedExchange.Value });
            }
            if (invoked) _toolActivity.Calls++;
            else if (success) _toolActivity.Completed++;
            else _toolActivity.Failed++;
            var previous = _capturedTools.Value;
            _capturedTools.Value = _toolActivity;
            Interlocked.Increment(ref _revision);
            return new OutputScope(() => _capturedTools.Value = previous);
        }
    }
    public void CompleteToolTurn()
    {
        lock (_sync)
        {
            if (_toolActivity is null) return;
            _toolActivity.Finished = true;
            _toolActivity.Expanded = false;
            _toolActivity = null;
            Interlocked.Increment(ref _revision);
        }
    }
    private sealed class OutputScope(Action close) : IDisposable { public void Dispose() => close(); }
    public long Revision => Interlocked.Read(ref _revision);
    public TuiSnapshot Snapshot()
    {
        lock (_sync) return new(_entries.ToArray(), _running, _activity, _preview) { Spinner = _spinner, LoadingMessage = _loadingMessage, Elapsed = _elapsed };
    }
    public void Append(IRenderable content)
    {
        if (content is MandoCode.Translators.AnsiPassthroughRenderable ansi) content = AnsiTranscriptText.Parse(ansi.Content);
        lock (_sync) { AddEntry(new(Interlocked.Increment(ref _nextId), content) { ToolOutput = _capturedTools.Value, AgentOutput = _capturedExchange.Value }); Interlocked.Increment(ref _revision); }
    }
    public void AppendUserPrompt(string prompt, IEnumerable<string>? agentNames = null)
    {
        lock (_sync)
        {
            var display = TerminalEmojiPresentation.Normalize(prompt);
            IRenderable content = agentNames is null ? new Text("> " + display, new Style(new Color(255, 200, 80))) : new Markup("> " + CliAgentPresentation.Inline(display, agentNames), new Style(new Color(255, 200, 80)));
            AddEntry(new(Interlocked.Increment(ref _nextId), content) { UserPrompt = prompt });
            Interlocked.Increment(ref _revision);
        }
    }
    public void AppendSpaced(IRenderable content)
    {
        lock (_sync)
        {
            AddEntry(new(Interlocked.Increment(ref _nextId), content) { SpaceAfter = true, AgentOutput = _capturedExchange.Value });
            Interlocked.Increment(ref _revision);
        }
    }
    public void AppendAgentExchange(string text, Color color, string? id = null, string? status = null)
    {
        var newline = text.IndexOf('\n');
        var heading = newline < 0 ? text : text[..newline];
        var body = newline < 0 ? text : text[(newline + 1)..];
        AppendAgentDetail(id ?? Guid.NewGuid().ToString("N"), heading,
            CliAgentExchangeRenderer.Result(heading, body, color != Color.Yellow), status,
            status is "Failed" or "Cancelled" or "Declined");
    }
    public void AppendRestored(IRenderable content, string? userPrompt, bool spaceAfter, ToolActivity? activity = null, ToolActivity? toolOutput = null, AgentExchange? agentActivity = null, AgentExchange? agentOutput = null)
    {
        lock (_sync)
        {
            AddEntry(new(Interlocked.Increment(ref _nextId), content) { UserPrompt = userPrompt, SpaceAfter = spaceAfter, ToolActivity = activity, ToolOutput = toolOutput, AgentActivity = agentActivity, AgentOutput = agentOutput });
            if (agentActivity is not null) _exchanges[agentActivity.Id] = agentActivity;
            Interlocked.Increment(ref _revision);
        }
    }
    public IRenderable? Find(long id)
    {
        lock (_sync) return _registered.GetValueOrDefault(id) ?? _entryContent.GetValueOrDefault(id);
    }
    // Called under _sync. Layout resolves every entry on scrolling; scanning the
    // whole history for each ID made that work quadratic in conversation length.
    private void AddEntry(TuiEntry entry)
    {
        _entries.Add(entry);
        _entryContent[entry.Id] = entry.Content;
    }
    public long Register(IRenderable content)
    {
        lock (_sync) { var id = Interlocked.Increment(ref _nextId); _registered[id] = content; return id; }
    }
    public void Unregister(long id) { lock (_sync) _registered.Remove(id); }
    public void Clear()
    {
        lock (_sync) { _entries.Clear(); _entryContent.Clear(); _exchanges.Clear(); Interlocked.Increment(ref _revision); }
    }
    private bool _fixedLoadingMessage;
    public void SetRunning(bool running, string? activity = null, string? message = null)
    {
        lock (_sync)
        {
            _running = running;
            _activity = running ? activity ?? "Thinking…" : "";
            if (running)
            {
                _spinner = MandoCode.Models.LoadingMessages.GetRandomSpinner();
                _fixedLoadingMessage = message is not null;
                _loadingMessage = message ?? MandoCode.Models.LoadingMessages.GetRandom();
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
            if (!_fixedLoadingMessage && now - _verbChangedAt >= TimeSpan.FromSeconds(15))
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
    public AgentExchange? AgentActivity { get; init; }
    public AgentExchange? AgentOutput { get; init; }
    public ToolActivity? ToolActivity { get; init; }
    public ToolActivity? ToolOutput { get; init; }
    public string? UserPrompt { get; init; }
    public bool SpaceAfter { get; init; }
}
public sealed class AgentExchange
{
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Status { get; set; } = "Sent";
    public bool Expanded { get; set; }
}
public sealed class ToolActivity
{
    public int Calls { get; set; }
    public int Completed { get; set; }
    public int Failed { get; set; }
    public bool Finished { get; set; }
    public bool Expanded { get; set; }
    public string Summary => $"{Calls} tool call{(Calls == 1 ? "" : "s")} · {Completed} completed"
        + (Failed > 0 ? $" · {Failed} failed" : "")
        + (Calls > Completed + Failed ? $" · {Calls - Completed - Failed} unfinished" : "");
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
