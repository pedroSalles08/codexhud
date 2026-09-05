using System.Globalization;
using System.Windows.Media;
using CodexHud.Core;

namespace CodexHud.App;

internal enum HudAvailability
{
    Loading,
    Ready,
    WaitingForSession,
    CodexNotDetected,
    Unavailable,
}

internal enum HudMetricTone
{
    Normal,
    Stale,
    Attention,
    Critical,
    Unavailable,
}

internal sealed record HudMetric(
    string Label,
    string Value,
    HudMetricTone Tone,
    string Tooltip);

internal sealed record HudBucketItem(string Label, string Value);

internal sealed record HudView(
    HudMetric Context,
    HudMetric Primary,
    HudMetric Secondary,
    string ContextUsage,
    string PrimaryReset,
    string SecondaryReset,
    string Freshness,
    string? StatusMessage,
    bool HasEstimate,
    bool ShowContextDetail,
    bool ShowPrimaryDetail,
    bool ShowSecondaryDetail,
    bool ShowFreshness,
    IReadOnlyList<HudBucketItem> AdditionalBuckets,
    DateTimeOffset? NextFreshnessChange);

internal static class HudPresentation
{
    // Presentation-only thresholds. They do not affect collection or rate-limit semantics.
    internal const int AttentionRemainingPercent = 20;
    internal const int CriticalRemainingPercent = 10;
    internal static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(15);

    internal static readonly Brush ValueBrush = Freeze("#F3F4F5");
    internal static readonly Brush StaleBrush = Freeze("#B7BBC1");
    internal static readonly Brush AttentionBrush = Freeze("#D8AA61");
    internal static readonly Brush CriticalBrush = Freeze("#D77373");
    internal static readonly Brush UnavailableBrush = Freeze("#777C83");

    internal static HudView Create(
        UsageState state,
        HudAvailability availability,
        DateTimeOffset now)
    {
        var snapshot = state.ActiveSnapshot;
        var limits = state.CurrentRateLimits;
        var contextObservedAt = snapshot?.ObservedAt;
        var rateObservedAt = state.RateLimitObservation?.ObservedAt ?? snapshot?.ObservedAt;
        var contextStale = IsStale(contextObservedAt, now);
        var rateStale = IsStale(rateObservedAt, now);

        var context = new HudMetric(
            "Context",
            FormatPercent(snapshot?.ContextRemainingPercent),
            ToneFor(snapshot?.ContextRemainingPercent, contextStale, availability),
            BuildContextTooltip(snapshot, contextObservedAt, now, availability));
        var primary = CreateRateMetric(
            limits?.Primary,
            "Usage",
            rateObservedAt,
            rateStale,
            availability,
            now);
        var secondary = CreateRateMetric(
            limits?.Secondary,
            "Limit",
            rateObservedAt,
            rateStale,
            availability,
            now);

        var hasEstimate = new[] { limits?.Primary, limits?.Secondary }
            .Any(window => window?.EvaluateAt(now).IsEstimatedAfterReset == true);
        var freshest = Latest(contextObservedAt, rateObservedAt);
        var oldestStale = Oldest(
            contextStale ? contextObservedAt : null,
            rateStale ? rateObservedAt : null);
        var status = BuildStatus(availability, oldestStale, now);
        var additionalBuckets = CreateAdditionalBuckets(limits, now);

        return new HudView(
            context,
            primary,
            secondary,
            FormatContextUsage(snapshot),
            FormatReset(limits?.Primary?.ResetsAt, now),
            FormatReset(limits?.Secondary?.ResetsAt, now),
            FormatFreshness(freshest, state.RateLimitObservation?.Source, now),
            status,
            hasEstimate,
            snapshot?.LastTokenUsage.TotalTokens is not null || snapshot?.ModelContextWindow is not null,
            limits?.Primary is not null,
            limits?.Secondary is not null,
            freshest is not null,
            additionalBuckets,
            NextFreshnessChange(contextObservedAt, rateObservedAt, now));
    }

    internal static string FormatWindowLabel(int? windowMinutes)
    {
        if (windowMinutes is not int minutes || minutes <= 0)
        {
            return "Limit";
        }

        if (minutes == 10_080)
        {
            return "Week";
        }

        if (minutes % 10_080 == 0)
        {
            return $"{minutes / 10_080}w";
        }

        if (minutes % 1_440 == 0)
        {
            return $"{minutes / 1_440}d";
        }

        if (minutes % 60 == 0)
        {
            return $"{minutes / 60}h";
        }

        if (minutes > 60)
        {
            return $"{minutes / 60}h {minutes % 60}m";
        }

        return $"{minutes}m";
    }

    internal static string FormatAge(DateTimeOffset observedAt, DateTimeOffset now)
    {
        var age = now - observedAt;
        if (age < TimeSpan.Zero || age < TimeSpan.FromMinutes(1))
        {
            return "just now";
        }

        if (age < TimeSpan.FromHours(1))
        {
            return $"{Math.Max(1, (int)age.TotalMinutes)}m ago";
        }

        if (age < TimeSpan.FromDays(1))
        {
            return $"{Math.Max(1, (int)age.TotalHours)}h ago";
        }

        return $"{Math.Max(1, (int)age.TotalDays)}d ago";
    }

    private static HudMetric CreateRateMetric(
        RateLimitWindow? window,
        string fallbackLabel,
        DateTimeOffset? observedAt,
        bool stale,
        HudAvailability availability,
        DateTimeOffset now)
    {
        var label = window?.WindowMinutes is null
            ? fallbackLabel
            : FormatWindowLabel(window.WindowMinutes);
        var estimate = window?.EvaluateAt(now);
        var prefix = estimate?.IsEstimatedAfterReset == true ? "~" : string.Empty;
        var value = estimate?.RemainingPercent is int percentage
            ? $"{prefix}{percentage}%"
            : "—";
        var tooltip = BuildRateTooltip(label, window, estimate, observedAt, now, availability);

        return new HudMetric(
            label,
            value,
            ToneFor(estimate?.RemainingPercent, stale, availability),
            tooltip);
    }

    private static HudMetricTone ToneFor(
        int? remainingPercent,
        bool stale,
        HudAvailability availability)
    {
        if (remainingPercent is null || availability is HudAvailability.Loading or HudAvailability.Unavailable)
        {
            return HudMetricTone.Unavailable;
        }

        if (remainingPercent <= CriticalRemainingPercent)
        {
            return HudMetricTone.Critical;
        }

        if (remainingPercent <= AttentionRemainingPercent)
        {
            return HudMetricTone.Attention;
        }

        return stale ? HudMetricTone.Stale : HudMetricTone.Normal;
    }

    private static string BuildContextTooltip(
        UsageSnapshot? snapshot,
        DateTimeOffset? observedAt,
        DateTimeOffset now,
        HudAvailability availability)
    {
        if (snapshot is null)
        {
            return AvailabilityTooltip(availability);
        }

        var age = observedAt is DateTimeOffset timestamp ? FormatAge(timestamp, now) : "time unavailable";
        return $"Context remaining · updated {age}";
    }

    private static string BuildRateTooltip(
        string label,
        RateLimitWindow? window,
        RateLimitEstimate? estimate,
        DateTimeOffset? observedAt,
        DateTimeOffset now,
        HudAvailability availability)
    {
        if (window is null)
        {
            return AvailabilityTooltip(availability);
        }

        var parts = new List<string>();
        if (window.ResetsAt is DateTimeOffset reset)
        {
            parts.Add($"Resets {FormatReset(reset, now).ToLowerInvariant()}");
        }

        if (observedAt is DateTimeOffset timestamp)
        {
            parts.Add($"updated {FormatAge(timestamp, now)}");
        }

        if (estimate?.IsEstimatedAfterReset == true)
        {
            parts.Add("estimated after reset");
        }

        return parts.Count == 0 ? $"{label} limit" : string.Join(" · ", parts);
    }

    private static string AvailabilityTooltip(HudAvailability availability) => availability switch
    {
        HudAvailability.Loading => "Loading Codex usage",
        HudAvailability.WaitingForSession => "Waiting for a Codex Desktop session",
        HudAvailability.CodexNotDetected => "Codex was not detected",
        HudAvailability.Unavailable => "Usage data is temporarily unavailable",
        _ => "No reading available",
    };

    private static string? BuildStatus(
        HudAvailability availability,
        DateTimeOffset? oldestStale,
        DateTimeOffset now) =>
        availability switch
        {
            HudAvailability.Loading => "Loading Codex usage…",
            HudAvailability.WaitingForSession => "Waiting for a Codex Desktop session. Limits may appear before context usage.",
            HudAvailability.CodexNotDetected => "Codex was not detected. Open Codex Desktop and start a session.",
            HudAvailability.Unavailable => "Usage data is temporarily unavailable.",
            _ when oldestStale is DateTimeOffset timestamp =>
                $"Some values are from {FormatAge(timestamp, now)}.",
            _ => null,
        };

    private static string FormatContextUsage(UsageSnapshot? snapshot)
    {
        var used = snapshot?.LastTokenUsage.TotalTokens;
        var window = snapshot?.ModelContextWindow;
        return (used, window) switch
        {
            (long usedValue, long windowValue) =>
                $"{usedValue.ToString("N0", CultureInfo.InvariantCulture)} / {windowValue.ToString("N0", CultureInfo.InvariantCulture)} tokens",
            (long usedValue, null) => $"{usedValue.ToString("N0", CultureInfo.InvariantCulture)} tokens",
            _ => "—",
        };
    }

    private static string FormatPercent(int? percentage) =>
        percentage is int value ? $"{value}%" : "—";

    private static string FormatReset(DateTimeOffset? reset, DateTimeOffset now)
    {
        if (reset is not DateTimeOffset timestamp)
        {
            return "—";
        }

        var localReset = timestamp.ToLocalTime();
        var localNow = now.ToLocalTime();
        var day = localReset.Date == localNow.Date
            ? "Today"
            : localReset.Date == localNow.Date.AddDays(1)
                ? "Tomorrow"
                : localReset.ToString("MMM d", CultureInfo.InvariantCulture);
        return $"{day}, {localReset:HH:mm}";
    }

    private static string FormatFreshness(
        DateTimeOffset? observedAt,
        RateLimitSource? rateLimitSource,
        DateTimeOffset now)
    {
        if (observedAt is not DateTimeOffset timestamp)
        {
            return "—";
        }

        var source = rateLimitSource switch
        {
            RateLimitSource.Probe => "Codex status",
            RateLimitSource.Rollout => "current session",
            _ => "current session",
        };
        return $"{FormatAge(timestamp, now)} · {source}";
    }

    private static IReadOnlyList<HudBucketItem> CreateAdditionalBuckets(
        RateLimits? limits,
        DateTimeOffset now)
    {
        if (limits?.ByLimitId is null)
        {
            return [];
        }

        return limits.ByLimitId
            .Where(pair => !IsSelectedBucket(limits, pair.Key, pair.Value))
            .OrderBy(
                pair => pair.Value.LimitName ?? pair.Value.LimitId ?? pair.Key,
                StringComparer.CurrentCultureIgnoreCase)
            .Select(pair => new HudBucketItem(
                pair.Value.LimitName ?? pair.Value.LimitId ?? "Other limit",
                FormatBucketWindows(pair.Value, now)))
            .Where(item => item.Value != "—")
            .ToArray();
    }

    private static string FormatBucketWindows(RateLimitBucket bucket, DateTimeOffset now)
    {
        var windows = new[] { bucket.Primary, bucket.Secondary }
            .Where(window => window is not null)
            .Select(window =>
            {
                var estimate = window!.EvaluateAt(now);
                var prefix = estimate.IsEstimatedAfterReset ? "~" : string.Empty;
                var remaining = estimate.RemainingPercent is int value ? $"{prefix}{value}%" : "—";
                return $"{FormatWindowLabel(window.WindowMinutes)} {remaining}";
            })
            .ToArray();
        return windows.Length == 0 ? "—" : string.Join(" · ", windows);
    }

    private static bool IsStale(DateTimeOffset? observedAt, DateTimeOffset now) =>
        observedAt is DateTimeOffset timestamp && now - timestamp >= StaleAfter;

    private static DateTimeOffset? NextFreshnessChange(
        DateTimeOffset? contextObservedAt,
        DateTimeOffset? rateObservedAt,
        DateTimeOffset now)
    {
        return new[] { contextObservedAt, rateObservedAt }
            .Where(timestamp => timestamp is not null)
            .Select(timestamp => timestamp!.Value + StaleAfter)
            .Where(deadline => deadline > now)
            .Cast<DateTimeOffset?>()
            .Min();
    }

    private static DateTimeOffset? Latest(DateTimeOffset? first, DateTimeOffset? second) =>
        first is null ? second : second is null ? first : first > second ? first : second;

    private static DateTimeOffset? Oldest(DateTimeOffset? first, DateTimeOffset? second) =>
        first is null ? second : second is null ? first : first < second ? first : second;

    private static bool IsSelectedBucket(
        RateLimits limits,
        string bucketKey,
        RateLimitBucket bucket) =>
        string.Equals(bucketKey, limits.LimitId, StringComparison.Ordinal) ||
        limits.LimitId is null &&
        bucket.Primary == limits.Primary &&
        bucket.Secondary == limits.Secondary;

    private static SolidColorBrush Freeze(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }
}
