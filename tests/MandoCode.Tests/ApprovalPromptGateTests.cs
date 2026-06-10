using Xunit;
using MandoCode.Services;

namespace MandoCode.Tests;

/// <summary>
/// Tests the async mutex that serializes approval prompts. Two concurrent approval-gated
/// tool calls must never render two blocking Spectre prompts at once — the second must
/// queue until the first releases. See ApprovalPromptGate for the full failure story.
/// </summary>
public class ApprovalPromptGateTests
{
    [Fact]
    public async Task SecondAcquire_WaitsUntilFirstReleases()
    {
        var gate = new ApprovalPromptGate();

        var first = await gate.AcquireAsync();
        var secondTask = gate.AcquireAsync();

        // Second acquisition must not complete while the first hold is live.
        var winner = await Task.WhenAny(secondTask, Task.Delay(200));
        Assert.NotSame(secondTask, winner);

        first.Dispose();

        var second = await secondTask.WaitAsync(TimeSpan.FromSeconds(5));
        second.Dispose();
    }

    [Fact]
    public async Task QueuedAcquire_IsCancellable()
    {
        var gate = new ApprovalPromptGate();
        using var cts = new CancellationTokenSource();

        var hold = await gate.AcquireAsync();
        var queued = gate.AcquireAsync(cts.Token);

        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);

        // The cancelled waiter must not have consumed the gate — a fresh acquire
        // succeeds once the original hold releases.
        hold.Dispose();
        var next = await gate.AcquireAsync().WaitAsync(TimeSpan.FromSeconds(5));
        next.Dispose();
    }

    [Fact]
    public async Task DoubleDispose_ReleasesOnlyOnce()
    {
        var gate = new ApprovalPromptGate();

        var hold = await gate.AcquireAsync();
        hold.Dispose();
        hold.Dispose(); // must be a no-op, not a second Release

        // If the double-dispose over-released, two concurrent holds would both succeed.
        var a = await gate.AcquireAsync();
        var bTask = gate.AcquireAsync();
        var winner = await Task.WhenAny(bTask, Task.Delay(200));
        Assert.NotSame(bTask, winner);

        a.Dispose();
        (await bTask.WaitAsync(TimeSpan.FromSeconds(5))).Dispose();
    }

    [Fact]
    public async Task ConcurrentHolders_NeverOverlap()
    {
        var gate = new ApprovalPromptGate();
        var insideCount = 0;
        var maxObserved = 0;
        var sync = new object();

        var tasks = Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            using (await gate.AcquireAsync())
            {
                lock (sync) { insideCount++; maxObserved = Math.Max(maxObserved, insideCount); }
                await Task.Delay(20);
                lock (sync) { insideCount--; }
            }
        }));

        await Task.WhenAll(tasks);

        Assert.Equal(1, maxObserved);
    }
}
