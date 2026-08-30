using System.Diagnostics;
using System.Text.Json;

namespace CodexHud.Core;

public interface IRateLimitProbe
{
    Task<RateLimitProbeResult?> ReadAsync(CancellationToken cancellationToken = default);
}

public sealed record RateLimitProbeResult(
    RateLimits RateLimits,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt);

public sealed record RateLimitProbeOptions
{
    public string? ExecutablePath { get; init; }

    public IReadOnlyList<string>? Arguments { get; init; }

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(10);
}

/// <summary>
/// Runs one short-lived, read-only Codex App Server connection.
/// </summary>
public sealed class RateLimitProbe : IRateLimitProbe
{
    private const int InitializeRequestId = 1;
    private const int RateLimitsRequestId = 2;
    private static readonly string[] DefaultArguments = ["app-server", "--listen", "stdio://"];
    private readonly RateLimitProbeOptions _options;
    private readonly Action<string>? _diagnosticLog;

    public RateLimitProbe(
        RateLimitProbeOptions? options = null,
        Action<string>? diagnosticLog = null)
    {
        _options = options ?? new RateLimitProbeOptions();
        if (_options.Timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Probe timeout must be positive.");
        }

        _diagnosticLog = diagnosticLog ?? (message => Trace.TraceInformation(message));
    }

    public async Task<RateLimitProbeResult?> ReadAsync(
        CancellationToken cancellationToken = default)
    {
        var startedAt = DateTimeOffset.UtcNow;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.Timeout);

        Process? process = null;
        Task? stderrDrain = null;

        try
        {
            process = StartProcess();
            stderrDrain = DrainStandardErrorAsync(process.StandardError);

            await WriteMessageAsync(
                process,
                new
                {
                    method = "initialize",
                    id = InitializeRequestId,
                    @params = new
                    {
                        clientInfo = new
                        {
                            name = "codex_hud",
                            title = "Codex HUD",
                            version = "0.1.0",
                        },
                    },
                },
                timeout.Token);

            await ReadResponseAsync(process, InitializeRequestId, timeout.Token);

            await WriteMessageAsync(
                process,
                new { method = "initialized", @params = new { } },
                timeout.Token);
            await WriteMessageAsync(
                process,
                new { method = "account/rateLimits/read", id = RateLimitsRequestId },
                timeout.Token);

            var response = await ReadResponseAsync(process, RateLimitsRequestId, timeout.Token);
            if (!RateLimitProbeResponseParser.TryParse(response, out var rateLimits) ||
                rateLimits is null ||
                !rateLimits.HasMeasuredWindow)
            {
                Log("RateLimitProbe returned no measured rate-limit window.");
                return null;
            }

            return new RateLimitProbeResult(rateLimits, startedAt, DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException) when (
            !cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            Log("RateLimitProbe timed out.");
            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception exception) when (
            exception is IOException or
            InvalidOperationException or
            System.ComponentModel.Win32Exception or
            JsonException or
            UnauthorizedAccessException)
        {
            Log($"RateLimitProbe failed ({exception.GetType().Name}).");
            return null;
        }
        finally
        {
            if (process is not null)
            {
                if (!await StopProcessAsync(process))
                {
                    Log("RateLimitProbe could not confirm subprocess termination.");
                }

                process.Dispose();
            }

            if (stderrDrain is not null)
            {
                try
                {
                    await stderrDrain.WaitAsync(TimeSpan.FromSeconds(1));
                }
                catch (Exception exception) when (
                    exception is TimeoutException or IOException or ObjectDisposedException)
                {
                    _ = stderrDrain.ContinueWith(
                        task => _ = task.Exception,
                        CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);
                }
            }
        }
    }

    private Process StartProcess()
    {
        var executable = _options.ExecutablePath ?? CodexAppServerLocator.FindExecutable();
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (var argument in _options.Arguments ?? DefaultArguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException("Codex App Server did not start.");
        }

        return process;
    }

    private static async Task WriteMessageAsync(
        Process process,
        object message,
        CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(message);
        await process.StandardInput.WriteLineAsync(json.AsMemory(), cancellationToken);
        await process.StandardInput.FlushAsync(cancellationToken);
    }

    private static async Task DrainStandardErrorAsync(StreamReader standardError)
    {
        while (await standardError.ReadLineAsync(CancellationToken.None) is not null)
        {
            // Intentionally discard diagnostics so credentials or response bodies cannot be retained.
        }
    }

    private static async Task<JsonElement> ReadResponseAsync(
        Process process,
        int expectedId,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var line = await process.StandardOutput.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                throw new IOException($"Codex App Server exited before response {expectedId}.");
            }

            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (!HasRequestId(root, expectedId))
            {
                // Notifications may be interleaved with responses. They are intentionally ignored.
                continue;
            }

            if (root.TryGetProperty("error", out var error))
            {
                var code = error.ValueKind == JsonValueKind.Object &&
                           error.TryGetProperty("code", out var codeValue) &&
                           codeValue.TryGetInt64(out var numericCode)
                    ? numericCode.ToString()
                    : "unknown";
                throw new InvalidOperationException(
                    $"Codex App Server request {expectedId} failed (code={code}).");
            }

            if (!root.TryGetProperty("result", out var result))
            {
                throw new InvalidOperationException(
                    $"Codex App Server response {expectedId} has no result.");
            }

            return result.Clone();
        }
    }

    private static bool HasRequestId(JsonElement root, int expectedId) =>
        root.ValueKind == JsonValueKind.Object &&
        root.TryGetProperty("id", out var id) &&
        id.ValueKind == JsonValueKind.Number &&
        id.TryGetInt32(out var value) &&
        value == expectedId;

    private static async Task<bool> StopProcessAsync(Process process)
    {
        try
        {
            process.StandardInput.Close();
        }
        catch (Exception exception) when (
            exception is IOException or InvalidOperationException or ObjectDisposedException)
        {
        }

        try
        {
            if (process.HasExited)
            {
                return true;
            }

            try
            {
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMilliseconds(750));
                return true;
            }
            catch (TimeoutException)
            {
            }

            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2));
            return process.HasExited;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or
            System.ComponentModel.Win32Exception or
            TimeoutException or
            IOException or
            ObjectDisposedException or
            NotSupportedException)
        {
            return false;
        }
    }

    private void Log(string message) => _diagnosticLog?.Invoke(message);
}

internal static class CodexAppServerLocator
{
    public static string FindExecutable()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            var installed = Path.Combine(
                localAppData,
                "Programs",
                "OpenAI",
                "Codex",
                "bin",
                OperatingSystem.IsWindows() ? "codex.exe" : "codex");
            if (File.Exists(installed))
            {
                return installed;
            }

            var versionedRoot = Path.Combine(localAppData, "OpenAI", "Codex", "bin");
            try
            {
                if (Directory.Exists(versionedRoot))
                {
                    var versioned = Directory
                        .EnumerateFiles(
                            versionedRoot,
                            OperatingSystem.IsWindows() ? "codex.exe" : "codex",
                            SearchOption.AllDirectories)
                        .Select(path => new FileInfo(path))
                        .OrderByDescending(file => file.LastWriteTimeUtc)
                        .FirstOrDefault();
                    if (versioned is not null)
                    {
                        return versioned.FullName;
                    }
                }
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
            }
        }

        // ProcessStartInfo resolves this through PATH without invoking another locator process.
        return OperatingSystem.IsWindows() ? "codex.exe" : "codex";
    }
}

internal static class RateLimitProbeResponseParser
{
    public static bool TryParse(JsonElement result, out RateLimits? rateLimits)
    {
        rateLimits = null;
        if (result.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var buckets = ReadBuckets(result);
        RateLimitBucket? selected = null;
        if (result.TryGetProperty("rateLimits", out var legacy))
        {
            selected = ReadBucket(legacy, fallbackLimitId: null);
        }

        if (selected?.HasMeasuredWindow != true && buckets is not null)
        {
            if (selected?.LimitId is string selectedId &&
                buckets.TryGetValue(selectedId, out var matching) &&
                matching.HasMeasuredWindow)
            {
                selected = matching;
            }
            else if (buckets.TryGetValue("codex", out var codex) && codex.HasMeasuredWindow)
            {
                selected = codex;
            }
            else
            {
                selected = buckets.Values.FirstOrDefault(bucket => bucket.HasMeasuredWindow);
            }
        }

        if (selected?.HasMeasuredWindow != true)
        {
            return false;
        }

        rateLimits = new RateLimits(
            selected.LimitId,
            selected.Primary,
            selected.Secondary,
            buckets);
        return true;
    }

    private static IReadOnlyDictionary<string, RateLimitBucket>? ReadBuckets(JsonElement result)
    {
        if (!result.TryGetProperty("rateLimitsByLimitId", out var byLimitId) ||
            byLimitId.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var buckets = new Dictionary<string, RateLimitBucket>(StringComparer.Ordinal);
        foreach (var property in byLimitId.EnumerateObject())
        {
            var bucket = ReadBucket(property.Value, property.Name);
            if (bucket is not null)
            {
                buckets[property.Name] = bucket;
            }
        }

        return buckets.Count == 0 ? null : buckets;
    }

    private static RateLimitBucket? ReadBucket(JsonElement element, string? fallbackLimitId)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return new RateLimitBucket(
            ReadString(element, "limitId") ?? fallbackLimitId,
            ReadString(element, "limitName"),
            ReadWindow(element, "primary"),
            ReadWindow(element, "secondary"));
    }

    private static RateLimitWindow? ReadWindow(JsonElement bucket, string propertyName)
    {
        if (!bucket.TryGetProperty(propertyName, out var window) ||
            window.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var usedPercent = ReadPercent(window, "usedPercent");
        var duration = ReadPositiveInt32(window, "windowDurationMins");
        var resetsAt = ReadUnixTimestamp(window, "resetsAt");
        return usedPercent is null && duration is null && resetsAt is null
            ? null
            : new RateLimitWindow(usedPercent, duration, resetsAt);
    }

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) &&
        property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static double? ReadPercent(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) &&
        property.TryGetDouble(out var value) &&
        double.IsFinite(value) &&
        value is >= 0 and <= 100
            ? value
            : null;

    private static int? ReadPositiveInt32(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) ||
            !property.TryGetInt64(out var value) ||
            value is <= 0 or > int.MaxValue)
        {
            return null;
        }

        return (int)value;
    }

    private static DateTimeOffset? ReadUnixTimestamp(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) ||
            !property.TryGetInt64(out var value))
        {
            return null;
        }

        try
        {
            return DateTimeOffset.FromUnixTimeSeconds(value);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }
}
