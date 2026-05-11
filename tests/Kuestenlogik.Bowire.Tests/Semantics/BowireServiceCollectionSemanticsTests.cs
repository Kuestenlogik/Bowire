// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Semantics;
using Microsoft.Extensions.DependencyInjection;

namespace Kuestenlogik.Bowire.Tests.Semantics;

/// <summary>
/// Covers the Phase-1 DI surface added to
/// <see cref="BowireServiceCollectionExtensions"/>: the
/// <see cref="LayeredAnnotationStore"/> singleton and its
/// <see cref="IAnnotationStore"/> alias, plus the
/// <see cref="BowireOptions.SchemaHintsPath"/> override.
/// </summary>
public sealed class BowireServiceCollectionSemanticsTests
{
    [Fact]
    public void AddBowire_Registers_LayeredAnnotationStore_As_Singleton()
    {
        var services = new ServiceCollection();
        services.AddBowire();

        using var sp = services.BuildServiceProvider();
        var a = sp.GetRequiredService<LayeredAnnotationStore>();
        var b = sp.GetRequiredService<LayeredAnnotationStore>();

        Assert.Same(a, b);
    }

    [Fact]
    public void AddBowire_Exposes_IAnnotationStore_Pointing_At_Same_Singleton()
    {
        var services = new ServiceCollection();
        services.AddBowire();

        using var sp = services.BuildServiceProvider();
        var concrete = sp.GetRequiredService<LayeredAnnotationStore>();
        var iface = sp.GetRequiredService<IAnnotationStore>();

        Assert.Same(concrete, iface);
    }

    [Fact]
    public void SchemaHintsPath_Override_Is_Honoured_By_Store_Factory()
    {
        var customPath = Path.Combine(Path.GetTempPath(),
            $"bowire-custom-hints-{Guid.NewGuid():N}.json");

        var services = new ServiceCollection();
        services.AddBowire(options => options.SchemaHintsPath = customPath);

        using var sp = services.BuildServiceProvider();
        var store = sp.GetRequiredService<LayeredAnnotationStore>();

        Assert.NotNull(store.UserFileLayer);
        Assert.Equal(customPath, store.UserFileLayer!.FilePath);
    }

    [Fact]
    public void SchemaHintsPath_Empty_String_Disables_UserFileLayer()
    {
        var services = new ServiceCollection();
        services.AddBowire(options => options.SchemaHintsPath = string.Empty);

        using var sp = services.BuildServiceProvider();
        var store = sp.GetRequiredService<LayeredAnnotationStore>();

        Assert.Null(store.UserFileLayer);
    }

    [Fact]
    public void Default_UserFile_Path_Lives_Under_DotBowire()
    {
        var path = BowireServiceCollectionExtensions.DefaultUserSchemaHintsPath();

        // Either an empty string (no UserProfile, degraded mode) or a
        // path containing the canonical ".bowire" segment + filename.
        if (!string.IsNullOrEmpty(path))
        {
            Assert.Contains(".bowire", path, StringComparison.Ordinal);
            Assert.EndsWith("schema-hints.json", path, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Default_ProjectFile_Path_Lives_In_Current_Directory()
    {
        var path = BowireServiceCollectionExtensions.DefaultProjectSchemaHintsPath();
        Assert.NotNull(path);
        Assert.EndsWith("bowire.schema-hints.json", path!, StringComparison.Ordinal);
    }
}
