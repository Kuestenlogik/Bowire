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
    public void VulnerableWhen_translates_introspection_status_plus_word_matchers()
    {
        // matchers-condition: and → AllOf at the top
        // status [200]                → Status = 200
        // word [__schema, queryType], condition: and → AllOf of BodyContains
        var path = Path.Combine("TestData", "introspection-enabled.yaml");
        var template = NucleiTemplateReader.ReadFile(path);
        var recording = NucleiTemplateConverter.ToBowireRecording(template);

        Assert.NotNull(recording.VulnerableWhen);
        var top = recording.VulnerableWhen!;
        Assert.NotNull(top.AllOf);
        Assert.Equal(2, top.AllOf!.Count);

        var statusLeaf = top.AllOf.Single(p => p.Status is not null);
        Assert.Equal(200, statusLeaf.Status);

        var wordComposite = top.AllOf.Single(p => p.AllOf is not null);
        Assert.Equal(2, wordComposite.AllOf!.Count);
        Assert.Contains(wordComposite.AllOf, leaf => leaf.BodyContains == "__schema");
        Assert.Contains(wordComposite.AllOf, leaf => leaf.BodyContains == "queryType");
    }

    [Fact]
    public void VulnerableWhen_is_null_when_template_declares_no_matchers()
    {
        // Empty-matchers safety — the converter must not invent a
        // predicate. Scanner shows it as "no actionable predicate".
        var template = new NucleiTemplate
        {
            Id = "no-matchers",
            Info = new NucleiInfo { Name = "Empty", Severity = "low" },
        };
        template.Http.Add(new NucleiHttpRequest { Method = "GET", Path = { "{{BaseURL}}/" } });

        var recording = NucleiTemplateConverter.ToBowireRecording(template);
        Assert.Null(recording.VulnerableWhen);
    }
}
