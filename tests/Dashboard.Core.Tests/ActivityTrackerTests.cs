using Dashboard.Application.Idle;

namespace Dashboard.Core.Tests;

public class ActivityTrackerTests
{
    [Fact]
    public void LastActivityUtc_ReturnsNullForUntouchedSource()
    {
        var tracker = new ActivityTracker();
        Assert.Null(tracker.LastActivityUtc("never-seen"));
    }

    [Fact]
    public void Touch_RecordsACurrentUtcTimestamp()
    {
        var tracker = new ActivityTracker();
        var before = DateTime.UtcNow;
        tracker.Touch("src-1");
        var after = DateTime.UtcNow;

        var recorded = tracker.LastActivityUtc("src-1");
        Assert.NotNull(recorded);
        Assert.InRange(recorded!.Value, before.AddMilliseconds(-5), after.AddMilliseconds(5));
    }

    [Fact]
    public void Touch_TracksEachSourceIndependently()
    {
        var tracker = new ActivityTracker();
        tracker.Touch("src-1");
        Assert.Null(tracker.LastActivityUtc("src-2"));
    }

    [Fact]
    public void Touch_OverwritesThePreviousTimestampForTheSameSource()
    {
        var tracker = new ActivityTracker();
        tracker.Touch("src-1");
        var first = tracker.LastActivityUtc("src-1");
        Thread.Sleep(10);
        tracker.Touch("src-1");
        var second = tracker.LastActivityUtc("src-1");
        Assert.True(second > first);
    }
}
