// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Oast;
using Kuestenlogik.Bowire.Security.Scanner;

namespace Kuestenlogik.Bowire.Tests.Security;

/// <summary>
/// Coverage for <see cref="OastWorkbenchSession"/> — the long-lived session
/// behind the manual OAST panel (#486). Driven through a fake
/// <see cref="IOastClient"/> so the session's own logic (eager register,
/// feed accumulation, field mapping, not-configured behaviour) is exercised
/// without a live interaction server; the client↔server protocol is proven
/// separately in the Oast package's tests.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "Test scope")]
public sealed class OastWorkbenchSessionTests
{
    [Fact]
    public void Not_configured_when_no_server_is_set()
    {
        var session = new OastWorkbenchSession(server: null, token: null);
        Assert.False(session.Configured);
        Assert.Null(session.ServerDomain);
    }

    [Fact]
    public void A_malformed_server_url_disables_oast_with_a_reason()
    {
        // Must not throw out of the container build — the panel shows the reason.
        var session = new OastWorkbenchSession(server: "not-a-url", token: null);
        Assert.False(session.Configured);
        Assert.NotNull(session.ConfigError);
    }

    [Fact]
    public async Task Poll_on_an_unconfigured_session_is_empty_not_an_error()
    {
        var session = new OastWorkbenchSession(server: null, token: null);
        Assert.Empty(await session.PollAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Allocate_registers_before_handing_out_the_first_payload()
    {
        // The server drops callbacks for an unregistered correlation id, so a
        // payload handed out before registration would silently lose its
        // callback. The session must register on the first allocate.
        var fake = new FakeOastClient();
        var session = new OastWorkbenchSession(fake);

        var host = await session.AllocateAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, fake.RegisterCalls);
        Assert.EndsWith(".oast.test", host, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Registration_happens_once_across_allocate_and_poll()
    {
        var fake = new FakeOastClient();
        var session = new OastWorkbenchSession(fake);

        await session.AllocateAsync(TestContext.Current.CancellationToken);
        await session.PollAsync(TestContext.Current.CancellationToken);
        await session.AllocateAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, fake.RegisterCalls);
    }

    [Fact]
    public async Task Poll_returns_the_accumulated_feed_not_just_the_delta()
    {
        // The client's PollAsync drains — each callback is returned once. The
        // session accumulates so the panel, polling at its own cadence, always
        // sees the full history rather than a flicker of only-the-newest.
        var fake = new FakeOastClient();
        var session = new OastWorkbenchSession(fake);

        fake.Enqueue(new OastInteraction { Protocol = "dns", FullId = "a.oast.test" });
        var first = await session.PollAsync(TestContext.Current.CancellationToken);
        Assert.Single(first);

        fake.Enqueue(new OastInteraction { Protocol = "http", FullId = "b.oast.test" });
        var second = await session.PollAsync(TestContext.Current.CancellationToken);

        // Both, not just the new one.
        Assert.Equal(2, second.Count);
        Assert.Equal("dns", second[0].Protocol);
        Assert.Equal("http", second[1].Protocol);
    }

    [Fact]
    public async Task Interaction_fields_map_onto_the_callback_shape()
    {
        var fake = new FakeOastClient();
        var session = new OastWorkbenchSession(fake);
        var ts = DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000);
        fake.Enqueue(new OastInteraction
        {
            Protocol = "dns",
            FullId = "abc.oast.test",
            QType = "A",
            RemoteAddress = "203.0.113.9",
            Timestamp = ts,
            RawRequest = ";; QUESTION",
        });

        var cb = Assert.Single(await session.PollAsync(TestContext.Current.CancellationToken));

        Assert.Equal("dns", cb.Protocol);
        Assert.Equal("abc.oast.test", cb.Id);
        Assert.Equal("A", cb.QueryType);
        Assert.Equal("203.0.113.9", cb.RemoteAddress);
        Assert.Equal(1_700_000_000_000, cb.TimestampUnixMs);
        Assert.Equal(";; QUESTION", cb.RawRequest);
    }

    /// <summary>A minimal in-memory <see cref="IOastClient"/> for the session tests.</summary>
    private sealed class FakeOastClient : IOastClient
    {
        private readonly Queue<OastInteraction> _pending = new();
        private int _n;

        public int RegisterCalls { get; private set; }
        public string ServerDomain => "oast.test";

        public void Enqueue(OastInteraction i) => _pending.Enqueue(i);

        public Task RegisterAsync(CancellationToken ct = default)
        {
            RegisterCalls++;
            return Task.CompletedTask;
        }

        public OastAllocation Allocate()
        {
            var id = "corr" + _n++;
            return new OastAllocation(id + ".oast.test", id);
        }

        public Task<IReadOnlyList<OastInteraction>> PollAsync(CancellationToken ct = default)
        {
            var drained = new List<OastInteraction>();
            while (_pending.TryDequeue(out var i)) drained.Add(i);
            return Task.FromResult<IReadOnlyList<OastInteraction>>(drained);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
