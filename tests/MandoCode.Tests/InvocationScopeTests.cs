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
}
