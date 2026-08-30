using System.Collections.Concurrent;
using CodexHud.Core;

namespace CodexHud.Core.Tests;

[TestClass]
[DoNotParallelize]
public sealed class RateLimitReconciliationTests
{
    [TestMethod]
    public async Task StartupProbeReplacesOldRateLimitsWithoutChangingContextTimestamp()
    {
        using var directory = new TemporaryDirectory();
        var observedAt = DateTimeOffset.UtcNow.AddHours(-2);
        await TestRollout.WriteAsync(
            directory.RolloutPath("old"),
            TestRollout.SessionMeta("thread-old", rootDesktop: true),
            TestRollout.TokenCount(
                observedAt.ToString("O"),
                50_000,
                258_400,
                80,
                70,
                observedAt.AddMinutes(10).ToUnixTimeSeconds(),
                observedAt.AddHours(10).ToUnixTimeSeconds()));
        var probe = new ScriptedRateLimitProbe();
        var pending = probe.EnqueuePending();

        await using var monitor = new CodexUsageMonitor(
            directory.Path,
            rateLimitProbe: probe);
        await monitor.StartAsync();
        await probe.WaitForCallsAsync(1);
        var contextSnapshot = monitor.State.ActiveSnapshot;
        var startedAt = DateTimeOffset.UtcNow;
        pending.SetResult(ProbeResult(usedPercent: 21, startedAt));
        await monitor.CurrentProbeTask;

        Assert.AreSame(contextSnapshot, monitor.State.ActiveSnapshot);
        Assert.AreEqual(observedAt, monitor.State.ActiveSnapshot?.ObservedAt);
        Assert.AreEqual(RateLimitSource.Probe, monitor.State.RateLimitObservation?.Source);
        Assert.AreEqual(79, monitor.State.CurrentRateLimits?.Primary?.ReportedRemainingPercent);
    }

    [TestMethod]
    public async Task ProbeAfterResetReplacesEstimatedFullValueWithRealSnapshot()
    {
        using var directory = new TemporaryDirectory();
        var now = DateTimeOffset.UtcNow;
        await TestRollout.WriteAsync(
            directory.RolloutPath("reset"),
            TestRollout.SessionMeta("thread-reset", rootDesktop: true),
            TestRollout.TokenCount(
                now.AddMinutes(-10).ToString("O"),
                40_000,
                258_400,
                55,
                30,
                now.AddMinutes(-1).ToUnixTimeSeconds(),
                now.AddDays(2).ToUnixTimeSeconds()));
        var probe = new ScriptedRateLimitProbe();
        probe.EnqueueResult(null);

        await using var monitor = new CodexUsageMonitor(
            directory.Path,
            rateLimitProbe: probe);
        await monitor.StartAsync();
        await probe.WaitForCallsAsync(1);
        await monitor.CurrentProbeTask;
        Assert.IsTrue(
            monitor.State.CurrentRateLimits?.Primary?.EvaluateAt(now).IsEstimatedAfterReset);

        probe.EnqueueResult(ProbeResult(usedPercent: 8, now));
        await monitor.ReconcileEstimatedRateLimitsAsync(now);

        Assert.AreEqual(2, probe.CallCount);
        Assert.AreEqual(RateLimitSource.Probe, monitor.State.RateLimitObservation?.Source);
        Assert.AreEqual(92, monitor.State.CurrentRateLimits?.Primary?.ReportedRemainingPercent);
        Assert.IsFalse(
            monitor.State.CurrentRateLimits?.Primary?.EvaluateAt(now).IsEstimatedAfterReset);
    }

    [TestMethod]
    public async Task RolloutArrivingDuringProbeWinsEvenWhenProbeCompletesLater()
    {
        using var directory = new TemporaryDirectory();
        var firstAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        var path = directory.RolloutPath("race");
        await TestRollout.WriteAsync(
            path,
            TestRollout.SessionMeta("thread-race", rootDesktop: true),
            TestRollout.TokenCount(
                firstAt.ToString("O"),
                40_000,
                258_400,
                20,
                30,
                DateTimeOffset.UtcNow.AddHours(3).ToUnixTimeSeconds(),
                DateTimeOffset.UtcNow.AddDays(2).ToUnixTimeSeconds()));
        var probe = new ScriptedRateLimitProbe();
        var pending = probe.EnqueuePending();

        await using var monitor = new CodexUsageMonitor(
            directory.Path,
            rateLimitProbe: probe);
        await monitor.StartAsync();
        await probe.WaitForCallsAsync(1);
        var probeStartedAt = DateTimeOffset.UtcNow;

        var rolloutAt = DateTimeOffset.UtcNow;
        await File.AppendAllTextAsync(
            path,
            TestRollout.TokenCount(
                rolloutAt.ToString("O"),
                42_000,
                258_400,
                44,
                35,
                rolloutAt.AddHours(3).ToUnixTimeSeconds(),
                rolloutAt.AddDays(2).ToUnixTimeSeconds()) + Environment.NewLine);
        await WaitUntilAsync(() =>
            monitor.State.RateLimitObservation?.Source == RateLimitSource.Rollout &&
            monitor.State.CurrentRateLimits?.Primary?.UsedPercent == 44);

        pending.SetResult(ProbeResult(usedPercent: 1, probeStartedAt));
        await monitor.CurrentProbeTask;

        Assert.AreEqual(RateLimitSource.Rollout, monitor.State.RateLimitObservation?.Source);
        Assert.AreEqual(44d, monitor.State.CurrentRateLimits?.Primary?.UsedPercent);
    }

    [TestMethod]
    public async Task OlderRolloutCannotOverwriteNewerProbeSnapshot()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.RolloutPath("precedence");
        var oldAt = DateTimeOffset.UtcNow.AddHours(-3);
        await TestRollout.WriteAsync(
            path,
            TestRollout.SessionMeta("thread-precedence", rootDesktop: true),
            TestRollout.TokenCount(oldAt.ToString("O"), 30_000, 258_400, 70, 60));
        var probe = new ScriptedRateLimitProbe();
        probe.EnqueueResult(ProbeResult(usedPercent: 12, DateTimeOffset.UtcNow));

        await using var monitor = new CodexUsageMonitor(
            directory.Path,
            rateLimitProbe: probe);
        await monitor.StartAsync();
        await probe.WaitForCallsAsync(1);
        await monitor.CurrentProbeTask;
        Assert.AreEqual(RateLimitSource.Probe, monitor.State.RateLimitObservation?.Source);

        await File.AppendAllTextAsync(
            path,
            TestRollout.TokenCount(
                oldAt.AddMinutes(1).ToString("O"),
                31_000,
                258_400,
                99,
                99) + Environment.NewLine);
        await Task.Delay(250);

        Assert.AreEqual(RateLimitSource.Probe, monitor.State.RateLimitObservation?.Source);
        Assert.AreEqual(12d, monitor.State.CurrentRateLimits?.Primary?.UsedPercent);
    }

    [TestMethod]
    public async Task ConcurrentResetRequestsShareSingleProbe()
    {
        using var directory = new TemporaryDirectory();
        var now = DateTimeOffset.UtcNow;
        await TestRollout.WriteAsync(
            directory.RolloutPath("single-flight"),
            TestRollout.SessionMeta("thread-single-flight", rootDesktop: true),
            TestRollout.TokenCount(
                now.AddMinutes(-2).ToString("O"),
                10_000,
                258_400,
                20,
                20,
                now.AddMinutes(-1).ToUnixTimeSeconds(),
                now.AddDays(2).ToUnixTimeSeconds()));
        var probe = new ScriptedRateLimitProbe();
        probe.EnqueueResult(null);

        await using var monitor = new CodexUsageMonitor(
            directory.Path,
            rateLimitProbe: probe);
        await monitor.StartAsync();
        await probe.WaitForCallsAsync(1);
        await monitor.CurrentProbeTask;
        var pending = probe.EnqueuePending();

        var first = monitor.ReconcileEstimatedRateLimitsAsync(now);
        var second = monitor.ReconcileEstimatedRateLimitsAsync(now);
        await probe.WaitForCallsAsync(2);
        Assert.AreEqual(2, probe.CallCount);

        pending.SetResult(null);
        await Task.WhenAll(first, second);
        Assert.AreEqual(2, probe.CallCount);
    }

    private static RateLimitProbeResult ProbeResult(
        double usedPercent,
        DateTimeOffset startedAt)
    {
        var limits = new RateLimits(
            "codex",
            new RateLimitWindow(
                usedPercent,
                300,
                startedAt.AddHours(5)),
            new RateLimitWindow(
                25,
                10_080,
                startedAt.AddDays(7)));
        return new RateLimitProbeResult(limits, startedAt, DateTimeOffset.UtcNow.AddMilliseconds(1));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            await Task.Delay(25, timeout.Token);
        }
    }

    private sealed class ScriptedRateLimitProbe : IRateLimitProbe
    {
        private readonly ConcurrentQueue<Func<CancellationToken, Task<RateLimitProbeResult?>>> _steps = new();
        private readonly SemaphoreSlim _calls = new(0);
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public TaskCompletionSource<RateLimitProbeResult?> EnqueuePending()
        {
            var pending = new TaskCompletionSource<RateLimitProbeResult?>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _steps.Enqueue(token => pending.Task.WaitAsync(token));
            return pending;
        }

        public void EnqueueResult(RateLimitProbeResult? result) =>
            _steps.Enqueue(_ => Task.FromResult(result));

        public async Task<RateLimitProbeResult?> ReadAsync(
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _callCount);
            _calls.Release();
            if (!_steps.TryDequeue(out var step))
            {
                return null;
            }

            return await step(cancellationToken);
        }

        public async Task WaitForCallsAsync(int count)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (CallCount < count)
            {
                await _calls.WaitAsync(timeout.Token);
            }
        }
    }
}
