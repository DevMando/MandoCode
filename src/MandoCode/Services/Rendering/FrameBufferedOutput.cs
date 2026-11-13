using System.Text;
using Spectre.Console;

namespace MandoCode.Services;

/// <summary>Delivers synchronized frames in one write while retaining terminal capabilities.</summary>
internal sealed class FrameBufferedOutput(IAnsiConsoleOutput inner) : IAnsiConsoleOutput, IDisposable
{
    private readonly FrameWriter _writer = new(inner.Writer);
    public TextWriter Writer => _writer;
    public bool IsTerminal => inner.IsTerminal;
    public int Width => inner.Width;
    public int Height => inner.Height;
    public void SetEncoding(Encoding encoding) => inner.SetEncoding(encoding);
    public void Dispose() => _writer.Dispose();

    internal sealed class FrameWriter(TextWriter inner) : TextWriter
    {
        internal const string Begin = "\u001b[?2026h";
        internal const string End = "\u001b[?2026l";
        private const int MaximumBufferedCharacters = 1024 * 1024;
        private readonly object _sync = new();
        private readonly StringBuilder _frame = new();
        private string _pending = "";
        private bool _inFrame;
        public override Encoding Encoding => inner.Encoding;
        public override void Write(char value) => Write(value.ToString());
        public override void Write(char[] buffer, int index, int count) => Write(new string(buffer, index, count));
        public override void Write(ReadOnlySpan<char> buffer) => Write(buffer.ToString());
        public override void Write(string? value)
        {
            if (string.IsNullOrEmpty(value)) return;
            lock (_sync)
            {
                _pending += value;
                while (_pending.Length > 0)
                {
                    var marker = _inFrame ? End : Begin;
                    var index = _pending.IndexOf(marker, StringComparison.Ordinal);
                    if (index >= 0)
                    {
                        Emit(_pending[..index]);
                        _pending = _pending[(index + marker.Length)..];
                        if (_inFrame)
                        {
                            _frame.Append(End);
                            DeliverFrame();
                            _inFrame = false;
                        }
                        else
                        {
                            _inFrame = true;
                            _frame.Append(Begin);
                        }
                        continue;
                    }
                    // A marker can be split across Spectre writes, including char writes.
                    var keep = Math.Min(marker.Length - 1, _pending.Length);
                    while (keep > 0 && !_pending.EndsWith(marker[..keep], StringComparison.Ordinal)) keep--;
                    Emit(_pending[..(_pending.Length - keep)]);
                    _pending = keep == 0 ? "" : _pending[^keep..];
                    break;
                }
            }
        }
        private void Emit(string text)
        {
            if (text.Length == 0) return;
            if (!_inFrame) inner.Write(text);
            else
            {
                _frame.Append(text);
                // Bound memory even if a producer fails to finish its frame.
                if (_frame.Length >= MaximumBufferedCharacters) DeliverFrame();
            }
        }
        private void DeliverFrame()
        {
            if (_frame.Length == 0) return;
            inner.Write(_frame.ToString());
            _frame.Clear();
            inner.Flush();
        }
        public override void Flush()
        {
            lock (_sync) { if (!_inFrame) inner.Flush(); }
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                lock (_sync)
                {
                    Emit(_pending);
                    _pending = "";
                    if (_inFrame) _frame.Append(End);
                    DeliverFrame();
                    _inFrame = false;
                    inner.Flush();
                }
            }
            base.Dispose(disposing);
        }
    }

    public static IDisposable Attach(IAnsiConsole console)
    {
        var original = console.Profile.Out;
        var buffered = new FrameBufferedOutput(original);
        console.Profile.Out = buffered;
        return new OutputScope(() =>
        {
            try { buffered.Dispose(); }
            finally { console.Profile.Out = original; }
        });
    }
    private sealed class OutputScope(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }
}
