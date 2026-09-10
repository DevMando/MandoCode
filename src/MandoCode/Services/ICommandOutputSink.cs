namespace MandoCode.Services;

/// <summary>
/// Receives an agent shell command's lifecycle as it happens, so a host can SHOW the work
/// instead of only reporting it after the fact. Purely observational: the sink never feeds the
/// model, never gates execution, and cannot change what a command does.
///
/// <para>Why this exists rather than a pseudo-terminal: the agent's shell tool deliberately runs
/// through redirected pipes, which is what keeps stdout and stderr separate, keeps the output free
/// of escape sequences, and leaves a real exit code to report. Routing it through a PTY to make it
/// visible would trade all three away for cursor control the model has no use for. Teeing the lines
/// that already exist costs none of that.</para>
///
/// <para><b>Threading:</b> <see cref="CommandOutput"/> is raised on the process's output-reader
/// threads, and the other two on whichever thread invoked the tool. Implementations must be
/// thread-safe and must marshal to their own UI thread. They must also be fast — every callback
/// runs inline with reading the command's output.</para>
///
/// <para><b>Failure:</b> the caller swallows exceptions from every method. A broken host display is
/// not allowed to take down the command the agent is running.</para>
/// </summary>
public interface ICommandOutputSink
{
    /// <summary>A command is about to run. Raised once, before the process starts.</summary>
    void CommandStarted(string command, string workingDirectory);

    /// <summary>
    /// One line of output. <paramref name="isError"/> distinguishes stderr from stdout.
    /// Raised for EVERY line, including those past the point where the model's copy is
    /// truncated — a host display has its own scrollback and no reason to inherit that cap.
    /// </summary>
    void CommandOutput(string line, bool isError);

    /// <summary>
    /// The command is over. Exactly one of the two is set: <paramref name="exitCode"/> when the
    /// process ended on its own, or <paramref name="killReason"/> when it was killed for running
    /// too long or failed to start.
    /// </summary>
    void CommandFinished(int? exitCode, string? killReason);
}
