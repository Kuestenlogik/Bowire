// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Kuestenlogik.Bowire.Projects;

namespace Kuestenlogik.Bowire.Tests.Projects;

/// <summary>
/// Coverage for the <c>.bowire/project.json</c> manifest model + loader (#172).
/// Parse mirrors <c>AuthRecording.Parse</c>'s JsonException / ArgumentException
/// contract; the loader tests exercise the walk-up auto-discovery against a
/// per-test temp directory tree (no cwd mutation, so this class stays
/// parallel-safe and out of the CwdSerialised collection).
/// </summary>
public sealed class BowireProjectFileTests : IDisposable
{
    private readonly string _tempRoot;

    public BowireProjectFileTests()
    {
        _tempRoot = Directory.CreateTempSubdirectory("bowire-project-tests-").FullName;
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, recursive: true); }
        catch { /* best-effort */ }
    }

    private const string ValidManifest = """
        {
          "$schema": "https://bowire.io/schemas/project.v1.json",
          "version": 1,
          "name": "order-service",
          "sources": [
            { "url": "https://api.example.com", "schemas": ["./proto/orders.proto"] }
          ],
          "suites": {
            "smoke": "./bowire/suites/smoke.collection.json",
            "regression": "./bowire/suites/regression.collection.json"
          },
          "security": {
            "auth": "./bowire/auth/login.flow.json",
            "scan": ["owasp-api", "cve", "protocol"]
          },
          "rules": "./bowire/rules.json"
        }
        """;

    // ---------------------------------------------------------------- model

    [Fact]
    public void Parse_valid_manifest_maps_every_field()
    {
        var project = BowireProjectFile.Parse(ValidManifest);

        Assert.Equal(1, project.Version);
        Assert.Equal("order-service", project.Name);
        var source = Assert.Single(project.Sources);
        Assert.Equal("https://api.example.com", source.Url);
        Assert.Equal("./proto/orders.proto", Assert.Single(source.Schemas));
        Assert.Equal(2, project.Suites.Count);
        Assert.Equal("./bowire/suites/smoke.collection.json", project.Suites["smoke"]);
        Assert.NotNull(project.Security);
        Assert.Equal("./bowire/auth/login.flow.json", project.Security!.Auth);
        Assert.Equal(["owasp-api", "cve", "protocol"], project.Security.Scan);
        Assert.Equal("./bowire/rules.json", project.Rules);
    }

    [Fact]
    public void Parse_valid_manifest_reports_no_validation_errors()
    {
        Assert.Empty(BowireProjectFile.Parse(ValidManifest).Validate());
    }

    [Fact]
    public void ToJson_round_trips_through_Parse()
    {
        var reparsed = BowireProjectFile.Parse(BowireProjectFile.Parse(ValidManifest).ToJson());
        Assert.Equal("order-service", reparsed.Name);
        Assert.Equal("./bowire/rules.json", reparsed.Rules);
        Assert.Empty(reparsed.Validate());
    }

    [Fact]
    public void Parse_malformed_json_throws_JsonException()
    {
        Assert.ThrowsAny<JsonException>(() => BowireProjectFile.Parse("{ not json"));
    }

    [Fact]
    public void Parse_missing_version_throws_ArgumentException()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => BowireProjectFile.Parse("""{ "name": "no-version" }"""));
        Assert.Equal("json", ex.ParamName);
    }

    [Fact]
    public void Parse_empty_document_throws_ArgumentException()
    {
        Assert.Throws<ArgumentException>(() => BowireProjectFile.Parse("   "));
    }

    [Fact]
    public void Parse_unknown_field_throws_JsonException()
    {
        // A typo like "suite" instead of "suites" is an authoring mistake, not
        // silently-tolerated noise — Disallow surfaces it as a JsonException.
        Assert.ThrowsAny<JsonException>(
            () => BowireProjectFile.Parse("""{ "version": 1, "suite": {} }"""));
    }

    // ------------------------------------------------------------ validation

    [Fact]
    public void Validate_unsupported_version_names_the_field()
    {
        var errors = BowireProjectFile.Parse("""{ "version": 2 }""").Validate();
        Assert.Contains(errors, e => e.StartsWith("version:", StringComparison.Ordinal) && e.Contains('2', StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_absolute_suite_path_is_flagged_with_field_and_value()
    {
        var errors = BowireProjectFile.Parse("""
            { "version": 1, "suites": { "smoke": "/etc/bowire/smoke.json" } }
            """).Validate();
        Assert.Contains(errors, e => e.Contains("suites.smoke", StringComparison.Ordinal)
            && e.Contains("/etc/bowire/smoke.json", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_empty_source_url_is_flagged()
    {
        var errors = BowireProjectFile.Parse("""
            { "version": 1, "sources": [ { "url": "", "schemas": [] } ] }
            """).Validate();
        Assert.Contains(errors, e => e.Contains("sources[0].url", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_absolute_auth_and_rules_paths_are_flagged()
    {
        var errors = BowireProjectFile.Parse("""
            { "version": 1, "security": { "auth": "/abs/login.flow.json" }, "rules": "/abs/rules.json" }
            """).Validate();
        Assert.Contains(errors, e => e.Contains("security.auth", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("rules", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------- loader

    [Fact]
    public void Discover_walks_up_to_a_parent_directory()
    {
        var manifestDir = Path.Combine(_tempRoot, BowireProjectLoader.ConventionDirName);
        Directory.CreateDirectory(manifestDir);
        File.WriteAllText(Path.Combine(manifestDir, BowireProjectLoader.ConventionFileName), ValidManifest);

        // Start several levels DOWN from the project root.
        var deep = Path.Combine(_tempRoot, "src", "orders", "handlers");
        Directory.CreateDirectory(deep);

        var located = BowireProjectLoader.Discover(deep);

        Assert.NotNull(located);
        // ProjectRoot is the directory that OWNS .bowire/, so relative paths
        // resolve against it — not the deep start directory.
        Assert.Equal(Path.GetFullPath(_tempRoot), Path.GetFullPath(located!.ProjectRoot));
        Assert.True(File.Exists(located.FilePath));
    }

    [Fact]
    public void Discover_returns_null_when_no_manifest_up_the_chain()
    {
        var lonely = Path.Combine(_tempRoot, "no", "manifest", "here");
        Directory.CreateDirectory(lonely);

        Assert.Null(BowireProjectLoader.Discover(lonely));
    }

    [Fact]
    public void Load_reads_and_parses_a_specific_file()
    {
        var path = Path.Combine(_tempRoot, "project.json");
        File.WriteAllText(path, ValidManifest);

        var project = BowireProjectLoader.Load(path);

        Assert.Equal("order-service", project.Name);
    }
}
