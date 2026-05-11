// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Buffers;
using System.Buffers.Text;
using System.Text.Json;

namespace Kuestenlogik.Bowire.Semantics.Detectors;

/// <summary>
/// Shared low-level helpers for the built-in detectors. Decoding
/// base64 prefixes and matching magic-byte sequences is the same shape
/// across <see cref="ImageBytesDetector"/> and
/// <see cref="AudioBytesDetector"/>; keeping the logic in one place
/// avoids divergent bugs and keeps each detector focused on its own
/// magic table.
/// </summary>
internal static class DetectorHelpers
{
    /// <summary>
    /// Decode the first <paramref name="byteCount"/> bytes of
    /// <paramref name="base64"/>. Returns the actual byte-count
    /// decoded (≤ <paramref name="byteCount"/>) into the front of
    /// <paramref name="buffer"/>, or <c>0</c> when the string is
    /// obviously not base64 (too short, illegal characters in the
    /// prefix). The full string is never decoded — bounded work,
    /// bounded latency.
    /// </summary>
    /// <remarks>
    /// We grab enough base64 characters to cover
    /// <paramref name="byteCount"/> bytes (4 chars per 3 bytes,
    /// rounded up to the next group of four), then call
    /// <see cref="Base64.DecodeFromUtf8"/>. Reading more characters
    /// than needed costs a tiny fixed amount; reading fewer would
    /// fail on a padding-free prefix of a real string.
    /// </remarks>
    internal static int TryDecodeBase64Prefix(string base64, int byteCount, Span<byte> buffer)
    {
        if (string.IsNullOrEmpty(base64)) return 0;
        if (buffer.Length < byteCount) return 0;

        // 4 base64 chars = 3 bytes. Round up to the next group of 4
        // so DecodeFromUtf8 always sees a complete quantum.
        var charsNeeded = ((byteCount + 2) / 3) * 4;
        if (base64.Length < charsNeeded) return 0;

        // Quick reject: any obviously-not-base64 character in the
        // prefix tells us this is just a regular string.
        for (var i = 0; i < charsNeeded; i++)
        {
            var c = base64[i];
            var ok =
                (c >= 'A' && c <= 'Z') ||
                (c >= 'a' && c <= 'z') ||
                (c >= '0' && c <= '9') ||
                c == '+' || c == '/' || c == '=' ||
                c == '-' || c == '_'; // base64url variants
            if (!ok) return 0;
        }

        Span<byte> utf8 = stackalloc byte[64];
        if (charsNeeded > utf8.Length) return 0;
        for (var i = 0; i < charsNeeded; i++) utf8[i] = (byte)base64[i];

        var status = Base64.DecodeFromUtf8(
            utf8[..charsNeeded], buffer, out _, out var bytesWritten);
        if (status != OperationStatus.Done && status != OperationStatus.NeedMoreData)
            return 0;
        return Math.Min(bytesWritten, byteCount);
    }

    /// <summary>
    /// True when <paramref name="bytes"/> starts with every byte in
    /// <paramref name="prefix"/>. Skips the comparison when
    /// <paramref name="bytes"/> is shorter than
    /// <paramref name="prefix"/>.
    /// </summary>
    internal static bool StartsWith(ReadOnlySpan<byte> bytes, ReadOnlySpan<byte> prefix)
    {
        if (bytes.Length < prefix.Length) return false;
        return bytes[..prefix.Length].SequenceEqual(prefix);
    }

    /// <summary>
    /// True when <paramref name="bytes"/> contains
    /// <paramref name="needle"/> starting at
    /// <paramref name="offset"/>. Used for "magic at fixed offset"
    /// rules — e.g. WebP's <c>52 49 46 46 .. .. .. .. 57 45 42 50</c>
    /// (RIFF at 0, WEBP at 8) or AVIF's <c>... 66 74 79 70 61 76 69 66</c>
    /// (ftyp box header starting at 4).
    /// </summary>
    internal static bool MatchesAt(ReadOnlySpan<byte> bytes, int offset, ReadOnlySpan<byte> needle)
    {
        if (offset < 0) return false;
        if (bytes.Length < offset + needle.Length) return false;
        return bytes.Slice(offset, needle.Length).SequenceEqual(needle);
    }

    /// <summary>
    /// Walk a <see cref="JsonElement"/> tree depth-first, invoking
    /// <paramref name="onObject"/> at every object node and
    /// <paramref name="onLeaf"/> at every non-object / non-array
    /// leaf. The <see cref="JsonElement"/> path is built lazily — the
    /// callback receives the JSONPath string the resolver expects
    /// (<c>$.foo.bar[0].baz</c>).
    /// </summary>
    /// <remarks>
    /// Arrays are walked but no element-index callback fires for
    /// the array node itself — only its children. That matches the
    /// detector model where rules trigger on object-shapes
    /// (lat+lon pair) or leaf values (an image-bytes string), never
    /// on the array container itself.
    /// </remarks>
    internal static void Walk(
        JsonElement root,
        Action<string, JsonElement> onObject,
        Action<string, string, JsonElement> onLeaf)
    {
        WalkRecursive(root, "$", onObject, onLeaf);
    }

    private static void WalkRecursive(
        JsonElement node,
        string path,
        Action<string, JsonElement> onObject,
        Action<string, string, JsonElement> onLeaf)
    {
        switch (node.ValueKind)
        {
            case JsonValueKind.Object:
                onObject(path, node);
                foreach (var prop in node.EnumerateObject())
                {
                    var childPath = path + "." + prop.Name;
                    if (prop.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                    {
                        WalkRecursive(prop.Value, childPath, onObject, onLeaf);
                    }
                    else
                    {
                        onLeaf(childPath, prop.Name, prop.Value);
                    }
                }
                break;

            case JsonValueKind.Array:
                var i = 0;
                foreach (var element in node.EnumerateArray())
                {
                    var childPath = path + "[" + i.ToString(System.Globalization.CultureInfo.InvariantCulture) + "]";
                    if (element.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                    {
                        WalkRecursive(element, childPath, onObject, onLeaf);
                    }
                    else
                    {
                        // No property name at an array slot — pass the empty
                        // string so leaf-name-based rules naturally don't match.
                        onLeaf(childPath, string.Empty, element);
                    }
                    i++;
                }
                break;

            default:
                // Root-level scalar — synthesise a leaf callback with empty name.
                onLeaf(path, string.Empty, node);
                break;
        }
    }
}
