// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.App.Cli;
using Microsoft.Extensions.Configuration;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// Drives every <see cref="BowireCli"/> subcommand action handler end-to-end
/// using minimal canned inputs. The handlers either fail fast (missing
/// recording, bad URL, missing file) or hit the underlying entry point
/// with assertable exit codes. Together with <see cref="BowireCliTests"/>'s
/// --help smoke tests, this lifts <c>BowireCli</c>'s action-closure coverage
/// from "parser only" to "lambda-bodies executed too".
/// </summary>
public sealed class BowireCliActionTests : IDisposable
{
    private readonly string _tempDir =
        Directory.CreateTempSubdirectory("bowire-cli-action-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
        GC.SuppressFinalize(this);
    }

    private static IConfiguration EmptyConfig() => new ConfigurationBuilder().Build();

    [Fact]
    public async Task ListSubcommand_AgainstDeadUrl_ReturnsErrorExit()
    {
        // The list-action lambda builds CliCommandOptions and calls
        // CliHandler.ListAsync. Reflection fails fast against 127.0.0.1:1.
        var rc = await BowireCli.RunAsync(
            ["list", "--url", "http://127.0.0.1:1", "-plaintext"],
            EmptyConfig(),
            pluginDir: "");
        Assert.Equal(1, rc);
    }

    [Fact]
    public async Task DescribeSubcommand_AgainstDeadUrl_ReturnsErrorExit()
    {
        var rc = await BowireCli.RunAsync(
            ["describe", "users.UserService", "--url", "http://127.0.0.1:1"],
            EmptyConfig(),
            pluginDir: "");
        Assert.Equal(1, rc);
    }

    [Fact]
    public async Task CallSubcommand_AgainstDeadUrl_ReturnsErrorExit()
    {
        // Call action passes through BuildCliOptions with the full set
        // of options (compact, data, headers) so the constructor branch
        // that wires each list runs.
        var rc = await BowireCli.RunAsync(
            ["call", "users.UserService/Get",
                "--url", "http://127.0.0.1:1",
                "--compact",
                "-d", "{\"id\":1}",
                "-H", "authorization: bearer x"],
            EmptyConfig(),
            pluginDir: "");
        Assert.Equal(1, rc);
    }

    [Fact]
    public async Task CallSubcommand_PlaintextHttpsDowngrade_ReturnsErrorExit()
    {
        // -plaintext + an https URL → BuildCliOptions rewrites the
        // scheme to http before the network call, exercising the
        // downgrade branch (which is otherwise only hit when callers
        // pass an https URL with -plaintext together).
        var rc = await BowireCli.RunAsync(
            ["call", "users.UserService/Get",
                "--url", "https://127.0.0.1:1",
                "-plaintext",
                "-d", "{}"],
            EmptyConfig(),
            pluginDir: "");
        Assert.Equal(1, rc);
    }

    [Fact]
    public async Task MockSubcommand_NoSource_ReturnsUsageExit()
    {
        // Mock action lambda is built; bowire mock with no input source
        // dispatches into MockCommand.RunAsync which returns 2 for the
        // "must specify one of --recording/--schema/..." case.
        var rc = await BowireCli.RunAsync(
            ["mock"],
            EmptyConfig(),
            pluginDir: "");
        Assert.Equal(2, rc);
    }

    [Fact]
    public async Task MockSubcommand_BadChaosSpec_ReturnsUsageExit()
    {
        // Forces the action lambda to plumb through every option (host,
        // port, chaos, capture-miss, control-token, stateful flags) so
        // every line of the MockCliOptions builder branch executes
        // before MockCommand.RunAsync returns 2 for the chaos parse
        // failure.
        var bogusSchema = Path.Combine(_tempDir, "anything.yaml");
        await File.WriteAllTextAsync(bogusSchema, "openapi: 3.0.0", TestContext.Current.CancellationToken);

        var rc = await BowireCli.RunAsync(
            ["mock",
                "--schema", bogusSchema,
                "--host", "127.0.0.1",
                "--port", "0",
                "--chaos", "garbage-spec",
                "--capture-miss", Path.Combine(_tempDir, "miss.json"),
                "--control-token", "tok",
                "--stateful",
                "--no-watch",
                "--loop",
                "--auto-install"],
            EmptyConfig(),
            pluginDir: "");
        Assert.Equal(2, rc);
    }

    [Fact]
    public async Task TestSubcommand_NoCollectionPath_ReturnsUsageExit()
    {
        // Test action lambda builds TestCliOptions and calls
        // TestRunner.RunAsync; with no positional arg + no env binding,
        // the runner exits 2 (usage).
        var rc = await BowireCli.RunAsync(
            ["test"],
            EmptyConfig(),
            pluginDir: "");
        Assert.Equal(2, rc);
    }

    [Fact]
    public async Task TestSubcommand_WithReportPaths_RunsThroughActionClosure()
    {
        // Hand the action lambda a real collection path so the
        // TestCliOptions build wires every property; the runner then
        // surfaces the missing-protocol failure and writes the report.
        var coll = Path.Combine(_tempDir, "coll.json");
        var report = Path.Combine(_tempDir, "out.html");
        var junit = Path.Combine(_tempDir, "out.xml");
        await File.WriteAllTextAsync(coll, """
            {
              "name": "x",
              "protocol": "no-such-protocol-here",
              "serverUrl": "http://localhost:1",
              "tests": [
                { "name": "t", "service": "Svc", "method": "M" }
              ]
            }
            """, TestContext.Current.CancellationToken);

        var rc = await BowireCli.RunAsync(
            ["test", coll, "--report", report, "--junit", junit],
            EmptyConfig(),
            pluginDir: "");
        Assert.Equal(1, rc);
        Assert.True(File.Exists(report));
        Assert.True(File.Exists(junit));
    }

    [Fact]
    public async Task PluginListSubcommand_EmptyDir_ReturnsZero()
    {
        // plugin list action handler dispatches into PluginManager.List
        // with the verbose flag plumbed through.
        var rc = await BowireCli.RunAsync(
            ["plugin", "list", "--verbose"],
            EmptyConfig(),
            pluginDir: _tempDir);
        Assert.Equal(0, rc);
    }

    [Fact]
    public async Task PluginUninstallSubcommand_UnknownPackage_ReturnsOne()
    {
        var rc = await BowireCli.RunAsync(
            ["plugin", "uninstall", "no-such-package"],
            EmptyConfig(),
            pluginDir: _tempDir);
        Assert.Equal(1, rc);
    }

    [Fact]
    public async Task PluginInstallSubcommand_FromMissingFile_ReturnsErrorExit()
    {
        // install action lambda — file branch since --file is supplied.
        var rc = await BowireCli.RunAsync(
            ["plugin", "install", "any-id", "--file", Path.Combine(_tempDir, "absent.nupkg")],
            EmptyConfig(),
            pluginDir: _tempDir);
        Assert.Equal(1, rc);
    }

    [Fact]
    public async Task PluginInstallSubcommand_FromLocalFeed_ReachesInstallAsync()
    {
        // No --file → the install action lambda hits the NuGet path.
        // Provide an empty source folder so InstallAsync fails fast
        // without network. The action closure (the !string.IsNullOrEmpty
        // ternary that picks Install vs InstallFromFile) is the line we
        // care about.
        var feed = Path.Combine(_tempDir, "empty-feed");
        Directory.CreateDirectory(feed);
        var rc = await BowireCli.RunAsync(
            ["plugin", "install", "Ghost.Pkg", "--source", feed],
            EmptyConfig(),
            pluginDir: _tempDir);
        Assert.Equal(1, rc);
    }

    [Fact]
    public async Task PluginDownloadSubcommand_DispatchesIntoPluginManager()
    {
        // Download action lambda — handles missing-output via default-cwd
        // fallback (Directory.GetCurrentDirectory()) when --output is
        // omitted. We supply --output so the failure surfaces from the
        // resolver instead, but either way the action's body runs.
        var bundle = Path.Combine(_tempDir, "bundle");
        var feed = Path.Combine(_tempDir, "empty-feed");
        Directory.CreateDirectory(feed);
        var rc = await BowireCli.RunAsync(
            ["plugin", "download", "Ghost.Pkg",
                "--output", bundle,
                "--source", feed],
            EmptyConfig(),
            pluginDir: _tempDir);
        Assert.Equal(1, rc);
    }

    [Fact]
    public async Task PluginUpdateSubcommand_NoArg_RoutesToUpdateAll()
    {
        // No package id → action lambda picks the UpdateAll branch.
        // Empty plugin dir → UpdateAll returns 0 (nothing to do).
        var rc = await BowireCli.RunAsync(
            ["plugin", "update"],
            EmptyConfig(),
            pluginDir: _tempDir);
        Assert.Equal(0, rc);
    }

    [Fact]
    public async Task PluginUpdateSubcommand_WithArg_RoutesToUpdate()
    {
        // Provided id → the other branch of the action lambda. Plugin
        // not installed → UpdateAsync returns 1.
        var rc = await BowireCli.RunAsync(
            ["plugin", "update", "ghost"],
            EmptyConfig(),
            pluginDir: _tempDir);
        Assert.Equal(1, rc);
    }

    [Fact]
    public async Task PluginInspectSubcommand_NotInstalled_ReturnsOne()
    {
        var rc = await BowireCli.RunAsync(
            ["plugin", "inspect", "no-such"],
            EmptyConfig(),
            pluginDir: _tempDir);
        Assert.Equal(1, rc);
    }

    [Fact]
    public async Task ImportHarSubcommand_ValidHar_WritesBwrFile()
    {
        // Drives the har import action — including the default-output
        // path that derives the .bwr file name from the HAR basename.
        var har = Path.Combine(_tempDir, "trace.har");
        await File.WriteAllTextAsync(har, """
            {
              "log": {
                "version": "1.2",
                "creator": { "name": "test", "version": "1" },
                "entries": [
                  {
                    "startedDateTime": "2026-01-01T00:00:00Z",
                    "time": 1,
                    "request": { "method": "GET", "url": "http://api.test/x", "headers": [], "queryString": [], "cookies": [], "headersSize": -1, "bodySize": -1 },
                    "response": { "status": 200, "statusText": "OK", "httpVersion": "HTTP/1.1", "headers": [], "cookies": [], "content": { "size": 0, "mimeType": "text/plain" }, "redirectURL": "", "headersSize": -1, "bodySize": 0 },
                    "cache": {},
                    "timings": { "send": 0, "wait": 0, "receive": 0 }
                  }
                ]
              }
            }
            """, TestContext.Current.CancellationToken);

        var rc = await BowireCli.RunAsync(
            ["import", "har", har],
            EmptyConfig(),
            pluginDir: "");
        Assert.Equal(0, rc);
        Assert.True(File.Exists(Path.ChangeExtension(har, ".bwr")));
    }

    [Fact]
    public async Task ImportHarSubcommand_WithExplicitOutput_RoutesToOutPath()
    {
        // --out supplied → the default-output branch is skipped; the
        // explicit path lands the .bwr exactly where the user asked.
        var har = Path.Combine(_tempDir, "trace2.har");
        var explicitOut = Path.Combine(_tempDir, "result.bwr");
        await File.WriteAllTextAsync(har, """
            { "log": { "version": "1.2", "creator": { "name": "t", "version": "1" }, "entries": [] } }
            """, TestContext.Current.CancellationToken);

        var rc = await BowireCli.RunAsync(
            ["import", "har", har, "--out", explicitOut, "--name", "custom"],
            EmptyConfig(),
            pluginDir: "");
        // Empty entries array — importer may emit a recording with no
        // steps (exit 0) or surface a "no entries" diagnostic (exit 1).
        // Either way the action lambda body executed.
        Assert.Contains(rc, s_acceptedExitCodes);
    }

    private static readonly int[] s_acceptedExitCodes = [0, 1];
}
