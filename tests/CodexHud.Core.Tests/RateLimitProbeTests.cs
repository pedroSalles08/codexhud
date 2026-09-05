using System.Diagnostics;
using System.Text.Json;
using CodexHud.Core;

namespace CodexHud.Core.Tests;

[TestClass]
[DoNotParallelize]
public sealed class RateLimitProbeTests
{
    [TestMethod]
    public void ParsesMultipleBucketsWithoutAssumingWindowDurations()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "rateLimits": {
                "limitId": "codex",
                "primary": { "usedPercent": 27, "windowDurationMins": 300, "resetsAt": 1788123648 },
                "secondary": { "usedPercent": 29, "windowDurationMins": 10080, "resetsAt": 1788648856 }
              },
              "rateLimitsByLimitId": {
                "codex": {
                  "limitId": "codex",
                  "primary": { "usedPercent": 27, "windowDurationMins": 300, "resetsAt": 1788123648 },
                  "secondary": { "usedPercent": 29, "windowDurationMins": 10080, "resetsAt": 1788648856 }
                },
                "other": {
                  "limitId": "other",
                  "limitName": "Other meter",
                  "primary": { "usedPercent": 4, "windowDurationMins": 90, "resetsAt": 1788129999 }
                }
              }
            }
            """);

        Assert.IsTrue(RateLimitProbeResponseParser.TryParse(document.RootElement, out var limits));
        Assert.IsNotNull(limits);
        Assert.AreEqual("codex", limits.LimitId);
        Assert.AreEqual(300, limits.Primary?.WindowMinutes);
        Assert.AreEqual(10_080, limits.Secondary?.WindowMinutes);
        Assert.AreEqual(2, limits.ByLimitId?.Count);
        Assert.AreEqual(90, limits.ByLimitId?["other"].Primary?.WindowMinutes);
        Assert.AreEqual("Other meter", limits.ByLimitId?["other"].LimitName);
    }

    [TestMethod]
    public void RejectsIncompleteResponseInsteadOfInventingValues()
    {
        using var document = JsonDocument.Parse(
            """{"rateLimits":{"limitId":"codex","primary":{"windowDurationMins":300}}}""");

        Assert.IsFalse(RateLimitProbeResponseParser.TryParse(document.RootElement, out var limits));
        Assert.IsNull(limits);
    }

    [TestMethod]
    public async Task UsesOnlyReadOnlyHandshakeAndTerminatesSubprocess()
    {
        var powershell = FindWindowsPowerShell();
        if (powershell is null)
        {
            Assert.Inconclusive("Windows PowerShell is unavailable for subprocess lifecycle test.");
        }

        using var directory = new TemporaryDirectory();
        var messagesPath = Path.Combine(directory.Path, "messages.jsonl");
        var pidPath = Path.Combine(directory.Path, "pid.txt");
        var script = BuildFakeServerScript(messagesPath, pidPath, respond: true);
        var logs = new List<string>();
        var probe = new RateLimitProbe(
            new RateLimitProbeOptions
            {
                ExecutablePath = powershell,
                Arguments = ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", script],
                Timeout = TimeSpan.FromSeconds(10),
            },
            logs.Add);

        var result = await probe.ReadAsync();

        Assert.IsNotNull(result);
        Assert.AreEqual(73, result.RateLimits.Primary?.ReportedRemainingPercent);
        var messages = File.ReadAllLines(messagesPath)
            .Select(line => JsonDocument.Parse(line))
            .ToArray();
        try
        {
            CollectionAssert.AreEqual(
                new[] { "initialize", "initialized", "account/rateLimits/read" },
                messages.Select(message => message.RootElement.GetProperty("method").GetString()).ToArray());
            Assert.IsFalse(messages.Any(message =>
                message.RootElement.GetProperty("method").GetString()?.StartsWith("thread/", StringComparison.Ordinal) == true));
        }
        finally
        {
            foreach (var message in messages)
            {
                message.Dispose();
            }
        }

        var pid = int.Parse(await File.ReadAllTextAsync(pidPath));
        Assert.IsFalse(IsProcessRunning(pid), "The fake App Server process was left running.");
        Assert.IsEmpty(logs);
    }

    [TestMethod]
    public async Task TimeoutKillsSubprocessAndReturnsNoSnapshot()
    {
        var powershell = FindWindowsPowerShell();
        if (powershell is null)
        {
            Assert.Inconclusive("Windows PowerShell is unavailable for subprocess lifecycle test.");
        }

        using var directory = new TemporaryDirectory();
        var messagesPath = Path.Combine(directory.Path, "messages.jsonl");
        var pidPath = Path.Combine(directory.Path, "pid.txt");
        var script = BuildFakeServerScript(messagesPath, pidPath, respond: false);
        var logs = new List<string>();
        var probe = new RateLimitProbe(
            new RateLimitProbeOptions
            {
                ExecutablePath = powershell,
                Arguments = ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", script],
                Timeout = TimeSpan.FromSeconds(5),
            },
            logs.Add);

        var stopwatch = Stopwatch.StartNew();
        var result = await probe.ReadAsync();
        stopwatch.Stop();

        Assert.IsNull(result);
        Assert.IsLessThan(TimeSpan.FromSeconds(10), stopwatch.Elapsed);
        var pid = int.Parse(await File.ReadAllTextAsync(pidPath));
        Assert.IsFalse(IsProcessRunning(pid), "The timed-out App Server process was left running.");
        CollectionAssert.Contains(logs, "RateLimitProbe timed out.");
    }

    [TestMethod]
    public async Task UnavailableExecutableFailsClosedWithoutThrowing()
    {
        var logs = new List<string>();
        var probe = new RateLimitProbe(
            new RateLimitProbeOptions
            {
                ExecutablePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".exe"),
                Timeout = TimeSpan.FromMilliseconds(500),
            },
            logs.Add);

        var result = await probe.ReadAsync();

        Assert.IsNull(result);
        Assert.HasCount(1, logs);
        StringAssert.StartsWith(logs[0], "RateLimitProbe failed (");
    }

    private static string? FindWindowsPowerShell()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var systemRoot = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var path = Path.Combine(
            systemRoot,
            "System32",
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        return File.Exists(path) ? path : null;
    }

    private static string BuildFakeServerScript(
        string messagesPath,
        string pidPath,
        bool respond)
    {
        static string Quote(string value) => value.Replace("'", "''", StringComparison.Ordinal);

        var common =
            $"[IO.File]::WriteAllText('{Quote(pidPath)}', [string]$PID);" +
            $"$init=[Console]::In.ReadLine();[IO.File]::AppendAllText('{Quote(messagesPath)}',$init+[Environment]::NewLine);";
        if (!respond)
        {
            return common + "Start-Sleep -Seconds 30;";
        }

        return common +
            "[Console]::Out.WriteLine('{\"id\":1,\"result\":{\"userAgent\":\"fake\",\"codexHome\":\"C:/fake\",\"platformFamily\":\"windows\",\"platformOs\":\"windows\"}}');" +
            "$initialized=[Console]::In.ReadLine();" +
            $"[IO.File]::AppendAllText('{Quote(messagesPath)}',$initialized+[Environment]::NewLine);" +
            "$read=[Console]::In.ReadLine();" +
            $"[IO.File]::AppendAllText('{Quote(messagesPath)}',$read+[Environment]::NewLine);" +
            "[Console]::Out.WriteLine('{\"id\":2,\"result\":{\"rateLimits\":{\"limitId\":\"codex\",\"primary\":{\"usedPercent\":27,\"windowDurationMins\":300,\"resetsAt\":1988123648}}}}');" +
            "Start-Sleep -Seconds 30;";
    }

    private static bool IsProcessRunning(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
