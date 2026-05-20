// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Security.Templates.Nuclei;

namespace Kuestenlogik.Bowire.Security.Templates.Nuclei.Tests;

/// <summary>
/// Coverage for the Nuclei YAML reader. Phase 2a — happy-path
/// single-request templates with status + word matchers. Phase 2b
/// extends with regex / multi-matcher composition / variable
/// substitution; Phase 2c adds multi-step + payloads.
/// </summary>
public sealed class NucleiTemplateReaderTests
{
    [Fact]
    public void ReadFile_parses_introspection_template_topology()
    {
        var path = Path.Combine("TestData", "introspection-enabled.yaml");
        var template = NucleiTemplateReader.ReadFile(path);

        Assert.Equal("graphql-introspection-enabled", template.Id);
        Assert.Equal("GraphQL Introspection Enabled", template.Info.Name);
        Assert.Equal("thomas-stegemann", template.Info.Author);
        Assert.Equal("medium", template.Info.Severity);
        Assert.Equal(2, template.Info.Reference.Count);
        Assert.Contains("graphql", template.Info.Tags);
        Assert.Contains("exposure", template.Info.Tags);
    }

    [Fact]
    public void ReadFile_captures_http_method_path_and_body()
    {
        var path = Path.Combine("TestData", "introspection-enabled.yaml");
        var template = NucleiTemplateReader.ReadFile(path);

        var http = Assert.Single(template.Http);
        Assert.Equal("POST", http.Method);
        Assert.Equal("{{BaseURL}}/graphql", Assert.Single(http.Path));
        Assert.Contains("__schema", http.Body, StringComparison.Ordinal);
        Assert.Equal("and", http.MatchersCondition);
    }

    [Fact]
    public void ReadFile_captures_status_and_word_matchers()
    {
        var path = Path.Combine("TestData", "introspection-enabled.yaml");
        var template = NucleiTemplateReader.ReadFile(path);

        var http = template.Http[0];
        Assert.Equal(2, http.Matchers.Count);

        var statusMatcher = http.Matchers.Single(m => m.Type == "status");
        Assert.Equal(200, Assert.Single(statusMatcher.Status));

        var wordMatcher = http.Matchers.Single(m => m.Type == "word");
        Assert.Equal("and", wordMatcher.Condition);
        Assert.Equal("body", wordMatcher.Part);
        Assert.Contains("__schema", wordMatcher.Words);
        Assert.Contains("queryType", wordMatcher.Words);
        Assert.False(wordMatcher.Negative);
    }

    [Fact]
    public void ReadText_returns_empty_template_on_blank_input()
    {
        var template = NucleiTemplateReader.ReadText("");
        Assert.Equal("", template.Id);
        Assert.Empty(template.Http);
    }

    [Fact]
    public void ReadText_parses_payloads_block_into_variable_map()
    {
        var yaml = """
            id: with-payloads
            info:
              name: Payloads
              severity: low
            http:
              - method: GET
                path:
                  - '{{BaseURL}}/{{file}}'
                payloads:
                  file:
                    - "robots.txt"
                    - ".env"
                    - ".git/config"
            """;
        var template = NucleiTemplateReader.ReadText(yaml);
        var http = Assert.Single(template.Http);
        Assert.True(http.Payloads.ContainsKey("file"));
        Assert.Equal(3, http.Payloads["file"].Count);
        Assert.Contains(".env", http.Payloads["file"]);
    }

    [Fact]
    public void ReadText_negative_matcher_flag_round_trips()
    {
        var yaml = """
            id: x
            info:
              name: Negative
              severity: low
            http:
              - method: GET
                path:
                  - '{{BaseURL}}/'
                matchers:
                  - type: word
                    words:
                      - "should-be-absent"
                    negative: true
            """;
        var template = NucleiTemplateReader.ReadText(yaml);
        var matcher = template.Http[0].Matchers[0];
        Assert.True(matcher.Negative);
    }
}
