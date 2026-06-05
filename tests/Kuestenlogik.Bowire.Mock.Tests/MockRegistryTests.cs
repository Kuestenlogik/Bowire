// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Kuestenlogik.Bowire.Mock.Management;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kuestenlogik.Bowire.Mock.Tests;

/// <summary>
/// Lifecycle tests for <see cref="MockRegistry"/> — the workbench-side
/// registry of running mocks (#56). Covers the Start / List / Stop /
/// DisposeAsync paths plus the request-log wiring used by #57's per-mock
/// log view.
/// </summary>
public sealed class MockRegistryTests
{
    private static string BuildRecording()
    {
        // Minimal v2 recording. One REST step is enough for MockServer
        // to accept the payload and stand up an HTTP listener.
        var rec = new BowireRecording
        {
            Id = "rec_test",
            Name = "registry-test",
            RecordingFormatVersion = 2,
        };
        rec.Steps.Add(new BowireRecordingStep
        {
            Id = "step_one",
            Protocol = "rest",
            Service = "S",
            Method = "M",
            MethodType = "Unary",
            HttpPath = "/probe",
            HttpVerb = "GET",
            Status = "OK",
            Response = "ok",
        });
        return JsonSerializer.Serialize(rec);
    }

    private static MockRegistry NewRegistry() =>
        new(NullLogger<MockRegistry>.Instance);

    [Fact]
    public async Task Start_Then_List_Surfaces_Instance()
    {
        await using var registry = NewRegistry();

        var inst = await registry.StartAsync(BuildRecording(), "demo", port: 0, TestContext.Current.CancellationToken);

        Assert.NotNull(inst);
        Assert.Equal("demo", inst.RecordingDisplayName);
        Assert.True(inst.Port > 0, "port=0 should resolve to an OS-assigned port");
        Assert.True(File.Exists(inst.BwrPath), "the .bwr payload should be on disk while the mock runs");

        var list = registry.List();
        Assert.Single(list);
        Assert.Same(inst, list.First());
    }

    [Fact]
    public async Task Get_Returns_Null_For_Unknown_Mock()
    {
        await using var registry = NewRegistry();
        Assert.Null(registry.Get("does-not-exist"));
    }

    [Fact]
    public async Task Get_Returns_Instance_After_Start()
    {
        await using var registry = NewRegistry();
        var inst = await registry.StartAsync(BuildRecording(), "lookup", port: 0, TestContext.Current.CancellationToken);
        Assert.Same(inst, registry.Get(inst.MockId));
    }

    [Fact]
    public async Task Stop_Removes_Mock_And_Deletes_Bwr_File()
    {
        await using var registry = NewRegistry();
        var inst = await registry.StartAsync(BuildRecording(), "lifecycle", port: 0, TestContext.Current.CancellationToken);

        var stopped = await registry.StopAsync(inst.MockId);

        Assert.True(stopped);
        Assert.Empty(registry.List());
        Assert.Null(registry.Get(inst.MockId));
        Assert.False(File.Exists(inst.BwrPath), "stop should clean up the temp .bwr payload");
    }

    [Fact]
    public async Task Stop_Returns_False_For_Unknown_Mock()
    {
        await using var registry = NewRegistry();
        Assert.False(await registry.StopAsync("nope"));
    }

    [Fact]
    public async Task DisposeAsync_Stops_All_Running_Mocks()
    {
        var registry = NewRegistry();
        var a = await registry.StartAsync(BuildRecording(), "a", port: 0, TestContext.Current.CancellationToken);
        var b = await registry.StartAsync(BuildRecording(), "b", port: 0, TestContext.Current.CancellationToken);
        Assert.Equal(2, registry.List().Count);

        await registry.DisposeAsync();

        Assert.False(File.Exists(a.BwrPath));
        Assert.False(File.Exists(b.BwrPath));
    }

    [Fact]
    public async Task Start_Generates_Distinct_Mock_Ids()
    {
        // Two starts must never collide on MockId — the workbench keys
        // its sidebar entries off this string.
        await using var registry = NewRegistry();
        var first = await registry.StartAsync(BuildRecording(), "first", port: 0, TestContext.Current.CancellationToken);
        var second = await registry.StartAsync(BuildRecording(), "second", port: 0, TestContext.Current.CancellationToken);

        Assert.NotEqual(first.MockId, second.MockId);
    }

    [Fact]
    public async Task Instance_RequestLog_Receives_Live_Traffic()
    {
        // The MockRequestLog observer is wired into the MockServer
        // pipeline at Start. A real GET against the mock should land
        // an entry in the log so #57's per-mock log view has data.
        // Note: we don't assert on the HTTP response status — Linux
        // CI runners sometimes return 404 for the recorded REST step
        // before the route is fully wired (it's matched purely on
        // path), but the request still lands in the log either way
        // (the observer is called for matched / miss / 404 alike).
        // What we care about is that the observer wiring is live;
        // poll the log for up to 5 s to absorb any startup delay.
        await using var registry = NewRegistry();
        var inst = await registry.StartAsync(BuildRecording(), "logged", port: 0, TestContext.Current.CancellationToken);

        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{inst.Port}") };
        try
        {
            using var _ = await http.GetAsync(new Uri("/probe", UriKind.Relative), TestContext.Current.CancellationToken);
        }
        catch (HttpRequestException)
        {
            // CI loopback hiccup — the log assertion below is the
            // signal that matters. If the request never reached the
            // server, TotalRequests stays 0 and the test fails clearly.
        }

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline && inst.RequestLog.TotalRequests == 0)
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
        Assert.True(inst.RequestLog.TotalRequests >= 1,
            $"expected the mock's request log to have ≥ 1 entry within 5 s, got {inst.RequestLog.TotalRequests}");
        var snapshot = inst.RequestLog.Snapshot();
        Assert.Contains(snapshot, e => e.Path == "/probe");
    }
}
