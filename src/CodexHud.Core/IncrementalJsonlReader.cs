namespace CodexHud.Core;

internal sealed class JsonlCursor
{
    public long Offset { get; set; }
    public byte[] PendingBytes { get; set; } = [];
    public bool DiscardingOversizedLine { get; set; }
}

internal sealed class IncrementalJsonlReader(int maximumLineBytes = 1024 * 1024)
{
    private readonly int _maximumLineBytes = maximumLineBytes > 0
        ? maximumLineBytes
        : throw new ArgumentOutOfRangeException(nameof(maximumLineBytes));

    public async Task ReadNewLinesAsync(
        string path,
        JsonlCursor cursor,
        Func<ReadOnlyMemory<byte>, bool> onLine,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 64 * 1024,
            options: FileOptions.Asynchronous | FileOptions.SequentialScan);

        if (stream.Length < cursor.Offset)
        {
            cursor.Offset = 0;
            cursor.PendingBytes = [];
            cursor.DiscardingOversizedLine = false;
        }

        stream.Position = cursor.Offset;
        var readBuffer = new byte[64 * 1024];

        while (true)
        {
            var bytesRead = await stream.ReadAsync(readBuffer, cancellationToken);
            if (bytesRead == 0)
            {
                break;
            }

            cursor.Offset += bytesRead;
            var start = 0;

            for (var index = 0; index < bytesRead; index++)
            {
                if (readBuffer[index] != (byte)'\n')
                {
                    continue;
                }

                var segmentLength = index - start;
                if (!cursor.DiscardingOversizedLine)
                {
                    if (!AppendAndDispatch(cursor, readBuffer.AsSpan(start, segmentLength), onLine))
                    {
                        cursor.Offset = stream.Length;
                        cursor.PendingBytes = [];
                        cursor.DiscardingOversizedLine = false;
                        return;
                    }
                }

                cursor.PendingBytes = [];
                cursor.DiscardingOversizedLine = false;
                start = index + 1;
            }

            if (start < bytesRead && !cursor.DiscardingOversizedLine)
            {
                AppendPending(cursor, readBuffer.AsSpan(start, bytesRead - start));
            }
        }
    }

    private bool AppendAndDispatch(
        JsonlCursor cursor,
        ReadOnlySpan<byte> finalSegment,
        Func<ReadOnlyMemory<byte>, bool> onLine)
    {
        var lineLength = cursor.PendingBytes.Length + finalSegment.Length;
        if (lineLength > _maximumLineBytes)
        {
            return true;
        }

        var trimCarriageReturn = finalSegment.Length > 0 && finalSegment[^1] == (byte)'\r';
        var finalLength = finalSegment.Length - (trimCarriageReturn ? 1 : 0);
        var line = new byte[cursor.PendingBytes.Length + finalLength];
        cursor.PendingBytes.CopyTo(line, 0);
        finalSegment[..finalLength].CopyTo(line.AsSpan(cursor.PendingBytes.Length));

        return line.Length == 0 || onLine(line);
    }

    private void AppendPending(JsonlCursor cursor, ReadOnlySpan<byte> segment)
    {
        var combinedLength = cursor.PendingBytes.Length + segment.Length;
        if (combinedLength > _maximumLineBytes)
        {
            cursor.PendingBytes = [];
            cursor.DiscardingOversizedLine = true;
            return;
        }

        var pending = new byte[combinedLength];
        cursor.PendingBytes.CopyTo(pending, 0);
        segment.CopyTo(pending.AsSpan(cursor.PendingBytes.Length));
        cursor.PendingBytes = pending;
    }
}
