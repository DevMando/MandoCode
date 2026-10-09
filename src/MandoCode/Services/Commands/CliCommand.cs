namespace MandoCode.Services;

/// <summary>Separate case-insensitive dispatch from verbatim command arguments.</summary>
public sealed record CliCommand(string Name, string Arguments, string DispatchText)
{
    public static CliCommand Parse(string input)
    {
        var raw = input.TrimStart();
        if (!raw.StartsWith('/')) throw new ArgumentException("Expected a slash command.", nameof(input));
        raw = raw[1..].Trim();
        var space = raw.IndexOf(' ');
        return new(space < 0 ? raw.ToLowerInvariant() : raw[..space].ToLowerInvariant(),
            space < 0 ? string.Empty : raw[(space + 1)..].Trim(), InputStateMachine.GetCommandName(input));
    }
}

internal enum CliCommandResult { Continue, Exit }
