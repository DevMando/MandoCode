using System.Text;
using System.Text.RegularExpressions;
using Spectre.Console;

namespace MandoCode.Services;

/// <summary>
/// Regex-based syntax highlighter that produces Spectre.Console markup strings
/// for fenced code blocks. Supports C#, Python, JavaScript/TypeScript, Bash,
/// and a generic fallback.
/// </summary>
public static class SyntaxHighlighter
{
    // ── Language alias map ───────────────────────────────────────────

    private static readonly Dictionary<string, string> LanguageAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["cs"] = "csharp",
        ["csharp"] = "csharp",
        ["c#"] = "csharp",
        ["py"] = "python",
        ["python"] = "python",
        ["js"] = "javascript",
        ["javascript"] = "javascript",
        ["ts"] = "typescript",
        ["typescript"] = "typescript",
        ["jsx"] = "javascript",
        ["tsx"] = "typescript",
        ["sh"] = "bash",
        ["bash"] = "bash",
        ["shell"] = "bash",
        ["zsh"] = "bash",
    };

    // ── Keyword sets ────────────────────────────────────────────────

    private static readonly HashSet<string> CSharpKeywords = new()
    {
        "abstract", "as", "async", "await", "base", "break", "case", "catch",
        "checked", "class", "const", "continue", "default", "delegate", "do",
        "else", "enum", "event", "explicit", "extern", "false", "finally",
        "fixed", "for", "foreach", "goto", "if", "implicit", "in", "interface",
        "internal", "is", "lock", "namespace", "new", "null", "operator", "out",
        "override", "params", "private", "protected", "public", "readonly",
        "ref", "return", "sealed", "sizeof", "stackalloc", "static", "struct",
        "switch", "this", "throw", "true", "try", "typeof", "unchecked",
        "unsafe", "using", "var", "virtual", "void", "volatile", "while",
        "yield", "record", "init", "required", "global", "when", "where",
        "get", "set", "value", "partial",
    };

    private static readonly HashSet<string> CSharpTypes = new()
    {
        "bool", "byte", "char", "decimal", "double", "float", "int", "long",
        "object", "sbyte", "short", "string", "uint", "ulong", "ushort",
        "dynamic", "nint", "nuint", "Task", "List", "Dictionary", "HashSet",
        "IEnumerable", "IList", "ICollection", "Action", "Func", "Span",
        "ReadOnlySpan", "Memory", "StringBuilder", "Console", "Math",
        "Exception", "ArgumentException", "InvalidOperationException",
    };

    private static readonly HashSet<string> PythonKeywords = new()
    {
        "False", "None", "True", "and", "as", "assert", "async", "await",
        "break", "class", "continue", "def", "del", "elif", "else", "except",
        "finally", "for", "from", "global", "if", "import", "in", "is",
        "lambda", "nonlocal", "not", "or", "pass", "raise", "return", "try",
        "while", "with", "yield", "self", "match", "case",
    };

    private static readonly HashSet<string> PythonTypes = new()
    {
        "int", "float", "str", "bool", "list", "dict", "tuple", "set",
        "frozenset", "bytes", "bytearray", "type", "object", "None",
        "range", "enumerate", "zip", "map", "filter", "print", "len",
        "isinstance", "issubclass", "super", "property", "staticmethod",
        "classmethod", "Exception", "ValueError", "TypeError", "KeyError",
    };

    private static readonly HashSet<string> JsKeywords = new()
    {
        "async", "await", "break", "case", "catch", "class", "const",
        "continue", "debugger", "default", "delete", "do", "else", "enum",
        "export", "extends", "false", "finally", "for", "from", "function",
        "if", "implements", "import", "in", "instanceof", "interface", "let",
        "new", "null", "of", "package", "private", "protected", "public",
        "return", "static", "super", "switch", "this", "throw", "true",
        "try", "type", "typeof", "undefined", "var", "void", "while",
        "with", "yield", "as", "abstract", "declare", "readonly", "keyof",
    };

    private static readonly HashSet<string> JsTypes = new()
    {
        "string", "number", "boolean", "any", "unknown", "never", "void",
        "object", "symbol", "bigint", "Array", "Map", "Set", "Promise",
        "Date", "RegExp", "Error", "JSON", "Math", "console", "window",
        "document", "Buffer", "Record", "Partial", "Required", "Readonly",
        "Pick", "Omit",
    };

    private static readonly HashSet<string> BashKeywords = new()
    {
        "if", "then", "else", "elif", "fi", "for", "do", "done", "while",
        "until", "case", "esac", "in", "function", "select", "time",
        "coproc", "echo", "exit", "return", "export", "local", "declare",
        "typeset", "readonly", "unset", "shift", "source", "eval", "exec",
        "trap", "set", "read", "printf", "cd", "pwd", "test", "true",
        "false",
    };

    private static readonly HashSet<string> BashTypes = new()
    {
        "grep", "sed", "awk", "find", "xargs", "sort", "uniq", "wc",
        "cut", "tr", "head", "tail", "cat", "ls", "mkdir", "rm", "cp",
        "mv", "chmod", "chown", "curl", "wget", "tar", "git", "docker",
        "npm", "node", "python", "pip", "dotnet", "sudo", "apt", "yum",
        "brew",
    };

    // ── Regex patterns ──────────────────────────────────────────────

    // Single-line comment: // or #
    private static readonly Regex SingleLineCommentSlash = new(@"\G//[^\n]*", RegexOptions.Compiled);
    private static readonly Regex SingleLineCommentHash = new(@"\G#[^\n]*", RegexOptions.Compiled);

    // Multi-line comment: /* ... */
    private static readonly Regex MultiLineComment = new(@"\G/\*[\s\S]*?\*/", RegexOptions.Compiled);

    // Strings
    private static readonly Regex TripleQuoteDouble = new(@"\G""""""[\s\S]*?""""""", RegexOptions.Compiled);
    private static readonly Regex TripleQuoteSingle = new(@"\G'''[\s\S]*?'''", RegexOptions.Compiled);
    private static readonly Regex VerbatimString = new(@"\G@""(?:[^""]|"""")*""", RegexOptions.Compiled);
    private static readonly Regex DoubleQuoteString = new(@"\G""(?:[^""\\]|\\.)*""", RegexOptions.Compiled);
    private static readonly Regex SingleQuoteString = new(@"\G'(?:[^'\\]|\\.)*'", RegexOptions.Compiled);
    private static readonly Regex BacktickString = new(@"\G`(?:[^`\\]|\\.)*`", RegexOptions.Compiled);

    // Numbers
    private static readonly Regex NumberLiteral = new(@"\G\b0[xX][0-9a-fA-F_]+[lLuU]*\b|\G\b0[bB][01_]+[lLuU]*\b|\G\b\d[\d_]*\.?[\d_]*(?:[eE][+-]?\d+)?[fFdDmMlLuU]?\b", RegexOptions.Compiled);

    // Identifier (word)
    private static readonly Regex Word = new(@"\G[a-zA-Z_]\w*", RegexOptions.Compiled);

    // ── Public API ──────────────────────────────────────────────────

    /// <summary>
    /// Returns a Spectre.Console markup string with syntax-highlighted code.
    /// </summary>
    public static string Highlight(string code, string language)
    {
        var lang = NormalizeLanguage(language);
        var (keywords, types, commentStyle) = GetLanguageProfile(lang);

        var sb = new StringBuilder(code.Length * 2);
        var pos = 0;

        while (pos < code.Length)
        {
            Match match;

            // 1. Comments (highest priority)
            if (TryMatchComment(code, pos, commentStyle, out match))
            {
                sb.Append("[dim]");
                sb.Append(Markup.Escape(match.Value));
                sb.Append("[/]");
                pos += match.Length;
                continue;
            }

            // 2. Strings
            if (TryMatchString(code, pos, lang, out match))
            {
                sb.Append("[green]");
                sb.Append(Markup.Escape(match.Value));
                sb.Append("[/]");
                pos += match.Length;
                continue;
            }

            // 3. Numbers
            match = NumberLiteral.Match(code, pos);
            if (match.Success && match.Index == pos)
            {
                sb.Append("[magenta]");
                sb.Append(Markup.Escape(match.Value));
                sb.Append("[/]");
                pos += match.Length;
                continue;
            }

            // 4. Words (keywords, types, or plain identifiers)
            match = Word.Match(code, pos);
            if (match.Success && match.Index == pos)
            {
                var word = match.Value;
                if (keywords.Contains(word))
                {
                    sb.Append("[yellow]");
                    sb.Append(Markup.Escape(word));
                    sb.Append("[/]");
                }
                else if (types.Contains(word))
                {
                    sb.Append("[cyan]");
                    sb.Append(Markup.Escape(word));
                    sb.Append("[/]");
                }
                else
                {
                    sb.Append(Markup.Escape(word));
                }
                pos += match.Length;
                continue;
            }

            // 5. Single character — escape and emit
            sb.Append(Markup.Escape(code[pos].ToString()));
            pos++;
        }

        return sb.ToString();
    }

    // ── Internals ───────────────────────────────────────────────────

    private enum CommentStyle
    {
        SlashAndBlock,   // C#, JS/TS: // and /* */
        Hash,            // Python, Bash: #
    }

    private static string NormalizeLanguage(string language)
    {
        if (string.IsNullOrWhiteSpace(language))
            return "generic";

        var trimmed = language.Trim().ToLowerInvariant();
        return LanguageAliases.TryGetValue(trimmed, out var normalized)
            ? normalized
            : "generic";
    }

    private static (HashSet<string> keywords, HashSet<string> types, CommentStyle commentStyle)
        GetLanguageProfile(string lang)
    {
        return lang switch
        {
            "csharp"     => (CSharpKeywords, CSharpTypes, CommentStyle.SlashAndBlock),
            "python"     => (PythonKeywords, PythonTypes, CommentStyle.Hash),
            "javascript" => (JsKeywords, JsTypes, CommentStyle.SlashAndBlock),
            "typescript" => (JsKeywords, JsTypes, CommentStyle.SlashAndBlock),
            "bash"       => (BashKeywords, BashTypes, CommentStyle.Hash),
            _            => (new HashSet<string>(), new HashSet<string>(), CommentStyle.SlashAndBlock),
        };
    }

    private static bool TryMatchComment(string code, int pos, CommentStyle style, out Match match)
    {
        // Always try multi-line block comments for slash-style languages
        if (style == CommentStyle.SlashAndBlock)
        {
            match = MultiLineComment.Match(code, pos);
            if (match.Success && match.Index == pos)
                return true;

            match = SingleLineCommentSlash.Match(code, pos);
            if (match.Success && match.Index == pos)
                return true;
        }
        else // Hash
        {
            match = SingleLineCommentHash.Match(code, pos);
            if (match.Success && match.Index == pos)
                return true;
        }

        match = Match.Empty;
        return false;
    }

    private static bool TryMatchString(string code, int pos, string lang, out Match match)
    {
        // Python triple-quoted strings (must check before regular quotes)
        if (lang == "python")
        {
            match = TripleQuoteDouble.Match(code, pos);
            if (match.Success && match.Index == pos)
                return true;

            match = TripleQuoteSingle.Match(code, pos);
            if (match.Success && match.Index == pos)
                return true;
        }

        // C# verbatim strings
        if (lang == "csharp")
        {
            match = VerbatimString.Match(code, pos);
            if (match.Success && match.Index == pos)
                return true;
        }

        // Double-quoted string
        match = DoubleQuoteString.Match(code, pos);
        if (match.Success && match.Index == pos)
            return true;

        // Single-quoted string
        match = SingleQuoteString.Match(code, pos);
        if (match.Success && match.Index == pos)
            return true;

        // Backtick template literals (JS/TS)
        if (lang is "javascript" or "typescript")
        {
            match = BacktickString.Match(code, pos);
            if (match.Success && match.Index == pos)
                return true;
        }

        match = Match.Empty;
        return false;
    }
}
