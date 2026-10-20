// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Kuestenlogik.Bowire.Protocol.Mqtt;

/// <summary>
/// Converts MQTT binary payloads to a display-friendly string.
///
/// Strategy:
///   1. Try JSON.parse -> pretty-print
///   2. Try UTF-8 decode -> plain text (if it looks like valid text)
///   3. Fallback -> hex dump with size annotation
///
/// This keeps the UI usable for the common case (JSON IoT data) while
/// still rendering raw binary payloads (protobuf, images, sensor blobs)
/// as hex so the user at least sees the shape and size of the data.
/// </summary>
internal static class MqttPayloadHelper
{
    private static readonly JsonSerializerOptions IndentedOptions = new() { WriteIndented = true };

    // Throws on invalid bytes (#856). Encoding.UTF8 replaces them with
    // U+FFFD, which passes LooksLikeText, so binary never reached the hex dump.
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static string PayloadToDisplayString(byte[] payload)
    {
        if (payload.Length == 0)
            return "";

        // 1. Try JSON
        try
        {
            // Parse rejects trailing content (#855); TryParseValue read the
            // first value only and cut "42 apples" down to 42.
            using var doc = JsonDocument.Parse(payload);
            return JsonSerializer.Serialize(doc.RootElement, IndentedOptions);
        }
        catch
        {
            // Not valid JSON
        }

        // 2. Try UTF-8 text (reject if it contains control chars other than whitespace)
        try
        {
            var text = StrictUtf8.GetString(payload);
            if (LooksLikeText(text))
                return text;
        }
        catch (DecoderFallbackException)
        {
            // Invalid UTF-8
        }

        // 3. Hex dump
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
            if (i > 0 && i % 16 == 0)
                sb.AppendLine();
            else if (i > 0)
                sb.Append(' ');
            sb.Append(bytes[i].ToString("X2", CultureInfo.InvariantCulture));
        }

        if (bytes.Length > maxBytes)
            sb.Append(CultureInfo.InvariantCulture, $"\n... ({bytes.Length - maxBytes} more bytes)");

        return sb.ToString();
    }
}
