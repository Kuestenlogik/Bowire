// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Security;
using Kuestenlogik.Bowire.Security.Templates.Nuclei;

namespace Kuestenlogik.Bowire.Security.Templates.Nuclei.Tests;

/// <summary>
/// #491 (#35 Phase 2g) — reading and converting the network:/tcp: and ssl:
/// transport blocks. The executors are covered separately; this is the half
/// that decides what they are handed.
/// </summary>
public sealed class NucleiTransportBlockTests
{
    [Fact]
    public void Reads_A_Network_Block()
    {
        var template = NucleiTemplateReader.ReadText("""
            id: redis-unauth
            info:
              name: Unauthenticated Redis
              severity: high
            network:
              - host:
                  - "{{Host}}:6379"
                inputs:
                  - data: "PING\r\n"
                read-size: 2048
                matchers:
                  - type: word
                    words:
                      - "+PONG"
            """);

        var req = Assert.Single(template.Network);
        Assert.Equal("{{Host}}:6379", Assert.Single(req.Host));
        Assert.Equal(2048, req.ReadSize);
        // A DOUBLE-quoted YAML scalar has already expanded \r\n by the time the
        // reader sees it — the parser does that, not us. So the payload here is
        // a real CR LF, and NetworkProbeExecutor.Unescape is a no-op on it.
        Assert.Equal("PING\r\n", Assert.Single(req.Inputs).Data);
        Assert.Equal("+PONG", Assert.Single(Assert.Single(req.Matchers).Words));
    }

    [Fact]
    public void A_Single_Quoted_Payload_Keeps_Its_Escapes_For_The_Executor()
    {
        // The other half of the same coin, and the reason Unescape exists:
        // single-quoted (and plain) YAML scalars do NOT expand escapes, so the
        // eight literal characters reach the executor and it has to expand
        // them. Sending them as-is would get no answer from a Redis and the
        // probe would then report a wide-open server as clean.
        var template = NucleiTemplateReader.ReadText("""
            id: redis-unauth
            network:
              - host: ["{{Host}}:6379"]
                inputs:
                  - data: 'PING\r\n'
                matchers:
                  - type: word
                    words: ["+PONG"]
            """);

        var data = Assert.Single(Assert.Single(template.Network).Inputs).Data;

        // Eight literal characters, backslashes intact. Expanding them is the
        // executor's job — NetworkProbeExecutorTests.Unescape_* covers that
        // side, and asserting it here would make this project depend on the
        // scanner for no gain.
        Assert.Equal(@"PING\r\n", data);
        Assert.Contains(@"\r\n", data, StringComparison.Ordinal);
    }

    [Fact]
    public void Reads_A_Tcp_Block_Under_Its_Other_Name()
    {
        // Nuclei accepts both spellings and the corpus uses both; a reader that
        // only knew one would skip half the templates without saying so.
        var template = NucleiTemplateReader.ReadText("""
            id: banner
            tcp:
              - host:
                  - "{{Host}}:22"
                matchers:
                  - type: word
                    words:
                      - "SSH-2.0"
            """);

        Assert.Single(template.Network);
        Assert.Equal("{{Host}}:22", Assert.Single(Assert.Single(template.Network).Host));
    }

    [Fact]
    public void Defaults_Read_Size_When_The_Template_Omits_It()
    {
        var template = NucleiTemplateReader.ReadText("""
            id: banner
            network:
              - host: ["{{Host}}:22"]
                matchers:
                  - type: word
                    words: ["SSH"]
            """);

        Assert.Equal(1024, Assert.Single(template.Network).ReadSize);
    }

    [Fact]
    public void Reads_An_Ssl_Block()
    {
        var template = NucleiTemplateReader.ReadText("""
            id: expired-cert
            info:
              name: Expired certificate
              severity: low
            ssl:
              - address: "{{Host}}:{{Port}}"
                matchers:
                  - type: word
                    words:
                      - "expired: true"
            """);

        var req = Assert.Single(template.Ssl);
        Assert.Equal("{{Host}}:{{Port}}", req.Address);
        Assert.Equal("expired: true", Assert.Single(Assert.Single(req.Matchers).Words));
    }

    [Fact]
    public void Converts_A_Network_Template_Into_An_Executable_Step()
    {
        // Service/Messages/Metadata is the contract NetworkProbeExecutor reads
        // back; nothing else in the pipeline restates it.
        var template = NucleiTemplateReader.ReadText("""
            id: redis-unauth
            network:
              - host: ["{{Host}}:6379"]
                inputs:
                  - data: "PING\r\n"
                read-size: 2048
                matchers:
                  - type: word
                    words: ["+PONG"]
            """);

        var recording = NucleiTemplateConverter.ToBowireRecording(template);

        Assert.Equal(new List<string> { "network" }, recording.Vulnerability!.Protocols);
        var step = Assert.Single(recording.Steps);
        Assert.Equal("network", step.Protocol);
        Assert.Equal("{{Host}}:6379", step.Service);
        Assert.Equal("TCP", step.Method);
        Assert.Equal("PING\r\n", Assert.Single(step.Messages));
        Assert.Equal("2048", step.Metadata!["read-size"]);
        Assert.NotNull(recording.VulnerableWhen);
        Assert.Equal("+PONG", recording.VulnerableWhen!.BodyContains);
    }

    [Fact]
    public void Marks_A_Hex_Input_So_The_Executor_Can_Tell()
    {
        var template = NucleiTemplateReader.ReadText("""
            id: hex-probe
            network:
              - host: ["{{Host}}:9000"]
                inputs:
                  - data: "50494e47"
                    type: hex
                matchers:
                  - type: word
                    words: ["ok"]
            """);

        var step = Assert.Single(NucleiTemplateConverter.ToBowireRecording(template).Steps);

        Assert.Equal("hex:50494e47", Assert.Single(step.Messages));
    }

    [Fact]
    public void Converts_An_Ssl_Template_Into_An_Executable_Step()
    {
        var template = NucleiTemplateReader.ReadText("""
            id: expired-cert
            ssl:
              - address: "{{Host}}:{{Port}}"
                matchers:
                  - type: word
                    words: ["expired: true"]
            """);

        var recording = NucleiTemplateConverter.ToBowireRecording(template);

        Assert.Equal(new List<string> { "ssl" }, recording.Vulnerability!.Protocols);
        var step = Assert.Single(recording.Steps);
        Assert.Equal("ssl", step.Protocol);
        Assert.Equal("{{Host}}:{{Port}}", step.Service);
        Assert.Equal("TLS", step.Method);
        Assert.Empty(step.Messages);
    }

    [Fact]
    public void Http_Still_Wins_When_A_Template_Carries_Both()
    {
        // Picking a fixed order keeps a hybrid template deterministic instead
        // of depending on which block the reader happened to fill first.
        var template = NucleiTemplateReader.ReadText("""
            id: hybrid
            http:
              - method: GET
                path: ["{{BaseURL}}/x"]
                matchers:
                  - type: status
                    status: [200]
            ssl:
              - address: "{{Host}}:443"
                matchers:
                  - type: word
                    words: ["expired: true"]
            """);

        var recording = NucleiTemplateConverter.ToBowireRecording(template);

        Assert.Equal("http", Assert.Single(recording.Steps).Protocol);
        Assert.Equal(200, recording.VulnerableWhen!.Status);
    }

    [Theory]
    [InlineData("data")]
    [InlineData("raw")]
    [InlineData("all")]
    public void Network_Matchers_Accept_The_Whole_Response_Parts(string part)
    {
        var matcher = new NucleiMatcher { Type = "word", Part = part };
        matcher.Words.Add("+PONG");

        var predicate = NucleiMatcherTranslator.Translate(
            [matcher], "or", NucleiMatcherSurface.Network);

        Assert.NotNull(predicate);
        Assert.Equal("+PONG", predicate.BodyContains);
    }

    [Fact]
    public void Fqdn_Resolves_So_Dns_Templates_Can_Actually_Run()
    {
        // Every dns: template in the projectdiscovery corpus addresses
        // {{FQDN}}. Until this resolved, the DNS transport could not run one of
        // them — it shipped "done" and was inert against real templates. A
        // corpus run found that; no unit test in this file would have.
        var template = NucleiTemplateReader.ReadText("""
            id: caa-fingerprint
            dns:
              - name: "{{FQDN}}"
                type: CAA
                matchers:
                  - type: word
                    words: ["issue"]
            """);
        var context = NucleiVariableContext.FromTarget("https://example.com");

        var step = Assert.Single(NucleiTemplateConverter.ToBowireRecording(template, context).Steps);

        Assert.Equal("example.com", step.Service);
        Assert.DoesNotContain("{{", step.Service, StringComparison.Ordinal);
    }

    [Fact]
    public void Fqdn_Resolves_Inside_A_Prefixed_Name()
    {
        // Several templates prefix it, e.g. _acme-challenge.{{FQDN}}.
        var template = NucleiTemplateReader.ReadText("""
            id: acme-challenge-detect
            dns:
              - name: "_acme-challenge.{{FQDN}}"
                type: TXT
                matchers:
                  - type: word
                    words: ["token"]
            """);
        var context = NucleiVariableContext.FromTarget("https://example.com");

        var step = Assert.Single(NucleiTemplateConverter.ToBowireRecording(template, context).Steps);

        Assert.Equal("_acme-challenge.example.com", step.Service);
    }

    [Fact]
    public void An_Internal_Matcher_Is_Not_A_Finding_Condition()
    {
        // Regression for a CRITICAL false positive found by running the real
        // corpus: CVE-2018-0171's only tcp matcher is `internal: true` with
        // `words: [""]`. Both halves independently make it fire against any
        // reachable port, and it reported critical against a plain HTTP file
        // server on localhost.
        var matcher = new NucleiMatcher { Type = "word", Part = "raw", Internal = true };
        matcher.Words.Add("anything");

        Assert.Null(NucleiMatcherTranslator.Translate(
            [matcher], "or", NucleiMatcherSurface.Network));
    }

    [Fact]
    public void An_Empty_Word_Does_Not_Become_A_Predicate_That_Always_Fires()
    {
        // AttackPredicateEvaluator reads an empty BodyContains as "no
        // constraint", so translating `words: [""]` yields a predicate true
        // against every target.
        var matcher = new NucleiMatcher { Type = "word", Part = "raw" };
        matcher.Words.Add("");

        Assert.Null(NucleiMatcherTranslator.Translate(
            [matcher], "or", NucleiMatcherSurface.Network));
    }

    [Fact]
    public void An_Empty_Word_Is_Dropped_But_Real_Ones_Survive()
    {
        var matcher = new NucleiMatcher { Type = "word", Part = "raw" };
        matcher.Words.Add("");
        matcher.Words.Add("+PONG");

        var predicate = NucleiMatcherTranslator.Translate(
            [matcher], "or", NucleiMatcherSurface.Network);

        Assert.NotNull(predicate);
        Assert.Equal("+PONG", predicate.BodyContains);
    }

    [Fact]
    public void The_Cve_2018_0171_Template_Now_Yields_No_Predicate()
    {
        // The whole shape, end to end: internal matcher, empty word, explicit
        // port. Nothing about it may produce a verdict.
        var template = NucleiTemplateReader.ReadText("""
            id: CVE-2018-0171
            info:
              name: Cisco Smart Install - Configuration Download
              severity: critical
            tcp:
              - inputs:
                  - data: "0000000100000001"
                    type: hex
                host:
                  - "{{Hostname}}"
                port: 4786
                matchers:
                  - type: word
                    part: raw
                    words:
                      - ""
                    internal: true
            """);

        var recording = NucleiTemplateConverter.ToBowireRecording(template);

        Assert.Null(recording.VulnerableWhen);
    }

    [Fact]
    public void An_Explicit_Port_Overrides_The_One_Carried_By_The_Host()
    {
        // {{Hostname}} resolves to the scan target's port; a template pinned to
        // 4786 means 4786. Probing the wrong service and judging it with the
        // right template is how false positives are made.
        var template = NucleiTemplateReader.ReadText("""
            id: smart-install
            network:
              - host: ["{{Hostname}}"]
                port: 4786
                matchers:
                  - type: word
                    words: ["marker"]
            """);
        var context = NucleiVariableContext.FromTarget("http://127.0.0.1:8099");

        var step = Assert.Single(NucleiTemplateConverter.ToBowireRecording(template, context).Steps);

        Assert.Equal("127.0.0.1:4786", step.Service);
    }

    [Theory]
    [InlineData("issuer")]
    [InlineData("subject")]
    [InlineData("serial")]
    public void Ssl_Matchers_Refuse_A_Single_Field_Part(string part)
    {
        // The certificate renders into one body, so honouring `part: issuer`
        // would let its words match a subject that happens to contain them.
        var matcher = new NucleiMatcher { Type = "word", Part = part };
        matcher.Words.Add("Let's Encrypt");

        Assert.Null(NucleiMatcherTranslator.Translate(
            [matcher], "or", NucleiMatcherSurface.Ssl));
    }
}
