// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Security.Templates.Nuclei;

namespace Kuestenlogik.Bowire.Security.Templates.Nuclei.Tests;

/// <summary>
/// Coverage for the Nuclei → BowireRecording converter. Phase 2a
/// validates the metadata + first-step copy; VulnerableWhen predicate
/// translation lands in Phase 2b and gets its own suite.
/// </summary>
public sealed class NucleiTemplateConverterTests
{
    [Fact]
    public void Converts_introspection_template_metadata()
    {
        var path = Path.Combine("TestData", "introspection-enabled.yaml");
        var template = NucleiTemplateReader.ReadFile(path);

        var recording = NucleiTemplateConverter.ToBowireRecording(template);

        Assert.Equal("graphql-introspection-enabled", recording.Id);
        Assert.Equal("GraphQL Introspection Enabled", recording.Name);
        Assert.True(recording.Attack);
        Assert.NotNull(recording.Vulnerability);
        Assert.Equal("medium", recording.Vulnerability!.Severity);
        Assert.Equal("graphql-introspection-enabled", recording.Vulnerability.Id);
        Assert.Equal(2, recording.Vulnerability.References.Count);
        Assert.Single(recording.Vulnerability.Authors);
        Assert.Equal("thomas-stegemann", recording.Vulnerability.Authors[0]);
    }

    [Fact]
    public void Converts_first_http_request_to_step()
    {
        var path = Path.Combine("TestData", "introspection-enabled.yaml");
        var template = NucleiTemplateReader.ReadFile(path);

        var recording = NucleiTemplateConverter.ToBowireRecording(template);

        var step = Assert.Single(recording.Steps);
        Assert.Equal("POST", step.HttpVerb);
        Assert.Equal("{{BaseURL}}/graphql", step.HttpPath);
        Assert.Contains("__schema", step.Body!, StringComparison.Ordinal);
        Assert.Equal("http", step.Protocol);
    }

    [Theory]
    [InlineData("Critical", "critical")]
    [InlineData("HIGH", "high")]
    [InlineData("medium", "medium")]
    [InlineData("unknown", "info")]
    [InlineData("", "info")]
    public void Severity_normalisation_handles_casing_and_unknown(string input, string expected)
    {
        var template = new NucleiTemplate { Id = "x", Info = new NucleiInfo { Severity = input } };
        var recording = NucleiTemplateConverter.ToBowireRecording(template);
        Assert.Equal(expected, recording.Vulnerability!.Severity);
    }

    [Fact]
    public void VulnerableWhen_is_null_until_phase_2b_lands()
    {
        // Predicate translation is deferred — the converter explicitly
        // leaves VulnerableWhen null so the scanner reports the
        // template as "no predicate, will not fire" rather than
        // silently treating every probe as vulnerable. Pin the
        // contract so a future change to default behaviour breaks
        // visibly.
        var path = Path.Combine("TestData", "introspection-enabled.yaml");
        var template = NucleiTemplateReader.ReadFile(path);
        var recording = NucleiTemplateConverter.ToBowireRecording(template);
        Assert.Null(recording.VulnerableWhen);
    }
}
