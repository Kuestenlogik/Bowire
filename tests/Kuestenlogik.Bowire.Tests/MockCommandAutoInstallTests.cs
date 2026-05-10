// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.App;
using Kuestenlogik.Bowire.App.Configuration;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// Exercises <see cref="MockCommand"/>'s auto-install fan-out — the
/// path that fires when a recording references a protocol whose plugin
/// is missing and <c>--auto-install</c> is set. The
/// <see cref="MockCommand.AutoInstallInvoker"/> seam lets us intercept
/// the actual install call so we can drive both success and failure
/// branches offline.
/// </summary>
[Collection(MockCommandAutoInstallTests.CollectionName)]
public sealed class MockCommandAutoInstallTests : IDisposable
{
    public const string CollectionName = "MockCommandAutoInstallSerial";

    private readonly string _tempDir =
        Directory.CreateTempSubdirectory("bowire-mock-ai-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task RunAsync_AutoInstall_KnownProtocol_InvokesInstaller()
    {
        // Recording references "kafka" (mapped to Kuestenlogik.Bowire.Protocol.Kafka
        // in PluginPackageMap; not loaded by the host). With
        // AutoInstall=true and the installer stubbed to "succeed",
        // TryAutoInstallAsync exits true, MockCommand reloads plugins,
        // re-enumerates emitters/hosts, then MockServer.StartAsync
        // tries to bring up the recording — it'll still fail at the
        // protocol-bind step because the stub doesn't actually drop a
        // DLL in pluginDir, but the auto-install foreach + reload
        // branches all ran. Pre-cancel the token so any server that
        // does come up shuts down immediately instead of waiting on
        // Ctrl+C.
        var prev = MockCommand.AutoInstallInvoker;
        var seenPackageIds = new List<string>();
        try
        {
            MockCommand.AutoInstallInvoker = (pkg, _, _, _) =>
            {
                seenPackageIds.Add(pkg);
                return Task.FromResult(0);
            };

            var rec = Path.Combine(_tempDir, "rec.json");
            await File.WriteAllTextAsync(rec, MakeRecordingJson("kafka"),
                TestContext.Current.CancellationToken);

            var cli = new MockCliOptions
            {
                RecordingPath = rec,
                AutoInstall = true,
                Host = "127.0.0.1",
                Port = 0,
            };
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var rc = await MockCommand.RunAsync(cli, cts.Token);

            Assert.Contains(rc, s_acceptedExitCodes);
            Assert.Single(seenPackageIds);
            Assert.Equal("Kuestenlogik.Bowire.Protocol.Kafka", seenPackageIds[0]);
        }
        finally
        {
            MockCommand.AutoInstallInvoker = prev;
        }
    }

    [Fact]
    public async Task RunAsync_AutoInstall_InstallerFails_ReturnsOne()
    {
        // Stubbed installer returns non-zero → failures > 0 →
        // TryAutoInstallAsync returns false → MockCommand returns 1.
        var prev = MockCommand.AutoInstallInvoker;
        try
        {
            MockCommand.AutoInstallInvoker = (_, _, _, _) => Task.FromResult(99);

            var rec = Path.Combine(_tempDir, "rec.json");
            await File.WriteAllTextAsync(rec, MakeRecordingJson("kafka"),
                TestContext.Current.CancellationToken);

            var cli = new MockCliOptions { RecordingPath = rec, AutoInstall = true };
            var rc = await MockCommand.RunAsync(cli, TestContext.Current.CancellationToken);

            Assert.Equal(1, rc);
        }
        finally
        {
            MockCommand.AutoInstallInvoker = prev;
        }
    }

    [Fact]
    public async Task RunAsync_AutoInstall_MultipleMissing_InstallsEach()
    {
        // Recording references two unknown-but-mapped protocols (kafka +
        // dis) → TryAutoInstallAsync iterates both, the stub records
        // each install call.
        var prev = MockCommand.AutoInstallInvoker;
        var seen = new List<string>();
        try
        {
            MockCommand.AutoInstallInvoker = (pkg, _, _, _) =>
            {
                seen.Add(pkg);
                return Task.FromResult(0);
            };

            var rec = Path.Combine(_tempDir, "multi.json");
            await File.WriteAllTextAsync(rec, MakeMultiRecordingJson("kafka", "dis"),
                TestContext.Current.CancellationToken);

            var cli = new MockCliOptions
            {
                RecordingPath = rec,
                AutoInstall = true,
                Host = "127.0.0.1",
                Port = 0,
            };
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await MockCommand.RunAsync(cli, cts.Token);

            Assert.Equal(2, seen.Count);
            Assert.Contains("Kuestenlogik.Bowire.Protocol.Kafka", seen);
            Assert.Contains("Kuestenlogik.Bowire.Protocol.Dis", seen);
        }
        finally
        {
            MockCommand.AutoInstallInvoker = prev;
        }
    }

    private static readonly int[] s_acceptedExitCodes = [0, 1];

    private static string MakeRecordingJson(string protocolId) => $$"""
        {
          "id": "rec_test",
          "name": "Test recording",
          "createdAt": 0,
          "recordingFormatVersion": 2,
          "steps": [
            {
              "id": "step_1",
              "capturedAt": 0,
              "protocol": "{{protocolId}}",
              "service": "svc",
              "method": "m",
              "methodType": "Unary",
              "serverUrl": "http://localhost:1",
              "httpVerb": "GET",
              "httpPath": "/x",
              "status": "OK"
            }
          ]
        }
        """;

    private static string MakeMultiRecordingJson(string a, string b) => $$"""
        {
          "id": "rec_test",
          "name": "Test recording",
          "createdAt": 0,
          "recordingFormatVersion": 2,
          "steps": [
            {
              "id": "step_a",
              "capturedAt": 0,
              "protocol": "{{a}}",
              "service": "svc",
              "method": "m",
              "methodType": "Unary",
              "serverUrl": "http://localhost:1",
              "httpVerb": "GET",
              "httpPath": "/a",
              "status": "OK"
            },
            {
              "id": "step_b",
              "capturedAt": 0,
              "protocol": "{{b}}",
              "service": "svc",
              "method": "m",
              "methodType": "Unary",
              "serverUrl": "http://localhost:1",
              "httpVerb": "GET",
              "httpPath": "/b",
              "status": "OK"
            }
          ]
        }
        """;
}

[CollectionDefinition(MockCommandAutoInstallTests.CollectionName, DisableParallelization = true)]
#pragma warning disable CA1515 // xUnit collection definitions must be public.
#pragma warning disable CA1711 // Suffix "Collection" is xUnit convention.
public sealed class MockCommandAutoInstallTestsCollection { }
#pragma warning restore CA1711
#pragma warning restore CA1515
