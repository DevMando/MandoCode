using MandoCode.Services;
using Xunit;

namespace MandoCode.Tests;

public class TuiStatusTests
{
    [Fact]
    public void PreviewAndActivityUpdates_PreserveSelectedVerbAndSpinner()
    {
        var session = new TuiSession();
        session.SetRunning(true, "Thinking");
        var initial = session.Snapshot();
        Assert.NotEmpty(initial.LoadingMessage);
        Assert.NotEmpty(initial.Spinner.Frames);
        session.SetPreview("partial response");
        session.SetActivity("Reading a file");
        var updated = session.Snapshot();
        Assert.Same(initial.Spinner, updated.Spinner);
        Assert.Equal(initial.LoadingMessage, updated.LoadingMessage);
        Assert.Equal("Reading a file", updated.Activity);
        session.SetRunning(false);
        Assert.Empty(session.Snapshot().LoadingMessage);
        Assert.Empty(session.Snapshot().Preview);
    }

    [Fact]
    public void StatusClock_RefreshesElapsedAndVerbRotation_OnlyWhileRunning()
    {
        var clock = new StatusClock();
        var session = new TuiSession(clock);
        session.SetRunning(true);
        var initial = session.Snapshot();
        clock.Now += TimeSpan.FromSeconds(14);
        session.RefreshStatus();
        Assert.Equal(TimeSpan.FromSeconds(14), session.Snapshot().Elapsed);
        Assert.Equal(initial.LoadingMessage, session.Snapshot().LoadingMessage);
        var revision = session.Revision;
        clock.Now += TimeSpan.FromSeconds(1);
        session.RefreshStatus();
        Assert.True(session.Revision > revision);
        Assert.NotEmpty(session.Snapshot().LoadingMessage);
        Assert.Same(initial.Spinner, session.Snapshot().Spinner);
        session.SetRunning(false);
        revision = session.Revision;
        clock.Now += TimeSpan.FromSeconds(30);
        session.RefreshStatus();
        Assert.Equal(revision, session.Revision);
    }

    private sealed class StatusClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}