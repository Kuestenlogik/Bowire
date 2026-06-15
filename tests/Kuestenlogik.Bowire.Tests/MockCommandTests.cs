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
            () => MockCommand.RunAsync(null!, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RunAsync_NoSource_ReturnsUsageExit()
    {
        // No --recording / --schema / --grpc-schema / --graphql-schema
        // specified → exit code 2 with a usage hint on stderr.
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var rc = await MockCommand.RunAsync(new MockCliOptions(), stdout, stderr, TestContext.Current.CancellationToken);
        Assert.Equal(2, rc);
        // Concrete usage line — guards the "which flag is missing" copy
        // so a future refactor that drops the hint blows the test
        // instead of silently passing with rc=2 from elsewhere.
        var err = stderr.ToString();
        Assert.Contains("one of --recording", err);
        Assert.Contains("--help", err);
    }

    [Fact]
    public async Task RunAsync_TwoSources_ReturnsUsageExit()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var cli = new MockCliOptions
        {
            RecordingPath = "rec.json",
            SchemaPath = "openapi.yml",
        };
        var rc = await MockCommand.RunAsync(cli, stdout, stderr, TestContext.Current.CancellationToken);
        Assert.Equal(2, rc);
        Assert.Contains("mutually exclusive", stderr.ToString());
    }

    [Fact]
    public async Task RunAsync_AllFourSources_ReturnsUsageExit()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var cli = new MockCliOptions
        {
            RecordingPath = "rec.json",
            SchemaPath = "openapi.yml",
            GrpcSchemaPath = "fds.pb",
            GraphQlSchemaPath = "schema.graphql",
        };
        var rc = await MockCommand.RunAsync(cli, stdout, stderr, TestContext.Current.CancellationToken);
        Assert.Equal(2, rc);
        Assert.Contains("mutually exclusive", stderr.ToString());
    }

    [Fact]
    public async Task RunAsync_BadChaosSpec_ReturnsUsageExit()
    {
        // --chaos with garbage syntax → ChaosOptions.Parse throws
        // FormatException → exit 2 (usage), not 1 (runtime).
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var cli = new MockCliOptions
        {
            // A schema-only mock so the chaos parse runs before any
            // schema-loader IO.
            SchemaPath = "openapi.yml",
            Chaos = "this-is-not-valid-syntax-!@#$",
        };
        var rc = await MockCommand.RunAsync(cli, stdout, stderr, TestContext.Current.CancellationToken);
        Assert.Equal(2, rc);
        // Asserts the chaos parser actually surfaces a parse error
        // (rather than e.g. a generic "missing recording" failing to
        // catch the bad flag at all).
        var err = stderr.ToString();
        Assert.Contains("bowire mock:", err);
        Assert.Contains("--chaos", err);
    }

    [Fact]
    public async Task RunAsync_RecordingWithUnknownProtocol_ReturnsErrorExit()
    {
        // No --auto-install → unknown protocol surfaces as "missing
        // plugin" diagnostic and exit 1. Exercises the detection +
        // PrintMissingPlugins path without touching the network. The
        // TextWriter overloads on MockCommand.RunAsync give us
        // race-free per-test capture of the stderr lines that the
        // PrintMissingPlugins helper emits, so we assert on the
        // diagnostic content too — not just the exit code.
        var dir = Directory.CreateTempSubdirectory("bowire-mock-").FullName;
        var rec = SafePath.Combine(dir, "rec.json");
        try
        {
            await File.WriteAllTextAsync(rec, MakeRecordingJson("nonexistent-proto-x"),
                TestContext.Current.CancellationToken);

            using var stdout = new StringWriter();
            using var stderr = new StringWriter();
            var cli = new MockCliOptions { RecordingPath = rec };
            var rc = await MockCommand.RunAsync(cli, stdout, stderr, TestContext.Current.CancellationToken);

            Assert.Equal(1, rc);
            var err = stderr.ToString();
            Assert.Contains("Recording references", err);
            Assert.Contains("nonexistent-proto-x", err);
            Assert.Contains("(unknown — third-party plugin?)", err);
            Assert.Contains("not in Bowire's catalogue", err);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task RunAsync_KnownMissingProtocol_PrintsInstallHint()
    {
        // Recording references "kafka" — mapped in PluginPackageMap but
        // not loaded in this host. Without --auto-install, MockCommand
        // calls PrintMissingPlugins which lists the suggested
        // `bowire plugin install …` command, then returns 1. Asserts
        // on the actual hint content so a future refactor that drops
        // PrintMissingPlugins fails this test instead of slipping
        // through on the exit-code match alone.
        var dir = Directory.CreateTempSubdirectory("bowire-mock-pmp-").FullName;
        var rec = SafePath.Combine(dir, "rec.json");
        try
        {
            await File.WriteAllTextAsync(rec, MakeRecordingJson("kafka"),
                TestContext.Current.CancellationToken);

            using var stdout = new StringWriter();
            using var stderr = new StringWriter();
            var cli = new MockCliOptions { RecordingPath = rec };
            var rc = await MockCommand.RunAsync(cli, stdout, stderr, TestContext.Current.CancellationToken);

            Assert.Equal(1, rc);
            var err = stderr.ToString();
            Assert.Contains("Recording references", err);
            Assert.Contains("kafka", err);
            Assert.Contains("Kuestenlogik.Bowire.Protocol.Kafka", err);
            Assert.Contains("Install with:", err);
            Assert.Contains("bowire plugin install Kuestenlogik.Bowire.Protocol.Kafka", err);
            Assert.Contains("--auto-install", err);
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
        var rec = SafePath.Combine(dir, "rec.json");
        try
        {
            await File.WriteAllTextAsync(rec, MakeRecordingJson("totally-made-up-zzz"),
                TestContext.Current.CancellationToken);

            using var stdout = new StringWriter();
            using var stderr = new StringWriter();
            var cli = new MockCliOptions { RecordingPath = rec, AutoInstall = true };
            var rc = await MockCommand.RunAsync(cli, stdout, stderr, TestContext.Current.CancellationToken);

            Assert.Equal(1, rc);
            var err = stderr.ToString();
            // Verifies the unresolvable-package diagnostic — distinct
            // from the resolvable "Install with: bowire plugin install …"
            // path so the two branches stay testable independently.
            Assert.Contains("auto-install can't help", err);
            Assert.Contains("totally-made-up-zzz", err);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task RunAsync_RecordingFileMissing_ReturnsErrorExit()
    {
        // Recording path is set but the file doesn't exist on disk →
        // the loader throws on Load → catch path returns 1.
        var dir = Directory.CreateTempSubdirectory("bowire-mock-").FullName;
        try
        {
            using var stdout = new StringWriter();
            using var stderr = new StringWriter();
            var cli = new MockCliOptions
            {
                RecordingPath = SafePath.Combine(dir, "absent.bwr"),
            };
            var rc = await MockCommand.RunAsync(cli, stdout, stderr, TestContext.Current.CancellationToken);
            Assert.Equal(1, rc);
            Assert.Contains("bowire mock:", stderr.ToString());
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task RunAsync_RecordingMalformedJson_ReturnsErrorExit()
    {
        // Recording exists but is not valid JSON → load throws → catch
        // path returns 1.
        var dir = Directory.CreateTempSubdirectory("bowire-mock-").FullName;
        var rec = SafePath.Combine(dir, "broken.bwr");
        try
        {
            await File.WriteAllTextAsync(rec, "{ not json", TestContext.Current.CancellationToken);
            using var stdout = new StringWriter();
            using var stderr = new StringWriter();
            var cli = new MockCliOptions { RecordingPath = rec };
            var rc = await MockCommand.RunAsync(cli, stdout, stderr, TestContext.Current.CancellationToken);
            Assert.Equal(1, rc);
            // Confirms the failure is the JSON parser, not a downstream
            // path that happens to also return 1.
            var err = stderr.ToString();
            Assert.Contains("bowire mock:", err);
            Assert.Contains("invalid start of a property name", err);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task RunAsync_SchemaPathMissing_ReturnsErrorExit()
    {
        // Schema file pointed at by --schema doesn't exist → MockServer
        // bubbles the IO error up; exit 1.
        var dir = Directory.CreateTempSubdirectory("bowire-mock-").FullName;
        try
        {
            using var stdout = new StringWriter();
            using var stderr = new StringWriter();
            var cli = new MockCliOptions
            {
                SchemaPath = SafePath.Combine(dir, "missing-openapi.yml"),
            };
            var rc = await MockCommand.RunAsync(cli, stdout, stderr, TestContext.Current.CancellationToken);
            Assert.Equal(1, rc);
            Assert.Contains("bowire mock:", stderr.ToString());
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort */ }
        }
    }

    private static readonly int[] s_acceptedExitCodes = [0, 1];

    [Fact]
    public async Task RunAsync_PreCancelledToken_DoesNotPropagateException()
    {
        // Token already cancelled → MockServer.StartAsync throws
        // OperationCanceledException → MockCommand swallows it and
        // returns 0 (graceful Ctrl+C path), or surfaces the IO error
        // before the cancel hits. Either way we want the call to
        // return cleanly, no exception escaping.
        var dir = Directory.CreateTempSubdirectory("bowire-mock-").FullName;
        try
        {
            var cli = new MockCliOptions
            {
                SchemaPath = SafePath.Combine(dir, "anything.yml"),
            };
            using var cts = new CancellationTokenSource();
            await cts.CancelAsync();
            var rc = await MockCommand.RunAsync(cli, ct: cts.Token);
            Assert.Contains(rc, s_acceptedExitCodes);
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
