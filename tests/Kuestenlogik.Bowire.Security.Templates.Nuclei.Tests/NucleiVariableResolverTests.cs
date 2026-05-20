// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Security.Templates.Nuclei;

namespace Kuestenlogik.Bowire.Security.Templates.Nuclei.Tests;

/// <summary>
/// Variable-substitution tests. Covers the canonical Nuclei
/// placeholder surface (BaseURL, Hostname, Host, Port, Path) plus
/// the random helpers (RandStr, RandStr_N, RandInt) — including
/// the per-template memoisation contract where the same
/// placeholder name reused inside one template resolves to the
/// same value.
/// </summary>
public sealed class NucleiVariableResolverTests
{
    [Fact]
    public void Resolves_BaseURL_against_default_port_target()
    {
        var ctx = NucleiVariableContext.FromTarget("https://api.example.com");
        var output = NucleiVariableResolver.Resolve("{{BaseURL}}/admin", ctx);
        Assert.Equal("https://api.example.com/admin", output);
    }

    [Fact]
    public void Resolves_BaseURL_preserves_explicit_non_default_port()
    {
        var ctx = NucleiVariableContext.FromTarget("https://api.example.com:8443/v2");
        var output = NucleiVariableResolver.Resolve("{{BaseURL}}/login", ctx);
        Assert.Equal("https://api.example.com:8443/v2/login", output);
    }

    [Fact]
    public void Resolves_BaseURL_drops_default_http_port_80()
    {
        var ctx = NucleiVariableContext.FromTarget("http://example.com:80");
        Assert.Equal("http://example.com", ctx.BaseUrl);
    }

    [Fact]
    public void Resolves_Hostname_Host_Port_Path()
    {
        var ctx = NucleiVariableContext.FromTarget("https://api.example.com:8443/v2");
        Assert.Equal("api.example.com:8443", ctx.Hostname);
        Assert.Equal("api.example.com", ctx.Host);
        Assert.Equal("8443", ctx.Port);
        Assert.Equal("/v2", ctx.Path);

        var output = NucleiVariableResolver.Resolve(
            "Host: {{Hostname}} | Server: {{Host}} | Port: {{Port}} | Path: {{Path}}",
            ctx);
        Assert.Equal("Host: api.example.com:8443 | Server: api.example.com | Port: 8443 | Path: /v2", output);
    }

    [Fact]
    public void RandStr_default_length_is_8_chars()
    {
        var ctx = NucleiVariableContext.FromTarget("https://x", seed: 42);
        var output = NucleiVariableResolver.Resolve("{{RandStr}}", ctx);
        Assert.Equal(8, output.Length);
        Assert.Matches("^[A-Za-z0-9]+$", output);
    }

    [Fact]
    public void RandStr_with_N_uses_requested_length()
    {
        var ctx = NucleiVariableContext.FromTarget("https://x", seed: 42);
        var output = NucleiVariableResolver.Resolve("{{RandStr_16}}", ctx);
        Assert.Equal(16, output.Length);
    }

    [Fact]
    public void Same_RandStr_placeholder_twice_resolves_to_same_value()
    {
        // Nuclei memoises per-template: `prefix-{{RandStr}}-suffix-{{RandStr}}`
        // sees the same string both times. Critical for templates that
        // round-trip a generated token through a request-response cycle.
        var ctx = NucleiVariableContext.FromTarget("https://x", seed: 42);
        var output = NucleiVariableResolver.Resolve("prefix-{{RandStr}}-suffix-{{RandStr}}", ctx);
        var halves = output.Split('-');
        Assert.Equal(4, halves.Length); // prefix, randstr, suffix, randstr
        Assert.Equal(halves[1], halves[3]);
    }

    [Fact]
    public void Distinct_RandStr_lengths_resolve_to_distinct_caches()
    {
        // {{RandStr_8}} and {{RandStr_16}} are different placeholder
        // names — different cache slots, no shared value.
        var ctx = NucleiVariableContext.FromTarget("https://x", seed: 42);
        var output = NucleiVariableResolver.Resolve("{{RandStr_8}}|{{RandStr_16}}", ctx);
        var halves = output.Split('|');
        Assert.NotEqual(halves[0], halves[1]);
    }

    [Fact]
    public void RandInt_default_is_6_digits()
    {
        var ctx = NucleiVariableContext.FromTarget("https://x", seed: 42);
        var output = NucleiVariableResolver.Resolve("{{RandInt}}", ctx);
        Assert.Equal(6, output.Length);
        Assert.Matches("^[1-9][0-9]{5}$", output);
    }

    [Fact]
    public void Seed_makes_substitution_reproducible()
    {
        var ctx1 = NucleiVariableContext.FromTarget("https://x", seed: 42);
        var ctx2 = NucleiVariableContext.FromTarget("https://x", seed: 42);
        var output1 = NucleiVariableResolver.Resolve("{{RandStr}}-{{RandInt}}", ctx1);
        var output2 = NucleiVariableResolver.Resolve("{{RandStr}}-{{RandInt}}", ctx2);
        Assert.Equal(output1, output2);
    }

    [Fact]
    public void Unknown_placeholder_passes_through_untouched()
    {
        // If we ever land on a template with an unsupported
        // placeholder ({{md5(BaseURL)}}, {{interactsh-url}}, …)
        // the raw {{…}} must survive so the operator notices in
        // the SARIF / log output rather than us silently
        // producing a wrong URL.
        var ctx = NucleiVariableContext.FromTarget("https://x", seed: 42);
        var output = NucleiVariableResolver.Resolve("path-with-{{unknown}}-placeholder", ctx);
        Assert.Equal("path-with-{{unknown}}-placeholder", output);
    }

    [Fact]
    public void Empty_input_passes_through_untouched()
    {
        var ctx = NucleiVariableContext.FromTarget("https://x");
        Assert.Equal("", NucleiVariableResolver.Resolve("", ctx));
    }

    [Fact]
    public void Converter_with_context_substitutes_path_and_body()
    {
        var template = new NucleiTemplate
        {
            Id = "test",
            Info = new NucleiInfo { Name = "T", Severity = "info" },
        };
        template.Http.Add(new NucleiHttpRequest
        {
            Method = "POST",
            Path = { "{{BaseURL}}/admin" },
            Body = "{\"callback\":\"{{BaseURL}}/cb\"}",
        });

        var ctx = NucleiVariableContext.FromTarget("https://api.example.com");
        var recording = NucleiTemplateConverter.ToBowireRecording(template, ctx);

        var step = Assert.Single(recording.Steps);
        Assert.Equal("https://api.example.com/admin", step.HttpPath);
        Assert.Equal("{\"callback\":\"https://api.example.com/cb\"}", step.Body);
    }

    [Fact]
    public void ResolveVariables_walks_existing_recording_and_substitutes_in_place()
    {
        // Phase 2e flow: corpus loaded ahead of target binding,
        // recording carries placeholders, scanner calls
        // ResolveVariables right before probe-execute.
        var template = new NucleiTemplate
        {
            Id = "t",
            Info = new NucleiInfo { Name = "T", Severity = "info" },
        };
        template.Http.Add(new NucleiHttpRequest
        {
            Method = "GET",
            Path = { "{{BaseURL}}/x" },
        });
        var recording = NucleiTemplateConverter.ToBowireRecording(template);
        Assert.Contains("{{BaseURL}}", recording.Steps[0].HttpPath!, StringComparison.Ordinal);

        var ctx = NucleiVariableContext.FromTarget("https://post-load.example.com");
        NucleiTemplateConverter.ResolveVariables(recording, ctx);
        Assert.Equal("https://post-load.example.com/x", recording.Steps[0].HttpPath);
        Assert.Equal("GET https://post-load.example.com/x", recording.Steps[0].Method);
    }
}
