// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Http;
using Kuestenlogik.Bowire.Oast.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging;

namespace Kuestenlogik.Bowire.Oast.Tests;

/// <summary>
/// The pairing that matters for #35 Phase 2f: <c>bowire oast serve</c> driven by
/// the real <see cref="InteractshClient"/>, over the real endpoints.
/// </summary>
/// <remarks>
/// Client and server were written against the same protocol notes, so testing
/// either alone proves nothing about interop — the crypto especially would
/// round-trip happily while being wrong in the same way on both sides. These
/// tests run the actual exchange: register with a real RSA key, plant a real
/// callback, poll it back through the real RSA-unwrap + AES-CTR decrypt.
/// </remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "Test scope")]
public sealed class BowireOastServerTests
{
    /// <summary>
    /// An in-process server plus a client already pointed at it.
    /// <paramref name="token"/> gates the server; <paramref name="clientToken"/>
    /// is what the client presents — pass different values to test the
    /// auth mismatch path.
    /// </summary>
    private static async Task<(WebApplication App, InteractshClient Client, HttpClient Raw)> StartAsync(
        OastInteractionStore store, string? token = null, string? clientToken = null)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        var app = builder.Build();
        app.MapBowireOast(store, token);
        await app.StartAsync(TestContext.Current.CancellationToken);

        // The TestServer handler makes the client talk to the real endpoints
        // without a socket. Host is irrelevant beyond naming the callback zone.
        var handler = app.GetTestServer().CreateHandler();
        var client = new InteractshClient("http://oast.example.com", token: clientToken, httpHandler: handler);
        var raw = app.GetTestServer().CreateClient();
        return (app, client, raw);
    }

    /// <summary>
    /// Exactly what a target does when it fetches a planted host: an HTTP
    /// request whose Host header is the callback host.
    /// </summary>
    private static async Task SimulateHttpCallbackAsync(HttpClient raw, string callbackHost, string path = "/probe")
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"http://{callbackHost}{path}");
        using var resp = await raw.SendAsync(req, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task Client_registers_plants_a_callback_and_polls_it_back_decrypted()
    {
        // The whole point of Phase 2f, end to end and in-process.
        var store = new OastInteractionStore();
        var (app, client, raw) = await StartAsync(store);
        await using var _ = app;

        await client.RegisterAsync(TestContext.Current.CancellationToken);
        var allocation = client.Allocate();

        // Nothing has called back yet.
        Assert.Empty(await client.PollAsync(TestContext.Current.CancellationToken));

        await SimulateHttpCallbackAsync(raw, allocation.CallbackHost);

        var interactions = await client.PollAsync(TestContext.Current.CancellationToken);

        // Survived: server-side AES-CTR encrypt → RSA-OAEP key wrap →
        // client-side unwrap → decrypt → deserialise.
        var one = Assert.Single(interactions);
        Assert.Equal("http", one.Protocol);
        Assert.Equal(allocation.CallbackHost, one.FullId);
        Assert.Contains("GET /probe", one.RawRequest, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Poll_drains_so_a_callback_is_reported_once()
    {
        var store = new OastInteractionStore();
        var (app, client, raw) = await StartAsync(store);
        await using var _ = app;

        await client.RegisterAsync(TestContext.Current.CancellationToken);
        var allocation = client.Allocate();
        await SimulateHttpCallbackAsync(raw, allocation.CallbackHost);

        Assert.Single(await client.PollAsync(TestContext.Current.CancellationToken));
        // A second poll must not re-report it, or a scan would keep "finding"
        // the same callback.
        Assert.Empty(await client.PollAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Two_allocations_are_attributed_separately()
    {
        // Callbacks must be pinned to the probe that planted them — this is
        // what stops one template's callback proving another's finding.
        var store = new OastInteractionStore();
        var (app, client, raw) = await StartAsync(store);
        await using var _ = app;

        await client.RegisterAsync(TestContext.Current.CancellationToken);
        var first = client.Allocate();
        var second = client.Allocate();
        Assert.NotEqual(first.CallbackHost, second.CallbackHost);

        await SimulateHttpCallbackAsync(raw, second.CallbackHost);

        var interactions = await client.PollAsync(TestContext.Current.CancellationToken);
        var one = Assert.Single(interactions);
        Assert.Equal(second.CallbackHost, one.FullId);
        Assert.NotEqual(first.CallbackHost, one.FullId);
    }

    [Fact]
    public async Task Callback_for_an_unregistered_host_is_dropped()
    {
        // Port 80 gets scanned by the whole internet; traffic nobody registered
        // for is noise, not evidence, and must not accumulate.
        var store = new OastInteractionStore();
        var (app, client, raw) = await StartAsync(store);
        await using var _ = app;

        await client.RegisterAsync(TestContext.Current.CancellationToken);
        await SimulateHttpCallbackAsync(raw, "aaaaaaaaaaaaaaaaaaaabbbbbbbbbbbbb.oast.example.com");

        Assert.Empty(await client.PollAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Poll_with_a_wrong_secret_is_refused()
    {
        var store = new OastInteractionStore();
        var (app, client, raw) = await StartAsync(store);
        await using var _ = app;

        await client.RegisterAsync(TestContext.Current.CancellationToken);
        var allocation = client.Allocate();
        await SimulateHttpCallbackAsync(raw, allocation.CallbackHost);

        // A stranger who guessed the correlation id (it is in every DNS query
        // the target makes) still cannot read the callbacks.
        var id = allocation.CorrelationId;
        using var resp = await raw.GetAsync(
            new Uri($"http://oast.example.com/poll?id={id}&secret=wrong"), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Unknown_correlation_id_and_wrong_secret_are_indistinguishable()
    {
        // Same 401 either way, so a caller cannot enumerate live sessions.
        var store = new OastInteractionStore();
        var (app, _, raw) = await StartAsync(store);
        await using var __ = app;

        using var unknown = await raw.GetAsync(
            new Uri("http://oast.example.com/poll?id=00000000000000000000&secret=x"), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
    }

    [Fact]
    public async Task Register_is_gated_when_a_token_is_configured()
    {
        var store = new OastInteractionStore();
        var (app, client, _) = await StartAsync(store, token: "s3cret");
        await using var _ = app;

        // The client sends no token, so a gated instance must refuse it —
        // rather than silently accepting and looking like it works.
        var ex = await Assert.ThrowsAsync<OastException>(
            () => client.RegisterAsync(TestContext.Current.CancellationToken));
        Assert.Contains("401", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Client_with_the_matching_token_registers_and_the_full_flow_works()
    {
        // --oast-token end to end: a gated server + a client carrying the token
        // must complete the whole plant-and-poll cycle, not just register.
        var store = new OastInteractionStore();
        var (app, client, raw) = await StartAsync(store, token: "s3cret", clientToken: "s3cret");
        await using var _ = app;

        await client.RegisterAsync(TestContext.Current.CancellationToken);
        var allocation = client.Allocate();
        await SimulateHttpCallbackAsync(raw, allocation.CallbackHost);

        var one = Assert.Single(await client.PollAsync(TestContext.Current.CancellationToken));
        Assert.Equal("http", one.Protocol);
        Assert.Equal(allocation.CallbackHost, one.FullId);
    }

    [Fact]
    public async Task Client_with_the_wrong_token_is_refused()
    {
        var store = new OastInteractionStore();
        var (app, client, _) = await StartAsync(store, token: "s3cret", clientToken: "nope");
        await using var _ = app;

        var ex = await Assert.ThrowsAsync<OastException>(
            () => client.RegisterAsync(TestContext.Current.CancellationToken));
        Assert.Contains("401", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Client_sends_the_token_verbatim_as_the_authorization_header()
    {
        // Interactsh uses the raw token as the Authorization value — no Bearer
        // scheme. A structured AuthenticationHeaderValue would reformat it and
        // the server's fixed-time compare would then never match.
        string? seenAuth = null;
        using var handler = new CapturingHandler(req =>
        {
            seenAuth = req.Headers.TryGetValues("Authorization", out var v) ? string.Join(",", v) : null;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json"),
            };
        });
        var client = new InteractshClient("https://oast.example.com", token: "raw-token-value", httpHandler: handler);

        await client.RegisterAsync(TestContext.Current.CancellationToken);

        Assert.Equal("raw-token-value", seenAuth);
    }

    private sealed class CapturingHandler(Func<HttpRequestMessage, HttpResponseMessage> route) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(route(request));
    }

    [Fact]
    public async Task Register_rejects_a_public_key_that_is_not_a_key()
    {
        var store = new OastInteractionStore();
        var (app, _, raw) = await StartAsync(store);
        await using var __ = app;

        using var content = new StringContent(
            """{"PublicKey":"bm90LWEta2V5","SecretKey":"s","CorrelationID":"00000000000000000000"}""",
            System.Text.Encoding.UTF8, "application/json");
        using var resp = await raw.PostAsync(new Uri("http://oast.example.com/register"), content, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Equal(0, store.SessionCount);
    }

    [Fact]
    public void Store_evicts_idle_sessions()
    {
        var time = new FakeTime(DateTimeOffset.UnixEpoch);
        var store = new OastInteractionStore(time, TimeSpan.FromMinutes(30));
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var pem = new string(System.Security.Cryptography.PemEncoding.Write(
            "PUBLIC KEY", rsa.ExportSubjectPublicKeyInfo()));

        Assert.True(store.TryRegister("00000000000000000000", "s", pem));
        Assert.Equal(1, store.SessionCount);

        time.Now = DateTimeOffset.UnixEpoch.AddMinutes(10);
        Assert.Equal(0, store.EvictIdle());
        Assert.Equal(1, store.SessionCount);

        // A catcher that never forgets would hold other people's callback
        // traffic indefinitely.
        time.Now = DateTimeOffset.UnixEpoch.AddHours(2);
        Assert.Equal(1, store.EvictIdle());
        Assert.Equal(0, store.SessionCount);
    }

    private sealed class FakeTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
