using CodexHud.Core;

namespace CodexHud.App;

#if DEBUG
internal sealed record HudPreview(
    UsageState State,
    HudAvailability Availability,
    DateTimeOffset Now,
    bool StartExpanded)
{
    internal static HudPreview? TryCreate(string[] arguments)
    {
        var previewArgument = arguments.FirstOrDefault(
            argument => argument.StartsWith("--preview=", StringComparison.OrdinalIgnoreCase));
        if (previewArgument is null)
        {
            return null;
        }

        var scenario = previewArgument["--preview=".Length..].ToLowerInvariant();
        var expanded = arguments.Any(
            argument => string.Equals(argument, "--expanded", StringComparison.OrdinalIgnoreCase));
        var now = new DateTimeOffset(2026, 8, 30, 14, 20, 0, TimeSpan.FromHours(-3));

        if (scenario == "loading")
        {
            return new HudPreview(UsageState.Empty, HudAvailability.Loading, now, expanded);
        }

        if (scenario == "unavailable")
        {
            return new HudPreview(UsageState.Empty, HudAvailability.Unavailable, now, expanded);
        }

        if (scenario == "codex-missing")
        {
            return new HudPreview(UsageState.Empty, HudAvailability.CodexNotDetected, now, expanded);
        }

        var observedAt = scenario == "stale" ? now.AddMinutes(-37) : now.AddSeconds(-18);
        var primaryReset = scenario == "estimated" ? now.AddMinutes(-2) : now.AddHours(2).AddMinutes(12);
        var secondaryReset = now.AddDays(4).AddHours(3);
        var primaryMinutes = scenario == "labels" ? 90 : 300;
        var secondaryMinutes = scenario == "labels" ? 20_160 : 10_080;
        var primaryUsed = scenario == "critical" ? 94d : 38d;
        var limits = new RateLimits(
            "codex",
            new RateLimitWindow(primaryUsed, primaryMinutes, primaryReset),
            new RateLimitWindow(70, secondaryMinutes, secondaryReset),
            new Dictionary<string, RateLimitBucket>
            {
                ["codex"] = new(
                    "codex",
                    "Codex",
                    new RateLimitWindow(primaryUsed, primaryMinutes, primaryReset),
                    new RateLimitWindow(70, secondaryMinutes, secondaryReset)),
                ["reviews"] = new(
                    "reviews",
                    "Code reviews",
                    new RateLimitWindow(12, 90, now.AddMinutes(48)),
                    new RateLimitWindow(55, 1_440, now.AddHours(9))),
            });
        var snapshot = new UsageSnapshot(
            "preview-7bf2",
            new TokenUsage(82_944, 70_000, 44_000, 8_500, 4_444),
            258_400,
            68,
            limits,
            observedAt);
        var observation = new RateLimitObservation(limits, observedAt, RateLimitSource.Probe);
        return new HudPreview(
            new UsageState(snapshot, observation),
            HudAvailability.Ready,
            now,
            expanded);
    }
}
#else
internal sealed class HudPreview
{
    internal UsageState State => UsageState.Empty;
    internal HudAvailability Availability => HudAvailability.Loading;
    internal DateTimeOffset Now => DateTimeOffset.UtcNow;
    internal bool StartExpanded => false;
}
#endif
