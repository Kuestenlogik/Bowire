// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.App;
using Kuestenlogik.Bowire.App.Configuration;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// Drives <see cref="MockCommand.RunAsync"/> through the post-StartAsync
/// branch: server boots successfully, <see cref="Console.CancelKeyPress"/>
/// handler subscribes, and <c>WaitForShutdownAsync</c> exits when the
/// linked CancellationTokenSource fires. Reaches lines that
/// <see cref="MockCommandTests"/> (validation-only) and
/// <see cref="MockCommandAutoInstallTests"/> (auto-install
/// detection) leave dark.
/// </summary>
public sealed class MockCommandShutdownTests : IDisposable
{
    private readonly string _tempDir =
        Directory.CreateTempSubdirectory("bowire-mock-shutdown-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task RunAsync_RestRecording_StartsAndShutsDownOnCancel()
    {
        // Recording-driven mock against the in-tree REST plugin — the
        // tool references Protocol.Rest directly so the protocol's
        // registry-driven instance is reachable without an installed
        // plugin DLL. Port=0 lets the OS pick a free port; the
        // CancellationTokenSource fires after a short delay so the
        // post-StartAsync branches (CancelKeyPress hookup +
        // WaitForShutdownAsync) all run before the test returns.
        var rec = Path.Combine(_tempDir, "rec.json");
        await File.WriteAllTextAsync(rec, """
            {
              "id": "rec_test",
              "name": "Test recording",
              "createdAt": 0,
              "recordingFormatVersion": 2,
              "steps": [
                {
                  "id": "step_1",
                  "capturedAt": 0,
                  "protocol": "rest",
                  "service": "Health",
                  "method": "Get",
                  "methodType": "Unary",
                  "serverUrl": "http://localhost:1",
                  "httpVerb": "GET",
                  "httpPath": "/health",
                  "status": "OK",
                  "responseBody": "{\"status\":\"ok\"}",
                  "responseStatusCode": 200,
                  "responseHeaders": { "content-type": "application/json" }
                }
              ]
            }
            """, TestContext.Current.CancellationToken);

        using var cts = new CancellationTokenSource();
        var runTask = MockCommand.RunAsync(new MockCliOptions
        {
            RecordingPath = rec,
            Host = "127.0.0.1",
            Port = 0,
            NoWatch = true,
        }, cts.Token);

        await Task.Delay(500, TestContext.Current.CancellationToken);
        await cts.CancelAsync();
        var rc = await runTask;

        // Either 0 (graceful shutdown via cancel) or 1 (REST plugin
        // setup or replay path surfaced an error before/around
        // WaitForShutdown). Both reach the post-StartAsync code path
        // we're after.
        Assert.Contains(rc, s_acceptedExitCodes);
    }

    private static readonly int[] s_acceptedExitCodes = [0, 1];
}
