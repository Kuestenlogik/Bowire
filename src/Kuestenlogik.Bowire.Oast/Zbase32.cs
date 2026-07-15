// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Oast;

/// <summary>
/// z-base-32 encoding — the alphabet interactsh uses for the nonce half of a
/// callback host. It is NOT RFC-4648 base32: the alphabet is permuted to drop
/// visually confusable characters, so encoding with a stock base32 routine
/// produces hosts the server won't correlate.
/// </summary>
/// <remarks>
/// Only the encode direction is needed — the nonce is generated here and never
/// read back. Emits <c>ceil(bits/5)</c> characters, lowest bit first, matching
/// the reference implementation's bit order.
/// </remarks>
internal static class Zbase32
{
    private const string Alphabet = "ybndrfg8ejkmcpqxot1uwisza345h769";

    /// <summary>Encode <paramref name="data"/>, returning at most <paramref name="maxChars"/> characters.</summary>
    internal static string Encode(ReadOnlySpan<byte> data, int maxChars)
    {
        var sb = new System.Text.StringBuilder(maxChars);
        var buffer = 0;
        var bits = 0;
        foreach (var b in data)
        {
            // Little-endian bit accumulation: the reference encoder pushes each
            // byte above the bits already held, then drains 5 at a time from
            // the bottom.
            buffer |= b << bits;
            bits += 8;
            while (bits >= 5)
            {
                sb.Append(Alphabet[buffer & 0x1F]);
                if (sb.Length == maxChars) return sb.ToString();
                buffer >>= 5;
                bits -= 5;
            }
        }
        if (bits > 0 && sb.Length < maxChars) sb.Append(Alphabet[buffer & 0x1F]);
        return sb.ToString();
    }
}
