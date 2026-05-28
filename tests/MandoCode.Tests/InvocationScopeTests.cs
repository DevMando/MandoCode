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
}
