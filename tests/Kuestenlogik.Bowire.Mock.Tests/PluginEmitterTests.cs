// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Mocking;
using Microsoft.Extensions.Logging;

namespace Kuestenlogik.Bowire.Mock.Tests;

/// <summary>
/// Verifies the IBowireMockEmitter extension point. Plugins
/// contribute broadcast-style replay (DIS, DDS, ...) without adding
/// code to Kuestenlogik.Bowire.Mock itself — MockServer iterates
/// MockServerOptions.Emitters, calls CanEmit, starts the ones that
/// claim the recording, and disposes them on shutdown.
/// </summary>
public sealed class PluginEmitterTests : IDisposable
{
    private readonly string _tempDir;

    public PluginEmitterTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "bowire-mock-emitter-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Emitter_WithMatchingRecording_IsStartedAndDisposed()
    {
        var probe = new ProbeEmitter(claim: true);
        var path = await WriteRestRecordingAsync("probe.json");

        await using (var server = await MockServer.StartAsync(
            new MockServerOptions
            {
                RecordingPath = path,
                Port = 0,
                Watch = false,
                Emitters = new[] { probe }
            },
            TestContext.Current.CancellationToken))
        {
            Assert.True(probe.StartCalled, "CanEmit → true means StartAsync must run");
        }

        // DisposeAsync on the server tears down the emitter too.
        Assert.True(probe.DisposeCalled);
    }

    [Fact]
    public async Task Emitter_NotClaimingRecording_IsNotStarted()
    {
        var probe = new ProbeEmitter(claim: false);
        var path = await WriteRestRecordingAsync("no-probe.json");

        await using var server = await MockServer.StartAsync(
            new MockServerOptions
            {
                RecordingPath = path,
                Port = 0,
                Watch = false,
                Emitters = new[] { probe }
            },
            TestContext.Current.CancellationToken);

        Assert.False(probe.StartCalled, "CanEmit → false must short-circuit StartAsync");
    }

    [Fact]
    public async Task Emitter_ThrowingOnStart_DoesNotAbortMockStartup()
    {
        // A plugin whose emitter crashes on start shouldn't bring the
        // rest of the mock down — the mock logs a warning and keeps
        // the HTTP replay path available.
        var brokenProbe = new ProbeEmitter(claim: true, throwOnStart: true);
        var path = await WriteRestRecordingAsync("broken.json");

        await using var server = await MockServer.StartAsync(
            new MockServerOptions
            {
                RecordingPath = path,
                Port = 0,
                Watch = false,
                Emitters = new[] { brokenProbe }
            },
            TestContext.Current.CancellationToken);

        // HTTP listener still up — sanity check a recorded GET responds.
        using var http = new HttpClient();
        var resp = await http.GetAsync(new Uri($"http://127.0.0.1:{server.Port}/ping"), TestContext.Current.CancellationToken);
        Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);
    }

    // ---- helpers ----

    private async Task<string> WriteRestRecordingAsync(string name)
    {
        var rec = new
        {
            id = "rec_probe",
            name = "probe",
            recordingFormatVersion = 2,
            steps = new[]
            {
                new
                {
                    id = "step_ping",
                    protocol = "rest",
                    service = "Ping",
                    method = "Ping",
                    methodType = "Unary",
                    httpPath = "/ping",
                    httpVerb = "GET",
                    status = "OK",
                    response = "\"pong\""
                }
            }
        };
        var file = Path.Combine(_tempDir, name);
        await File.WriteAllTextAsync(file,
            System.Text.Json.JsonSerializer.Serialize(rec), TestContext.Current.CancellationToken);
        return file;
    }

    private sealed class ProbeEmitter : IBowireMockEmitter
    {
        private readonly bool _claim;
        private readonly bool _throwOnStart;

        public ProbeEmitter(bool claim, bool throwOnStart = false)
        {
            _claim = claim;
            _throwOnStart = throwOnStart;
        }

        public string Id => "probe";
        public bool CanEmitCalled { get; private set; }
        public bool StartCalled { get; private set; }
        public bool DisposeCalled { get; private set; }

        public bool CanEmit(BowireRecording recording)
        {
            CanEmitCalled = true;
            return _claim;
        }

        public Task StartAsync(
            BowireRecording recording, MockEmitterOptions options, ILogger logger, CancellationToken ct)
        {
            StartCalled = true;
            if (_throwOnStart) throw new InvalidOperationException("probe crash");
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            DisposeCalled = true;
            return ValueTask.CompletedTask;
        }
    }
}
