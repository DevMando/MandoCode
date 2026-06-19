using Xunit;
using MandoCode.Services;

namespace MandoCode.Tests;

public class InvocationScopeTests
{
    [Fact]
    public void IsRedundantRead_FalseOnFirstRead()
    {
        var scope = new InvocationScope(400_000);
        Assert.False(scope.IsRedundantRead("read:foo.cs", "foo.cs"));
    }

    [Fact]
    public void IsRedundantRead_TrueOnSecondRead_NoWriteBetween()
    {
        var scope = new InvocationScope(400_000);
        scope.RecordRead("read:foo.cs", "foo.cs");
        Assert.True(scope.IsRedundantRead("read:foo.cs", "foo.cs"));
    }

    [Fact]
    public void IsRedundantRead_FalseAfterWriteInvalidatesRead()
    {
        var scope = new InvocationScope(400_000);
        scope.RecordRead("read:foo.cs", "foo.cs");
        scope.RecordWrite("foo.cs");
        // After a write, re-reading is legitimate (content may have changed).
        Assert.False(scope.IsRedundantRead("read:foo.cs", "foo.cs"));
    }

    [Fact]
    public void IsRedundantRead_DifferentPathsTrackedIndependently()
    {
        var scope = new InvocationScope(400_000);
        scope.RecordRead("read:foo.cs", "foo.cs");
        Assert.False(scope.IsRedundantRead("read:bar.cs", "bar.cs"));
    }

    [Fact]
    public void IsRedundantRead_IsCaseInsensitive()
    {
        // Windows treats src/Foo.cs and SRC/FOO.CS as the same file. The scope must agree,
        // or a model alternating casing would bypass the dup-read circuit.
        var scope = new InvocationScope(400_000);
        scope.RecordRead("read:src/Foo.cs", "src/Foo.cs");
        Assert.True(scope.IsRedundantRead("read:SRC/FOO.CS", "SRC/FOO.CS"));
    }

    [Fact]
    public void BudgetExhausted_FiresOnceTotalExceedsBudget()
    {
        var scope = new InvocationScope(100);

        scope.RecordResultChars(40);
        Assert.False(scope.BudgetExhausted);

        scope.RecordResultChars(40);
        Assert.False(scope.BudgetExhausted);

        scope.RecordResultChars(40);
        // 40+40+40 = 120, past the 100 budget.
        Assert.True(scope.BudgetExhausted);
    }

    [Fact]
    public void RecordResultChars_IgnoresNonPositive()
    {
        var scope = new InvocationScope(100);
        scope.RecordResultChars(0);
        scope.RecordResultChars(-50);
        Assert.False(scope.BudgetExhausted);
        Assert.Equal(0, scope.TotalResultChars);
    }

    [Fact]
    public void RecordRead_AfterWrite_ResetsModifiedFlag()
    {
        // After writing, then reading, a *subsequent* read without another write
        // should be redundant again.
        var scope = new InvocationScope(400_000);
        scope.RecordRead("read:foo.cs", "foo.cs");
        scope.RecordWrite("foo.cs");
        scope.RecordRead("read:foo.cs", "foo.cs"); // Legit re-read after write.

        // Now reading again with no further write → redundant.
        Assert.True(scope.IsRedundantRead("read:foo.cs", "foo.cs"));
    }

    [Fact]
    public void Dispose_WithoutHook_DoesNotThrow()
    {
        var scope = new InvocationScope(100);
        scope.Dispose();
        scope.Dispose(); // Idempotent.
    }

    [Fact]
    public void PlanCancellationRequested_DefaultsToFalse()
    {
        var scope = new InvocationScope(100);
        Assert.False(scope.PlanCancellationRequested);
    }

    [Fact]
    public void RequestPlanCancellation_SetsFlag()
    {
        var scope = new InvocationScope(100);
        scope.RequestPlanCancellation();
        Assert.True(scope.PlanCancellationRequested);
    }

    // ────────────────────────────────────────────────
    //  Edit-hint dedup
    // ────────────────────────────────────────────────

    [Fact]
    public void HasEmittedEditHint_DefaultsToFalse()
    {
        var scope = new InvocationScope(100);
        Assert.False(scope.HasEmittedEditHint("foo.cs"));
    }

    [Fact]
    public void MarkEditHintEmitted_PersistsAcrossChecks()
    {
        var scope = new InvocationScope(100);
        scope.MarkEditHintEmitted("foo.cs");
        Assert.True(scope.HasEmittedEditHint("foo.cs"));
    }

    [Fact]
    public void HasEmittedEditHint_TracksPathsIndependently()
    {
        var scope = new InvocationScope(100);
        scope.MarkEditHintEmitted("foo.cs");
        Assert.False(scope.HasEmittedEditHint("bar.cs"));
    }

    [Fact]
    public void HasEmittedEditHint_IsCaseInsensitive()
    {
        // Aligns with the read-dedup behavior — Windows treats src/Foo.cs and SRC/FOO.CS
        // as the same file, and the hint dedup must agree or the model gets the content
        // blob twice for the same file under different casing.
        var scope = new InvocationScope(100);
        scope.MarkEditHintEmitted("src/Foo.cs");
        Assert.True(scope.HasEmittedEditHint("SRC/FOO.CS"));
    }

    [Fact]
    public void RecordWrite_ClearsEditHintFlag()
    {
        // After a write, the file content changed — the previously-shown hint is stale,
        // so the next failure should emit fresh content again.
        var scope = new InvocationScope(100);
        scope.MarkEditHintEmitted("foo.cs");
        scope.RecordWrite("foo.cs");
        Assert.False(scope.HasEmittedEditHint("foo.cs"));
    }

    // ────────────────────────────────────────────────
    //  Edit-failure circuit
    // ────────────────────────────────────────────────

    [Fact]
    public void GetEditFailureCount_DefaultsToZero()
    {
        var scope = new InvocationScope(100);
        Assert.Equal(0, scope.GetEditFailureCount("foo.cs"));
    }

    [Fact]
    public void RecordEditFailure_ReturnsPostIncrementCount()
    {
        var scope = new InvocationScope(100);
        Assert.Equal(1, scope.RecordEditFailure("foo.cs"));
        Assert.Equal(2, scope.RecordEditFailure("foo.cs"));
        Assert.Equal(3, scope.RecordEditFailure("foo.cs"));
    }

    [Fact]
    public void EditFailureCount_TracksPathsIndependently()
    {
        var scope = new InvocationScope(100);
        scope.RecordEditFailure("foo.cs");
        scope.RecordEditFailure("foo.cs");
        scope.RecordEditFailure("bar.cs");

        Assert.Equal(2, scope.GetEditFailureCount("foo.cs"));
        Assert.Equal(1, scope.GetEditFailureCount("bar.cs"));
    }

    [Fact]
    public void RecordWrite_ClearsEditFailureCount()
    {
        // A successful write means the file changed — past failure attempts no longer
        // describe its current state, so the streak resets.
        var scope = new InvocationScope(100);
        scope.RecordEditFailure("foo.cs");
        scope.RecordEditFailure("foo.cs");
        scope.RecordWrite("foo.cs");

        Assert.Equal(0, scope.GetEditFailureCount("foo.cs"));
    }

    [Fact]
    public void EditFailureCircuitThreshold_IsThree()
    {
        // The circuit constant lives in InvocationScope and is consumed by
        // FunctionInvocationFilter. This test pins the value so an accidental tweak
        // is visible in code review — three is a deliberate sweet spot between
        // "tolerate a normal retry" and "catch a thrash loop fast."
        Assert.Equal(3, InvocationScope.EditFailureCircuitThreshold);
    }

    // ────────────────────────────────────────────────
    //  Interval-coverage read dedup
    // ────────────────────────────────────────────────

    [Fact]
    public void IsReadRangeCovered_FalseWhenNothingRecorded()
    {
        var scope = new InvocationScope(400_000);
        Assert.False(scope.IsReadRangeCovered("foo.cs", 1, 100));
    }

    [Fact]
    public void IsReadRangeCovered_TrueForRangeContainedInDeliveredSlice()
    {
        // Delivered lines 1-200 of a 510-line file; a re-read of 1-100 returns nothing new.
        // This is the exact loop that exact-key dedup misses (different endLine = new key).
        var scope = new InvocationScope(400_000);
        scope.RecordReadRange("style.css", 1, 200, 510);
        Assert.True(scope.IsReadRangeCovered("style.css", 1, 100));
    }

    [Fact]
    public void IsReadRangeCovered_FalseForRangeThatExtendsPastWhatWasSeen()
    {
        // Paging forward (1-200 seen, now asking through 311) reaches NEW content — must allow.
        var scope = new InvocationScope(400_000);
        scope.RecordReadRange("style.css", 1, 200, 510);
        Assert.False(scope.IsReadRangeCovered("style.css", 1, 311));
    }

    [Fact]
    public void IsReadRangeCovered_FalseForNewRegionPastTruncation()
    {
        // Classic large-file paging: saw 1-312, now reading from 313 onward — never redundant.
        var scope = new InvocationScope(400_000);
        scope.RecordReadRange("big.cs", 1, 312, 1051);
        Assert.False(scope.IsReadRangeCovered("big.cs", 313, null));
    }

    [Fact]
    public void IsReadRangeCovered_UnboundedReread_TrueOnceWholeFileSeen()
    {
        // A whole-file read (start=1, end=total) marks EOF reached; an unbounded re-read of
        // the unchanged file can't return anything new, so it's redundant.
        var scope = new InvocationScope(400_000);
        scope.RecordReadRange("index.html", 1, 113, 113);
        Assert.True(scope.IsReadRangeCovered("index.html", 1, null));
    }

    [Fact]
    public void IsReadRangeCovered_UnboundedReread_FalseWhenFileNotFullySeen()
    {
        // Only a truncated prefix seen; an unbounded re-read might reach new lines, so allow it
        // (the no-progress circuit catches a genuine loop instead).
        var scope = new InvocationScope(400_000);
        scope.RecordReadRange("big.cs", 1, 312, 1051);
        Assert.False(scope.IsReadRangeCovered("big.cs", 1, null));
    }

    [Fact]
    public void IsReadRangeCovered_FalseAfterWriteInvalidatesCoverage()
    {
        var scope = new InvocationScope(400_000);
        scope.RecordReadRange("foo.cs", 1, 200, 200);
        scope.RecordWrite("foo.cs");
        // File changed — re-reading any range is legitimate again.
        Assert.False(scope.IsReadRangeCovered("foo.cs", 1, 100));
    }

    [Fact]
    public void IsReadRangeCovered_MergesAdjacentDeliveredRanges()
    {
        // 1-100 then 101-200 delivered separately; together they cover 1-200 contiguously.
        var scope = new InvocationScope(400_000);
        scope.RecordReadRange("foo.cs", 1, 100, 400);
        scope.RecordReadRange("foo.cs", 101, 200, 400);
        Assert.True(scope.IsReadRangeCovered("foo.cs", 1, 200));
    }

    [Fact]
    public void IsReadRangeCovered_FalseWhenGapBetweenDeliveredRanges()
    {
        // 1-100 and 150-200 seen, with 101-149 never delivered — a read spanning the gap
        // still reaches unseen lines.
        var scope = new InvocationScope(400_000);
        scope.RecordReadRange("foo.cs", 1, 100, 400);
        scope.RecordReadRange("foo.cs", 150, 200, 400);
        Assert.False(scope.IsReadRangeCovered("foo.cs", 1, 200));
    }

    [Fact]
    public void IsReadRangeCovered_IsCaseInsensitive()
    {
        var scope = new InvocationScope(400_000);
        scope.RecordReadRange("src/Foo.cs", 1, 200, 200);
        Assert.True(scope.IsReadRangeCovered("SRC/FOO.CS", 1, 100));
    }

    // ────────────────────────────────────────────────
    //  No-progress read loop circuit
    // ────────────────────────────────────────────────

    [Fact]
    public void ReadLoopTripped_DefaultsToFalse()
    {
        var scope = new InvocationScope(400_000);
        Assert.False(scope.ReadLoopTripped);
        Assert.Equal(0, scope.ReadsSinceMutation);
    }

    [Fact]
    public void ReadLoopTripped_TrueAtThreshold()
    {
        var scope = new InvocationScope(400_000);
        for (var i = 0; i < InvocationScope.ReadLoopCircuitThreshold; i++)
            scope.RecordRead($"read:f{i}.cs", $"f{i}.cs");

        Assert.Equal(InvocationScope.ReadLoopCircuitThreshold, scope.ReadsSinceMutation);
        Assert.True(scope.ReadLoopTripped);
    }

    [Fact]
    public void ReadLoop_ResetsOnWrite()
    {
        var scope = new InvocationScope(400_000);
        for (var i = 0; i < InvocationScope.ReadLoopCircuitThreshold; i++)
            scope.RecordRead($"read:f{i}.cs", $"f{i}.cs");
        Assert.True(scope.ReadLoopTripped);

        // A mutation is forward progress — the counter resets.
        scope.RecordWrite("game.js");
        Assert.False(scope.ReadLoopTripped);
        Assert.Equal(0, scope.ReadsSinceMutation);
    }

    [Fact]
    public void ReadLoopCircuitThreshold_IsTwenty()
    {
        // Pins the constant so a change is visible in review — generous enough that a
        // well-formed step (read a few files, then act) never trips it.
        Assert.Equal(20, InvocationScope.ReadLoopCircuitThreshold);
    }
}
