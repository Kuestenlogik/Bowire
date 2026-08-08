// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.CommandLine;
using Kuestenlogik.Bowire.App.Cli;
using Kuestenlogik.Bowire.Projects;

namespace Kuestenlogik.Bowire.Tests.Cli;

/// <summary>
/// Behavioural coverage for <c>bowire project show / validate</c> (#172).
/// Output-capture + concrete substring assertions per the RecordingCommand /
/// WorkspaceCommand suites; exit codes pin the sysexits contract the help text
/// promises. `show` and `validate`-without-<c>--file</c> auto-discover from the
/// current working directory, so this class mutates process-wide cwd and joins
/// the <c>CwdSerialised</c> collection to serialise against every other
/// cwd-flipping test.
/// </summary>
[Collection("CwdSerialised")]
public sealed class ProjectCommandTests : IDisposable
{
    private const int ExitOk = 0;
    private const int ExitDataErr = 65;
    private const int ExitNoInput = 66;

    private readonly string _tempRoot;
    private readonly string _originalCwd;

    public ProjectCommandTests()
    {
        _originalCwd = Directory.GetCurrentDirectory();
        _tempRoot = Directory.CreateTempSubdirectory("bowire-project-cli-").FullName;
        // Start in a manifest-free directory so auto-discovery is deterministic.
        Directory.SetCurrentDirectory(_tempRoot);
    }

    public void Dispose()
    {
        Directory.SetCurrentDirectory(_originalCwd);
        try { if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, recursive: true); }
        catch { /* best-effort */ }
    }

    private const string ValidManifest = """
        {
          "version": 1,
          "name": "order-service",
          "sources": [ { "url": "https://api.example.com", "schemas": ["./proto/orders.proto"] } ],
          "suites": { "smoke": "./bowire/suites/smoke.collection.json" },
          "security": { "auth": "./bowire/auth/login.flow.json", "scan": ["owasp-api"] },
          "rules": "./bowire/rules.json"
        }
        """;

    private void WriteManifestInCwd(string json)
    {
        var dir = Path.Combine(_tempRoot, BowireProjectLoader.ConventionDirName);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, BowireProjectLoader.ConventionFileName), json);
    }

    private static async Task<(int rc, string stdout, string stderr)> Invoke(params string[] args)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var project = ProjectCommand.Build();
        var parse = project.Parse(args);
        var rc = await parse.InvokeAsync(new InvocationConfiguration
        {
            Output = stdout,
            Error = stderr,
        }, TestContext.Current.CancellationToken);

        return (rc, stdout.ToString(), stderr.ToString());
    }

    // ------------------------------------------------------------------ show

    [Fact]
    public async Task Show_no_manifest_exits_no_input()
    {
        var (rc, _, stderr) = await Invoke("show");
        Assert.Equal(ExitNoInput, rc);
        Assert.Contains("no .bowire/project.json", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Show_valid_manifest_prints_the_resolved_project()
    {
        WriteManifestInCwd(ValidManifest);

        var (rc, stdout, stderr) = await Invoke("show");

        Assert.Equal(ExitOk, rc);
        Assert.Empty(stderr);
        Assert.Contains("order-service", stdout, StringComparison.Ordinal);
        Assert.Contains("https://api.example.com", stdout, StringComparison.Ordinal);
        Assert.Contains("smoke:", stdout, StringComparison.Ordinal);
        Assert.Contains("owasp-api", stdout, StringComparison.Ordinal);
        Assert.Contains("./bowire/rules.json", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Show_invalid_manifest_exits_data_err()
    {
        WriteManifestInCwd("""{ "name": "no-version" }""");

        var (rc, _, stderr) = await Invoke("show");

        Assert.Equal(ExitDataErr, rc);
        Assert.Contains("version", stderr, StringComparison.Ordinal);
    }

    // -------------------------------------------------------------- validate

    [Fact]
    public async Task Validate_discovered_valid_manifest_exits_ok()
    {
        WriteManifestInCwd(ValidManifest);

        var (rc, stdout, _) = await Invoke("validate");

        Assert.Equal(ExitOk, rc);
        Assert.Contains("OK:", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Validate_no_manifest_exits_no_input()
    {
        var (rc, _, stderr) = await Invoke("validate");
        Assert.Equal(ExitNoInput, rc);
        Assert.Contains("no .bowire/project.json", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Validate_file_override_valid_exits_ok()
    {
        var path = Path.Combine(_tempRoot, "custom.project.json");
        await File.WriteAllTextAsync(path, ValidManifest, TestContext.Current.CancellationToken);

        var (rc, stdout, _) = await Invoke("validate", "--file", path);

        Assert.Equal(ExitOk, rc);
        Assert.Contains("valid project file", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Validate_file_override_with_validation_errors_exits_data_err()
    {
        var path = Path.Combine(_tempRoot, "bad.project.json");
        await File.WriteAllTextAsync(path, """
            { "version": 1, "suites": { "smoke": "/absolute/smoke.json" } }
            """, TestContext.Current.CancellationToken);

        var (rc, _, stderr) = await Invoke("validate", "--file", path);

        Assert.Equal(ExitDataErr, rc);
        Assert.Contains("suites.smoke", stderr, StringComparison.Ordinal);
        Assert.Contains("problem(s)", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Validate_file_override_missing_exits_no_input()
    {
        var path = Path.Combine(_tempRoot, "nope.project.json");

        var (rc, _, stderr) = await Invoke("validate", "--file", path);

        Assert.Equal(ExitNoInput, rc);
        Assert.Contains(path, stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Validate_file_override_malformed_exits_data_err()
    {
        var path = Path.Combine(_tempRoot, "broken.project.json");
        await File.WriteAllTextAsync(path, "{ not json", TestContext.Current.CancellationToken);

        var (rc, _, stderr) = await Invoke("validate", "--file", path);

        Assert.Equal(ExitDataErr, rc);
        Assert.NotEmpty(stderr);
    }
}
