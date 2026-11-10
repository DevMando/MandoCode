using System.Text;

namespace MandoCode.Acp.Transport;

/// <summary>
/// Collects what an agent writes to stderr: a ring buffer of the last lines (for error messages) and,
/// optionally, a rotated log file. It never writes to the console: stderr of a subprocess must not
/// corrupt the terminal UI.
/// </summary>
public sealed class StderrLog
{
    private readonly object _gate = new();
    private readonly Queue<string> _tail = new();
    private readonly int _capacity;
    private readonly string? _filePath;
    private readonly long _maxFileBytes;
    private StreamWriter? _file;
    private long _fileBytes;

    /// <param name="capacity">How many recent lines to keep in memory.</param>
    /// <param name="filePath">Where to persist the log, or null to keep it in memory only.</param>
    /// <param name="maxFileBytes">The file is rotated (to <c>.1</c>) once it grows past this size.</param>
    public StderrLog(int capacity = 200, string? filePath = null, long maxFileBytes = 1024 * 1024)
    {
        _capacity = Math.Max(1, capacity);
        _filePath = filePath;
        _maxFileBytes = Math.Max(1024, maxFileBytes);
    }

    /// <summary>The log file, when persistence is on.</summary>
    public string? FilePath => _filePath;

    public void Append(string line)
    {
        lock (_gate)
        {
            _tail.Enqueue(line);
            while (_tail.Count > _capacity)
                _tail.Dequeue();

            WriteToFile(line);
        }
    }

    /// <summary>The most recent lines, oldest first, optionally limited to the last <paramref name="maxLines"/>.</summary>
    public string Tail(int maxLines = int.MaxValue)
    {
        lock (_gate)
        {
            var lines = _tail.AsEnumerable();
            if (maxLines < _tail.Count)
                lines = lines.Skip(_tail.Count - maxLines);
            return string.Join('\n', lines);
        }
    }

    public void Close()
    {
        lock (_gate)
        {
            _file?.Dispose();
            _file = null;
        }
    }

    private void WriteToFile(string line)
    {
        if (_filePath is null)
            return;

        try
        {
            if (_file is null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_filePath))!);
                var stream = new FileStream(_filePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                _fileBytes = stream.Length;
                _file = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
            }

            if (_fileBytes > _maxFileBytes)
            {
                _file.Dispose();
                _file = null;
                var rotated = _filePath + ".1";
                File.Move(_filePath, rotated, overwrite: true);
                _fileBytes = 0;
                _file = new StreamWriter(new FileStream(_filePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete),
                    new UTF8Encoding(false)) { AutoFlush = true };
            }

            _file.WriteLine(line);
            _fileBytes += Encoding.UTF8.GetByteCount(line) + 1;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Logging is best effort: a full disk must not take the agent session down.
            _file?.Dispose();
            _file = null;
        }
    }
}
