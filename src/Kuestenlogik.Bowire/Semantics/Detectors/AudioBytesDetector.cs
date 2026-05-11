// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;

namespace Kuestenlogik.Bowire.Semantics.Detectors;

/// <summary>
/// Detects string-typed leaves whose base64 prefix decodes to bytes
/// matching a known audio magic-number: WAV (RIFF + WAVE),
/// Ogg (OggS), FLAC (fLaC), MP3 (ID3 tag or MPEG sync byte). Only
/// the first 12 bytes are decoded — same bounded-work principle as
/// <see cref="ImageBytesDetector"/>.
/// </summary>
/// <remarks>
/// <para>
/// Magic table:
/// </para>
/// <list type="bullet">
///   <item><description>WAV — RIFF header at byte 0 + WAVE at byte 8.</description></item>
///   <item><description>Ogg — <c>4F 67 67 53</c> ("OggS").</description></item>
///   <item><description>FLAC — <c>66 4C 61 43</c> ("fLaC").</description></item>
///   <item><description>MP3 — <c>FF FB</c> / <c>FF F3</c> / <c>FF F2</c> MPEG frame sync, or an ID3v2 tag (<c>49 44 33</c>) at byte 0.</description></item>
/// </list>
/// </remarks>
public sealed class AudioBytesDetector : IBowireFieldDetector
{
    private const int MagicWindowBytes = 12;

    // RIFF + WAVE — every WAV begins with this; bytes 8..11 must equal "WAVE".
    private static readonly byte[] s_riffMagic = [0x52, 0x49, 0x46, 0x46];
    private static readonly byte[] s_waveBrand = [0x57, 0x41, 0x56, 0x45];

    // Ogg page header — "OggS".
    private static readonly byte[] s_oggMagic = [0x4F, 0x67, 0x67, 0x53];

    // FLAC native stream marker — "fLaC".
    private static readonly byte[] s_flacMagic = [0x66, 0x4C, 0x61, 0x43];

    // ID3v2 — "ID3" at byte 0. Common in MP3 / AIFF.
    private static readonly byte[] s_id3Magic = [0x49, 0x44, 0x33];

    /// <inheritdoc/>
    public string Id => "kuestenlogik.audio-bytes";

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

                if (IsAudio(decoded))
                {
                    results.Add(new DetectionResult(
                        new AnnotationKey(service, method, messageType, path),
                        BuiltInSemanticTags.AudioBytes));
                }
            });

        return results;
    }

    private static bool IsAudio(ReadOnlySpan<byte> bytes)
    {
        // WAV: RIFF<size>WAVE
        if (DetectorHelpers.StartsWith(bytes, s_riffMagic)
            && DetectorHelpers.MatchesAt(bytes, 8, s_waveBrand)) return true;
        if (DetectorHelpers.StartsWith(bytes, s_oggMagic)) return true;
        if (DetectorHelpers.StartsWith(bytes, s_flacMagic)) return true;
        if (DetectorHelpers.StartsWith(bytes, s_id3Magic)) return true;
        // MP3 frame sync: 0xFF 0xFB / 0xFF 0xF3 / 0xFF 0xF2 — three of
        // the common bitrate/sampling combinations. Excluding 0xFF 0xFA
        // (no CRC) keeps the false-positive surface small; that
        // variant is rare enough that a user right-click is the right
        // path.
        if (bytes.Length >= 2 && bytes[0] == 0xFF
            && (bytes[1] == 0xFB || bytes[1] == 0xF3 || bytes[1] == 0xF2)) return true;
        return false;
    }
}
