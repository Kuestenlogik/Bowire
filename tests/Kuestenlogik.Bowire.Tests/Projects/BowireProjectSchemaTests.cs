// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Kuestenlogik.Bowire.Projects;
using Xunit;

namespace Kuestenlogik.Bowire.Tests.Projects;

/// <summary>
/// #172 — the published <c>site/schemas/project.v1.json</c> JSON Schema stays
/// in sync with the <see cref="BowireProjectFile"/> model. Mirrors the sidecar
/// manifest's schema drift-guard: walk up to the repo root, load the served
/// schema, and assert its <c>$id</c> / <c>required</c> / <c>properties</c> match
/// the model exactly so the schema can't silently fall behind the type.
/// </summary>
public class BowireProjectSchemaTests
{
    private static JsonElement LoadProjectSchema()
    {
        // Walk up from the test binary to the repo root (the dir that owns
        // Directory.Build.props) and read the published schema from site/.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Directory.Build.props")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        var schemaPath = Path.Combine(dir!.FullName, "site", "schemas", "project.v1.json");
        Assert.True(File.Exists(schemaPath), "Missing published schema at " + schemaPath);
        using var doc = JsonDocument.Parse(File.ReadAllText(schemaPath));
        return doc.RootElement.Clone();
    }

    [Fact]
    public void ProjectSchema_Has_Expected_Id_And_Required_Version()
    {
        var schema = LoadProjectSchema();
        // The served-URL convention matches the sidecar schema's ($id under
        // /schemas/, not /schema/) and the model's canonical constant.
        Assert.Equal("https://bowire.io/schemas/project.v1.json", schema.GetProperty("$id").GetString());
        Assert.Equal(BowireProjectFile.SchemaUrl, schema.GetProperty("$id").GetString());

        var required = schema.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToHashSet();
        Assert.Contains("version", required);

        // additionalProperties:false mirrors UnmappedMemberHandling.Disallow —
        // an unknown top-level field is an authoring mistake in both.
        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
    }

    [Fact]
    public void ProjectSchema_Properties_Cover_Every_Manifest_Field()
    {
        // Drift guard: every JsonPropertyName on the model (and its nested
        // source + security types) must have a matching schema property, so the
        // published schema can't silently fall behind the model.
        var schema = LoadProjectSchema();
        var props = schema.GetProperty("properties");

        foreach (var name in JsonPropertyNames(typeof(BowireProjectFile)))
            Assert.True(props.TryGetProperty(name, out _), $"Schema is missing manifest field '{name}'.");

        var sourceProps = props.GetProperty("sources").GetProperty("items").GetProperty("properties");
        foreach (var name in JsonPropertyNames(typeof(BowireProjectSource)))
            Assert.True(sourceProps.TryGetProperty(name, out _), $"Schema is missing source field '{name}'.");

        var securityProps = props.GetProperty("security").GetProperty("properties");
        foreach (var name in JsonPropertyNames(typeof(BowireProjectSecurity)))
            Assert.True(securityProps.TryGetProperty(name, out _), $"Schema is missing security field '{name}'.");
    }

    [Fact]
    public void ProjectSchema_Version_Pins_The_Supported_Version()
    {
        var schema = LoadProjectSchema();
        var versionConst = schema.GetProperty("properties").GetProperty("version").GetProperty("const").GetInt32();
        Assert.Equal(BowireProjectFile.SupportedVersion, versionConst);
    }

    private static IEnumerable<string> JsonPropertyNames(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name)
            .Where(n => !string.IsNullOrEmpty(n))!
            .Cast<string>();
}
