using System.IO;
using System.Windows;
using System.Windows.Threading;
using CodexHud.Core;

namespace CodexHud.App;

public partial class MainWindow : Window
{
    private readonly CodexUsageMonitor _monitor = new();
    private readonly DispatcherTimer _resetTimer = new();

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        _monitor.UsageChanged += OnUsageChanged;
        _resetTimer.Tick += OnResetTimerTick;
    }

    private async void OnLoaded(object sender, RoutedEventArgs args)
    {
        Loaded -= OnLoaded;

        try
        {
            await _monitor.StartAsync();
            Render(_monitor.State);
            MonitorStatusText.Text = _monitor.State.ActiveSnapshot is null
                ? "No root Codex Desktop usage snapshot found yet."
                : "Watching rollout JSONL files (read-only).";
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            MonitorStatusText.Text = $"Monitor unavailable: {exception.Message}";
        }
    }

    private void OnUsageChanged(object? sender, UsageState state)
    {
        Dispatcher.InvokeAsync(() =>
        {
            Render(state);
            MonitorStatusText.Text = "Watching rollout JSONL files (read-only).";
        });
    }

    private void Render(UsageState state)
    {
        var snapshot = state.ActiveSnapshot;
        var rateLimits = state.CurrentRateLimits;
        var now = DateTimeOffset.UtcNow;
        var primary = FormatRateWindow(rateLimits?.Primary, "Primary", now);
        var secondary = FormatRateWindow(rateLimits?.Secondary, "Secondary", now);

        ContextText.Text = $"Context: {FormatPercent(snapshot?.ContextRemainingPercent)}";
        PrimaryText.Text = primary.Text;
        PrimaryText.ToolTip = primary.IsEstimated ? EstimatedRateLimitMessage : null;
        SecondaryText.Text = secondary.Text;
        SecondaryText.ToolTip = secondary.IsEstimated ? EstimatedRateLimitMessage : null;
        ThreadIdText.Text = snapshot?.ThreadId ?? "—";
        TotalTokensText.Text = snapshot?.LastTokenUsage.TotalTokens?.ToString("N0") ?? "—";
        ContextWindowText.Text = snapshot?.ModelContextWindow?.ToString("N0") ?? "—";
        SnapshotTimeText.Text = FormatTime(snapshot?.ObservedAt);
        PrimaryResetText.Text = FormatTime(rateLimits?.Primary?.ResetsAt);
        SecondaryResetText.Text = FormatTime(rateLimits?.Secondary?.ResetsAt);
        ScheduleNextReset(rateLimits, now);
    }

    private const string EstimatedRateLimitMessage =
        "Estimated after reset; awaiting a fresh Codex snapshot.";

    private static (string Text, bool IsEstimated) FormatRateWindow(
        RateLimitWindow? window,
        string fallbackLabel,
        DateTimeOffset now)
    {
        var label = window?.WindowMinutes switch
        {
            300 => "5h",
            10_080 => "Week",
            int minutes => $"{minutes}m",
            _ => fallbackLabel,
        };
        var estimate = window?.EvaluateAt(now);
        var prefix = estimate?.IsEstimatedAfterReset == true ? "~" : string.Empty;
        var percentage = estimate?.RemainingPercent is int value
            ? $"{prefix}{value}% remaining"
            : "—";

        return ($"{label}: {percentage}", estimate?.IsEstimatedAfterReset == true);
    }

    private static string FormatPercent(int? percentage) =>
        percentage is int value ? $"{value}% remaining" : "—";

    private static string FormatTime(DateTimeOffset? timestamp) =>
        timestamp?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz") ?? "—";

    private void ScheduleNextReset(RateLimits? rateLimits, DateTimeOffset now)
    {
        _resetTimer.Stop();
        var nextReset = new[]
        {
            rateLimits?.Primary?.ResetsAt,
            rateLimits?.Secondary?.ResetsAt,
        }
        .Where(reset => reset is not null && reset > now)
        .Min();

        if (nextReset is null)
        {
            return;
        }

        var delay = nextReset.Value - now + TimeSpan.FromMilliseconds(100);
        _resetTimer.Interval = delay < TimeSpan.FromMilliseconds(100)
            ? TimeSpan.FromMilliseconds(100)
            : delay;
        _resetTimer.Start();
    }

    private async void OnResetTimerTick(object? sender, EventArgs args)
    {
        _resetTimer.Stop();
        Render(_monitor.State);
        await _monitor.ReconcileEstimatedRateLimitsAsync(DateTimeOffset.UtcNow);
    }

    protected override void OnClosed(EventArgs args)
    {
        _resetTimer.Stop();
        _resetTimer.Tick -= OnResetTimerTick;
        _monitor.UsageChanged -= OnUsageChanged;
        _monitor.DisposeAsync().AsTask().GetAwaiter().GetResult();
        base.OnClosed(args);
    }
}
