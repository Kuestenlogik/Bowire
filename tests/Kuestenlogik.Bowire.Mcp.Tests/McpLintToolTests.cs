// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Kuestenlogik.Bowire.Mcp;
using Kuestenlogik.Bowire.Recording;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Kuestenlogik.Bowire.Mcp.Tests;

/// <summary>
/// <c>bowire.lint</c> — the design-time linter as an MCP tool.
/// </summary>
/// <remarks>
/// <para>
/// Two things separate it from the CLI and the rail that share its engine.
/// It reaches a URL, so the allowlist gates it before anything is sent; and
/// its answer is parsed by an agent rather than read by a person, which makes
/// the envelope the contract — <c>findings</c> and <c>summary</c> have to be
/// there even when the discovery found nothing, or the agent has to guess
/// whether the call failed.
/// </para>
/// <para>
/// The target is <c>127.0.0.1:1</c>, a port nothing listens on. Every probe
/// refuses immediately, so this exercises the whole path — allowlist, probe,
/// linter, envelope — without a packet leaving the machine.
/// </para>
/// </remarks>
[Collection(nameof(BowireConfigFixture))]
public sealed class McpLintToolTests : IAsyncDisposable
{
    private const string DeadTarget = "http://127.0.0.1:1";

    private readonly List<BowireMockHandleRegistry> _registries = [];

    public async ValueTask DisposeAsync()
    {
        foreach (var r in _registries) await r.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    private BowireMcpTools Tools(params string[] allowed)
    {
        var handles = new BowireMockHandleRegistry();
        _registries.Add(handles);
        var options = new BowireMcpOptions
        {
            LoadAllowlistFromEnvironments = false,
            LoadAllowlistFromTypedUrls = false,
        };
        foreach (var url in allowed) options.AllowedServerUrls.Add(url);
        return new BowireMcpTools(
            new BowireProtocolRegistry(),
            handles,
            new BowireMcpConfirmationStore(),
            new BowireRecordingSession(),
            Options.Create(options),
            NullLogger<BowireMcpTools>.Instance);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_Url_That_Is_Not_On_The_Allowlist_Is_Refused_Before_Anything_Is_Sent()
    {
        // The gate that makes this tool safe to hand an agent: it may lint
        // where the operator has already pointed Bowire, nowhere else.
        var result = await Tools().Lint(DeadTarget, ct: Ct);

        Assert.Contains("not on the allowlist", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_Refusal_Says_How_An_Operator_Would_Widen_It()
    {
        // An agent that hits this cannot fix it — the human reading the
        // transcript can, and only if the message names the two ways.
        var result = await Tools().Lint(DeadTarget, ct: Ct);

        Assert.Contains("environments.json", result, StringComparison.Ordinal);
        Assert.Contains("--allow-arbitrary-urls", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_Allowed_Url_That_Answers_Nothing_Still_Returns_The_Envelope()
    {
        // Discovery found no services, which is not an error — it is a lint
        // result of zero findings. An agent branches on the shape, so the
        // shape has to be there either way.
        var json = await Tools(DeadTarget).Lint(DeadTarget, ct: Ct);

        using var doc = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Array, doc.RootElement.GetProperty("findings").ValueKind);
        Assert.True(doc.RootElement.TryGetProperty("summary", out _));
    }

    [Fact]
    public async Task The_Summary_Carries_Every_Severity_Counter()
    {
        // The rail and the CLI both render these five; an agent asked to
        // "report anything high" indexes `summary.high` directly.
        var json = await Tools(DeadTarget).Lint(DeadTarget, ct: Ct);

        using var doc = JsonDocument.Parse(json);
        var summary = doc.RootElement.GetProperty("summary");
        foreach (var counter in SeverityCounters)
        {
            Assert.True(summary.TryGetProperty(counter, out _), $"summary.{counter} missing");
        }
    }

    // A field rather than an inline literal: CA1861 refuses a constant array
    // argument passed from a method that runs repeatedly.
    private static readonly string[] SeverityCounters = ["total", "high", "medium", "low", "info"];

    [Fact]
    public async Task Nothing_Found_Counts_As_Zero_Rather_Than_Missing()
    {
        var json = await Tools(DeadTarget).Lint(DeadTarget, ct: Ct);

        using var doc = JsonDocument.Parse(json);
        Assert.Equal(0, doc.RootElement.GetProperty("summary").GetProperty("total").GetInt32());
        Assert.Empty(doc.RootElement.GetProperty("findings").EnumerateArray());
    }

    [Fact]
    public async Task Pinning_A_Protocol_Narrows_The_Probe_Without_Changing_The_Shape()
    {
        // `protocol` is a hint for the discovery half; whatever it does to the
        // probe, the answer an agent parses stays the same.
        var json = await Tools(DeadTarget).Lint(DeadTarget, protocol: "rest", ct: Ct);

        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("findings", out _));
        Assert.True(doc.RootElement.TryGetProperty("summary", out _));
    }

    [Fact]
    public async Task A_Blank_Protocol_Is_Treated_As_No_Hint()
        // `--protocol ""` reaching the tool from a script must not look up a
        // plugin with an empty id.
        => Assert.Contains("summary",
            await Tools(DeadTarget).Lint(DeadTarget, protocol: "   ", ct: Ct),
            StringComparison.Ordinal);
}
