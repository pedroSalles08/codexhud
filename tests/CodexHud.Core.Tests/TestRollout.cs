using System.Text.Json;

namespace CodexHud.Core.Tests;

internal static class TestRollout
{
    public static string SessionMeta(string threadId, bool rootDesktop, bool cli = false)
    {
        object source = rootDesktop
            ? "vscode"
            : cli
                ? "cli"
                : new { subagent = new { thread_spawn = new { parent_thread_id = "parent" } } };

        return JsonSerializer.Serialize(new
        {
            timestamp = "2026-08-30T09:00:00Z",
            type = "session_meta",
            payload = new
            {
                id = threadId,
                originator = rootDesktop ? "Codex Desktop" : cli ? "Codex CLI" : "Codex Desktop",
                source,
            },
        });
    }

    public static string TokenCount(
        string timestamp,
        long totalTokens,
        long contextWindow,
        double primaryUsed,
        double secondaryUsed,
        long primaryResetsAt = 1_788_080_151,
        long secondaryResetsAt = 1_788_648_856,
        int primaryWindowMinutes = 300,
        int secondaryWindowMinutes = 10_080) =>
        JsonSerializer.Serialize(new
        {
            timestamp,
            type = "event_msg",
            payload = new
            {
                type = "token_count",
                info = new
                {
                    total_token_usage = new { total_tokens = totalTokens * 3 },
                    last_token_usage = new
                    {
                        input_tokens = totalTokens - 100,
                        cached_input_tokens = 10,
                        output_tokens = 80,
                        reasoning_output_tokens = 20,
                        total_tokens = totalTokens,
                    },
                    model_context_window = contextWindow,
                },
                rate_limits = new
                {
                    limit_id = "codex",
                    primary = new
                    {
                        used_percent = primaryUsed,
                        window_minutes = primaryWindowMinutes,
                        resets_at = primaryResetsAt,
                    },
                    secondary = new
                    {
                        used_percent = secondaryUsed,
                        window_minutes = secondaryWindowMinutes,
                        resets_at = secondaryResetsAt,
                    },
                },
            },
        });

    public static async Task WriteAsync(string path, params string[] records)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, string.Join(Environment.NewLine, records) + Environment.NewLine);
    }
}

internal sealed class NoOpRateLimitProbe : IRateLimitProbe
{
    public static NoOpRateLimitProbe Instance { get; } = new();

    public Task<RateLimitProbeResult?> ReadAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<RateLimitProbeResult?>(null);
}

internal sealed class TemporaryDirectory : IDisposable
{
    public TemporaryDirectory()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "codex-hud-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string RolloutPath(string name) =>
        System.IO.Path.Combine(Path, "2026", "08", "30", $"rollout-{name}.jsonl");

    public void Dispose()
    {
        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}
