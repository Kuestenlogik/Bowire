// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Kuestenlogik.Bowire.IntegrationTests;

/// <summary>
/// #587 — the rollup endpoint's trust boundary.
/// <para>
/// The paths arrive in a POST body. Where the workbench is embedded via
/// <c>MapBowire()</c> that body is attacker-controlled, so an unconfined
/// rollup over <c>/</c> would walk the host, read every JSON/SARIF/XML it can
/// open, and hand back the paths together with whatever parsed out of them.
/// CodeQL flagged the reader's three filesystem calls as path injection and
/// was right to; these pin the fix so it cannot regress quietly.
/// </para>
/// </summary>
[Collection("CwdSerialised")]
public sealed class BowireReportRollupEndpointTests
{
    // CA1861: the analyser wants constant array arguments hoisted out of
    // repeated calls rather than allocated per invocation.
    private static readonly string[] EscapingAndValid = [".bowire", "/etc"];
    private static readonly string[] WorkspaceRelative = ["reports"];

    [Theory]
    // Absolute paths on both platform shapes — the direct read-anything case.
    [InlineData("/etc")]
    [InlineData("C:\\Windows")]
    // Traversal, including the form that only escapes after normalisation.
    [InlineData("../..")]
    [InlineData(".bowire/../../..")]
    public async Task Rollup_RejectsPathsOutsideTheWorkspace(string path)
    {
        await using var host = await CreateHost();
        var client = host.GetTestClient();

        var resp = await client.PostAsJsonAsync(
            new Uri("/bowire/api/report/rollup", UriKind.Relative),
            new { from = new[] { path } },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);

        // Read the decoded field rather than the raw body: a Windows path
        // arrives JSON-escaped ("C:\\Windows"), so a substring check against
        // the raw text fails for a message that is in fact correct.
        var payload = await resp.Content.ReadFromJsonAsync<ErrorResponse>(TestContext.Current.CancellationToken);
        Assert.NotNull(payload);
        // The offending entry is named: the caller is usually the workbench's
        // own path field, and a bare 400 leaves the operator guessing which of
        // several paths was objected to.
        Assert.Contains(path, payload!.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rollup_RejectsTheWholeRequest_WhenAnyPathEscapes()
    {
        // Partial success would be worse than refusal: the caller would get a
        // rollup that silently omits one of the paths they asked for.
        await using var host = await CreateHost();
        var client = host.GetTestClient();

        var resp = await client.PostAsJsonAsync(
            new Uri("/bowire/api/report/rollup", UriKind.Relative),
            new { from = EscapingAndValid },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Rollup_AcceptsAWorkspaceRelativePath()
    {
        // The confinement must not break the normal case, which is exactly
        // what the workbench's Rollup rail sends.
        await using var host = await CreateHost();
        var client = host.GetTestClient();

        var resp = await client.PostAsJsonAsync(
            new Uri("/bowire/api/report/rollup", UriKind.Relative),
            new { from = WorkspaceRelative },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task Rollup_AcceptsAnEmptyBody()
    {
        // No paths means the workspace's own .bowire folder — the default the
        // endpoint documents, and it must survive the confinement unchanged.
        await using var host = await CreateHost();
        var client = host.GetTestClient();

        var resp = await client.PostAsJsonAsync(
            new Uri("/bowire/api/report/rollup", UriKind.Relative),
            new { },
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    private static async Task<WebApplication> CreateHost()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();

        var app = builder.Build();
        app.MapBowire("/bowire");

        await app.StartAsync(TestContext.Current.CancellationToken);
        return app;
    }

    /// <summary>The endpoint's error shape; camelCase on the wire.</summary>
    private sealed record ErrorResponse(string Error);
}
