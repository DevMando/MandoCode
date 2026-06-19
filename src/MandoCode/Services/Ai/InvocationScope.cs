using System.Collections.Concurrent;
using System.Linq;

namespace MandoCode.Services;

/// <summary>
/// Per-chat/per-step bookkeeping used by <see cref="FunctionInvocationFilter"/>
/// to catch pathological tool-call loops that would otherwise fill the model's
/// context window before the step finishes.
///
/// Circuits:
///   1. Duplicate-read detection — a second <c>read_file_contents</c> is short-circuited
///      (unless the path was written/edited since) when its args are byte-identical
///      (<see cref="IsRedundantRead"/>) OR its range is already covered by what we've
///      delivered (<see cref="IsReadRangeCovered"/>) — catching the "re-read the same
///      unchanged file in a slightly different slice" loop while still allowing paging.
///   2. No-progress read loop — too many delivered reads with no intervening mutation
///      (<see cref="ReadLoopTripped"/>) is a stuck analysis loop; the filter then steers
///      the model to act. Backstops shapes #1 can't prove redundant.
///   3. Duplicate web-call detection — a second <c>search_web</c>/<c>fetch_webpage</c>
///      with the same normalized query/URL is short-circuited (no escape hatch — the
///      result is already in history and nothing in-scope changes it).
///   4. Result-budget tracking — accumulated tool-result characters are capped
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
    // Per-path record of which line ranges have actually been DELIVERED this scope, plus the
    // file's known total line count. Drives interval-coverage read dedup: a re-read whose
    // range is already covered by what we've delivered (and the file hasn't changed) returns
    // nothing new, so it's refused — while a read of a genuinely NEW region (paging forward
    // past a truncation point) is still allowed. The exact-key _readKeys set above only
    // catches byte-identical args; this catches the "same unchanged file, different/overlapping
    // slice" loop that re-reads from the top with a varying endLine.
    private readonly Dictionary<string, ReadCoverage> _readCoverage = new(StringComparer.OrdinalIgnoreCase);
    // Successful (delivered) reads since the last filesystem mutation in this scope. Backstops
    // the no-progress read loop: a model that keeps reading without ever writing/editing is
    // stuck. Refused reads never increment this (they short-circuit before recording), so it
    // counts real content reads only — and interval-coverage refusals above keep it honest.
    private int _readsSinceMutation;
    private readonly object _lock = new();
    private Action? _onDispose;

    /// <summary>Tracks the delivered line ranges and known total for one file this scope.</summary>
    private sealed class ReadCoverage
    {
        public readonly List<(int Start, int End)> Intervals = new();
        public int Total;          // highest "of T" line count seen; 0 = unknown
        public bool ReachedEof;    // true once a delivered range reached the last line
    }

    /// <summary>
    /// After how many delivered reads with NO intervening write/edit/delete the no-progress
    /// circuit refuses further reads and steers the model to produce output. Generous on
    /// purpose: a well-formed step reads a handful of files then acts, so this only trips on
    /// a genuine read loop. Interval-coverage dedup is the primary guard; this is the backstop
    /// for shapes it can't prove redundant (unbounded re-reads of a truncated file, reads of
    /// many distinct files, results with no parseable line header).
    /// </summary>
    public const int ReadLoopCircuitThreshold = 20;

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
            _readsSinceMutation++;
        }
    }

    /// <summary>
    /// Records the line range a read actually DELIVERED (parsed from the result header),
    /// plus the file's total line count when known. Call alongside <see cref="RecordRead"/>
    /// whenever the delivered range is parseable; pass total=0 when unknown.
    /// </summary>
    public void RecordReadRange(string path, int start, int end, int total)
    {
        if (end < start) return;
        lock (_lock)
        {
            if (!_readCoverage.TryGetValue(path, out var cov))
                _readCoverage[path] = cov = new ReadCoverage();
            cov.Intervals.Add((start, end));
            if (total > cov.Total) cov.Total = total;
            if (cov.Total > 0 && end >= cov.Total) cov.ReachedEof = true;
        }
    }

    /// <summary>
    /// True when re-reading <paramref name="path"/> over [<paramref name="reqStart"/>,
    /// <paramref name="reqEnd"/>] would return only lines already delivered this scope and
    /// the file hasn't changed since — i.e. the read is redundant. A null <paramref name="reqEnd"/>
    /// means "to end of file"; that can only be judged redundant once we've delivered through
    /// EOF (otherwise the re-read might reach new lines past a truncation point, so we allow it
    /// and let the no-progress circuit catch a true loop). Reads of a genuinely new region
    /// return false, preserving large-file paging.
    /// </summary>
    public bool IsReadRangeCovered(string path, int reqStart, int? reqEnd)
    {
        lock (_lock)
        {
            // A write/edit since the last read legitimately changes the content — allow.
            if (_pathsModifiedSinceRead.Contains(path)) return false;
            if (!_readCoverage.TryGetValue(path, out var cov) || cov.Intervals.Count == 0) return false;

            var effectiveStart = Math.Max(1, reqStart);
            int effectiveEnd;
            if (reqEnd.HasValue)
                effectiveEnd = reqEnd.Value;
            else if (cov.Total > 0 && cov.ReachedEof)
                effectiveEnd = cov.Total;          // unbounded read, but we've seen the whole file
            else
                return false;                      // unbounded read on a not-fully-seen file — can't prove redundant

            if (cov.Total > 0) effectiveEnd = Math.Min(effectiveEnd, cov.Total);
            if (effectiveStart > effectiveEnd) return false;

            // Contiguous-coverage sweep from effectiveStart over the merged intervals.
            var reach = effectiveStart - 1;
            foreach (var (s, e) in cov.Intervals.OrderBy(i => i.Start))
            {
                if (s > reach + 1) break;          // gap at reach+1 that nothing later can fill (sorted by start)
                if (e > reach) reach = e;
                if (reach >= effectiveEnd) return true;
            }
            return reach >= effectiveEnd;
        }
    }

    /// <summary>
    /// True once <see cref="ReadLoopCircuitThreshold"/> delivered reads have happened with no
    /// intervening write/edit/delete. The filter then refuses further reads and steers the
    /// model to act on what it already has.
    /// </summary>
    public bool ReadLoopTripped
    {
        get { lock (_lock) return _readsSinceMutation >= ReadLoopCircuitThreshold; }
    }

    public int ReadsSinceMutation
    {
        get { lock (_lock) return _readsSinceMutation; }
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
            // The file changed, so previously-delivered ranges no longer describe it.
            _readCoverage.Remove(path);
            // A mutation is progress — reset the no-progress read counter.
            _readsSinceMutation = 0;
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
