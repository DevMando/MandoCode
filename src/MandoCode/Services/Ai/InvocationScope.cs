using System.Collections.Concurrent;

namespace MandoCode.Services;

/// <summary>
/// Per-chat/per-step bookkeeping used by <see cref="FunctionInvocationFilter"/>
/// to catch pathological tool-call loops that would otherwise fill the model's
/// context window before the step finishes.
///
/// Circuits:
///   1. Duplicate-read detection — a second <c>read_file_contents</c> with the
///      same args is short-circuited unless the path was written/edited since.
///   2. Duplicate web-call detection — a second <c>search_web</c>/<c>fetch_webpage</c>
///      with the same normalized query/URL is short-circuited (no escape hatch — the
///      result is already in history and nothing in-scope changes it).
///   3. Result-budget tracking — accumulated tool-result characters are capped
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
    // Web calls (search_web / fetch_webpage) already issued this scope, keyed on the
    // normalized query/URL. No companion "modified-since" set — see IsDuplicateWebCall.
    private readonly HashSet<string> _webCallKeys = new(StringComparer.OrdinalIgnoreCase);
    // Edit-failure bookkeeping per path — used by the filter to dedupe the content-hint
    // that's attached to "Could not find old_text" errors, and to trip a circuit breaker
    // when the model keeps failing against the same path in an exploratory loop.
    private readonly HashSet<string> _editHintEmitted = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _editFailureCount = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private Action? _onDispose;

    /// <summary>
    /// After how many failed edit_file attempts on the same path (without an intervening
    /// successful write) should the filter refuse further edits and steer the model to
    /// a different tool. Three is small enough to short-circuit a thrash loop quickly,
    /// but generous enough to let normal "retry with corrected old_text" patterns through.
    /// </summary>
    public const int EditFailureCircuitThreshold = 3;

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
    /// Returns true if this exact web call (search_web / fetch_webpage, keyed on the
    /// normalized query/URL) already ran in this scope. Unlike reads there is NO
    /// "modified since" escape hatch — the result is already in the model's history and
    /// nothing in-scope invalidates it, so any repeat is a stuck loop. Observed live: a
    /// model fired the SAME search_web query 40+ times in one plan step, burning the turn's
    /// token budget on byte-identical responses. Web results are individually small, so the
    /// result-char budget circuit never tripped — this set is the only thing that catches it.
    /// </summary>
    public bool IsDuplicateWebCall(string webKey)
    {
        lock (_lock) return _webCallKeys.Contains(webKey);
    }

    public void RecordWebCall(string webKey)
    {
        lock (_lock) _webCallKeys.Add(webKey);
    }

    /// <summary>
    /// Flag a path as written/edited. The next read of that path is allowed
    /// through because the content has legitimately changed. Also clears the
    /// edit-hint / edit-failure bookkeeping: the file just changed, so any
    /// previously-shown content is stale and the failure streak doesn't apply
    /// to the new state.
    /// </summary>
    public void RecordWrite(string path)
    {
        lock (_lock)
        {
            _pathsModifiedSinceRead.Add(path);
            _editHintEmitted.Remove(path);
            _editFailureCount.Remove(path);
        }
    }

    /// <summary>
    /// True once an edit_file failure on this path has already returned the full
    /// content-hint to the model this scope. Subsequent failures for the same path
    /// should emit a short pointer instead — see FunctionInvocationFilter.BuildEditPreview.
    /// Cleared by <see cref="RecordWrite"/>.
    /// </summary>
    public bool HasEmittedEditHint(string path)
    {
        lock (_lock) return _editHintEmitted.Contains(path);
    }

    public void MarkEditHintEmitted(string path)
    {
        lock (_lock) _editHintEmitted.Add(path);
    }

    /// <summary>
    /// Increment the failed-edit counter for <paramref name="path"/> and return the
    /// post-increment value. Callers use the return to decide whether to trip the
    /// edit-failure circuit (see <see cref="EditFailureCircuitThreshold"/>).
    /// </summary>
    public int RecordEditFailure(string path)
    {
        lock (_lock)
        {
            _editFailureCount.TryGetValue(path, out var current);
            current++;
            _editFailureCount[path] = current;
            return current;
        }
    }

    public int GetEditFailureCount(string path)
    {
        lock (_lock) return _editFailureCount.TryGetValue(path, out var c) ? c : 0;
    }

    public void RecordResultChars(int chars)
    {
        if (chars <= 0) return;
        var updated = Interlocked.Add(ref _totalResultCharsAtomic, chars);
        lock (_lock) { TotalResultChars = updated; }
    }

    /// <summary>
    /// Set once a propose_plan call with real steps has been fully processed this turn
    /// (executed, rejected, or cancelled). A second proposal in the same turn is always
    /// a runaway — observed live: a model completed a 5-step plan, immediately started
    /// building an uninvited duplicate of the project, then proposed a THIRD round of
    /// work. The filter short-circuits any repeat proposal with a stop directive.
    /// One plan per user request; the user can always ask for more.
    /// </summary>
    public bool PlanAlreadyProcessed { get; private set; }

    public void MarkPlanProcessed()
    {
        lock (_lock) PlanAlreadyProcessed = true;
    }

    /// <summary>
    /// Set when a plan executed at least one step to completion in this scope. The
    /// filter's post-plan mutation gate then refuses filesystem-mutating calls for the
    /// rest of the turn: the outer model never sees the steps run (each executes in its
    /// own chat history), so it tends to treat the returned summary as "not started yet"
    /// and redo the task — observed live overwriting a finished build under an
    /// auto-approved session. Reads stay allowed; a fresh scope (next turn) resets this.
    /// Distinct from <see cref="PlanAlreadyProcessed"/>, which is also set for rejected
    /// plans — after a rejection the model must do the work directly, so mutations
    /// must stay allowed there.
    /// </summary>
    public bool PlanWorkCompleted { get; private set; }

    public void MarkPlanWorkCompleted()
    {
        lock (_lock) PlanWorkCompleted = true;
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
