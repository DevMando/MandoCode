using System.Runtime.InteropServices;
using Microsoft.AspNetCore.Components.Web;

namespace MandoCode.Services;

internal readonly record struct PasteQueueState(bool TextPending, bool NewlinePending, bool BurstEvidence = false);

/// <summary>Observes, but never consumes, queued Windows console records. The framework remains the keyboard reader.</summary>
internal sealed class TerminalPasteGuard
{
    private bool _pasting;
    private readonly Func<PasteQueueState> _peek;
    internal TerminalPasteGuard(Func<PasteQueueState>? peek = null) => _peek = peek ?? PeekWindows;
    internal bool IsPasteKey(KeyboardEventArgs key)
    {
        if (key.CtrlKey || key.AltKey || key.MetaKey || !(key.Key.Length == 1 || key.Key is "Enter" or "Return" or "Tab")) { _pasting = false; return false; }
        var queued = _peek();
        // A queued newline is evidence of a multiline text burst; a leading Enter
        // with more text queued also belongs to the paste, including blank first lines.
        var paste = _pasting || (queued.NewlinePending && queued.BurstEvidence) || (key.Key is "Enter" or "Return" && queued.TextPending);
        _pasting = paste && queued.TextPending;
        return paste;
    }
    private static PasteQueueState PeekWindows()
    {
        if (!OperatingSystem.IsWindows() || Console.IsInputRedirected) return default;
        var records = new InputRecord[4096];
        if (!PeekConsoleInputW(GetStdHandle(-10), records, (uint)records.Length, out var count)) return default;
        var text = false; var newline = false; var characters = 0;
        for (var i = 0; i < count; i++) {
            var record = records[i];
            if (record.Type != 1 || record.KeyDown == 0 || (record.Modifiers & 0x0f) != 0) continue;
            if (record.Character is '\r' or '\n' || record.VirtualKey == 13) { text = true; newline = true; }
            else if (record.Character == '\t' || !char.IsControl(record.Character)) { text = true; characters++; }
        }
        return new(text, newline, characters >= 8);
    }
    [StructLayout(LayoutKind.Explicit, CharSet = CharSet.Unicode, Size = 20)]
    private struct InputRecord {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(4)] public int KeyDown;
        [FieldOffset(10)] public ushort VirtualKey;
        [FieldOffset(14)] public char Character;
        [FieldOffset(16)] public uint Modifiers;
    }
    [DllImport("kernel32.dll")] private static extern nint GetStdHandle(int handle);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekConsoleInputW(nint input, [Out] InputRecord[] records, uint length, out uint read);
}

internal static class PromptPaste
{
    internal static string Normalize(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
}