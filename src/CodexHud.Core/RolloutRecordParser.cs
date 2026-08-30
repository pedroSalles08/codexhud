using System.Text.Json;

namespace CodexHud.Core;

internal abstract record RolloutRecord(DateTimeOffset? Timestamp);

internal sealed record SessionMetaRecord(
    string? ThreadId,
    string? Originator,
    bool IsRootDesktopSession,
    DateTimeOffset? Timestamp) : RolloutRecord(Timestamp);

internal sealed record TokenCountRecord(
    TokenUsage LastTokenUsage,
    long? ModelContextWindow,
    RateLimits? RateLimits,
    DateTimeOffset? Timestamp) : RolloutRecord(Timestamp);

internal static class RolloutRecordParser
{
    public static bool TryParse(ReadOnlyMemory<byte> utf8Json, out RolloutRecord? record)
    {
        record = null;

        try
        {
            if (!IsUsageRecord(utf8Json.Span))
            {
                return false;
            }

            using var document = JsonDocument.Parse(utf8Json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !TryGetString(root, "type", out var recordType))
            {
                return false;
            }

            var timestamp = ReadTimestamp(root, "timestamp");
            if (!root.TryGetProperty("payload", out var payload) ||
                payload.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (string.Equals(recordType, "session_meta", StringComparison.Ordinal))
            {
                record = ParseSessionMeta(payload, timestamp);
                return true;
            }

            if (!string.Equals(recordType, "event_msg", StringComparison.Ordinal) ||
                !TryGetString(payload, "type", out var payloadType) ||
                !string.Equals(payloadType, "token_count", StringComparison.Ordinal))
            {
                return false;
            }

            record = ParseTokenCount(payload, timestamp);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static bool IsUsageRecord(ReadOnlySpan<byte> utf8Json)
    {
        var reader = new Utf8JsonReader(utf8Json);
        string? rootType = null;
        string? payloadType = null;
        var insidePayload = false;

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.PropertyName &&
                reader.CurrentDepth == 1 &&
                reader.ValueTextEquals("type"u8))
            {
                if (!reader.Read() || reader.TokenType != JsonTokenType.String)
                {
                    return false;
                }

                rootType = reader.GetString();
                if (!string.Equals(rootType, "session_meta", StringComparison.Ordinal) &&
                    !string.Equals(rootType, "event_msg", StringComparison.Ordinal))
                {
                    // Conversation and tool records are rejected before their payload is parsed.
                    return false;
                }

                if (string.Equals(rootType, "session_meta", StringComparison.Ordinal))
                {
                    return true;
                }

                if (payloadType is not null)
                {
                    return string.Equals(payloadType, "token_count", StringComparison.Ordinal);
                }
            }
            else if (reader.TokenType == JsonTokenType.PropertyName &&
                     reader.CurrentDepth == 1 &&
                     reader.ValueTextEquals("payload"u8))
            {
                insidePayload = true;
            }
            else if (insidePayload &&
                     reader.TokenType == JsonTokenType.PropertyName &&
                     reader.CurrentDepth == 2 &&
                     reader.ValueTextEquals("type"u8))
            {
                if (!reader.Read() || reader.TokenType != JsonTokenType.String)
                {
                    return false;
                }

                payloadType = reader.GetString();
                if (rootType is not null)
                {
                    return string.Equals(rootType, "event_msg", StringComparison.Ordinal) &&
                           string.Equals(payloadType, "token_count", StringComparison.Ordinal);
                }
            }
        }

        return false;
    }

    private static SessionMetaRecord ParseSessionMeta(JsonElement payload, DateTimeOffset? timestamp)
    {
        TryGetString(payload, "id", out var threadId);
        TryGetString(payload, "originator", out var originator);

        var isRootDesktop = string.Equals(originator, "Codex Desktop", StringComparison.Ordinal) &&
                            payload.TryGetProperty("source", out var source) &&
                            source.ValueKind == JsonValueKind.String &&
                            string.Equals(source.GetString(), "vscode", StringComparison.Ordinal);

        return new SessionMetaRecord(threadId, originator, isRootDesktop, timestamp);
    }

    private static TokenCountRecord ParseTokenCount(JsonElement payload, DateTimeOffset? timestamp)
    {
        long? totalTokens = null;
        long? inputTokens = null;
        long? cachedInputTokens = null;
        long? outputTokens = null;
        long? reasoningOutputTokens = null;
        long? modelContextWindow = null;

        if (payload.TryGetProperty("info", out var info) && info.ValueKind == JsonValueKind.Object)
        {
            modelContextWindow = ReadNonNegativeInt64(info, "model_context_window");
            if (info.TryGetProperty("last_token_usage", out var last) &&
                last.ValueKind == JsonValueKind.Object)
            {
                totalTokens = ReadNonNegativeInt64(last, "total_tokens");
                inputTokens = ReadNonNegativeInt64(last, "input_tokens");
                cachedInputTokens = ReadNonNegativeInt64(last, "cached_input_tokens");
                outputTokens = ReadNonNegativeInt64(last, "output_tokens");
                reasoningOutputTokens = ReadNonNegativeInt64(last, "reasoning_output_tokens");
            }
        }

        var usage = new TokenUsage(
            totalTokens,
            inputTokens,
            cachedInputTokens,
            outputTokens,
            reasoningOutputTokens);

        RateLimits? rateLimits = null;
        if (payload.TryGetProperty("rate_limits", out var limits) &&
            limits.ValueKind == JsonValueKind.Object)
        {
            TryGetString(limits, "limit_id", out var limitId);
            rateLimits = new RateLimits(
                limitId,
                ReadRateLimitWindow(limits, "primary"),
                ReadRateLimitWindow(limits, "secondary"));
        }

        return new TokenCountRecord(usage, modelContextWindow, rateLimits, timestamp);
    }

    private static RateLimitWindow? ReadRateLimitWindow(JsonElement limits, string propertyName)
    {
        if (!limits.TryGetProperty(propertyName, out var window) ||
            window.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return new RateLimitWindow(
            ReadDouble(window, "used_percent"),
            ReadPositiveInt32(window, "window_minutes"),
            ReadUnixTimestamp(window, "resets_at"));
    }

    private static bool TryGetString(JsonElement element, string propertyName, out string? value)
    {
        value = null;
        if (!element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString();
        return value is not null;
    }

    private static long? ReadInt64(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.TryGetInt64(out var value)
            ? value
            : null;

    private static long? ReadNonNegativeInt64(JsonElement element, string propertyName)
    {
        var value = ReadInt64(element, propertyName);
        return value >= 0 ? value : null;
    }

    private static int? ReadPositiveInt32(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) ||
            !property.TryGetInt32(out var value) ||
            value <= 0)
        {
            return null;
        }

        return value;
    }

    private static double? ReadDouble(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) ||
            !property.TryGetDouble(out var value) ||
            !double.IsFinite(value))
        {
            return null;
        }

        return value;
    }

    private static DateTimeOffset? ReadTimestamp(JsonElement element, string propertyName) =>
        TryGetString(element, propertyName, out var value) &&
        DateTimeOffset.TryParse(value, out var timestamp)
            ? timestamp
            : null;

    private static DateTimeOffset? ReadUnixTimestamp(JsonElement element, string propertyName)
    {
        var seconds = ReadInt64(element, propertyName);
        if (seconds is null)
        {
            return null;
        }

        try
        {
            return DateTimeOffset.FromUnixTimeSeconds(seconds.Value);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }
}
