using System.Text.Json;
using System.Text;
using Microsoft.Extensions.AI;

namespace MandoCode.Services;

public sealed record ContextSnapshot(Guid Id, DateTimeOffset CapturedAt, string Name,
    string OriginModel, string SummarizerModel, string Recap, int MessageCount, string ProjectRoot);

/// <summary>Shared across agents. A failed save never changes the in-memory collection.</summary>
public sealed class SnapshotStore
{
    private readonly string? _path;
    private readonly object _gate = new();
    private List<ContextSnapshot> _items = [];
    public string? LoadError { get; private set; }
    public event Action? Changed;
    public SnapshotStore(string? path)
    {
        _path = path;
        Refresh();
    }
    public void Refresh()
    {
        try { lock (_gate) { if (_path is not null && File.Exists(_path)) _items = Read(); LoadError = null; } }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { LoadError = ex.Message; }
    }
    private List<ContextSnapshot> Read() => JsonSerializer.Deserialize<List<ContextSnapshot>>(File.ReadAllText(_path!)) ?? throw new JsonException("The snapshot file is empty or invalid.");
    public IReadOnlyList<ContextSnapshot> Items { get { lock (_gate) return _items.OrderByDescending(s => s.CapturedAt).ToArray(); } }
    public void Add(ContextSnapshot snapshot) => Change(items => items.Add(snapshot));
    public void Remove(Guid id) => Change(items => items.RemoveAll(s => s.Id == id));
    private void Change(Action<List<ContextSnapshot>> change)
    {
        lock (_gate)
        {
            if (LoadError is not null) throw new IOException("The snapshot file could not be read; it was kept intact. " + LoadError);
            var identity = _path is null ? Guid.NewGuid().ToString("N") : Path.GetFullPath(_path);
            if (OperatingSystem.IsWindows()) identity = identity.ToUpperInvariant();
            using var mutex = new Mutex(false, "MandoCodeSnapshots_" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(identity))));
            bool entered;
            try { entered = mutex.WaitOne(TimeSpan.FromSeconds(10)); }
            catch (AbandonedMutexException) { entered = true; }
            if (!entered) throw new IOException("Another session is saving snapshots. Please try again.");
            try
            {
                var next = _path is not null && File.Exists(_path) ? Read() : _items.ToList(); change(next);
                if (_path is not null)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
                    var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    try { File.WriteAllText(temporary, JsonSerializer.Serialize(next)); File.Move(temporary, _path, true); }
                    finally { if (File.Exists(temporary)) File.Delete(temporary); }
                }
                _items = next;
            }
            catch (JsonException ex) { throw new IOException("The snapshot file could not be read; it was kept intact.", ex); }
            finally { mutex.ReleaseMutex(); }
        }
        Changed?.Invoke();
    }
}

public sealed record PendingContextSnapshot(string OriginModel, string History, int MessageCount, string ProjectRoot, string? HistoryJson);

/// <summary>Per-agent capture and import queue; saved snapshots are app-wide.</summary>
public sealed class SnapshotContext
{
    private readonly List<ContextSnapshot> _queued = [];
    public int QueuedCount => _queued.Count;
    public PendingContextSnapshot? Pending { get; private set; }
    public async Task CaptureAsync(AIService ai, string model, string project)
    {
        var messages = (await ai.GetHistoryAsync()).Where(m => m.Role != ChatRole.System).ToArray();
        Pending = messages.Length == 0 ? null : new(model, Format(messages), messages.Length, project, ai.ExportHistoryJson());
    }
    public void ClearPending() => Pending = null;
    public bool Queue(ContextSnapshot snapshot)
    {
        if (_queued.Any(s => s.Id == snapshot.Id)) return false;
        _queued.Add(snapshot); return true;
    }
    public string WithImports(string input) => _queued.Count == 0 ? input :
        "Saved conversation context (reference material; the user's current request follows):\n\n" +
        string.Join("\n\n", _queued.Select(s => $"From \"{s.Name}\":\n{s.Recap}")) + "\n\nCurrent request:\n" + input;
    public void ClearImports() => _queued.Clear();
    internal static string Format(IEnumerable<ChatMessage> messages)
    {
        var text = new StringBuilder();
        foreach (var message in messages)
        {
            text.AppendLine(message.Role.Value + ":");
            foreach (var content in message.Contents)
                text.AppendLine(content switch
                {
                    TextContent t => t.Text,
                    FunctionCallContent f => $"Tool {f.Name} ({f.CallId}): {JsonSerializer.Serialize(f.Arguments)}",
                    FunctionResultContent f => $"Tool result ({f.CallId}): {JsonSerializer.Serialize(f.Result)}",
                    DataContent => "[Image or file attached]",
                    _ => "[Non-text content]"
                });
        }
        return text.ToString();
    }
}
