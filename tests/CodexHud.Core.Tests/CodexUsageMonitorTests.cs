using CodexHud.Core;

namespace CodexHud.Core.Tests;

[TestClass]
[DoNotParallelize]
public sealed class CodexUsageMonitorTests
{
    [TestMethod]
    public async Task SelectsMostRecentActiveDesktopRootAndIgnoresSubagents()
    {
        using var directory = new TemporaryDirectory();
        var olderRoot = directory.RolloutPath("older-root");
        var newerSubagent = directory.RolloutPath("newer-subagent");

        await TestRollout.WriteAsync(
            olderRoot,
            TestRollout.SessionMeta("root-a", rootDesktop: true),
            TestRollout.TokenCount("2026-08-30T10:00:00Z", 50_000, 258_400, 10, 20));
        await TestRollout.WriteAsync(
            newerSubagent,
            TestRollout.SessionMeta("agent", rootDesktop: false),
            TestRollout.TokenCount("2026-08-30T11:00:00Z", 100, 1_000, 99, 99));
        File.SetLastWriteTimeUtc(olderRoot, new DateTime(2026, 8, 30, 10, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(newerSubagent, new DateTime(2026, 8, 30, 11, 0, 0, DateTimeKind.Utc));

        await using var monitor = CreateMonitor(directory.Path);
        await monitor.StartAsync();

        Assert.AreEqual("root-a", monitor.State.ActiveSnapshot?.ThreadId);
        Assert.AreEqual(50_000, monitor.State.ActiveSnapshot?.LastTokenUsage.TotalTokens);
    }

    [TestMethod]
    public async Task NewThreadWinsOnlyAfterItProducesUsageActivity()
    {
        using var directory = new TemporaryDirectory();
        var first = directory.RolloutPath("first");
        await TestRollout.WriteAsync(
            first,
            TestRollout.SessionMeta("thread-a", rootDesktop: true),
            TestRollout.TokenCount("2026-08-30T10:00:00Z", 40_000, 258_400, 10, 20));

        await using var monitor = CreateMonitor(directory.Path);
        await monitor.StartAsync();
        Assert.AreEqual("thread-a", monitor.State.ActiveSnapshot?.ThreadId);

        var second = directory.RolloutPath("second");
        await TestRollout.WriteAsync(second, TestRollout.SessionMeta("thread-b", rootDesktop: true));
        await Task.Delay(200);
        Assert.AreEqual("thread-a", monitor.State.ActiveSnapshot?.ThreadId);

        await File.AppendAllTextAsync(
            second,
            TestRollout.TokenCount("2026-08-30T12:00:00Z", 28_000, 258_400, 16, 39) + Environment.NewLine);
        await WaitUntilAsync(() => monitor.State.ActiveSnapshot?.ThreadId == "thread-b");

        Assert.AreEqual(89, monitor.State.ActiveSnapshot?.ContextRemainingPercent);
        Assert.AreEqual(84, monitor.State.ActiveSnapshot?.RateLimits?.Primary?.ReportedRemainingPercent);
        Assert.AreEqual(61, monitor.State.ActiveSnapshot?.RateLimits?.Secondary?.ReportedRemainingPercent);
    }

    [TestMethod]
    public async Task PartialLineAndConversationRecordsDoNotPublishSnapshots()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.RolloutPath("partial");
        await TestRollout.WriteAsync(path, TestRollout.SessionMeta("thread-partial", rootDesktop: true));

        await using var monitor = CreateMonitor(directory.Path);
        await monitor.StartAsync();
        Assert.IsNull(monitor.State.ActiveSnapshot);

        await File.AppendAllTextAsync(
            path,
            "{\"type\":\"response_item\",\"payload\":{\"type\":\"function_call\",\"arguments\":\"ignored\"}}\n");
        await Task.Delay(150);
        Assert.IsNull(monitor.State.ActiveSnapshot);

        var tokenLine = TestRollout.TokenCount("2026-08-30T12:30:00Z", 30_000, 258_400, 12, 22);
        var split = tokenLine.Length / 2;
        await File.AppendAllTextAsync(path, tokenLine[..split]);
        await Task.Delay(200);
        Assert.IsNull(monitor.State.ActiveSnapshot);

        await File.AppendAllTextAsync(path, tokenLine[split..] + "\n");
        await WaitUntilAsync(() => monitor.State.ActiveSnapshot is not null);
        Assert.AreEqual(30_000, monitor.State.ActiveSnapshot?.LastTokenUsage.TotalTokens);
    }

    [TestMethod]
    public async Task LaterTokenSnapshotUpdatesSameThreadAndCompactionCanLowerUsage()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.RolloutPath("compact");
        await TestRollout.WriteAsync(
            path,
            TestRollout.SessionMeta("thread-compact", rootDesktop: true),
            TestRollout.TokenCount("2026-08-30T10:00:00Z", 120_000, 258_400, 10, 20));

        await using var monitor = CreateMonitor(directory.Path);
        await monitor.StartAsync();
        var before = monitor.State.ActiveSnapshot?.ContextRemainingPercent;

        // A post-compaction last_token_usage may drop even though cumulative usage does not.
        await File.AppendAllTextAsync(
            path,
            TestRollout.TokenCount("2026-08-30T10:01:00Z", 25_000, 258_400, 11, 21) + "\n");
        await WaitUntilAsync(() => monitor.State.ActiveSnapshot?.LastTokenUsage.TotalTokens == 25_000);

        Assert.IsGreaterThan(
            before!.Value,
            monitor.State.ActiveSnapshot!.ContextRemainingPercent!.Value);
    }

    [TestMethod]
    public async Task ExistingSnapshotRemainsStableWhenNoWriterIsRunning()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.RolloutPath("closed");
        await TestRollout.WriteAsync(
            path,
            TestRollout.SessionMeta("thread-closed", rootDesktop: true),
            TestRollout.TokenCount("2026-08-30T10:00:00Z", 35_000, 258_400, 10, 20));

        await using var monitor = CreateMonitor(directory.Path);
        await monitor.StartAsync();
        var snapshot = monitor.State.ActiveSnapshot;
        await Task.Delay(250);

        Assert.AreSame(snapshot, monitor.State.ActiveSnapshot);
    }

    [TestMethod]
    public async Task InvalidTokenSnapshotDoesNotTerminateOrReplaceValidState()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.RolloutPath("invalid");
        await TestRollout.WriteAsync(
            path,
            TestRollout.SessionMeta("thread-valid", rootDesktop: true),
            TestRollout.TokenCount("2026-08-30T10:00:00Z", 35_000, 258_400, 10, 20));

        await using var monitor = CreateMonitor(directory.Path);
        await monitor.StartAsync();
        var valid = monitor.State.ActiveSnapshot;

        await File.AppendAllTextAsync(
            path,
            "{\"timestamp\":\"2026-08-30T11:00:00Z\",\"type\":\"event_msg\",\"payload\":{" +
            "\"type\":\"token_count\",\"info\":{\"last_token_usage\":{\"total_tokens\":\"invalid\"}," +
            "\"model_context_window\":-1},\"rate_limits\":null}}\n");
        await Task.Delay(200);

        Assert.AreSame(valid, monitor.State.ActiveSnapshot);
        Assert.AreEqual(35_000, monitor.State.ActiveSnapshot?.LastTokenUsage.TotalTokens);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            await Task.Delay(25, timeout.Token);
        }
    }

    private static CodexUsageMonitor CreateMonitor(string sessionsDirectory) =>
        new(
            sessionsDirectory,
            rateLimitProbe: NoOpRateLimitProbe.Instance);
}
