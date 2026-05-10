// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.App;
using Kuestenlogik.Bowire.App.Configuration;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// Tests the <see cref="MockCommand.RunAsync"/> argument-validation
/// frontier — exclusivity of the four input modes (recording / schema
/// / grpc-schema / graphql-schema), chaos-string parsing, and the
/// null-cli guard. The happy-path "spin up MockServer, replay" is
/// covered by the integration harness in <c>tests/Kuestenlogik.Bowire.IntegrationTests</c>.
/// </summary>
public sealed class MockCommandTests
{
    [Fact]
    public async Task RunAsync_NullCli_Throws()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => MockCommand.RunAsync(null!, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RunAsync_NoSource_ReturnsUsageExit()
    {
        // No --recording / --schema / --grpc-schema / --graphql-schema
        // specified → exit code 2 with a usage hint on stderr.
        var rc = await MockCommand.RunAsync(new MockCliOptions(), TestContext.Current.CancellationToken);
        Assert.Equal(2, rc);
    }

    [Fact]
    public async Task RunAsync_TwoSources_ReturnsUsageExit()
    {
        var cli = new MockCliOptions
        {
            RecordingPath = "rec.json",
            SchemaPath = "openapi.yml",
        };
        var rc = await MockCommand.RunAsync(cli, TestContext.Current.CancellationToken);
        Assert.Equal(2, rc);
    }

    [Fact]
    public async Task RunAsync_AllFourSources_ReturnsUsageExit()
    {
        var cli = new MockCliOptions
        {
            RecordingPath = "rec.json",
            SchemaPath = "openapi.yml",
            GrpcSchemaPath = "fds.pb",
            GraphQlSchemaPath = "schema.graphql",
        };
        var rc = await MockCommand.RunAsync(cli, TestContext.Current.CancellationToken);
        Assert.Equal(2, rc);
    }

    [Fact]
    public async Task RunAsync_BadChaosSpec_ReturnsUsageExit()
    {
        // --chaos with garbage syntax → ChaosOptions.Parse throws
        // FormatException → exit 2 (usage), not 1 (runtime).
        var cli = new MockCliOptions
        {
            // A schema-only mock so the chaos parse runs before any
            // schema-loader IO.
            SchemaPath = "openapi.yml",
            Chaos = "this-is-not-valid-syntax-!@#$",
        };
        var rc = await MockCommand.RunAsync(cli, TestContext.Current.CancellationToken);
        Assert.Equal(2, rc);
    }

    [Fact]
    public async Task RunAsync_RecordingWithUnknownProtocol_ReturnsErrorExit()
    {
        // No --auto-install → unknown protocol surfaces as "missing
        // plugin" diagnostic and exit 1. Exercises the detection +
        // PrintMissingPlugins path without touching the network.
        // Stderr is left at its default sink — capturing process-wide
        // streams is racy under xUnit's parallel runner, so we assert
        // on the exit code only.
        var dir = Directory.CreateTempSubdirectory("bowire-mock-").FullName;
        var rec = Path.Combine(dir, "rec.json");
        try
        {
            await File.WriteAllTextAsync(rec, MakeRecordingJson("nonexistent-proto-x"),
                TestContext.Current.CancellationToken);

            var cli = new MockCliOptions { RecordingPath = rec };
            var rc = await MockCommand.RunAsync(cli, TestContext.Current.CancellationToken);

            Assert.Equal(1, rc);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task RunAsync_AutoInstallWithThirdPartyProtocol_FailsFast()
    {
        // --auto-install + an unknown protocol id with no entry in
        // PluginPackageMap → TryAutoInstallAsync immediately bails
        // because there's no NuGet id to install. Exit 1.
        var dir = Directory.CreateTempSubdirectory("bowire-mock-").FullName;
        var rec = Path.Combine(dir, "rec.json");
        try
        {
            await File.WriteAllTextAsync(rec, MakeRecordingJson("totally-made-up-zzz"),
                TestContext.Current.CancellationToken);

            var cli = new MockCliOptions { RecordingPath = rec, AutoInstall = true };
            var rc = await MockCommand.RunAsync(cli, TestContext.Current.CancellationToken);

            Assert.Equal(1, rc);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    private static string MakeRecordingJson(string protocolId) => $$"""
        {
          "id": "rec_test",
          "name": "Test recording",
          "description": "fixture",
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
}
