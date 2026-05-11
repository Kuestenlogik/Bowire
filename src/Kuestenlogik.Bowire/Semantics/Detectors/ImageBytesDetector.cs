// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;

namespace Kuestenlogik.Bowire.Semantics.Detectors;

/// <summary>
/// Detects string-typed leaves whose base64 prefix decodes to bytes
/// matching a known image magic-number: PNG, JPEG, GIF87a / 89a, WebP,
/// AVIF. Only the first 12 bytes are decoded so the cost is bounded
/// regardless of the payload size.
/// </summary>
/// <remarks>
/// <para>
/// Magic table:
/// </para>
/// <list type="bullet">
///   <item><description>PNG — <c>89 50 4E 47 0D 0A 1A 0A</c>.</description></item>
///   <item><description>JPEG — <c>FF D8 FF</c>.</description></item>
///   <item><description>GIF — <c>47 49 46 38 [37|39] 61</c> (GIF87a or GIF89a).</description></item>
///   <item><description>WebP — RIFF header at byte 0 + WEBP at byte 8.</description></item>
///   <item><description>AVIF — <c>ftyp</c>+<c>avif</c> brand at byte 4.</description></item>
/// </list>
/// <para>
/// The 12-byte window is the minimum needed to discriminate WebP and
/// AVIF (both have content past byte 8 that disambiguates them from
/// other RIFF/ftyp containers).
/// </para>
/// </remarks>
public sealed class ImageBytesDetector : IBowireFieldDetector
{
    private const int MagicWindowBytes = 12;

    // PNG magic — 89 50 4E 47 0D 0A 1A 0A
    private static readonly byte[] s_pngMagic = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    // JPEG SOI + start of frame marker — FF D8 FF
    private static readonly byte[] s_jpegMagic = [0xFF, 0xD8, 0xFF];

    // GIF87a / GIF89a — first 4 bytes "GIF8", byte 4 is '7' or '9', byte 5 is 'a'.
    private static readonly byte[] s_gifPrefix = [0x47, 0x49, 0x46, 0x38];

    // RIFF header — every WebP starts with this; need bytes 8..11 == "WEBP" to confirm.
    private static readonly byte[] s_riffMagic = [0x52, 0x49, 0x46, 0x46];
    private static readonly byte[] s_webpBrand = [0x57, 0x45, 0x42, 0x50];

    // AVIF — bytes 4..7 == "ftyp", bytes 8..11 == "avif" (or compatible-brand at 8).
    private static readonly byte[] s_ftypTag = [0x66, 0x74, 0x79, 0x70];
    private static readonly byte[] s_avifBrand = [0x61, 0x76, 0x69, 0x66];

    /// <inheritdoc/>
    public string Id => "kuestenlogik.image-bytes";

    /// <inheritdoc/>
    public IEnumerable<DetectionResult> Detect(in DetectionContext ctx)
    {
        var results = new List<DetectionResult>();
        var service = ctx.ServiceId;
        var method = ctx.MethodId;
        var messageType = ctx.MessageType;

        DetectorHelpers.Walk(ctx.Frame,
            onObject: (_, _) => { },
            onLeaf: (path, _, value) =>
            {
                if (value.ValueKind != JsonValueKind.String) return;
                var s = value.GetString();
                if (string.IsNullOrEmpty(s)) return;

                Span<byte> bytes = stackalloc byte[MagicWindowBytes];
                var got = DetectorHelpers.TryDecodeBase64Prefix(s, MagicWindowBytes, bytes);
                if (got <= 0) return;
                var decoded = bytes[..got];

                if (IsImage(decoded))
                {
                    results.Add(new DetectionResult(
                        new AnnotationKey(service, method, messageType, path),
                        BuiltInSemanticTags.ImageBytes));
                }
            });

        return results;
    }

    private static bool IsImage(ReadOnlySpan<byte> bytes)
    {
        if (DetectorHelpers.StartsWith(bytes, s_pngMagic)) return true;
        if (DetectorHelpers.StartsWith(bytes, s_jpegMagic)) return true;
        if (DetectorHelpers.StartsWith(bytes, s_gifPrefix)
            && bytes.Length >= 6
            && (bytes[4] == 0x37 || bytes[4] == 0x39)
            && bytes[5] == 0x61) return true;
        if (DetectorHelpers.StartsWith(bytes, s_riffMagic)
            && DetectorHelpers.MatchesAt(bytes, 8, s_webpBrand)) return true;
        if (DetectorHelpers.MatchesAt(bytes, 4, s_ftypTag)
            && DetectorHelpers.MatchesAt(bytes, 8, s_avifBrand)) return true;
        return false;
    }
}
