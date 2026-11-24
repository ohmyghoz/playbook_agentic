using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Surveillance.Agents.Abstractions;

/// <summary>
/// The evidence fields of <c>tool_call_finished</c>: <c>source</c> from the idx JSON envelope and
/// <c>resultPreview</c>, the raw output cut to 8192 UTF-8 bytes. Mirrored in the Python services'
/// contract.py so all adapters cut at the same place.
/// </summary>
public static class ToolEvidence
{
    public const string Server = "idx";
    public const int MaxPreviewBytes = 8192;
    public const string TruncationMarker = "\n…[dipotong]";

    /// <summary>Cuts <paramref name="raw"/> at a character boundary so the result, marker included, fits.</summary>
    public static string Preview(string raw)
    {
        if (Encoding.UTF8.GetByteCount(raw) <= MaxPreviewBytes)
        {
            return raw;
        }

        var budget = MaxPreviewBytes - Encoding.UTF8.GetByteCount(TruncationMarker);
        var builder = new StringBuilder();
        var used = 0;
        var runes = raw.EnumerateRunes();
        foreach (var rune in runes)
        {
            if (used + rune.Utf8SequenceLength > budget)
            {
                break;
            }

            builder.Append(rune.ToString());
            used += rune.Utf8SequenceLength;
        }

        return builder.Append(TruncationMarker).ToString();
    }

    /// <summary>Reads provider, fetched_at and stale from an idx envelope; any of them may be missing.</summary>
    public static ToolSource Source(JsonElement? envelope)
    {
        if (envelope is not { ValueKind: JsonValueKind.Object } root)
        {
            return new ToolSource(Server);
        }

        string? provider = root.TryGetProperty("provider", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
        DateTime? fetchedAt = root.TryGetProperty("fetched_at", out var f) && f.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(f.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)
                ? at.UtcDateTime
                : null;
        bool? stale = root.TryGetProperty("stale", out var s) && s.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? s.GetBoolean()
            : null;
        return new ToolSource(Server, provider, fetchedAt, stale);
    }
}
