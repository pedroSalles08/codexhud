namespace CodexHud.Core;

public sealed record TokenUsage(
    long? TotalTokens,
    long? InputTokens,
    long? CachedInputTokens,
    long? OutputTokens,
    long? ReasoningOutputTokens);

public sealed record RateLimitWindow(
    double? UsedPercent,
    int? WindowMinutes,
    DateTimeOffset? ResetsAt)
{
    public int? ReportedRemainingPercent => UsedPercent is double used && double.IsFinite(used)
        ? (int)Math.Round(Math.Clamp(100d - used, 0d, 100d), MidpointRounding.AwayFromZero)
        : null;

    public RateLimitEstimate EvaluateAt(DateTimeOffset now)
    {
        if (ReportedRemainingPercent is not null &&
            ResetsAt is DateTimeOffset reset &&
            now >= reset)
        {
            return new RateLimitEstimate(100, IsEstimatedAfterReset: true);
        }

        return new RateLimitEstimate(ReportedRemainingPercent, IsEstimatedAfterReset: false);
    }
}

public sealed record RateLimitEstimate(
    int? RemainingPercent,
    bool IsEstimatedAfterReset);

public sealed record RateLimits(
    string? LimitId,
    RateLimitWindow? Primary,
    RateLimitWindow? Secondary,
    IReadOnlyDictionary<string, RateLimitBucket>? ByLimitId = null)
{
    public bool HasMeasuredWindow =>
        Primary?.UsedPercent is double primary && double.IsFinite(primary) ||
        Secondary?.UsedPercent is double secondary && double.IsFinite(secondary) ||
        ByLimitId?.Values.Any(bucket => bucket.HasMeasuredWindow) == true;
}

public sealed record RateLimitBucket(
    string? LimitId,
    string? LimitName,
    RateLimitWindow? Primary,
    RateLimitWindow? Secondary)
{
    public bool HasMeasuredWindow =>
        Primary?.UsedPercent is double primary && double.IsFinite(primary) ||
        Secondary?.UsedPercent is double secondary && double.IsFinite(secondary);
}

public enum RateLimitSource
{
    Rollout,
    Probe,
}

public sealed record RateLimitObservation(
    RateLimits Value,
    DateTimeOffset ObservedAt,
    RateLimitSource Source);

public sealed record UsageSnapshot(
    string ThreadId,
    TokenUsage LastTokenUsage,
    long? ModelContextWindow,
    int? ContextRemainingPercent,
    RateLimits? RateLimits,
    DateTimeOffset ObservedAt);

public sealed record UsageState(
    UsageSnapshot? ActiveSnapshot,
    RateLimitObservation? RateLimitObservation = null)
{
    public static UsageState Empty { get; } = new((UsageSnapshot?)null);

    public RateLimits? CurrentRateLimits =>
        RateLimitObservation?.Value ?? ActiveSnapshot?.RateLimits;
}
