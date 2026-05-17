// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Kuestenlogik.Bowire.Semantics.Extensions;

namespace Kuestenlogik.Bowire.Tests.Semantics;

/// <summary>
/// Coverage for <see cref="EmbeddedExtensionAsset"/> — the helper
/// every <see cref="IBowireUiExtension"/> uses to load its embedded
/// JS / CSS / JSON assets. Drives both OpenRead resolution
/// strategies (literal name + dotted-form + on-disk fallback) and
/// the GuessContentType MIME-table.
/// </summary>
public sealed class EmbeddedExtensionAssetTests
{
    private sealed class StubExtension : IBowireUiExtension
    {
        public string Id => "test.stub";
        public string BowireApiRange => "1.x";
        public IReadOnlyList<string> Kinds => Array.Empty<string>();
        public ExtensionCapabilities Capabilities => ExtensionCapabilities.Viewer;
        public string BundleResourceName => "bundle.js";
        public string? StylesResourceName => null;
    }

    [Fact]
    public void OpenRead_ArgumentChecks()
    {
        var asm = typeof(EmbeddedExtensionAssetTests).Assembly;
        var ext = new StubExtension();
        Assert.Throws<ArgumentNullException>(() => EmbeddedExtensionAsset.OpenRead(null!, ext, "x"));
        Assert.Throws<ArgumentNullException>(() => EmbeddedExtensionAsset.OpenRead(asm, null!, "x"));
        Assert.Throws<ArgumentNullException>(() => EmbeddedExtensionAsset.OpenRead(asm, ext, null!));
    }

    [Fact]
    public void OpenRead_UnknownAsset_ReturnsNull()
    {
        var asm = typeof(EmbeddedExtensionAssetTests).Assembly;
        using var stream = EmbeddedExtensionAsset.OpenRead(asm, new StubExtension(), "this-asset-does-not-exist-at-all.xyz");
        Assert.Null(stream);
    }

    [Fact]
    public void OpenRead_OnDiskFallback_FindsFileNextToAssembly()
    {
        // Create a fake asset next to the test assembly. The on-disk
        // fallback path is the third strategy — this triggers it.
        var asm = typeof(EmbeddedExtensionAssetTests).Assembly;
        var dir = Path.GetDirectoryName(asm.Location)!;
        var name = $"_bowire-asset-test-{Guid.NewGuid():N}.txt";
        var path = Path.Combine(dir, name);
        File.WriteAllText(path, "asset-body");
        try
        {
            using var stream = EmbeddedExtensionAsset.OpenRead(asm, new StubExtension(), name);
            Assert.NotNull(stream);
            using var reader = new StreamReader(stream!);
            Assert.Equal("asset-body", reader.ReadToEnd());
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("widget.js", "application/javascript")]
    [InlineData("style.css", "text/css")]
    [InlineData("manifest.json", "application/json")]
    [InlineData("icon.png", "image/png")]
    [InlineData("logo.svg", "image/svg+xml")]
    [InlineData("runtime.wasm", "application/wasm")]
    [InlineData("bundle.map", "application/json")]
    [InlineData("RANDOM.JS", "application/javascript")]   // case-insensitive
    [InlineData("readme.txt", "application/octet-stream")] // unknown → octet
    [InlineData("noext", "application/octet-stream")]      // no extension
    public void GuessContentType_MapsKnownExtensionsAndFallsBack(string name, string expected)
    {
        Assert.Equal(expected, EmbeddedExtensionAsset.GuessContentType(name));
    }

    [Fact]
    public void GuessContentType_NullName_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => EmbeddedExtensionAsset.GuessContentType(null!));
    }
}
