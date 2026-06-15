// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Kuestenlogik.Bowire.Protocol.Otlp;
using Microsoft.Extensions.DependencyInjection;

namespace Kuestenlogik.Bowire.Protocol.Otlp.Tests;

/// <summary>
/// IBowireProtocol contract tests for <see cref="BowireOtlpProtocol"/>:
/// identity, Discover returns the three receiver methods, Invoke / Stream
/// dispatch by method name, OpenChannel is null (passive listener).
/// </summary>
public sealed class BowireOtlpProtocolTests
{
    [Fact]
    public void Identity_Constants()
    {
        var sut = new BowireOtlpProtocol();
        Assert.Equal("otlp", sut.Id);
        Assert.Equal("OTLP", sut.Name);
        Assert.Contains("OpenTelemetry", sut.Description);
        Assert.Contains("<svg", sut.IconSvg, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DiscoverAsync_ReturnsSingleServiceWithThreeMethods()
    {
        var sut = new BowireOtlpProtocol();
        var services = await sut.DiscoverAsync("http://localhost:4318", showInternalServices: false, TestContext.Current.CancellationToken);

        Assert.Single(services);
        var svc = services[0];
        Assert.Equal("OtlpReceiver", svc.Name);
        Assert.Equal("opentelemetry.proto.collector.v1", svc.Package);
        Assert.Equal("otlp-listener", svc.Source);
        Assert.Equal("http://localhost:4318", svc.OriginUrl);
        Assert.Equal(3, svc.Methods.Count);

        var names = svc.Methods.Select(m => m.Name).ToArray();
        Assert.Contains("ReceiveTraces", names);
        Assert.Contains("ReceiveMetrics", names);
        Assert.Contains("ReceiveLogs", names);

        // Every method is server-streaming + carries the canonical
        // POST verb + /v1/<kind> path for the REST guide / docs surface.
        foreach (var m in svc.Methods)
        {
            Assert.True(m.ServerStreaming);
            Assert.False(m.ClientStreaming);
            Assert.Equal("ServerStreaming", m.MethodType);
            Assert.Equal("POST", m.HttpMethod);
            Assert.StartsWith("/v1/", m.HttpPath!);
        }
    }

    [Fact]
    public async Task DiscoverAsync_EmptyServerUrl_OriginUrlIsNull()
    {
        var sut = new BowireOtlpProtocol();
        var services = await sut.DiscoverAsync("", showInternalServices: false, TestContext.Current.CancellationToken);
        Assert.Null(services[0].OriginUrl);
    }

    [Fact]
    public async Task InvokeAsync_UnknownMethod_Returns400ShapeWithBadRequestStatus()
    {
        var sut = new BowireOtlpProtocol();
        var rs = await sut.InvokeAsync("u", "OtlpReceiver", "GhostMethod", [], false, ct: TestContext.Current.CancellationToken);
        Assert.Equal("BadRequest", rs.Status);
        Assert.Contains("Unknown OTLP method", rs.Response!);
    }

    [Fact]
    public async Task InvokeAsync_ReceiverNotRegistered_ReturnsFailedPrecondition()
    {
        var sut = new BowireOtlpProtocol();
        // Initialize without a SP / store — Initialize(null) leaves
        // _store null which is the embedded-host-forgot-to-register
        // path we want to surface clearly.
        sut.Initialize(null);
        var rs = await sut.InvokeAsync("u", "OtlpReceiver", "ReceiveTraces", [], false, ct: TestContext.Current.CancellationToken);
        Assert.Equal("FailedPrecondition", rs.Status);
        Assert.Contains("AddBowireOtlpReceiver", rs.Response!);
    }

    [Fact]
    public async Task InvokeAsync_EmptyStore_ReturnsEmptyShape()
    {
        var sut = new BowireOtlpProtocol();
        sut.Initialize(BuildSpWithStore(new OtlpEnvelopeStore()));
        var rs = await sut.InvokeAsync("u", "OtlpReceiver", "ReceiveTraces", [], false, ct: TestContext.Current.CancellationToken);
        Assert.Equal("OK", rs.Status);
        using var doc = JsonDocument.Parse(rs.Response!);
        Assert.Equal("empty", doc.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task InvokeAsync_WithLatestEnvelope_ReturnsLatestJson()
    {
        var store = new OtlpEnvelopeStore();
        store.Append(new OtlpEnvelope(OtlpSignalKind.Traces, DateTimeOffset.UnixEpoch,
            "application/json", "{\"key\":\"value\"}", null, 15, "127.0.0.1"));
        var sut = new BowireOtlpProtocol();
        sut.Initialize(BuildSpWithStore(store));

        var rs = await sut.InvokeAsync("u", "OtlpReceiver", "ReceiveTraces", [], false, ct: TestContext.Current.CancellationToken);
        Assert.Equal("OK", rs.Status);
        using var doc = JsonDocument.Parse(rs.Response!);
        Assert.Equal("Traces", doc.RootElement.GetProperty("kind").GetString());
        Assert.Equal("application/json", doc.RootElement.GetProperty("contentType").GetString());
        Assert.Equal("{\"key\":\"value\"}", doc.RootElement.GetProperty("bodyJson").GetString());
    }

    [Fact]
    public async Task InvokeAsync_AcceptsFullyQualifiedMethodId()
    {
        var store = new OtlpEnvelopeStore();
        store.Append(new OtlpEnvelope(OtlpSignalKind.Logs, DateTimeOffset.UnixEpoch,
            "application/json", "{}", null, 2, null));
        var sut = new BowireOtlpProtocol();
        sut.Initialize(BuildSpWithStore(store));

        // Workbench may pass the fully-qualified id from BowireMethodInfo.FullName.
        var rs = await sut.InvokeAsync("u", "OtlpReceiver",
            "opentelemetry.proto.collector.v1.OtlpReceiver/ReceiveLogs", [], false, ct: TestContext.Current.CancellationToken);
        Assert.Equal("OK", rs.Status);
    }

    [Fact]
    public async Task InvokeStreamAsync_UnknownMethod_YieldsSingleErrorAndStops()
    {
        var sut = new BowireOtlpProtocol();
        var yielded = new List<string>();
        await foreach (var x in sut.InvokeStreamAsync("u", "OtlpReceiver", "GhostMethod", [], false, ct: TestContext.Current.CancellationToken))
        {
            yielded.Add(x);
        }
        Assert.Single(yielded);
        Assert.Contains("Unknown OTLP method", yielded[0]);
    }

    [Fact]
    public async Task InvokeStreamAsync_NoStore_YieldsSingleErrorAndStops()
    {
        var sut = new BowireOtlpProtocol();
        sut.Initialize(null);
        var yielded = new List<string>();
        await foreach (var x in sut.InvokeStreamAsync("u", "OtlpReceiver", "ReceiveTraces", [], false, ct: TestContext.Current.CancellationToken))
        {
            yielded.Add(x);
        }
        Assert.Single(yielded);
        Assert.Contains("AddBowireOtlpReceiver", yielded[0]);
    }

    [Fact]
    public async Task InvokeStreamAsync_ReplaysExistingThenSubscribes()
    {
        var store = new OtlpEnvelopeStore();
        store.Append(new OtlpEnvelope(OtlpSignalKind.Traces, DateTimeOffset.UnixEpoch,
            "application/json", "{\"n\":1}", null, 7, null));
        store.Append(new OtlpEnvelope(OtlpSignalKind.Metrics, DateTimeOffset.UnixEpoch,
            "application/json", "{\"n\":2}", null, 7, null));

        var sut = new BowireOtlpProtocol();
        sut.Initialize(BuildSpWithStore(store));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var received = new List<string>();
        var task = Task.Run(async () =>
        {
            await foreach (var x in sut.InvokeStreamAsync("u", "OtlpReceiver", "ReceiveTraces", [], false, ct: cts.Token))
            {
                received.Add(x);
                if (received.Count >= 2) await cts.CancelAsync();
            }
        }, cts.Token);

        await Task.Delay(50, TestContext.Current.CancellationToken);
        store.Append(new OtlpEnvelope(OtlpSignalKind.Traces, DateTimeOffset.UnixEpoch,
            "application/json", "{\"n\":3}", null, 7, null));

        try { await task; } catch (OperationCanceledException) { /* expected */ }

        // First the snapshot ("n":1, Traces only — Metrics filtered out),
        // then the live "{\"n\":3}" appended after subscribe.
        Assert.Equal(2, received.Count);
        using var first  = JsonDocument.Parse(received[0]);
        using var second = JsonDocument.Parse(received[1]);
        Assert.Equal("Traces", first.RootElement.GetProperty("kind").GetString());
        Assert.Equal("Traces", second.RootElement.GetProperty("kind").GetString());
    }

    [Fact]
    public async Task OpenChannelAsync_ReturnsNull()
    {
        var sut = new BowireOtlpProtocol();
        var ch = await sut.OpenChannelAsync("u", "OtlpReceiver", "ReceiveTraces", false, ct: TestContext.Current.CancellationToken);
        Assert.Null(ch);
    }

    [Fact]
    public void MapMethodToKind_HandlesBareNameAndFullName()
    {
        Assert.Equal(OtlpSignalKind.Traces,
            BowireOtlpProtocol.MapMethodToKind("ReceiveTraces"));
        Assert.Equal(OtlpSignalKind.Metrics,
            BowireOtlpProtocol.MapMethodToKind("svc/ReceiveMetrics"));
        Assert.Equal(OtlpSignalKind.Logs,
            BowireOtlpProtocol.MapMethodToKind("opentelemetry.proto.collector.v1.OtlpReceiver/ReceiveLogs"));
        Assert.Null(BowireOtlpProtocol.MapMethodToKind(""));
        Assert.Null(BowireOtlpProtocol.MapMethodToKind("ReceiveTea"));
        Assert.Null(BowireOtlpProtocol.MapMethodToKind("ghost/"));
    }

    private static ServiceProvider BuildSpWithStore(OtlpEnvelopeStore store)
    {
        var services = new ServiceCollection();
        services.AddBowireOtlpReceiver(store);
        return services.BuildServiceProvider();
    }
}
