using CodexHud.Core;

namespace CodexHud.App.Tests;

[TestClass]
public sealed class HudPresentationTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 30, 14, 0, 0, TimeSpan.Zero);

    [TestMethod]
    [DataRow(45, "45m")]
    [DataRow(90, "1h 30m")]
    [DataRow(300, "5h")]
    [DataRow(1_440, "1d")]
    [DataRow(10_080, "Week")]
    [DataRow(20_160, "2w")]
    public void FormatWindowLabelHandlesRealDurations(int minutes, string expected)
    {
        Assert.AreEqual(expected, HudPresentation.FormatWindowLabel(minutes));
    }

    [TestMethod]
    public void CreatePreservesPostResetEstimateAndHidesSelectedBucketFromExtras()
    {
        var primary = new RateLimitWindow(62, 300, Now.AddMinutes(-1));
        var secondary = new RateLimitWindow(70, 10_080, Now.AddDays(2));
        var limits = new RateLimits(
            "codex",
            primary,
            secondary,
            new Dictionary<string, RateLimitBucket>
            {
                ["codex"] = new("codex", "Codex", primary, secondary),
                ["reviews"] = new(
                    "reviews",
                    "Code reviews",
                    new RateLimitWindow(12, 90, Now.AddHours(1)),
                    null),
            });

        var view = HudPresentation.Create(
            CreateState(limits, Now.AddSeconds(-10)),
            HudAvailability.Ready,
            Now);

        Assert.AreEqual("~100%", view.Primary.Value);
        Assert.IsTrue(view.HasEstimate);
        Assert.HasCount(1, view.AdditionalBuckets);
        Assert.AreEqual("Code reviews", view.AdditionalBuckets[0].Label);
        Assert.AreEqual("1h 30m 88%", view.AdditionalBuckets[0].Value);
    }

    [TestMethod]
    public void CreateMarksOnlyTheOldMetricAsStale()
    {
        var limits = CreateLimits(primaryUsed: 38);
        var snapshot = CreateSnapshot(limits, Now - HudPresentation.StaleAfter - TimeSpan.FromMinutes(1));
        var freshRateLimits = new RateLimitObservation(limits, Now.AddSeconds(-20), RateLimitSource.Probe);

        var view = HudPresentation.Create(
            new UsageState(snapshot, freshRateLimits),
            HudAvailability.Ready,
            Now);

        Assert.AreEqual(HudMetricTone.Stale, view.Context.Tone);
        Assert.AreEqual(HudMetricTone.Normal, view.Primary.Tone);
        Assert.AreEqual(HudMetricTone.Normal, view.Secondary.Tone);
    }

    [TestMethod]
    public void CreateAppliesIsolatedAttentionThresholds()
    {
        var attention = HudPresentation.Create(
            CreateState(CreateLimits(primaryUsed: 80), Now),
            HudAvailability.Ready,
            Now);
        var critical = HudPresentation.Create(
            CreateState(CreateLimits(primaryUsed: 90), Now),
            HudAvailability.Ready,
            Now);
        var normal = HudPresentation.Create(
            CreateState(CreateLimits(primaryUsed: 79), Now),
            HudAvailability.Ready,
            Now);

        Assert.AreEqual(HudMetricTone.Attention, attention.Primary.Tone);
        Assert.AreEqual(HudMetricTone.Critical, critical.Primary.Tone);
        Assert.AreEqual(HudMetricTone.Normal, normal.Primary.Tone);
    }

    [TestMethod]
    public void LoadingKeepsStablePlaceholders()
    {
        var view = HudPresentation.Create(UsageState.Empty, HudAvailability.Loading, Now);

        Assert.AreEqual("—", view.Context.Value);
        Assert.AreEqual("—", view.Primary.Value);
        Assert.AreEqual("—", view.Secondary.Value);
        Assert.AreEqual(HudMetricTone.Unavailable, view.Context.Tone);
        StringAssert.Contains(view.StatusMessage, "Loading");
    }

    [TestMethod]
    public void TemporaryUnavailabilityPreservesLastValuesButDeemphasizesThem()
    {
        var view = HudPresentation.Create(
            CreateState(CreateLimits(primaryUsed: 38), Now),
            HudAvailability.Unavailable,
            Now);

        Assert.AreEqual("68%", view.Context.Value);
        Assert.AreEqual("62%", view.Primary.Value);
        Assert.AreEqual(HudMetricTone.Unavailable, view.Context.Tone);
        Assert.AreEqual(HudMetricTone.Unavailable, view.Primary.Tone);
    }

    private static UsageState CreateState(RateLimits limits, DateTimeOffset observedAt)
    {
        var snapshot = CreateSnapshot(limits, observedAt);
        return new UsageState(
            snapshot,
            new RateLimitObservation(limits, observedAt, RateLimitSource.Probe));
    }

    private static UsageSnapshot CreateSnapshot(RateLimits limits, DateTimeOffset observedAt) =>
        new(
            "thread-test",
            new TokenUsage(82_944, null, null, null, null),
            258_400,
            68,
            limits,
            observedAt);

    private static RateLimits CreateLimits(double primaryUsed) =>
        new(
            "codex",
            new RateLimitWindow(primaryUsed, 300, Now.AddHours(2)),
            new RateLimitWindow(70, 10_080, Now.AddDays(2)));
}
