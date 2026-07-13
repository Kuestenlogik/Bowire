// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire;
using Kuestenlogik.Bowire.Mocking;

namespace Kuestenlogik.Bowire.Lighthouse.Tests;

/// <summary>
/// Coverage for <see cref="RecordingProbeExecutor"/> — the wire-level executor
/// that replays a probe's recording through the protocol registry. Uses a
/// <see cref="StubProtocol"/> so no live target is needed.
/// </summary>
public sealed class RecordingProbeExecutorTests
{
    private static Probe ProbeWith(params BowireRecordingStep[] steps)
    {
        Assert.True(ProbeSchedule.TryParse("every 60s", out var s, out _));
        var rec = new BowireRecording { Id = "r", Name = "p" };
        foreach (var step in steps) rec.Steps.Add(step);
        return new Probe { Name = "p", Schedule = s!, Recording = rec };
    }

    private static BowireRecordingStep Step(string protocol = "grpc", string? body = "{}")
        => new() { Id = "s1", Protocol = protocol, Service = "S", Method = "M", Body = body };

    private static BowireProtocolRegistry Registry(IBowireProtocol protocol)
    {
        var reg = new BowireProtocolRegistry();
        reg.Register(protocol);
        return reg;
    }

    [Fact]
    public async Task Replays_step_and_maps_status_latency_body()
    {
        var reg = Registry(new StubProtocol("grpc", () => new InvokeResult("""{"status":"up"}""", 42, "OK", new Dictionary<string, string>())));
        var executor = new RecordingProbeExecutor(reg);

        var result = await executor.ExecuteAsync(ProbeWith(Step()), TestContext.Current.CancellationToken);
        Assert.Equal(200, result.Status);         // "OK" → 200
        Assert.Equal(42, result.LatencyMs);
        Assert.Contains("up", result.Body!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_plugin_throws_probe_execution_exception()
    {
        var executor = new RecordingProbeExecutor(new BowireProtocolRegistry()); // empty
        await Assert.ThrowsAsync<ProbeExecutionException>(
            () => executor.ExecuteAsync(ProbeWith(Step("grpc")), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Plugin_throwing_becomes_probe_execution_exception()
    {
        var reg = Registry(new StubProtocol("grpc", throws: true));
        var executor = new RecordingProbeExecutor(reg);
        var ex = await Assert.ThrowsAsync<ProbeExecutionException>(
            () => executor.ExecuteAsync(ProbeWith(Step()), TestContext.Current.CancellationToken));
        Assert.Contains("connection refused", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Empty_recording_throws()
    {
        var executor = new RecordingProbeExecutor(Registry(new StubProtocol("grpc")));
        await Assert.ThrowsAsync<ProbeExecutionException>(
            () => executor.ExecuteAsync(ProbeWith(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Multi_step_uses_last_status_body_and_summed_latency()
    {
        var call = 0;
        var reg = Registry(new StubProtocol("grpc", () =>
        {
            call++;
            return call == 1
                ? new InvokeResult("""{"step":1}""", 10, "OK", new Dictionary<string, string>())
                : new InvokeResult("""{"step":2}""", 15, "500", new Dictionary<string, string>());
        }));
        var executor = new RecordingProbeExecutor(reg);

        var result = await executor.ExecuteAsync(ProbeWith(Step(), Step()), TestContext.Current.CancellationToken);
        Assert.Equal(500, result.Status);          // last step
        Assert.Equal(25, result.LatencyMs);        // 10 + 15
        Assert.Contains("step\":2", result.Body!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("OK", 200)]
    [InlineData("ok", 200)]
    [InlineData("404", 404)]
    [InlineData("weird", 0)]
    public void ParseStatus_maps_plugin_status_strings(string status, int expected)
        => Assert.Equal(expected, RecordingProbeExecutor.ParseStatus(status));
}
