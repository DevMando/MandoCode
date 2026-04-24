using System.Collections.Concurrent;

namespace MandoCode.Services;

/// <summary>
/// Per-chat/per-step bookkeeping used by <see cref="FunctionInvocationFilter"/>
/// to catch pathological tool-call loops that would otherwise fill the model's
/// context window before the step finishes.
///
/// Two circuits:
///   1. Duplicate-read detection — a second <c>read_file_contents</c> with the
///      same args is short-circuited unless the path was written/edited since.
///   2. Result-budget tracking — accumulated tool-result characters are capped
///      so we bail gracefully instead of blowing past the context window.
///
/// A scope is cheap; spin one up per chat turn and per plan step.
/// </summary>
public class InvocationScope : IDisposable
{
    // Both sets use OrdinalIgnoreCase so Windows path variants (Src/foo.cs vs src/foo.cs)
    // are treated as the same file — matches OS semantics and keeps the two maps aligned.
    private readonly HashSet<string> _readKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _pathsModifiedSinceRead = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private Action? _onDispose;

    public long ResultCharBudget { get; }
    public long TotalResultChars { get; private set; }

    /// <summary>
    /// True once tool results have eaten more than <see cref="ResultCharBudget"/>.
    /// When true, the filter refuses further tool calls.
    /// </summary>
    public bool BudgetExhausted => Interlocked.Read(ref _totalResultCharsAtomic) >= ResultCharBudget;
    private long _totalResultCharsAtomic;

    public InvocationScope(long resultCharBudget)
    {
        ResultCharBudget = resultCharBudget;
    }

    /// <summary>
    /// Returns true if this exact read has already happened in the scope AND no
    /// write/edit to the same path has occurred since. Caller should short-circuit.
    /// </summary>
    public bool IsRedundantRead(string readKey, string path)
    {
        lock (_lock)
        {
            if (!_readKeys.Contains(readKey))
                return false;
            // Re-read is fine if we've modified the file since.
            return !_pathsModifiedSinceRead.Contains(path);
        }
    }

    public void RecordRead(string readKey, string path)
    {
        lock (_lock)
        {
            _readKeys.Add(readKey);
            _pathsModifiedSinceRead.Remove(path);
        }
    }

    /// <summary>
    /// Flag a path as written/edited. The next read of that path is allowed
    /// through because the content has legitimately changed.
    /// </summary>
    public void RecordWrite(string path)
    {
        lock (_lock)
        {
            _pathsModifiedSinceRead.Add(path);
        }
    }

    public void RecordResultChars(int chars)
    {
        if (chars <= 0) return;
        var updated = Interlocked.Add(ref _totalResultCharsAtomic, chars);
        lock (_lock) { TotalResultChars = updated; }
    }

    /// <summary>
    /// Set when the user chooses "Cancel plan" from a diff-approval prompt mid-step.
    /// Checked by <see cref="AIService.ExecutePlanStepAsync"/> after the step returns,
    /// so the plan terminates instead of continuing to the next step.
    /// </summary>
    public bool PlanCancellationRequested { get; private set; }

    public void RequestPlanCancellation()
    {
        lock (_lock) PlanCancellationRequested = true;
    }

    /// <summary>
    /// Set when the user denies a tool-approval prompt. Subsequent approval prompts
    /// in the same scope auto-deny without re-prompting — denying one in a batch
    /// implicitly denies the rest. Reset when a new scope is begun.
    /// Distinct from <see cref="PlanCancellationRequested"/>: this only short-circuits
    /// the current batch's prompts; it does NOT terminate the whole plan.
    /// </summary>
    public bool ApprovalsRevoked { get; private set; }

    public void RevokeRemainingApprovals()
    {
        lock (_lock) ApprovalsRevoked = true;
    }

    /// <summary>Filter sets this so Dispose pops the scope back to its parent.</summary>
    internal void SetOnDispose(Action onDispose) => _onDispose = onDispose;

    public void Dispose()
    {
        var d = Interlocked.Exchange(ref _onDispose, null);
        d?.Invoke();
    }
}
