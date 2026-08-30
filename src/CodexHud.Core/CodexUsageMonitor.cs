using System.Collections.Concurrent;
using System.Threading.Channels;

namespace CodexHud.Core;

public sealed class CodexUsageMonitor : IAsyncDisposable
{
    private const string RecoverySignal = "\0recover";
    private readonly string _sessionsDirectory;
    private readonly IContextRemainingPolicy _contextPolicy;
    private readonly IRateLimitProbe _rateLimitProbe;
    private readonly IncrementalJsonlReader _reader;
    private readonly object _stateGate = new();
    private readonly object _probeGate = new();
    private readonly Dictionary<string, RolloutFileState> _files =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _queuedPaths =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Channel<string> _work = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly CancellationTokenSource _cancellation = new();
    private FileSystemWatcher? _watcher;
    private Task? _worker;
    private Task _probeTask = Task.CompletedTask;
    private long _rolloutRateLimitRevision;
    private bool _started;
    private bool _disposed;

    public CodexUsageMonitor(
        string? sessionsDirectory = null,
        IContextRemainingPolicy? contextPolicy = null,
        IRateLimitProbe? rateLimitProbe = null)
    {
        _sessionsDirectory = Path.GetFullPath(
            sessionsDirectory ?? CodexHomeLocator.FindSessionsDirectory());
        _contextPolicy = contextPolicy ?? CurrentCodexContextPolicy.Create();
        _rateLimitProbe = rateLimitProbe ?? new RateLimitProbe();
        _reader = new IncrementalJsonlReader();
    }

    public UsageState State { get; private set; } = UsageState.Empty;

    public event EventHandler<UsageState>? UsageChanged;

    internal Task CurrentProbeTask
    {
        get
        {
            lock (_probeGate)
            {
                return _probeTask;
            }
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started)
        {
            return;
        }

        _started = true;
        StartWatcherIfPossible();

        foreach (var path in EnumerateRolloutsNewestFirst())
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await ProcessFileAsync(path, cancellationToken);
            }
            catch (IOException)
            {
                QueuePath(path);
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            if (_files.TryGetValue(path, out var file) && file.LatestSnapshot is not null)
            {
                break;
            }
        }

        _worker = Task.Run(() => RunWorkerAsync(_cancellation.Token));
        _ = StartProbeIfIdle();
    }

    /// <summary>
    /// Starts one reconciliation only when a current rate-limit window is showing
    /// the existing post-reset estimate. Concurrent calls share the in-flight probe.
    /// </summary>
    public Task ReconcileEstimatedRateLimitsAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_stateGate)
        {
            var limits = State.CurrentRateLimits;
            if (limits is null ||
                !IsEstimatedAfterReset(limits.Primary, now) &&
                !IsEstimatedAfterReset(limits.Secondary, now))
            {
                return Task.CompletedTask;
            }
        }

        return StartProbeIfIdle(cancellationToken);
    }

    private void StartWatcherIfPossible()
    {
        var watchRoot = Directory.Exists(_sessionsDirectory)
            ? _sessionsDirectory
            : Directory.GetParent(_sessionsDirectory)?.FullName;

        if (watchRoot is null || !Directory.Exists(watchRoot))
        {
            return;
        }

        _watcher = new FileSystemWatcher(watchRoot, "*.jsonl")
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            InternalBufferSize = 16 * 1024,
        };

        _watcher.Created += OnRolloutChanged;
        _watcher.Changed += OnRolloutChanged;
        _watcher.Renamed += OnRolloutRenamed;
        _watcher.Error += OnWatcherError;
        _watcher.EnableRaisingEvents = true;
    }

    private void OnRolloutChanged(object sender, FileSystemEventArgs args) => QueuePath(args.FullPath);

    private void OnRolloutRenamed(object sender, RenamedEventArgs args) => QueuePath(args.FullPath);

    private void OnWatcherError(object sender, ErrorEventArgs args) => QueuePath(RecoverySignal);

    private void QueuePath(string path)
    {
        if (_disposed ||
            (!string.Equals(path, RecoverySignal, StringComparison.Ordinal) && !IsRolloutPath(path)) ||
            !_queuedPaths.TryAdd(path, 0))
        {
            return;
        }

        _work.Writer.TryWrite(path);
    }

    private async Task RunWorkerAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var path in _work.Reader.ReadAllAsync(cancellationToken))
            {
                try
                {
                    // Short event coalescing delay; this is event-driven, not a polling loop.
                    await Task.Delay(75, cancellationToken);

                    if (string.Equals(path, RecoverySignal, StringComparison.Ordinal))
                    {
                        QueueRecoveryCandidates();
                    }
                    else
                    {
                        await ProcessFileAsync(path, cancellationToken);
                    }
                }
                catch (IOException)
                {
                    // A later filesystem event will retry a file still being created or rotated.
                }
                catch (UnauthorizedAccessException)
                {
                    // A transient unreadable rollout must not terminate the monitor.
                }
                finally
                {
                    _queuedPaths.TryRemove(path, out _);
                    if (!string.Equals(path, RecoverySignal, StringComparison.Ordinal) &&
                        HasUnreadBytes(path))
                    {
                        QueuePath(path);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task ProcessFileAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path) || !IsRolloutPath(path))
        {
            return;
        }

        if (!_files.TryGetValue(path, out var file))
        {
            file = new RolloutFileState();
            _files.Add(path, file);
        }

        if (file.SessionMetaSeen && !file.IsRootDesktopSession)
        {
            file.Cursor.Offset = new FileInfo(path).Length;
            file.Cursor.PendingBytes = [];
            return;
        }

        await _reader.ReadNewLinesAsync(
            path,
            file.Cursor,
            line => ProcessLine(path, file, line),
            cancellationToken);
    }

    private bool ProcessLine(string path, RolloutFileState file, ReadOnlyMemory<byte> line)
    {
        if (!RolloutRecordParser.TryParse(line, out var record) || record is null)
        {
            return true;
        }

        if (record is SessionMetaRecord metadata)
        {
            file.SessionMetaSeen = true;
            file.IsRootDesktopSession = metadata.IsRootDesktopSession;
            file.ThreadId = metadata.ThreadId;
            return metadata.IsRootDesktopSession && !string.IsNullOrWhiteSpace(metadata.ThreadId);
        }

        if (record is not TokenCountRecord tokenCount ||
            !file.SessionMetaSeen ||
            !file.IsRootDesktopSession ||
            string.IsNullOrWhiteSpace(file.ThreadId) ||
            !HasUsageData(tokenCount))
        {
            return true;
        }

        var observedAt = tokenCount.Timestamp ?? GetLastWriteTime(path);
        var snapshot = new UsageSnapshot(
            file.ThreadId,
            tokenCount.LastTokenUsage,
            tokenCount.ModelContextWindow,
            _contextPolicy.Calculate(
                tokenCount.LastTokenUsage.TotalTokens,
                tokenCount.ModelContextWindow),
            tokenCount.RateLimits,
            observedAt);

        file.LatestSnapshot = snapshot;
        UsageState? changedState = null;
        lock (_stateGate)
        {
            var activeSnapshot = State.ActiveSnapshot;
            var rateLimitObservation = State.RateLimitObservation;
            var changed = false;

            if (activeSnapshot is null || snapshot.ObservedAt >= activeSnapshot.ObservedAt)
            {
                activeSnapshot = snapshot;
                changed = true;
            }

            if (tokenCount.RateLimits is { HasMeasuredWindow: true } rateLimits &&
                (rateLimitObservation is null ||
                 snapshot.ObservedAt >= rateLimitObservation.ObservedAt))
            {
                rateLimitObservation = new RateLimitObservation(
                    rateLimits,
                    snapshot.ObservedAt,
                    RateLimitSource.Rollout);
                _rolloutRateLimitRevision++;
                changed = true;
            }

            if (changed)
            {
                State = new UsageState(activeSnapshot, rateLimitObservation);
                changedState = State;
            }
        }

        if (changedState is not null)
        {
            UsageChanged?.Invoke(this, changedState);
        }

        return true;
    }

    private Task StartProbeIfIdle(CancellationToken cancellationToken = default)
    {
        lock (_probeGate)
        {
            if (_disposed)
            {
                return Task.CompletedTask;
            }

            if (!_probeTask.IsCompleted)
            {
                return _probeTask;
            }

            _probeTask = Task.Run(
                () => RunProbeAsync(cancellationToken),
                CancellationToken.None);
            return _probeTask;
        }
    }

    private async Task RunProbeAsync(CancellationToken cancellationToken)
    {
        long rolloutRevisionAtStart;
        lock (_stateGate)
        {
            rolloutRevisionAtStart = _rolloutRateLimitRevision;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _cancellation.Token);

        RateLimitProbeResult? result;
        try
        {
            result = await _rateLimitProbe.ReadAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            // An injected or future probe implementation must never terminate the HUD.
            System.Diagnostics.Trace.TraceInformation(
                "RateLimitProbe implementation failed ({0}).",
                exception.GetType().Name);
            return;
        }

        if (result is null || !result.RateLimits.HasMeasuredWindow || _disposed)
        {
            return;
        }

        UsageState? changedState = null;
        lock (_stateGate)
        {
            var current = State.RateLimitObservation;
            var rolloutArrivedDuringProbe =
                _rolloutRateLimitRevision != rolloutRevisionAtStart;
            var currentIsClearlyNewer = current is not null &&
                current.ObservedAt > result.CompletedAt;
            var currentRolloutIsNewerThanProbeStart =
                current?.Source == RateLimitSource.Rollout &&
                current.ObservedAt >= result.StartedAt;

            if (!rolloutArrivedDuringProbe &&
                !currentIsClearlyNewer &&
                !currentRolloutIsNewerThanProbeStart)
            {
                var observation = new RateLimitObservation(
                    result.RateLimits,
                    result.CompletedAt,
                    RateLimitSource.Probe);
                State = new UsageState(State.ActiveSnapshot, observation);
                changedState = State;
            }
        }

        if (changedState is not null)
        {
            UsageChanged?.Invoke(this, changedState);
        }
    }

    private static bool IsEstimatedAfterReset(
        RateLimitWindow? window,
        DateTimeOffset now) =>
        window?.EvaluateAt(now).IsEstimatedAfterReset == true;

    private static bool HasUsageData(TokenCountRecord record) =>
        record.LastTokenUsage.TotalTokens is not null ||
        record.ModelContextWindow is not null ||
        record.RateLimits is not null;

    private void QueueRecoveryCandidates()
    {
        foreach (var path in _files.Keys.ToArray())
        {
            QueuePath(path);
        }

        foreach (var path in EnumerateRolloutsNewestFirst().Take(64))
        {
            QueuePath(path);
        }
    }

    private IEnumerable<string> EnumerateRolloutsNewestFirst()
    {
        if (!Directory.Exists(_sessionsDirectory))
        {
            return [];
        }

        try
        {
            return Directory
                .EnumerateFiles(_sessionsDirectory, "rollout-*.jsonl", SearchOption.AllDirectories)
                .Select(path => new FileInfo(path))
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .Select(file => file.FullName)
                .ToArray();
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }

    private bool IsRolloutPath(string path)
    {
        if (!string.Equals(Path.GetExtension(path), ".jsonl", StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(path).StartsWith("rollout-", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var relative = Path.GetRelativePath(_sessionsDirectory, Path.GetFullPath(path));
        return !relative.Equals("..", StringComparison.Ordinal) &&
               !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal);
    }

    private bool HasUnreadBytes(string path)
    {
        try
        {
            return _files.TryGetValue(path, out var file) &&
                   File.Exists(path) &&
                   new FileInfo(path).Length > file.Cursor.Offset;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static DateTimeOffset GetLastWriteTime(string path)
    {
        try
        {
            return File.GetLastWriteTimeUtc(path);
        }
        catch (IOException)
        {
            return DateTimeOffset.UtcNow;
        }
        catch (UnauthorizedAccessException)
        {
            return DateTimeOffset.UtcNow;
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task probeTask;
        lock (_probeGate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            probeTask = _probeTask;
        }

        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
        }

        _work.Writer.TryComplete();
        _cancellation.Cancel();

        if (_worker is not null)
        {
            try
            {
                await _worker;
            }
            catch (OperationCanceledException)
            {
            }
        }

        try
        {
            await probeTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _cancellation.Dispose();
    }

    private sealed class RolloutFileState
    {
        public JsonlCursor Cursor { get; } = new();
        public bool SessionMetaSeen { get; set; }
        public bool IsRootDesktopSession { get; set; }
        public string? ThreadId { get; set; }
        public UsageSnapshot? LatestSnapshot { get; set; }
    }
}
