// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Kuestenlogik.Bowire.Semantics;
using Kuestenlogik.Bowire.Semantics.Detectors;

namespace Kuestenlogik.Bowire.Tests.Semantics.Detectors;

public sealed class AudioBytesDetectorTests
{
    private static readonly AudioBytesDetector s_detector = new();

    private static DetectionContext Ctx(string json)
        => new("svc", "m", AnnotationKey.Wildcard, JsonDocument.Parse(json).RootElement);

    private static string MagicPlusFiller(byte[] magic)
    {
        var blob = new byte[Math.Max(64, magic.Length + 32)];
        Array.Copy(magic, blob, magic.Length);
        return Convert.ToBase64String(blob);
    }

    [Fact]
    public void Detects_Wav_Magic()
    {
        // RIFF<4-byte-size>WAVE
        var wav = new byte[]
        {
            0x52, 0x49, 0x46, 0x46,
            0x00, 0x00, 0x00, 0x00,
            0x57, 0x41, 0x56, 0x45
        };
        var ctx = Ctx($$"""{"audio": "{{MagicPlusFiller(wav)}}"}""");

        var results = s_detector.Detect(in ctx).ToList();

        Assert.Single(results);
        Assert.Equal(BuiltInSemanticTags.AudioBytes, results[0].Semantic);
    }

    [Fact]
    public void Detects_Ogg_Magic()
    {
        var ogg = new byte[] { 0x4F, 0x67, 0x67, 0x53 };
        var ctx = Ctx($$"""{"audio": "{{MagicPlusFiller(ogg)}}"}""");

        var results = s_detector.Detect(in ctx).ToList();

        Assert.Single(results);
    }

    [Fact]
    public void Detects_Flac_Magic()
    {
        var flac = new byte[] { 0x66, 0x4C, 0x61, 0x43 };
        var ctx = Ctx($$"""{"audio": "{{MagicPlusFiller(flac)}}"}""");

        var results = s_detector.Detect(in ctx).ToList();

        Assert.Single(results);
    }

    [Fact]
    public void Detects_Mp3_Frame_Sync_FFFB()
    {
        var mp3 = new byte[] { 0xFF, 0xFB, 0x90, 0x44 };
        var ctx = Ctx($$"""{"audio": "{{MagicPlusFiller(mp3)}}"}""");

        var results = s_detector.Detect(in ctx).ToList();

        Assert.Single(results);
    }

    [Fact]
    public void Detects_Mp3_Frame_Sync_FFF3()
    {
        var mp3 = new byte[] { 0xFF, 0xF3, 0x80, 0x44 };
        var ctx = Ctx($$"""{"audio": "{{MagicPlusFiller(mp3)}}"}""");

        var results = s_detector.Detect(in ctx).ToList();

        Assert.Single(results);
    }

    [Fact]
    public void Detects_Id3_Tag()
    {
        // ID3v2 header on MP3 / AIFF.
        var id3 = new byte[] { 0x49, 0x44, 0x33, 0x03, 0x00 };
        var ctx = Ctx($$"""{"audio": "{{MagicPlusFiller(id3)}}"}""");

        var results = s_detector.Detect(in ctx).ToList();

        Assert.Single(results);
    }

    [Fact]
    public void Does_Not_Match_Plain_Text()
    {
        var ctx = Ctx("""{"name": "Hamburg harbour"}""");

        var results = s_detector.Detect(in ctx).ToList();

        Assert.Empty(results);
    }

    [Fact]
    public void Does_Not_Match_Webp_As_Audio()
    {
        // RIFF + WEBP. RIFF alone shouldn't fire — needs WAVE at offset 8.
        var webp = new byte[]
        {
            0x52, 0x49, 0x46, 0x46,
            0x00, 0x00, 0x00, 0x00,
            0x57, 0x45, 0x42, 0x50
        };
        var ctx = Ctx($$"""{"audio": "{{MagicPlusFiller(webp)}}"}""");

        var results = s_detector.Detect(in ctx).ToList();

        Assert.Empty(results);
    }

    [Fact]
    public void Does_Not_Match_Png_As_Audio()
    {
        var pngHeader = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        var ctx = Ctx($$"""{"audio": "{{MagicPlusFiller(pngHeader)}}"}""");

        var results = s_detector.Detect(in ctx).ToList();

        Assert.Empty(results);
    }

    [Fact]
    public void Does_Not_Match_Arbitrary_Base64_Without_Magic()
    {
        var bytes = new byte[32];
        for (var i = 0; i < bytes.Length; i++) bytes[i] = (byte)((i * 23 + 5) & 0xFF);
        bytes[0] = 0x01; bytes[1] = 0x02; bytes[2] = 0x03;
        var ctx = Ctx($$"""{"data": "{{Convert.ToBase64String(bytes)}}"}""");

        var results = s_detector.Detect(in ctx).ToList();

        Assert.Empty(results);
    }
}
