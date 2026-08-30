using System.Text;
using CodexHud.Core;

namespace CodexHud.Core.Tests;

[TestClass]
public sealed class IncrementalJsonlReaderTests
{
    [TestMethod]
    public async Task EmitsOnlyCompleteNewLinesAndNeverRereadsThem()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "rollout-test.jsonl");
        var cursor = new JsonlCursor();
        var reader = new IncrementalJsonlReader();
        var lines = new List<string>();

        await File.WriteAllTextAsync(path, "{\"type\":\"one\"}\n{\"type\":\"par");
        await reader.ReadNewLinesAsync(path, cursor, Capture, CancellationToken.None);

        CollectionAssert.AreEqual(new[] { "{\"type\":\"one\"}" }, lines);

        await File.AppendAllTextAsync(path, "tial\"}\n{\"type\":\"two\"}\n");
        await reader.ReadNewLinesAsync(path, cursor, Capture, CancellationToken.None);
        await reader.ReadNewLinesAsync(path, cursor, Capture, CancellationToken.None);

        CollectionAssert.AreEqual(
            new[] { "{\"type\":\"one\"}", "{\"type\":\"partial\"}", "{\"type\":\"two\"}" },
            lines);
        return;

        bool Capture(ReadOnlyMemory<byte> line)
        {
            lines.Add(Encoding.UTF8.GetString(line.Span));
            return true;
        }
    }

    [TestMethod]
    public async Task OversizedIncompleteRecordIsDiscardedWithoutBreakingNextRecord()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "rollout-test.jsonl");
        var cursor = new JsonlCursor();
        var reader = new IncrementalJsonlReader(maximumLineBytes: 32);
        var lines = new List<string>();

        await File.WriteAllTextAsync(path, new string('x', 100) + "\n{}\n");
        await reader.ReadNewLinesAsync(
            path,
            cursor,
            line =>
            {
                lines.Add(Encoding.UTF8.GetString(line.Span));
                return true;
            },
            CancellationToken.None);

        CollectionAssert.AreEqual(new[] { "{}" }, lines);
    }
}
