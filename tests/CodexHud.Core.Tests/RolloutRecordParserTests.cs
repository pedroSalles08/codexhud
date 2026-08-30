using System.Text;
using CodexHud.Core;

namespace CodexHud.Core.Tests;

[TestClass]
public sealed class RolloutRecordParserTests
{
    [TestMethod]
    public void ExtractsOnlyUsageFieldsFromTokenCount()
    {
        var json = TestRollout.TokenCount(
            "2026-08-30T12:00:00Z",
            totalTokens: 81_234,
            contextWindow: 258_400,
            primaryUsed: 16,
            secondaryUsed: 39);

        Assert.IsTrue(RolloutRecordParser.TryParse(Encoding.UTF8.GetBytes(json), out var record));
        var token = record as TokenCountRecord;
        Assert.IsNotNull(token);
        Assert.AreEqual(81_234, token.LastTokenUsage.TotalTokens);
        Assert.AreEqual(258_400, token.ModelContextWindow);
        Assert.AreEqual(84, token.RateLimits?.Primary?.ReportedRemainingPercent);
        Assert.AreEqual(61, token.RateLimits?.Secondary?.ReportedRemainingPercent);
        Assert.AreEqual(300, token.RateLimits?.Primary?.WindowMinutes);
        Assert.AreEqual(10_080, token.RateLimits?.Secondary?.WindowMinutes);
        Assert.IsNotNull(token.RateLimits?.Primary?.ResetsAt);
    }

    [TestMethod]
    public void DistinguishesDesktopRootFromSubagentAndCli()
    {
        ParseMeta(TestRollout.SessionMeta("root", rootDesktop: true), out var root);
        ParseMeta(TestRollout.SessionMeta("agent", rootDesktop: false), out var subagent);
        ParseMeta(TestRollout.SessionMeta("cli", rootDesktop: false, cli: true), out var cli);

        Assert.IsTrue(root.IsRootDesktopSession);
        Assert.IsFalse(subagent.IsRootDesktopSession);
        Assert.IsFalse(cli.IsRootDesktopSession);
    }

    [TestMethod]
    public void MissingNullAndMalformedFieldsAreTolerated()
    {
        var missing = Encoding.UTF8.GetBytes(
            "{\"timestamp\":null,\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":null,\"rate_limits\":null}}");

        Assert.IsTrue(RolloutRecordParser.TryParse(missing, out var record));
        var token = record as TokenCountRecord;
        Assert.IsNotNull(token);
        Assert.IsNull(token.LastTokenUsage.TotalTokens);
        Assert.IsNull(token.ModelContextWindow);
        Assert.IsNull(token.RateLimits);
        Assert.IsFalse(RolloutRecordParser.TryParse("not-json"u8.ToArray(), out _));
    }

    [TestMethod]
    public void ConversationRecordsAreIgnored()
    {
        var response = Encoding.UTF8.GetBytes(
            "{\"type\":\"response_item\",\"payload\":{\"content\":\"must not be retained\"}}");

        Assert.IsFalse(RolloutRecordParser.TryParse(response, out var record));
        Assert.IsNull(record);
    }

    private static void ParseMeta(string json, out SessionMetaRecord metadata)
    {
        Assert.IsTrue(RolloutRecordParser.TryParse(Encoding.UTF8.GetBytes(json), out var record));
        metadata = (SessionMetaRecord)record!;
    }
}
