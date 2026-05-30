// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Mocking;

namespace Kuestenlogik.Bowire.Tests.Mocking;

/// <summary>
/// Coverage for the process-wide <see cref="SourceSchemaCache"/>. The
/// cache itself is a thin ConcurrentDictionary wrapper; these tests
/// pin the public contract — Set / Get / Clear, case-insensitive
/// keying, overwrite semantics, null-handling.
/// </summary>
[Collection(nameof(SourceSchemaCacheTestGroup))]
public sealed class SourceSchemaCacheTests : IDisposable
{
    public SourceSchemaCacheTests() => SourceSchemaCache.Clear();
    public void Dispose() => SourceSchemaCache.Clear();

    [Fact]
    public void Set_then_Get_roundtrips_the_schema()
    {
        var schema = new RecordingSourceSchema("openapi-3.0", "openapi: 3.0.0", "http://h/openapi.yaml");
        SourceSchemaCache.Set("http://api.example.com", schema);
        var retrieved = SourceSchemaCache.Get("http://api.example.com");
        Assert.NotNull(retrieved);
        Assert.Equal("openapi-3.0", retrieved!.Format);
        Assert.Equal("openapi: 3.0.0", retrieved.Content);
        Assert.Equal("http://h/openapi.yaml", retrieved.SourceUrl);
    }

    [Fact]
    public void Get_returns_null_for_missing_key()
        => Assert.Null(SourceSchemaCache.Get("http://nothing/here"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Get_returns_null_for_empty_url(string? url)
        => Assert.Null(SourceSchemaCache.Get(url));

    [Fact]
    public void Set_overwrites_existing_entry()
    {
        var first = new RecordingSourceSchema("openapi-3.0", "first", null);
        var second = new RecordingSourceSchema("openapi-3.0", "second", null);
        SourceSchemaCache.Set("http://h", first);
        SourceSchemaCache.Set("http://h", second);
        Assert.Equal("second", SourceSchemaCache.Get("http://h")!.Content);
    }

    [Fact]
    public void Set_lookup_is_case_insensitive()
    {
        var schema = new RecordingSourceSchema("openapi-3.0", "x", null);
        SourceSchemaCache.Set("HTTP://API.example.com", schema);
        Assert.NotNull(SourceSchemaCache.Get("http://api.EXAMPLE.com"));
    }

    [Fact]
    public void Set_throws_for_empty_url()
        => Assert.Throws<ArgumentException>(() =>
            SourceSchemaCache.Set("", new RecordingSourceSchema("openapi-3.0", "x", null)));

    [Fact]
    public void Set_throws_for_null_schema()
        => Assert.Throws<ArgumentNullException>(() =>
            SourceSchemaCache.Set("http://h", null!));

    [Fact]
    public void Clear_drops_every_entry()
    {
        SourceSchemaCache.Set("http://a", new RecordingSourceSchema("openapi-3.0", "x", null));
        SourceSchemaCache.Set("http://b", new RecordingSourceSchema("openapi-3.0", "y", null));
        SourceSchemaCache.Clear();
        Assert.Null(SourceSchemaCache.Get("http://a"));
        Assert.Null(SourceSchemaCache.Get("http://b"));
    }
}

/// <summary>
/// Serialise every test class that mutates the process-wide
/// <see cref="SourceSchemaCache"/>. The cache is a static singleton
/// (one process = one cache), so two parallel test methods race on
/// the same dictionary unless we pin them to a collection.
/// </summary>
[CollectionDefinition(nameof(SourceSchemaCacheTestGroup), DisableParallelization = true)]
#pragma warning disable CA1515 // xunit reflection requires the type be public
public sealed class SourceSchemaCacheTestGroup;
#pragma warning restore CA1515
