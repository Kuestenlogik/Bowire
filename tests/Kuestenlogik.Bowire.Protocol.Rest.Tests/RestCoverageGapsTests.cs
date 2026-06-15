// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Kuestenlogik.Bowire.Mocking;
using Kuestenlogik.Bowire.Models;
using Kuestenlogik.Bowire.Protocol.Rest;
using Kuestenlogik.Bowire.Protocol.Rest.Mock;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Kuestenlogik.Bowire.Protocol.Rest.Tests;

/// <summary>
/// Targeted tests for the REST plugin's remaining gaps:
/// <see cref="BowireRestProtocol"/> error / no-schema InvokeAsync
/// branches, <see cref="RestMockHostingExtension"/> conversion
/// fallbacks for malformed YAML/JSON, and the
/// <c>EmbeddedDiscovery</c> helper functions (DeriveNameFromPath,
/// StripRouteConstraints, IsDeprecated, MapType).
/// </summary>
public sealed class RestCoverageGapsTests
{
    // ---- BowireRestProtocol error paths -------------------------------

    [Fact]
    public async Task InvokeAsync_NoCache_NoDoc_Returns_StructuredError()
    {
        using var p = new BowireRestProtocol();
        // No serverUrl with a real OpenAPI doc → DiscoverInternalAsync
        // returns empty + cache stays null → the error path fires.
        var result = await p.InvokeAsync(
            "http://127.0.0.1:1/openapi.json",
            service: "Anything",
            method: "GetX",
            jsonMessages: ["{}"],
            showInternalServices: false,
            metadata: null,
            ct: TestContext.Current.CancellationToken);
        Assert.Equal("Error", result.Status);
        Assert.Null(result.Response);
        Assert.True(result.Metadata.ContainsKey("error"));
        Assert.Contains("No OpenAPI document", result.Metadata["error"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvokeAsync_EmptyServerUrl_NoEmbeddedProvider_Returns_StructuredError()
    {
        using var p = new BowireRestProtocol();
        var result = await p.InvokeAsync(
            "",
            service: "X",
            method: "Y",
            jsonMessages: [],
            showInternalServices: false,
            metadata: null,
            ct: TestContext.Current.CancellationToken);
        Assert.Equal("Error", result.Status);
        Assert.True(result.Metadata.ContainsKey("error"));
    }

    [Fact]
    public async Task InvokeStreamAsync_Always_YieldsEmpty()
    {
        using var p = new BowireRestProtocol();
        var collected = new List<string>();
        await foreach (var msg in p.InvokeStreamAsync(
            "http://example.com", "x", "y", [], false, null,
            TestContext.Current.CancellationToken))
        {
            collected.Add(msg);
        }
        Assert.Empty(collected);
    }

    [Fact]
    public async Task OpenChannelAsync_Always_ReturnsNull()
    {
        using var p = new BowireRestProtocol();
        var ch = await p.OpenChannelAsync(
            "http://example.com", "x", "y", false, null,
            TestContext.Current.CancellationToken);
        Assert.Null(ch);
    }

    [Fact]
    public void Identity_And_Metadata_Are_Stable()
    {
        using var p = new BowireRestProtocol();
        Assert.Equal("rest", p.Id);
        Assert.Equal("REST", p.Name);
        Assert.Contains("<svg", p.IconSvg, StringComparison.Ordinal);
        Assert.Contains("OpenAPI", p.Description, StringComparison.Ordinal);
    }

    // ---- ResolveApiBaseUrl (private static, reflection) ----

    [Fact]
    public void ResolveApiBaseUrl_AbsoluteSpec_Wins_OverDoc()
    {
        var resolved = InvokeResolveApiBaseUrl(
            "https://docs.example.com/openapi.json",
            "https://api.example.com/v1/");
        Assert.Equal("https://api.example.com/v1", resolved);
    }

    [Fact]
    public void ResolveApiBaseUrl_RelativeSpec_Joined_To_DocUrl()
    {
        var resolved = InvokeResolveApiBaseUrl(
            "https://docs.example.com/spec/openapi.json",
            "/v1/api");
        Assert.StartsWith("https://docs.example.com", resolved, StringComparison.Ordinal);
        Assert.Contains("/v1/api", resolved, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveApiBaseUrl_NullSpec_FallsBack_To_DocOrigin()
    {
        var resolved = InvokeResolveApiBaseUrl(
            "https://docs.example.com:8443/spec/openapi.json",
            null);
        Assert.Equal("https://docs.example.com:8443", resolved);
    }

    [Fact]
    public void ResolveApiBaseUrl_DocNotAbsolute_ReturnsDocVerbatim()
    {
        var resolved = InvokeResolveApiBaseUrl(
            "not a url",
            null);
        Assert.Equal("not a url", resolved);
    }

    private static string InvokeResolveApiBaseUrl(string docUrl, string? fromSpec)
    {
        var mi = typeof(BowireRestProtocol).GetMethod(
            "ResolveApiBaseUrl", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("ResolveApiBaseUrl not found");
        return (string)mi.Invoke(null, [docUrl, fromSpec])!;
    }

    // ---- RestMockHostingExtension fallbacks ----------------------------

    [Fact]
    public void YamlToJson_MalformedYaml_ReturnsInputVerbatim()
    {
        // The catch block swallows YamlException and surfaces the raw
        // text so the consumer sees the original source.
        const string broken = ":\n  - [unbalanced\n";
        var result = RestMockHostingExtension.YamlToJson(broken);
        Assert.Equal(broken, result);
    }

    [Fact]
    public void YamlToJson_EmptyDocument_Returns_EmptyObject()
    {
        var result = RestMockHostingExtension.YamlToJson("");
        Assert.Equal("{}", result);
    }

    [Fact]
    public void JsonToYaml_MalformedJson_ReturnsInputVerbatim()
    {
        const string broken = "{not: valid: json:";
        var result = RestMockHostingExtension.JsonToYaml(broken);
        Assert.Equal(broken, result);
    }

    [Fact]
    public void JsonToYaml_PreservesNumbersAndBooleans()
    {
        const string json = """{"port":8080,"enabled":true,"name":"x","empty":null}""";
        var yaml = RestMockHostingExtension.JsonToYaml(json);
        Assert.Contains("8080", yaml, StringComparison.Ordinal);
        Assert.Contains("true", yaml, StringComparison.Ordinal);
    }

    [Fact]
    public void JsonToYaml_PreservesArrays()
    {
        const string json = """{"items":[1,2,3]}""";
        var yaml = RestMockHostingExtension.JsonToYaml(json);
        Assert.Contains("items", yaml, StringComparison.Ordinal);
        Assert.Contains("1", yaml, StringComparison.Ordinal);
    }

    [Fact]
    public void MapEndpoints_NullRecording_Throws()
    {
        var ext = new RestMockHostingExtension();
        var endpoints = Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder().Build();
        Assert.Throws<ArgumentNullException>(() => ext.MapEndpoints(endpoints, null!));
    }

    [Fact]
    public void MapEndpoints_NullEndpoints_Throws()
    {
        var ext = new RestMockHostingExtension();
        var recording = new BowireRecording { Id = "r", Name = "n" };
        Assert.Throws<ArgumentNullException>(() => ext.MapEndpoints(null!, recording));
    }

    [Fact]
    public void MapEndpoints_EmptyContent_SkipsAll()
    {
        var ext = new RestMockHostingExtension();
        var recording = new BowireRecording
        {
            Id = "r",
            Name = "n",
            SourceSchema = new RecordingSourceSchema("openapi-3.0", ""),
        };
        var app = Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder().Build();
        // Empty content → MapEndpoints bails before mapping; pass means
        // no throw.
        ext.MapEndpoints(app, recording);
    }

    // ---- EmbeddedDiscovery static helpers (reflection) ----------------

    [Theory]
    [InlineData("GET", "/cities", "GetCities")]
    [InlineData("POST", "/api/todos", "PostTodos")]
    [InlineData("GET", "/", "GetRoot")]
    [InlineData("GET", "/forecast/{city}", "GetForecast")]
    [InlineData("GET", "/users/{id}/comments/{cid}", "GetComments")]
    [InlineData("DELETE", "/x", "DeleteX")]
    [InlineData("", "/x", "X")]
    public void DeriveNameFromPath_Produces_CamelCase(string verb, string path, string expected)
    {
        var mi = typeof(BowireRestProtocol).Assembly
            .GetType("Kuestenlogik.Bowire.Protocol.Rest.EmbeddedDiscovery")!
            .GetMethod("DeriveNameFromPath", BindingFlags.NonPublic | BindingFlags.Static)!;
        var result = (string)mi.Invoke(null, [verb, path])!;
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("/users/{id:int}", "/users/{id}")]
    [InlineData("/users/{id:int}/posts/{slug:regex(^[a-z]+$)}", "/users/{id}/posts/{slug}")]
    [InlineData("/users/{name=foo}", "/users/{name}")]
    [InlineData("/users/{id?}", "/users/{id}")]
    [InlineData("/users/plain", "/users/plain")]
    [InlineData("", "")]
    public void StripRouteConstraints_DropsConstraintsAndDefaults(string input, string expected)
    {
        var mi = typeof(BowireRestProtocol).Assembly
            .GetType("Kuestenlogik.Bowire.Protocol.Rest.EmbeddedDiscovery")!
            .GetMethod("StripRouteConstraints", BindingFlags.NonPublic | BindingFlags.Static)!;
        var result = (string)mi.Invoke(null, [input])!;
        Assert.Equal(expected, result);
    }

    [Fact]
    public void StripRouteConstraints_UnclosedBrace_Tail_Kept_Verbatim()
    {
        // An unclosed placeholder is technically a route bug — the
        // helper preserves the tail rather than throwing.
        var mi = typeof(BowireRestProtocol).Assembly
            .GetType("Kuestenlogik.Bowire.Protocol.Rest.EmbeddedDiscovery")!
            .GetMethod("StripRouteConstraints", BindingFlags.NonPublic | BindingFlags.Static)!;
        var result = (string)mi.Invoke(null, ["/users/{id:int"])!;
        Assert.Equal("/users/{id:int", result);
    }

    [Theory]
    [InlineData(typeof(string), "string")]
    [InlineData(typeof(bool), "bool")]
    [InlineData(typeof(int), "int32")]
    [InlineData(typeof(short), "int32")]
    [InlineData(typeof(byte), "int32")]
    [InlineData(typeof(long), "int64")]
    [InlineData(typeof(float), "float")]
    [InlineData(typeof(double), "double")]
    [InlineData(typeof(decimal), "double")]
    [InlineData(typeof(Guid), "string")]
    [InlineData(typeof(DateTime), "string")]
    [InlineData(typeof(DateTimeOffset), "string")]
    [InlineData(typeof(DummyEnum), "string")]
    public void MapType_KnownClrTypes_MapToCanonical(Type input, string expected)
    {
        var mi = typeof(BowireRestProtocol).Assembly
            .GetType("Kuestenlogik.Bowire.Protocol.Rest.EmbeddedDiscovery")!
            .GetMethod("MapType", BindingFlags.NonPublic | BindingFlags.Static)!;
        var result = (string)mi.Invoke(null, [input])!;
        Assert.Equal(expected, result);
    }

    [Fact]
    public void MapType_NullType_ReturnsString()
    {
        var mi = typeof(BowireRestProtocol).Assembly
            .GetType("Kuestenlogik.Bowire.Protocol.Rest.EmbeddedDiscovery")!
            .GetMethod("MapType", BindingFlags.NonPublic | BindingFlags.Static)!;
        var result = (string)mi.Invoke(null, [null])!;
        Assert.Equal("string", result);
    }

    [Fact]
    public void MapType_NullableInt_TreatedAsUnderlying()
    {
        var mi = typeof(BowireRestProtocol).Assembly
            .GetType("Kuestenlogik.Bowire.Protocol.Rest.EmbeddedDiscovery")!
            .GetMethod("MapType", BindingFlags.NonPublic | BindingFlags.Static)!;
        var result = (string)mi.Invoke(null, [typeof(int?)])!;
        Assert.Equal("int32", result);
    }

    [Fact]
    public void MapType_ComplexClass_ReturnsMessage()
    {
        var mi = typeof(BowireRestProtocol).Assembly
            .GetType("Kuestenlogik.Bowire.Protocol.Rest.EmbeddedDiscovery")!
            .GetMethod("MapType", BindingFlags.NonPublic | BindingFlags.Static)!;
        var result = (string)mi.Invoke(null, [typeof(DummyComplex)])!;
        Assert.Equal("message", result);
    }

    private enum DummyEnum { A, B }
    private sealed class DummyComplex
    {
        public string Name { get; set; } = "";
        public int Age { get; set; }
    }

    // ---- EmbeddedDiscovery.TryDiscover early returns -----------------

    [Fact]
    public void EmbeddedDiscovery_TryDiscover_NullProvider_ReturnsFalse()
    {
        var mi = typeof(BowireRestProtocol).Assembly
            .GetType("Kuestenlogik.Bowire.Protocol.Rest.EmbeddedDiscovery")!
            .GetMethod("TryDiscover", BindingFlags.Public | BindingFlags.Static)!;
        object?[] args = [null, null];
        var result = (bool)mi.Invoke(null, args)!;
        Assert.False(result);
        Assert.NotNull(args[1]);
        Assert.Empty((List<BowireServiceInfo>)args[1]!);
    }

    [Fact]
    public void EmbeddedDiscovery_TryDiscover_NoApiExplorer_ReturnsFalse()
    {
        // Service provider exists but doesn't have IApiDescriptionGroupCollectionProvider
        var sp = new ServiceCollection().BuildServiceProvider();

        var mi = typeof(BowireRestProtocol).Assembly
            .GetType("Kuestenlogik.Bowire.Protocol.Rest.EmbeddedDiscovery")!
            .GetMethod("TryDiscover", BindingFlags.Public | BindingFlags.Static)!;
        object?[] args = [sp, null];
        var result = (bool)mi.Invoke(null, args)!;
        Assert.False(result);
    }

    [Fact]
    public void EmbeddedDiscovery_TryDiscover_EmptyApiExplorer_ReturnsFalse()
    {
        // ApiExplorer present but with zero groups → byTag stays empty
        // → method returns false.
        var sc = new ServiceCollection();
        sc.AddSingleton<IApiDescriptionGroupCollectionProvider>(new EmptyApiExplorer());
        using var sp = sc.BuildServiceProvider();

        var mi = typeof(BowireRestProtocol).Assembly
            .GetType("Kuestenlogik.Bowire.Protocol.Rest.EmbeddedDiscovery")!
            .GetMethod("TryDiscover", BindingFlags.Public | BindingFlags.Static)!;
        object?[] args = [sp, null];
        var result = (bool)mi.Invoke(null, args)!;
        Assert.False(result);
    }

    private sealed class EmptyApiExplorer : IApiDescriptionGroupCollectionProvider
    {
        public ApiDescriptionGroupCollection ApiDescriptionGroups =>
            new([], version: 1);
    }
}
