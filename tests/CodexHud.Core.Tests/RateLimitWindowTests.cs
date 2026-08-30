using CodexHud.Core;

namespace CodexHud.Core.Tests;

[TestClass]
public sealed class RateLimitWindowTests
{
    [TestMethod]
    public void UsesReportedPercentageBeforeReset()
    {
        var reset = new DateTimeOffset(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);
        var window = new RateLimitWindow(UsedPercent: 55, WindowMinutes: 300, ResetsAt: reset);

        var result = window.EvaluateAt(reset.AddSeconds(-1));

        Assert.AreEqual(45, result.RemainingPercent);
        Assert.IsFalse(result.IsEstimatedAfterReset);
    }

    [TestMethod]
    public void EstimatesFullWindowAtAndAfterReset()
    {
        var reset = new DateTimeOffset(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);
        var window = new RateLimitWindow(UsedPercent: 55, WindowMinutes: 300, ResetsAt: reset);

        var atReset = window.EvaluateAt(reset);
        var afterReset = window.EvaluateAt(reset.AddHours(1));

        Assert.AreEqual(100, atReset.RemainingPercent);
        Assert.IsTrue(atReset.IsEstimatedAfterReset);
        Assert.AreEqual(100, afterReset.RemainingPercent);
        Assert.IsTrue(afterReset.IsEstimatedAfterReset);
    }

    [TestMethod]
    public void DoesNotInventEstimateWithoutResetTimestamp()
    {
        var window = new RateLimitWindow(UsedPercent: null, WindowMinutes: 300, ResetsAt: null);

        var result = window.EvaluateAt(DateTimeOffset.UtcNow);

        Assert.IsNull(result.RemainingPercent);
        Assert.IsFalse(result.IsEstimatedAfterReset);
    }

    [TestMethod]
    public void DoesNotInventPostResetEstimateWithoutMeasuredPercentage()
    {
        var reset = DateTimeOffset.UtcNow.AddMinutes(-1);
        var window = new RateLimitWindow(UsedPercent: null, WindowMinutes: 300, ResetsAt: reset);

        var result = window.EvaluateAt(DateTimeOffset.UtcNow);

        Assert.IsNull(result.RemainingPercent);
        Assert.IsFalse(result.IsEstimatedAfterReset);
    }
}
