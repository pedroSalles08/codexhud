using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using CodexHud.Core;

namespace CodexHud.App;

public partial class MainWindow : Window
{
    private static readonly Duration ExpansionDuration = new(TimeSpan.FromMilliseconds(140));
    private static readonly Duration HoverDuration = new(TimeSpan.FromMilliseconds(70));

    private readonly CodexUsageMonitor _monitor = new();
    private readonly DispatcherTimer _resetTimer = new();
    private readonly DispatcherTimer _freshnessTimer = new();
    private readonly bool _animationsEnabled = SystemParameters.ClientAreaAnimation;
    private readonly HudPreview? _preview = null;

    private UsageState _state = UsageState.Empty;
    private HudAvailability _availability = HudAvailability.Loading;
    private Point _pointerDownPosition;
    private bool _isExpanded;

    public MainWindow()
    {
        InitializeComponent();
#if DEBUG
        _preview = HudPreview.TryCreate(Environment.GetCommandLineArgs());
#endif
        Loaded += OnLoaded;
        SourceInitialized += OnSourceInitialized;
        _monitor.UsageChanged += OnUsageChanged;
        _resetTimer.Tick += OnResetTimerTick;
        _freshnessTimer.Tick += OnFreshnessTimerTick;
        Render();
    }

    private async void OnLoaded(object sender, RoutedEventArgs args)
    {
        Loaded -= OnLoaded;
        PositionAtTopCenter();

        if (_preview is not null)
        {
            _state = _preview.State;
            _availability = _preview.Availability;
            Render();
            if (_preview.StartExpanded)
            {
                _ = Dispatcher.BeginInvoke(ToggleExpanded, DispatcherPriority.Loaded);
            }

            return;
        }

        try
        {
            await _monitor.StartAsync();
            _state = _monitor.State;
            _availability = _state.ActiveSnapshot is null
                ? HudAvailability.WaitingForSession
                : HudAvailability.Ready;
            Render();
        }
        catch (InvalidOperationException)
        {
            _availability = HudAvailability.CodexNotDetected;
            Render();
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            Trace.TraceInformation("Codex HUD monitor unavailable ({0}).", exception.GetType().Name);
            _availability = HudAvailability.Unavailable;
            Render();
        }
    }

    private void PositionAtTopCenter()
    {
        var workArea = SystemParameters.WorkArea;
        Left = workArea.Left + ((workArea.Width - ActualWidth) / 2);
        Top = workArea.Top;
    }

    private void OnSourceInitialized(object? sender, EventArgs args)
    {
        var materialApplied = WindowMaterial.TryApply(this);
        RootSurface.Background = new SolidColorBrush(
            (Color)ColorConverter.ConvertFromString(materialApplied ? "#D9111214" : "#FC111214"));
    }

    private void OnUsageChanged(object? sender, UsageState state)
    {
        Dispatcher.InvokeAsync(() =>
        {
            _state = state;
            _availability = state.ActiveSnapshot is null
                ? HudAvailability.WaitingForSession
                : HudAvailability.Ready;
            Render();
        });
    }

    private void Render()
    {
        var now = _preview?.Now ?? DateTimeOffset.UtcNow;
        var view = HudPresentation.Create(_state, _availability, now);

        ApplyMetric(ContextMetric, ContextValueText, view.Context);
        ApplyMetric(PrimaryMetric, PrimaryValueText, view.Primary);
        ApplyMetric(SecondaryMetric, SecondaryValueText, view.Secondary);
        PrimaryLabelText.Text = view.Primary.Label;
        SecondaryLabelText.Text = view.Secondary.Label;

        ContextTokensText.Text = view.ContextUsage;
        PrimaryResetLabelText.Text = $"{view.Primary.Label} resets";
        PrimaryResetText.Text = view.PrimaryReset;
        SecondaryResetLabelText.Text = $"{view.Secondary.Label} resets";
        SecondaryResetText.Text = view.SecondaryReset;
        FreshnessText.Text = view.Freshness;

        ContextDetailRow.Visibility = view.ShowContextDetail ? Visibility.Visible : Visibility.Collapsed;
        PrimaryDetailRow.Visibility = view.ShowPrimaryDetail ? Visibility.Visible : Visibility.Collapsed;
        SecondaryDetailRow.Visibility = view.ShowSecondaryDetail ? Visibility.Visible : Visibility.Collapsed;
        FreshnessDetailRow.Visibility = view.ShowFreshness ? Visibility.Visible : Visibility.Collapsed;

        EstimateExplanationText.Visibility = view.HasEstimate
            ? Visibility.Visible
            : Visibility.Collapsed;
        StatusText.Text = view.StatusMessage;
        StatusText.Visibility = string.IsNullOrWhiteSpace(view.StatusMessage)
            ? Visibility.Collapsed
            : Visibility.Visible;

        AdditionalBucketsList.ItemsSource = view.AdditionalBuckets;
        AdditionalBucketsSection.Visibility = view.AdditionalBuckets.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;

        ScheduleNextReset(_state.CurrentRateLimits, now);
        ScheduleFreshnessUpdate(view.NextFreshnessChange, now);
    }

    private static void ApplyMetric(Border hitArea, TextBlock valueText, HudMetric metric)
    {
        valueText.Text = metric.Value;
        valueText.Foreground = metric.Tone switch
        {
            HudMetricTone.Attention => HudPresentation.AttentionBrush,
            HudMetricTone.Critical => HudPresentation.CriticalBrush,
            HudMetricTone.Stale => HudPresentation.StaleBrush,
            HudMetricTone.Unavailable => HudPresentation.UnavailableBrush,
            _ => HudPresentation.ValueBrush,
        };
        hitArea.ToolTip = metric.Tooltip;
    }

    private void OnCompactMouseLeftButtonDown(object sender, MouseButtonEventArgs args)
    {
        if (args.ClickCount != 1 || IsExpandButtonSource(args.OriginalSource as DependencyObject))
        {
            return;
        }

        _pointerDownPosition = args.GetPosition(this);
        CompactRow.Focus();
        CompactRow.CaptureMouse();
    }

    private void OnCompactMouseMove(object sender, MouseEventArgs args)
    {
        if (args.LeftButton != MouseButtonState.Pressed || !CompactRow.IsMouseCaptured)
        {
            return;
        }

        var current = args.GetPosition(this);
        if (Math.Abs(current.X - _pointerDownPosition.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(current.Y - _pointerDownPosition.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        CompactRow.ReleaseMouseCapture();
        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // The button can be released between the threshold check and DragMove.
        }
    }

    private void OnCompactMouseLeftButtonUp(object sender, MouseButtonEventArgs args)
    {
        if (CompactRow.IsMouseCaptured)
        {
            CompactRow.ReleaseMouseCapture();
        }
    }

    private static bool IsExpandButtonSource(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is Button button && button.Name == "ExpandButton")
            {
                return true;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return false;
    }

    private void OnExpandButtonClick(object sender, RoutedEventArgs args) => ToggleExpanded();

    private void ToggleExpanded()
    {
        _isExpanded = !_isExpanded;
        UpdateExpandButton();
        if (_isExpanded)
        {
            // Keep the compact instrument's width stable while details unfold below it.
            Width = ActualWidth;
        }

        Render();

        if (_isExpanded)
        {
            ExpandedRegion.Visibility = Visibility.Visible;
            ExpandedRegion.Height = double.NaN;
            ExpandedRegion.Measure(new Size(Math.Max(ActualWidth - 2, 338), double.PositiveInfinity));
            var targetHeight = ExpandedRegion.DesiredSize.Height;
            ExpandedRegion.Height = 0;

            if (!_animationsEnabled)
            {
                ExpandedRegion.Height = targetHeight;
                ExpandedRegion.Opacity = 1;
                return;
            }

            var expansion = new DoubleAnimation(0, targetHeight, ExpansionDuration)
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            };
            expansion.Completed += (_, _) =>
            {
                ExpandedRegion.BeginAnimation(HeightProperty, null);
                ExpandedRegion.Height = double.NaN;
            };
            ExpandedRegion.BeginAnimation(HeightProperty, expansion);
            ExpandedRegion.BeginAnimation(
                OpacityProperty,
                new DoubleAnimation(0, 1, ExpansionDuration));
        }
        else
        {
            var currentHeight = ExpandedRegion.ActualHeight;
            if (!_animationsEnabled)
            {
                FinishCollapse();
                return;
            }

            var collapse = new DoubleAnimation(currentHeight, 0, ExpansionDuration)
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn },
            };
            collapse.Completed += (_, _) => FinishCollapse();
            ExpandedRegion.BeginAnimation(HeightProperty, collapse);
            ExpandedRegion.BeginAnimation(
                OpacityProperty,
                new DoubleAnimation(ExpandedRegion.Opacity, 0, ExpansionDuration));
        }
    }

    private void UpdateExpandButton()
    {
        var targetAngle = _isExpanded ? 180d : 0d;
        ExpandButton.ToolTip = _isExpanded ? "Hide details" : "Show details";
        AutomationProperties.SetName(
            ExpandButton,
            _isExpanded ? "Hide details" : "Show details");

        if (!_animationsEnabled)
        {
            ChevronRotation.Angle = targetAngle;
            return;
        }

        ChevronRotation.BeginAnimation(
            RotateTransform.AngleProperty,
            new DoubleAnimation(ChevronRotation.Angle, targetAngle, ExpansionDuration)
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            });
    }

    private void FinishCollapse()
    {
        ExpandedRegion.BeginAnimation(HeightProperty, null);
        ExpandedRegion.BeginAnimation(OpacityProperty, null);
        ExpandedRegion.Height = 0;
        ExpandedRegion.Opacity = 0;
        ExpandedRegion.Visibility = Visibility.Collapsed;
        Width = double.NaN;
    }

    private void OnSurfaceMouseEnter(object sender, MouseEventArgs args) =>
        AnimateHighlight(0.75);

    private void OnSurfaceMouseLeave(object sender, MouseEventArgs args) =>
        AnimateHighlight(0);

    private void AnimateHighlight(double opacity)
    {
        if (!_animationsEnabled)
        {
            InteractionHighlight.Opacity = opacity;
            return;
        }

        InteractionHighlight.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(InteractionHighlight.Opacity, opacity, HoverDuration));
    }

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

        if (nextReset is null || _preview is not null)
        {
            return;
        }

        _resetTimer.Interval = ClampTimerDelay(nextReset.Value - now + TimeSpan.FromMilliseconds(100));
        _resetTimer.Start();
    }

    private void ScheduleFreshnessUpdate(DateTimeOffset? nextChange, DateTimeOffset now)
    {
        _freshnessTimer.Stop();
        if (nextChange is null || nextChange <= now || _preview is not null)
        {
            return;
        }

        _freshnessTimer.Interval = ClampTimerDelay(nextChange.Value - now + TimeSpan.FromMilliseconds(100));
        _freshnessTimer.Start();
    }

    private static TimeSpan ClampTimerDelay(TimeSpan delay)
    {
        var minimum = TimeSpan.FromMilliseconds(100);
        var maximum = TimeSpan.FromDays(24);
        return delay < minimum ? minimum : delay > maximum ? maximum : delay;
    }

    private async void OnResetTimerTick(object? sender, EventArgs args)
    {
        _resetTimer.Stop();
        Render();
        if (_preview is null)
        {
            await _monitor.ReconcileEstimatedRateLimitsAsync(DateTimeOffset.UtcNow);
        }
    }

    private void OnFreshnessTimerTick(object? sender, EventArgs args)
    {
        _freshnessTimer.Stop();
        Render();
    }

    private void OnQuitClick(object sender, RoutedEventArgs args) => Close();

    protected override void OnClosed(EventArgs args)
    {
        _resetTimer.Stop();
        _freshnessTimer.Stop();
        _resetTimer.Tick -= OnResetTimerTick;
        _freshnessTimer.Tick -= OnFreshnessTimerTick;
        _monitor.UsageChanged -= OnUsageChanged;
        _monitor.DisposeAsync().AsTask().GetAwaiter().GetResult();

        base.OnClosed(args);
    }
}
