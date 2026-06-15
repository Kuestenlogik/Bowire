// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Http.Json;
using System.Text;
using Kuestenlogik.Bowire.Protocol.Otlp;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Kuestenlogik.Bowire.Protocol.Otlp.Tests;

/// <summary>
/// Endpoint behaviour tests for the OTLP HTTP receiver
/// (<c>POST /v1/{traces|metrics|logs}</c>): content-type detection,
/// JSON vs protobuf body split, envelope-store interaction, basePath
/// composition, and response shape.
/// </summary>
public sealed class OtlpReceiverEndpointsTests
{
    [Fact]
    public void IsJsonContentType_HandlesCommonShapes()
    {
        Assert.True(OtlpReceiverEndpoints.IsJsonContentType("application/json"));
        Assert.True(OtlpReceiverEndpoints.IsJsonContentType("application/json; charset=utf-8"));
        Assert.True(OtlpReceiverEndpoints.IsJsonContentType("APPLICATION/JSON"));
        Assert.True(OtlpReceiverEndpoints.IsJsonContentType("application/problem+json"));
        Assert.True(OtlpReceiverEndpoints.IsJsonContentType("application/vnd.bowire+json"));

        Assert.False(OtlpReceiverEndpoints.IsJsonContentType("application/x-protobuf"));
        Assert.False(OtlpReceiverEndpoints.IsJsonContentType("application/octet-stream"));
        Assert.False(OtlpReceiverEndpoints.IsJsonContentType(""));
        Assert.False(OtlpReceiverEndpoints.IsJsonContentType("text/plain"));
    }

    [Fact]
    public async Task PostJson_StoresAsBodyJson_Returns200()
    {
        var store = new OtlpEnvelopeStore();
        using var host = await BuildHostAsync(store, basePath: "");
        var client = host.GetTestClient();

        var json = "{\"resourceSpans\":[]}";
        using var body = new StringContent(json, Encoding.UTF8, "application/json");
        var resp = await client.PostAsync(
            new Uri("http://localhost/v1/traces", UriKind.Absolute), body, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var snap = store.Snapshot(OtlpSignalKind.Traces);
        Assert.Single(snap);
        Assert.Equal("application/json; charset=utf-8", snap[0].ContentType);
        Assert.Equal(json, snap[0].BodyJson);
        Assert.Null(snap[0].BodyBase64);
        Assert.Equal(json.Length, snap[0].BodyBytes);
    }

    [Fact]
    public async Task PostProtobuf_StoresAsBodyBase64()
    {
        var store = new OtlpEnvelopeStore();
        using var host = await BuildHostAsync(store, basePath: "");
        var client = host.GetTestClient();

        var bytes = new byte[] { 0x0a, 0x05, 0x68, 0x65, 0x6c, 0x6c, 0x6f }; // protobuf-shaped bytes
        using var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/x-protobuf");
        var resp = await client.PostAsync(new Uri("http://localhost/v1/metrics", UriKind.Absolute), content, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var snap = store.Snapshot(OtlpSignalKind.Metrics);
        Assert.Single(snap);
        Assert.Null(snap[0].BodyJson);
        Assert.Equal(Convert.ToBase64String(bytes), snap[0].BodyBase64);
        Assert.Equal(bytes.Length, snap[0].BodyBytes);
    }

    [Fact]
    public async Task PostLogs_RoutesToLogsKind()
    {
        var store = new OtlpEnvelopeStore();
        using var host = await BuildHostAsync(store, basePath: "");
        var client = host.GetTestClient();

        using var body = JsonContent.Create(new { resourceLogs = Array.Empty<object>() });
        var resp = await client.PostAsync(
            new Uri("http://localhost/v1/logs", UriKind.Absolute), body, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Empty(store.Snapshot(OtlpSignalKind.Traces));
        Assert.Empty(store.Snapshot(OtlpSignalKind.Metrics));
        Assert.Single(store.Snapshot(OtlpSignalKind.Logs));
    }

    [Fact]
    public async Task BasePath_MountsUnderPrefix()
    {
        var store = new OtlpEnvelopeStore();
        using var host = await BuildHostAsync(store, basePath: "/otlp");
        var client = host.GetTestClient();

        // Canonical /v1/traces should 404 — only /otlp/v1/traces wired.
        using var directBody = new StringContent("{}", Encoding.UTF8, "application/json");
        var directResp = await client.PostAsync(
            new Uri("http://localhost/v1/traces", UriKind.Absolute), directBody, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, directResp.StatusCode);

        using var prefixedBody = new StringContent("{}", Encoding.UTF8, "application/json");
        var prefixedResp = await client.PostAsync(
            new Uri("http://localhost/otlp/v1/traces", UriKind.Absolute), prefixedBody, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, prefixedResp.StatusCode);
        Assert.Single(store.Snapshot(OtlpSignalKind.Traces));
    }

    [Fact]
    public async Task NoContentType_StoresAsBase64Fallback()
    {
        var store = new OtlpEnvelopeStore();
        using var host = await BuildHostAsync(store, basePath: "");
        var client = host.GetTestClient();

        // No Content-Type header → default to base64 capture path.
        // TestHost won't let us send a literal-null header, so we use
        // a non-JSON content-type that exercises the same branch.
        using var content = new ByteArrayContent(new byte[] { 0xff });
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        var resp = await client.PostAsync(new Uri("http://localhost/v1/traces", UriKind.Absolute), content, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var snap = store.Snapshot(OtlpSignalKind.Traces);
        Assert.Single(snap);
        Assert.Null(snap[0].BodyJson);
        Assert.Equal(Convert.ToBase64String(new byte[] { 0xff }), snap[0].BodyBase64);
    }

    [Fact]
    public void MapBowireOtlpReceiver_NullEndpoints_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            OtlpReceiverEndpoints.MapBowireOtlpReceiver(null!));
    }

    [Fact]
    public void AddBowireOtlpReceiver_NullServices_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            OtlpServiceCollectionExtensions.AddBowireOtlpReceiver((IServiceCollection)null!));
    }

    [Fact]
    public void AddBowireOtlpReceiver_RegistersSingletonStore()
    {
        var services = new ServiceCollection();
        services.AddBowireOtlpReceiver();
        using var sp = services.BuildServiceProvider();

        var first = sp.GetRequiredService<OtlpEnvelopeStore>();
        var second = sp.GetRequiredService<OtlpEnvelopeStore>();
        Assert.Same(first, second);
    }

    private static async Task<IHost> BuildHostAsync(OtlpEnvelopeStore store, string basePath)
    {
        var builder = new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddBowireOtlpReceiver(store);
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapBowireOtlpReceiver(basePath);
                    });
                });
            });
        var host = await builder.StartAsync();
        return host;
    }
}
