using System.Text.Json;
using MandoCode.Models;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace MandoCode.Services;

public sealed record ArchivedSpan(string Text, string Style);
public sealed record ArchivedBlock(List<ArchivedSpan> Spans, string? UserPrompt, bool SpaceAfter)
{
    public long? ToolGroup { get; init; }
    public ToolActivity? ToolActivity { get; init; }
}
public sealed record ArchivedSettings(string? Model, double Temperature, int MaxTokens, int? ContextLength)
{
    public string? OllamaEndpoint { get; init; }
    public bool ContextLengthSetByUser { get; init; }
    public Dictionary<string, string>? AgentOptions { get; init; }
}
public sealed record ArchivedAgent(
    string Key, string Name, string ProjectRoot, string Workspace, DateTimeOffset? ClosedAt,
    ArchivedSettings Settings, List<ChatMsg> Messages, List<ArchivedBlock> Transcript,
    string? HistoryJson, long PromptTokens, long CompletionTokens, TokenUsageInfo? LastUsage)
{
    public int Version { get; init; } = 1;
    public string Preview => Messages.FirstOrDefault(m => m.Role == "user")?.Text ?? "";
    public string LastReply => Messages.LastOrDefault(m => m.Role == "assistant")?.Text ?? "";
    public int MessageCount { get; init; }
}

/// <summary>Separate durable identities for CLI conversations, including agents in the same project.</summary>
public sealed class AgentArchiveStore
{
    private const int MaxBytes = 32 * 1024 * 1024;
    private readonly string _folder;
    public AgentArchiveStore() : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".mandocode", "agents")) { }
    public AgentArchiveStore(string folder) => _folder = Path.GetFullPath(folder);
    public string? Error { get; private set; }
    public event Action? Changed;
    public IDisposable? Claim(string key)
    {
        try
        {
            Directory.CreateDirectory(_folder);
            return new FileStream(PathFor(key) + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch { Error = "This conversation is already open in another MandoCode window, or cannot be accessed."; return null; }
    }
    private string PathFor(string key)
    {
        if (!Guid.TryParseExact(key, "N", out _)) throw new ArgumentException("Invalid agent identity.");
        return Path.Combine(_folder, key + ".json");
    }
    public bool Save(ArchivedAgent agent)
    {
        string? temporary = null;
        try
        {
            var path = PathFor(agent.Key);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(agent);
            if (bytes.Length > MaxBytes) throw new IOException("Conversation exceeds the archive size limit.");
            Directory.CreateDirectory(_folder);
            temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, path, true);
            WriteSummary(agent);
            Error = null;
            if (agent.ClosedAt is not null) Trim();
            Changed?.Invoke();
            return true;
        }
        catch (Exception ex) { Error = "Couldn't save agent history: " + ex.Message; return false; }
        finally { if (temporary is not null) try { File.Delete(temporary); } catch { } }
    }
    public ArchivedAgent? Load(string key)
    {
        try
        {
            var path = PathFor(key);
            if (!File.Exists(path) || new FileInfo(path).Length > MaxBytes) return null;
            var value = JsonSerializer.Deserialize<ArchivedAgent>(File.ReadAllBytes(path));
            return value is { Version: 1, Settings: not null, Messages: not null, Transcript: not null }
                && value.Key == key && value.Messages.All(m => m is not null && m.Text is not null)
                && value.Transcript.All(b => b is not null && b.Spans is not null && b.Spans.All(s => s is not null && s.Text is not null)) ? value : null;
        }
        catch { return null; }
    }
    public IReadOnlyList<ArchivedAgent> Closed(string query = "")
    {
        try
        {
            return Directory.Exists(_folder) ? Directory.EnumerateFiles(_folder, "*.json")
                .Select(path => Summary(Path.GetFileNameWithoutExtension(path)))
                .OfType<ArchivedAgent>().Where(a => a.ClosedAt is not null && Matches(a, query))
                .OrderByDescending(a => a.ClosedAt).ToList() : [];
        }
        catch (Exception ex) { Error = "Couldn't read agent history: " + ex.Message; return []; }
    }
    private bool Matches(ArchivedAgent a, string query) => string.IsNullOrWhiteSpace(query)
        || new[] { a.Name, a.ProjectRoot, a.Workspace, a.Settings.Model ?? "" }.Any(t => t.Contains(query, StringComparison.OrdinalIgnoreCase))
        || a.Messages.Any(m => m.Text.Contains(query, StringComparison.OrdinalIgnoreCase))
        || Load(a.Key)?.Messages.Any(m => m.Text.Contains(query, StringComparison.OrdinalIgnoreCase)) == true;
    private ArchivedAgent? Summary(string key)
    {
        try
        {
            var path = PathFor(key);
            var meta = path + ".meta";
            if (File.Exists(meta) && new FileInfo(meta).Length < 64 * 1024 && File.GetLastWriteTimeUtc(meta) >= File.GetLastWriteTimeUtc(path))
            {
                var summary = JsonSerializer.Deserialize<ArchivedAgent>(File.ReadAllBytes(meta));
                if (summary is { Version: 1, Messages: not null, Settings: not null } && summary.Key == key) return summary;
            }
        }
        catch { }
        return Load(key);
    }
    private void WriteSummary(ArchivedAgent agent)
    {
        var picked = new[] { agent.Messages.FirstOrDefault(m => m.Role == "user"), agent.Messages.LastOrDefault(m => m.Role == "user"), agent.Messages.LastOrDefault(m => m.Role == "assistant") }
            .OfType<ChatMsg>().Distinct().Select(m => new ChatMsg { Role = m.Role, Text = m.Text.Length > 500 ? m.Text[..500] : m.Text }).ToList();
        var summary = agent with { Messages = picked, MessageCount = agent.Messages.Count, HistoryJson = null, Transcript = [] };
        var path = PathFor(agent.Key) + ".meta";
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(summary)); File.Move(temp, path, true); }
        finally { try { File.Delete(temp); } catch { } }
    }
    public bool Delete(string key)
    {
        try { File.Delete(PathFor(key)); File.Delete(PathFor(key) + ".meta"); Error = null; Changed?.Invoke(); return true; }
        catch (Exception ex) { Error = "Couldn't delete agent history: " + ex.Message; return false; }
    }
    public bool MarkOpen(ArchivedAgent agent) => Save(agent with { ClosedAt = null });
    /// <summary>Recover completed-turn checkpoints whose process no longer owns its lease.</summary>
    public void RecoverAbandoned()
    {
        try { RecoverAbandonedCore(); }
        catch (IOException ex) { Error = "Couldn't recover agent history: " + ex.Message; }
        catch (UnauthorizedAccessException ex) { Error = "Couldn't recover agent history: " + ex.Message; }
    }
    private void RecoverAbandonedCore()
    {
        if (!Directory.Exists(_folder)) return;
        foreach (var path in Directory.EnumerateFiles(_folder, "*.json").ToArray())
        {
            var key = Path.GetFileNameWithoutExtension(path);
            if (Summary(key) is not { ClosedAt: null }) continue;
            FileStream lease;
            try { lease = new FileStream(PathFor(key) + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { continue; } // Another live window still owns this conversation.
            catch (UnauthorizedAccessException) { continue; }
            using (lease)
            {
                // Re-read after obtaining the lock: another window may have just saved or closed it.
                if (Load(key) is not { ClosedAt: null } agent || agent.Messages.Count == 0) continue;
                var savedAt = new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero);
                Save(agent with { ClosedAt = savedAt });
            }
        }
    }
    public bool DeleteClosed(string key)
    {
        using var claim = Claim(key);
        return claim is not null && Delete(key);
    }
    private void Trim()
    {
        foreach (var old in Closed().Skip(60))
        {
            using var claim = Claim(old.Key);
            if (claim is not null) Delete(old.Key);
        }
    }
    public static List<ArchivedBlock> Capture(TuiSession session)
    {
        var entries = session.Snapshot().Entries;
        // Startup help/status is not part of a conversation. Transient spinner state is not an entry.
        var start = entries.ToList().FindIndex(e => e.UserPrompt is not null);
        if (start < 0) return [];
        var options = new RenderOptions(Spectre.Console.AnsiConsole.Profile.Capabilities, new Size(120, 10000));
        var blocks = new List<ArchivedBlock>();
        var groups = entries.Where(e => e.ToolActivity is not null).ToDictionary(e => e.ToolActivity!, e => e.Id);
        foreach (var entry in entries.Skip(start))
        {
            var spans = entry.Content.Render(options, 120).Where(s => !s.IsControlCode)
                .Select(s => new ArchivedSpan(s.Text, s.Style?.ToMarkup() ?? "")).ToList();
            blocks.Add(new(spans, entry.UserPrompt, entry.SpaceAfter)
            {
                ToolGroup = entry.ToolActivity is not null ? entry.Id : entry.ToolOutput is { } output ? groups.GetValueOrDefault(output) : null,
                ToolActivity = entry.ToolActivity
            });
        }
        return blocks;
    }
    public static void Replay(ArchivedAgent archive, TuiSession session)
    {
        session.Clear();
        var groups = archive.Transcript.Where(b => b.ToolActivity is not null && b.ToolGroup is not null).ToDictionary(b => b.ToolGroup!.Value, b => b.ToolActivity!);
        foreach (var block in archive.Transcript)
        {
            var text = new Paragraph();
            foreach (var span in block.Spans)
            {
                Style style;
                try { style = string.IsNullOrWhiteSpace(span.Style) ? Style.Plain : Style.Parse(span.Style); }
                catch { style = Style.Plain; }
                text.Append(span.Text, style);
            }
            if (block.ToolActivity is { } activity) { activity.Finished = true; activity.Expanded = false; }
            session.AppendRestored(text, block.UserPrompt, block.SpaceAfter, block.ToolActivity,
                block.ToolActivity is null && block.ToolGroup is { } group ? groups.GetValueOrDefault(group) : null);
        }
    }
}
