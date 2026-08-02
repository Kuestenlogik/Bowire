// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Text;
using Kuestenlogik.Bowire.Mocking;
using Kuestenlogik.Bowire.Security;

namespace Kuestenlogik.Bowire.Security.Scanner;

/// <summary>
/// Runs a translated Nuclei <c>network:</c> / <c>tcp:</c> template (#491,
/// #35 Phase 2g): open a socket, write the payloads, read the reply, match
/// over it. The shape behind unauthenticated-Redis, exposed-Memcached and
/// banner-grab templates.
/// </summary>
public static class NetworkProbeExecutor
{
    /// <summary>
    /// Connect, send, read. <paramref name="probe"/> carries the address in
    /// <see cref="BowireRecordingStep.Service"/>, the payloads in
    /// <see cref="BowireRecordingStep.Messages"/> and the read budget in
    /// <c>Metadata["read-size"]</c>, as written by the converter.
    /// </summary>
    /// <param name="probe">The recording step to run.</param>
    /// <param name="timeoutSeconds">Connect + read budget.</param>
    /// <param name="ct">Cancels the probe.</param>
    public static async Task<AttackProbeResponse> ExecuteAsync(
        BowireRecordingStep probe,
        int timeoutSeconds = 10,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(probe);

        var (host, port) = ParseAddress(probe.Service ?? string.Empty);

        var readSize = 1024;
        if (probe.Metadata is { } meta
            && meta.TryGetValue("read-size", out var raw)
            && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            && parsed > 0)
        {
            readSize = parsed;
        }

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds)));

        var sw = Stopwatch.StartNew();
        using var client = new TcpClient();
        await client.ConnectAsync(host, port, budget.Token).ConfigureAwait(false);

        await using var stream = client.GetStream();

        foreach (var message in probe.Messages)
        {
            var payload = DecodePayload(message);
            if (payload.Length == 0) continue;
            await stream.WriteAsync(payload, budget.Token).ConfigureAwait(false);
        }
        await stream.FlushAsync(budget.Token).ConfigureAwait(false);

        var buffer = new byte[readSize];
        var total = 0;
        try
        {
            // Read until the budget runs out or the peer stops talking. A
            // single ReadAsync would take whatever arrived in the first packet,
            // which for a banner split across segments is a truncated body and
            // therefore a missed match.
            while (total < buffer.Length)
            {
                var read = await stream
                    .ReadAsync(buffer.AsMemory(total, buffer.Length - total), budget.Token)
                    .ConfigureAwait(false);
                if (read <= 0) break;
                total += read;
                if (!client.Connected || !stream.DataAvailable) break;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // The read budget expired. Whatever arrived is still the answer —
            // plenty of services send a banner and then simply wait, and
            // throwing here would report an error for a successful probe.
        }
        sw.Stop();

        return new AttackProbeResponse
        {
            Status = 0,
            Body = DecodeResponse(buffer.AsSpan(0, total)),
            LatencyMs = (int)sw.ElapsedMilliseconds,
        };
    }

    /// <summary>
    /// Split <c>host:port</c>. A bare host is refused rather than guessed:
    /// <c>{{Hostname}}</c> resolves without a port on 80/443, and picking one
    /// silently would probe a service the template never named.
    /// </summary>
    public static (string Host, int Port) ParseAddress(string address)
    {
        var value = address.Trim();
        if (value.Length == 0)
        {
            throw new InvalidOperationException("network template carries no address to connect to.");
        }
        if (value.Contains("{{", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"network template address still holds an unresolved placeholder ({value}) — bind a target so it can be substituted.");
        }

        var colon = value.LastIndexOf(':');
        if (colon <= 0 || colon == value.Length - 1)
        {
            throw new InvalidOperationException(
                $"network template address '{value}' has no port. Nuclei's {{{{Hostname}}}} omits the port on 80/443, so the template has to pin one, e.g. {{{{Host}}}}:6379.");
        }

        var portText = value[(colon + 1)..];
        if (!int.TryParse(portText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port)
            || port is < 1 or > 65535)
        {
            throw new InvalidOperationException($"network template address '{value}' has no usable port.");
        }

        return (value[..colon], port);
    }

    /// <summary>
    /// Turn one converter-written message into bytes. <c>hex:</c> marks a
    /// hex-encoded payload (Nuclei's <c>type: hex</c>); everything else is text
    /// carrying Go-style escapes.
    /// </summary>
    public static byte[] DecodePayload(string message)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (message.StartsWith("hex:", StringComparison.Ordinal))
        {
            var hex = message[4..].Replace("\n", "", StringComparison.Ordinal).Trim();
            try
            {
                return Convert.FromHexString(hex);
            }
            catch (FormatException ex)
            {
                throw new InvalidOperationException($"network template input is not valid hex: {ex.Message}", ex);
            }
        }

        return Encoding.UTF8.GetBytes(Unescape(message));
    }

    /// <summary>
    /// Expand the escapes Nuclei templates write literally in YAML. Without
    /// this a <c>PING\r\n</c> goes out as the eight characters P I N G \ r \ n
    /// and no Redis answers it — the probe would report "not vulnerable" for a
    /// wide-open server.
    /// </summary>
    public static string Unescape(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!value.Contains('\\', StringComparison.Ordinal)) return value;

        var sb = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] != '\\' || i == value.Length - 1)
            {
                sb.Append(value[i]);
                continue;
            }

            var next = value[++i];
            switch (next)
            {
                case 'r': sb.Append('\r'); break;
                case 'n': sb.Append('\n'); break;
                case 't': sb.Append('\t'); break;
                case '0': sb.Append('\0'); break;
                case 'a': sb.Append('\a'); break;
                case 'b': sb.Append('\b'); break;
                case 'f': sb.Append('\f'); break;
                case 'v': sb.Append('\v'); break;
                case '\\': sb.Append('\\'); break;
                case '"': sb.Append('"'); break;
                case '\'': sb.Append('\''); break;
                case 'x' when i + 2 < value.Length
                    && byte.TryParse(value.AsSpan(i + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b):
                    sb.Append((char)b);
                    i += 2;
                    break;
                default:
                    // Unknown escape: keep both characters. Nuclei payloads
                    // contain literal backslashes often enough that swallowing
                    // them would corrupt the wire data.
                    sb.Append('\\').Append(next);
                    break;
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Render the reply as text for the matchers. Latin-1 rather than UTF-8:
    /// a binary banner is not valid UTF-8, and the replacement characters that
    /// decoding would produce destroy exactly the byte sequences a
    /// <c>word</c> matcher is hunting for. Latin-1 is lossless byte-to-char.
    /// </summary>
    public static string DecodeResponse(ReadOnlySpan<byte> bytes)
    {
        return bytes.Length == 0 ? string.Empty : Encoding.Latin1.GetString(bytes);
    }
}
