// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Kuestenlogik.Bowire.Protocol.Nats;

/// <summary>
/// Converts NATS binary payloads to a display-friendly string. Same
/// strategy as <c>MqttPayloadHelper</c>: try JSON, then UTF-8, then
/// hex — keeps the UI usable for both structured and binary payloads
/// without burying the user in escape codes.
/// </summary>
internal static class NatsPayloadHelper
{
    private static readonly JsonSerializerOptions s_indented = new() { WriteIndented = true };

    public static string PayloadToDisplayString(byte[] payload)
    {
        if (payload.Length == 0) return "";

        try
        {
            var reader = new Utf8JsonReader(payload);
            if (JsonDocument.TryParseValue(ref reader, out var doc))
            {
                using (doc)
                    return JsonSerializer.Serialize(doc.RootElement, s_indented);
            }
        }
        catch
        {
            // Not valid JSON — fall through.
        }

        try
        {
            var text = Encoding.UTF8.GetString(payload);
            if (LooksLikeText(text)) return text;
        }
        catch
        {
            // Invalid UTF-8.
        }

        return FormatHexDump(payload);
    }

    private static bool LooksLikeText(string s) =>
        !s.Any(c => char.IsControl(c) && c != '\n' && c != '\r' && c != '\t');

    private static string FormatHexDump(ReadOnlySpan<byte> bytes)
    {
        const int maxBytes = 256;
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"[binary: {bytes.Length} bytes]");
        sb.AppendLine();

        var limit = Math.Min(bytes.Length, maxBytes);
        for (var i = 0; i < limit; i++)
        {
            if (i > 0 && i % 16 == 0) sb.AppendLine();
            else if (i > 0) sb.Append(' ');
            sb.Append(bytes[i].ToString("X2", CultureInfo.InvariantCulture));
        }

        if (bytes.Length > maxBytes)
            sb.Append(CultureInfo.InvariantCulture, $"\n... ({bytes.Length - maxBytes} more bytes)");

        return sb.ToString();
    }
}
