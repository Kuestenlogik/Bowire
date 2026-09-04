// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using Kuestenlogik.Bowire.Protocol.WebSocket;
using Kuestenlogik.Bowire.Security.Scanner;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Kuestenlogik.Bowire.IntegrationTests;

/// <summary>
/// The WebSocket authorization probe against real Kestrel servers
/// (Kuestenlogik/Bowire.VulnDb#23).
/// </summary>
/// <remarks>
/// <para>
/// The finding this probe exists for is the one that survives every "is it
/// public?" check: a handler that establishes <em>who</em> the caller is and
/// then never asks what they are entitled to. Both servers below refuse
/// anonymous upgrades, so <see cref="WebSocketAuthProbe"/> would call each of
/// them healthy; they differ only in whether they look at <em>which</em>
/// credential arrived.
/// </para>
/// <para>
/// The verdict needs three observations, so the servers have to make all three
/// available — anonymous refused, A accepted, and then B either accepted (the
/// defect) or refused (the healthy case). A stub returning canned answers would
/// prove the comparison logic and not that the probe reads a real handshake.
/// </para>
/// </remarks>
public sealed class WebSocketAuthorizationProbeIntegrationTests
{
    private const string TokenA = "Bearer alice-token";
    private const string TokenB = "Bearer bob-token";

    [Fact]
    public async Task AuthenticatesButDoesNotAuthorize_IsReported()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await PluginTestHost.StartAsync(MapAnyCredentialAccepted);
        var wsUrl = WsUrl(host, "/ws/terminal");

        var findings = await new WebSocketAuthorizationProbe().RunAsync(
            wsUrl, new BowireWebSocketProtocol(),
            [$"Authorization: {TokenA}"], [$"Authorization: {TokenB}"], ct);

        var f = Assert.Single(findings);
        Assert.Equal(ScanFindingStatus.Vulnerable, f.Status);
        Assert.Equal("BWR-OWASP-API5-WS-NOAUTHZ", f.Template.Recording.Vulnerability?.Id);
        // CWE-285 rather than a missing-authentication code: the caller is
        // known, the decision about what they may do is what is absent.
        Assert.Equal("CWE-285", f.Template.Recording.Vulnerability?.Cwe);
    }

    [Fact]
    public async Task DistinguishingBetweenIdentities_ReportsSafe()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await PluginTestHost.StartAsync(MapOnlyAliceAccepted);
        var wsUrl = WsUrl(host, "/ws/terminal");

        var findings = await new WebSocketAuthorizationProbe().RunAsync(
            wsUrl, new BowireWebSocketProtocol(),
            [$"Authorization: {TokenA}"], [$"Authorization: {TokenB}"], ct);

        var f = Assert.Single(findings);
        Assert.Equal(ScanFindingStatus.Safe, f.Status);
        Assert.Contains("AUTHZ-ENFORCED", f.Template.Recording.Id, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAnonymouslyOpenSocketIsLeftToTheAuthProbe()
    {
        // Reporting here too would file two findings for one hole, and the
        // cross-identity claim would be true of any caller at all — including
        // one with no credential, which is a different and worse finding.
        var ct = TestContext.Current.CancellationToken;
        await using var host = await PluginTestHost.StartAsync(MapEveryoneAccepted);
        var wsUrl = WsUrl(host, "/ws/terminal");

        var findings = await new WebSocketAuthorizationProbe().RunAsync(
            wsUrl, new BowireWebSocketProtocol(),
            [$"Authorization: {TokenA}"], [$"Authorization: {TokenB}"], ct);

        var f = Assert.Single(findings);
        Assert.Equal(ScanFindingStatus.Skipped, f.Status);
        Assert.Contains("WS-PUBLIC", f.Template.Recording.Id, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithoutASecondIdentityTheProbeIsSilent()
    {
        // A cross-identity check with one identity has nothing to compare.
        // Silence rather than a marker: this runs on every ordinary scan, and
        // a note saying "you did not pass --auth-header-b" on each of them is
        // how a report stops being read.
        var ct = TestContext.Current.CancellationToken;
        await using var host = await PluginTestHost.StartAsync(MapAnyCredentialAccepted);
        var wsUrl = WsUrl(host, "/ws/terminal");

        var findings = await new WebSocketAuthorizationProbe().RunAsync(
            wsUrl, new BowireWebSocketProtocol(), [$"Authorization: {TokenA}"], [], ct);

        Assert.Empty(findings);
    }

    [Fact]
    public async Task TheSameCredentialTwiceProvesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var host = await PluginTestHost.StartAsync(MapAnyCredentialAccepted);
        var wsUrl = WsUrl(host, "/ws/terminal");

        var findings = await new WebSocketAuthorizationProbe().RunAsync(
            wsUrl, new BowireWebSocketProtocol(),
            [$"Authorization: {TokenA}"], [$"Authorization: {TokenA}"], ct);

        var f = Assert.Single(findings);
        Assert.Equal(ScanFindingStatus.Skipped, f.Status);
        Assert.Contains("SAME-IDENTITY", f.Template.Recording.Id, StringComparison.Ordinal);
    }

    private static string WsUrl(PluginTestHost host, string path)
        => host.BaseUrl.Replace("http://", "ws://", StringComparison.Ordinal) + path;

    // ---- servers ----

    // The defect: a credential is required, and which one it is never checked.
    // This is Dokploy's shape — validateRequest() establishes identity, and the
    // handler proceeds without consulting the permission model.
    private static void MapAnyCredentialAccepted(WebApplication app)
        => MapTerminal(app, accept: auth => !string.IsNullOrEmpty(auth));

    // The healthy case: the handler looks at whose credential arrived.
    private static void MapOnlyAliceAccepted(WebApplication app)
        => MapTerminal(app, accept: auth => string.Equals(auth, TokenA, StringComparison.Ordinal));

    // Not this probe's finding — WebSocketAuthProbe reports it.
    private static void MapEveryoneAccepted(WebApplication app)
        => MapTerminal(app, accept: _ => true);

    private static void MapTerminal(WebApplication app, Func<string?, bool> accept)
    {
        app.UseWebSockets();
        app.Map("/ws/terminal", async context =>
        {
            var auth = context.Request.Headers.Authorization.ToString();
            if (!accept(string.IsNullOrEmpty(auth) ? null : auth))
            {
                // 401 before the upgrade — what both an unauthenticated and an
                // unauthorised caller should meet.
                context.Response.StatusCode = (int)HttpStatusCode.Unauthorized;
                return;
            }

            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                return;
            }

            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            // The probe connects and closes without sending a frame, so the
            // server only has to hold the socket open long enough to be seen.
            await Task.Delay(TimeSpan.FromMilliseconds(200), context.RequestAborted);
        });
    }
}
