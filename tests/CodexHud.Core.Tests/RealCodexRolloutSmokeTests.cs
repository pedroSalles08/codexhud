using CodexHud.Core;

namespace CodexHud.Core.Tests;

[TestClass]
[DoNotParallelize]
[TestCategory("LocalIntegration")]
public sealed class RealCodexRolloutSmokeTests
{
    public TestContext TestContext { get; set; }

    [TestMethod]
    public async Task ReadsLatestLocalDesktopSnapshotWithoutConversationContent()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("CODEX_HUD_REAL_SMOKE"),
                "1",
                StringComparison.Ordinal))
        {
            Assert.Inconclusive("Set CODEX_HUD_REAL_SMOKE=1 to read the local Codex sessions directory.");
        }

        await using var monitor = new CodexUsageMonitor();
        await monitor.StartAsync();
        var rolloutLimits = monitor.State.RateLimitObservation;
        await monitor.CurrentProbeTask;
        var snapshot = monitor.State.ActiveSnapshot;

        Assert.IsNotNull(snapshot, "No root Codex Desktop token_count snapshot was found.");
        var now = DateTimeOffset.UtcNow;
        var limits = monitor.State.CurrentRateLimits;
        var primary = limits?.Primary?.EvaluateAt(now);
        var secondary = limits?.Secondary?.EvaluateAt(now);
        TestContext.WriteLine($"thread_id={snapshot.ThreadId}");
        TestContext.WriteLine($"last_total_tokens={snapshot.LastTokenUsage.TotalTokens?.ToString() ?? "null"}");
        TestContext.WriteLine($"model_context_window={snapshot.ModelContextWindow?.ToString() ?? "null"}");
        TestContext.WriteLine($"context_remaining={snapshot.ContextRemainingPercent?.ToString() ?? "null"}");
        TestContext.WriteLine($"rate_limit_source={monitor.State.RateLimitObservation?.Source.ToString() ?? "null"}");
        TestContext.WriteLine($"primary_reported_remaining={limits?.Primary?.ReportedRemainingPercent?.ToString() ?? "null"}");
        TestContext.WriteLine($"primary_display_remaining={primary?.RemainingPercent?.ToString() ?? "null"}");
        TestContext.WriteLine($"primary_is_estimated={primary?.IsEstimatedAfterReset.ToString() ?? "null"}");
        TestContext.WriteLine($"secondary_reported_remaining={limits?.Secondary?.ReportedRemainingPercent?.ToString() ?? "null"}");
        TestContext.WriteLine($"secondary_display_remaining={secondary?.RemainingPercent?.ToString() ?? "null"}");
        TestContext.WriteLine($"secondary_is_estimated={secondary?.IsEstimatedAfterReset.ToString() ?? "null"}");
        TestContext.WriteLine($"primary_resets_at={limits?.Primary?.ResetsAt?.ToString("O") ?? "null"}");
        TestContext.WriteLine($"secondary_resets_at={limits?.Secondary?.ResetsAt?.ToString("O") ?? "null"}");
        TestContext.WriteLine($"rollout_primary_remaining={rolloutLimits?.Value.Primary?.ReportedRemainingPercent?.ToString() ?? "null"}");
        TestContext.WriteLine($"rollout_secondary_remaining={rolloutLimits?.Value.Secondary?.ReportedRemainingPercent?.ToString() ?? "null"}");
        TestContext.WriteLine($"probe_bucket_count={limits?.ByLimitId?.Count.ToString() ?? "null"}");
        TestContext.WriteLine($"observed_at={snapshot.ObservedAt:O}");
    }
}
