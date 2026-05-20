// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Mocking;
using Kuestenlogik.Bowire.Security.Templates.Nuclei;

namespace Kuestenlogik.Bowire.Security.Templates.Nuclei.Tests;

/// <summary>
/// Coverage for the multi-path + payload-matrix unfolder
/// (<see cref="NucleiTemplateConverter.ToBowireRecordings"/>).
/// Verifies the cross-product expansion + the id-suffix contract +
/// the payload-placeholder substitution in path + body.
/// </summary>
public sealed class NucleiTemplateUnfoldTests
{
    [Fact]
    public void Single_path_single_payload_collapses_to_one_recording()
    {
        var template = MakeTemplate(paths: ["{{BaseURL}}/admin"]);
        var recordings = NucleiTemplateConverter.ToBowireRecordings(template);
        var only = Assert.Single(recordings);
        Assert.Equal(template.Id, only.Id); // no suffix when nothing to expand
        Assert.Equal("{{BaseURL}}/admin", only.Steps[0].HttpPath);
    }

    [Fact]
    public void Multi_path_emits_one_recording_per_path_with_suffix()
    {
        var template = MakeTemplate(paths: [
            "{{BaseURL}}/admin",
            "{{BaseURL}}/administrator",
            "{{BaseURL}}/admin.php",
        ]);
        var recordings = NucleiTemplateConverter.ToBowireRecordings(template);

        Assert.Equal(3, recordings.Count);
        Assert.Equal("test-template#p0#r0", recordings[0].Id);
        Assert.Equal("test-template#p1#r0", recordings[1].Id);
        Assert.Equal("test-template#p2#r0", recordings[2].Id);
        Assert.Equal("{{BaseURL}}/admin", recordings[0].Steps[0].HttpPath);
        Assert.Equal("{{BaseURL}}/administrator", recordings[1].Steps[0].HttpPath);
        Assert.Equal("{{BaseURL}}/admin.php", recordings[2].Steps[0].HttpPath);
    }

    [Fact]
    public void Single_variable_payload_emits_one_recording_per_value()
    {
        var template = MakeTemplate(
            paths: ["{{BaseURL}}/{{file}}"],
            payloads: new() { ["file"] = ["robots.txt", ".env", ".git/config"] });

        var recordings = NucleiTemplateConverter.ToBowireRecordings(template);

        Assert.Equal(3, recordings.Count);
        Assert.Equal("{{BaseURL}}/robots.txt", recordings[0].Steps[0].HttpPath);
        Assert.Equal("{{BaseURL}}/.env", recordings[1].Steps[0].HttpPath);
        Assert.Equal("{{BaseURL}}/.git/config", recordings[2].Steps[0].HttpPath);
        Assert.Equal("test-template#p0#r0", recordings[0].Id);
        Assert.Equal("test-template#p0#r2", recordings[2].Id);
    }

    [Fact]
    public void Multi_variable_payload_emits_cross_product()
    {
        var template = MakeTemplate(
            paths: ["{{BaseURL}}/login.{{ext}}"],
            payloads: new()
            {
                ["ext"] = ["php", "aspx"],
                ["username"] = ["admin", "root", "guest"],
            },
            body: "{\"user\":\"{{username}}\"}");

        var recordings = NucleiTemplateConverter.ToBowireRecordings(template);

        // 2 ext × 3 username = 6 rows
        Assert.Equal(6, recordings.Count);

        // Every recording should have neither var unresolved in path
        // (only the ext lands there) nor in body (only the username).
        foreach (var r in recordings)
        {
            Assert.DoesNotContain("{{ext}}", r.Steps[0].HttpPath!, StringComparison.Ordinal);
            Assert.DoesNotContain("{{username}}", r.Steps[0].Body!, StringComparison.Ordinal);
        }

        var paths = recordings.Select(r => r.Steps[0].HttpPath).Distinct().ToList();
        Assert.Equal(new HashSet<string?>
        {
            "{{BaseURL}}/login.php",
            "{{BaseURL}}/login.aspx",
        }, paths.ToHashSet());
    }

    [Fact]
    public void Path_payload_cross_product_multiplies_paths_and_rows()
    {
        var template = MakeTemplate(
            paths: ["{{BaseURL}}/{{file}}", "{{BaseURL}}/api/{{file}}"],
            payloads: new() { ["file"] = ["env", "log"] });

        var recordings = NucleiTemplateConverter.ToBowireRecordings(template);

        // 2 paths × 2 payload-rows = 4 recordings
        Assert.Equal(4, recordings.Count);
    }

    [Fact]
    public void Empty_payloads_dict_is_treated_as_no_payload_dimension()
    {
        // Payloads map with no entries — equivalent to no payloads at all.
        // Same shape as the single-path single-payload case.
        var template = MakeTemplate(paths: ["{{BaseURL}}/x"], payloads: new());
        var recordings = NucleiTemplateConverter.ToBowireRecordings(template);
        Assert.Single(recordings);
        Assert.Equal(template.Id, recordings[0].Id);
    }

    [Fact]
    public void Template_without_http_block_still_produces_one_recording()
    {
        // Guard against a future template-load error path — the caller
        // gets a recording back so the result-list never silently
        // shrinks to zero.
        var template = new NucleiTemplate
        {
            Id = "no-http",
            Info = new NucleiInfo { Name = "Empty", Severity = "info" },
        };
        var recordings = NucleiTemplateConverter.ToBowireRecordings(template);
        Assert.Single(recordings);
    }

    [Fact]
    public void Plural_unfold_passes_variable_context_through_to_singular()
    {
        var template = MakeTemplate(
            paths: ["{{BaseURL}}/admin", "{{BaseURL}}/login"]);
        var ctx = NucleiVariableContext.FromTarget("https://api.example.com");
        var recordings = NucleiTemplateConverter.ToBowireRecordings(template, ctx);

        Assert.Equal("https://api.example.com/admin", recordings[0].Steps[0].HttpPath);
        Assert.Equal("https://api.example.com/login", recordings[1].Steps[0].HttpPath);
    }

    private static NucleiTemplate MakeTemplate(
        List<string>? paths = null,
        Dictionary<string, List<string>>? payloads = null,
        string body = "")
    {
        var template = new NucleiTemplate
        {
            Id = "test-template",
            Info = new NucleiInfo { Name = "Test", Severity = "low" },
        };
        var http = new NucleiHttpRequest { Method = "GET", Body = body };
        foreach (var p in paths ?? []) http.Path.Add(p);
        if (payloads is not null)
        {
            foreach (var (k, v) in payloads) http.Payloads[k] = v;
        }
        template.Http.Add(http);
        return template;
    }
}
