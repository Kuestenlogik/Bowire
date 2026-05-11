// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Kuestenlogik.Bowire.Semantics;
using Kuestenlogik.Bowire.Semantics.Detectors;

namespace Kuestenlogik.Bowire.Tests.Semantics.Detectors;

public sealed class ImageBytesDetectorTests
{
    private static readonly ImageBytesDetector s_detector = new();

    private static DetectionContext Ctx(string json)
        => new("svc", "m", AnnotationKey.Wildcard, JsonDocument.Parse(json).RootElement);

    private static string Base64(params byte[] bytes) => Convert.ToBase64String(bytes);

    /// <summary>32 bytes of the given prefix + filler — guarantees the 12-byte sniff window has enough characters.</summary>
    private static string MagicPlusFiller(byte[] magic)
    {
        var blob = new byte[Math.Max(64, magic.Length + 32)];
        Array.Copy(magic, blob, magic.Length);
        return Convert.ToBase64String(blob);
    }

    [Fact]
    public void Detects_Png_Magic()
    {
        var pngHeader = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        var ctx = Ctx($$"""{"image": "{{MagicPlusFiller(pngHeader)}}"}""");

        var results = s_detector.Detect(in ctx).ToList();

        Assert.Single(results);
        Assert.Equal("$.image", results[0].Key.JsonPath);
        Assert.Equal(BuiltInSemanticTags.ImageBytes, results[0].Semantic);
    }

    [Fact]
    public void Detects_Jpeg_Magic()
    {
        var jpegHeader = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 };
        var ctx = Ctx($$"""{"image": "{{MagicPlusFiller(jpegHeader)}}"}""");

        var results = s_detector.Detect(in ctx).ToList();

        Assert.Single(results);
    }

    [Fact]
    public void Detects_Gif87a_Magic()
    {
        // "GIF87a"
        var gif = new byte[] { 0x47, 0x49, 0x46, 0x38, 0x37, 0x61 };
        var ctx = Ctx($$"""{"image": "{{MagicPlusFiller(gif)}}"}""");

        var results = s_detector.Detect(in ctx).ToList();

        Assert.Single(results);
    }

    [Fact]
    public void Detects_Gif89a_Magic()
    {
        // "GIF89a"
        var gif = new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61 };
        var ctx = Ctx($$"""{"image": "{{MagicPlusFiller(gif)}}"}""");

        var results = s_detector.Detect(in ctx).ToList();

        Assert.Single(results);
    }

    [Fact]
    public void Detects_Webp_Magic()
    {
        // RIFF<4-byte-size>WEBP
        var webp = new byte[]
        {
            0x52, 0x49, 0x46, 0x46, // RIFF
            0x00, 0x00, 0x00, 0x00, // file-size placeholder
            0x57, 0x45, 0x42, 0x50  // WEBP
        };
        var ctx = Ctx($$"""{"image": "{{MagicPlusFiller(webp)}}"}""");

        var results = s_detector.Detect(in ctx).ToList();

        Assert.Single(results);
    }

    [Fact]
    public void Detects_Avif_Magic()
    {
        // <4-byte-size>ftyp avif
        var avif = new byte[]
        {
            0x00, 0x00, 0x00, 0x20,
            0x66, 0x74, 0x79, 0x70, // ftyp
            0x61, 0x76, 0x69, 0x66  // avif
        };
        var ctx = Ctx($$"""{"image": "{{MagicPlusFiller(avif)}}"}""");

        var results = s_detector.Detect(in ctx).ToList();

        Assert.Single(results);
    }

    [Fact]
    public void Does_Not_Match_Plain_Text_String()
    {
        var ctx = Ctx("""{"name": "Hamburg harbour"}""");

        var results = s_detector.Detect(in ctx).ToList();

        Assert.Empty(results);
    }

    [Fact]
    public void Does_Not_Match_Arbitrary_Base64_Without_Magic()
    {
        // Deterministic non-magic-looking byte pattern. First three
        // bytes are chosen to NOT match any known image magic.
        var bytes = new byte[32];
        for (var i = 0; i < bytes.Length; i++) bytes[i] = (byte)((i * 17 + 3) & 0xFF);
        bytes[0] = 0x01; bytes[1] = 0x02; bytes[2] = 0x03;
        var ctx = Ctx($$"""{"data": "{{Convert.ToBase64String(bytes)}}"}""");

        var results = s_detector.Detect(in ctx).ToList();

        Assert.Empty(results);
    }

    [Fact]
    public void Does_Not_Match_Empty_String()
    {
        var ctx = Ctx("""{"image": ""}""");

        var results = s_detector.Detect(in ctx).ToList();

        Assert.Empty(results);
    }

    [Fact]
    public void Does_Not_Match_Numbers_Or_Booleans()
    {
        var ctx = Ctx("""{"a": 42, "b": true, "c": null}""");

        var results = s_detector.Detect(in ctx).ToList();

        Assert.Empty(results);
    }

    [Fact]
    public void Walks_Into_Nested_Fields()
    {
        var pngHeader = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        // String interpolation would force a triple-dollar dance because
        // the JSON ends in two closing braces — plain string.Format is
        // less stunt-shooty for this case.
        var json = string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            "{{\"thumb\": {{\"data\": \"{0}\"}}}}",
            MagicPlusFiller(pngHeader));
        var ctx = Ctx(json);

        var results = s_detector.Detect(in ctx).ToList();

        Assert.Single(results);
        Assert.Equal("$.thumb.data", results[0].Key.JsonPath);
    }
}
