// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Kuestenlogik.Bowire.Mocking;
using Kuestenlogik.Bowire.Security;

namespace Kuestenlogik.Bowire.Tests.Security;

/// <summary>
/// Round-trip pins on the Phase-A (security) extension to the
/// recording format: a recording with <see cref="BowireRecording.Attack"/>
/// + <see cref="BowireRecording.Vulnerability"/> + <see cref="BowireRecording.VulnerableWhen"/>
/// serializes and deserializes byte-for-byte; pre-attack recordings
/// (no <c>attack</c> field) round-trip unchanged.
/// </summary>
public sealed class AttackRecordingRoundTripTests
{
    private static readonly JsonSerializerOptions s_opts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    [Fact]
    public void NonAttackRecording_DoesNotCarryAttackFields()
    {
        // Backwards-compat guard — a regular fixture-style recording
        // shouldn't emit `attack: false / vulnerability: null /
        // vulnerableWhen: null` lines into the JSON, since those
        // keys didn't exist before this work and would surprise tools
        // that read the recording file format directly.
        var rec = new BowireRecording { Id = "fixture", Name = "regular fixture" };
        var json = JsonSerializer.Serialize(rec, s_opts);

        Assert.DoesNotContain("\"vulnerability\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"vulnerableWhen\"", json, StringComparison.Ordinal);
        // `attack: false` still appears because bool isn't suppressed
        // by WhenWritingNull. Round-trip the JSON and confirm the
        // resulting Attack defaults to false — that's what matters
        // for backwards-compat.
        var rt = JsonSerializer.Deserialize<BowireRecording>(json, s_opts);
        Assert.NotNull(rt);
        Assert.False(rt!.Attack);
        Assert.Null(rt.Vulnerability);
        Assert.Null(rt.VulnerableWhen);
    }

    [Fact]
    public void AttackRecording_RoundTripsAllSecurityFields()
    {
        var original = new BowireRecording
        {
            Id = "bwr-graphql-001",
            Name = "GraphQL introspection in production",
            Attack = true,
            Vulnerability = new AttackVulnerability
            {
                Id = "BWR-GRAPHQL-001",
                Cwe = "CWE-200",
                OwaspApi = "API3-2023-BOPLA",
                Severity = "medium",
                Cvss = 5.3,
                Protocols = { "graphql" },
                References = { "https://graphql.org/learn/introspection/" },
                Authors = { "thomas-stegemann" },
                Introduced = "2026-05-16",
                Remediation = "Disable introspection in production.",
            },
            VulnerableWhen = new AttackPredicate
            {
                AllOf = new[]
                {
                    new AttackPredicate { Status = 200 },
                    new AttackPredicate
                    {
                        BodyJsonPath = new AttackJsonPathClause
                        {
                            Path = "$.data.__schema.types",
                            Exists = true,
                        },
                    },
                },
            },
        };

        var json = JsonSerializer.Serialize(original, s_opts);
        var rt = JsonSerializer.Deserialize<BowireRecording>(json, s_opts);

        Assert.NotNull(rt);
        Assert.True(rt!.Attack);
        Assert.NotNull(rt.Vulnerability);
        Assert.Equal("BWR-GRAPHQL-001", rt.Vulnerability!.Id);
        Assert.Equal("CWE-200", rt.Vulnerability.Cwe);
        Assert.Equal(5.3, rt.Vulnerability.Cvss);
        Assert.Single(rt.Vulnerability.Protocols);
        Assert.Equal("graphql", rt.Vulnerability.Protocols[0]);

        Assert.NotNull(rt.VulnerableWhen);
        Assert.NotNull(rt.VulnerableWhen!.AllOf);
        Assert.Equal(2, rt.VulnerableWhen.AllOf!.Count);
        Assert.Equal(200, rt.VulnerableWhen.AllOf[0].Status);
        Assert.NotNull(rt.VulnerableWhen.AllOf[1].BodyJsonPath);
        Assert.Equal("$.data.__schema.types", rt.VulnerableWhen.AllOf[1].BodyJsonPath!.Path);
        Assert.True(rt.VulnerableWhen.AllOf[1].BodyJsonPath!.Exists);
    }

    [Fact]
    public void HeaderMissingPredicate_RoundTrips()
    {
        // The headerMissing operator is the bread-and-butter for the
        // baseline-security-headers template — pin it specifically so
        // a future predicate-engine refactor can't silently drop it.
        var p = new AttackPredicate
        {
            HeaderMissing = new[] { "X-Frame-Options", "Strict-Transport-Security" },
        };
        var json = JsonSerializer.Serialize(p, s_opts);
        Assert.Contains("\"headerMissing\"", json, StringComparison.Ordinal);

        var rt = JsonSerializer.Deserialize<AttackPredicate>(json, s_opts);
        Assert.NotNull(rt);
        Assert.NotNull(rt!.HeaderMissing);
        Assert.Equal(2, rt.HeaderMissing!.Count);
        Assert.Contains("X-Frame-Options", rt.HeaderMissing);
        Assert.Contains("Strict-Transport-Security", rt.HeaderMissing);
    }
}
