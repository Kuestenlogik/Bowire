// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using Kuestenlogik.Bowire.Testing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Logging;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// The mechanism that tells a test's own traffic from anybody else's (#737).
/// </summary>
/// <remarks>
/// <para>
/// This is the instrument #714 was missing. Two flows arrived for one
/// request and the conclusion drawn was "a foreign client" — which the
/// evidence did not support, because a flow says what was requested and not
/// who requested it. A marker on the test's own requests settles that, and
/// this file is where the marker is shown to work.
/// </para>
/// <para>
/// Every test here passes its own <c>onForeign</c> handler. The shared
/// report is the record that answers the open question in #714, and a test
/// of the mechanism must not write into the record it exists to make
/// trustworthy.
/// </para>
/// </remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "Test scope")]
public sealed class ForeignArrivalsTests
{
    private static async Task<WebApplication> StartAsync(
        ConcurrentQueue<string> sink, ConcurrentQueue<string> foreign, CancellationToken ct)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(
            o => o.Listen(IPAddress.Loopback, 0, l => l.Protocols = HttpProtocols.Http1));

        var app = builder.Build();
        app.UseArrivalLog(sink, foreign.Enqueue);
        app.MapGet("/api/hello", () => Results.Ok(new { greeting = "hi" }));
        await app.StartAsync(ct);
        return app;
    }

    [Fact]
    public async Task A_Marked_Request_Is_Recorded_And_Not_Reported()
    {
        var ct = TestContext.Current.CancellationToken;
        var sink = new ConcurrentQueue<string>();
        var foreign = new ConcurrentQueue<string>();
        await using var app = await StartAsync(sink, foreign, ct);

        using var http = ForeignArrivals.MarkedClient(app.Urls.First(), "my-test");
        using var resp = await http.GetAsync(new Uri("/api/hello", UriKind.Relative), ct);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        Assert.Single(sink);
        Assert.Contains("marker=my-test", sink.First(), StringComparison.Ordinal);
        // The point of the whole exercise: ours is not a find.
        Assert.Empty(foreign);
    }

    [Fact]
    public async Task An_Unmarked_Request_Is_Reported_With_Enough_To_Chase_It()
    {
        var ct = TestContext.Current.CancellationToken;
        var sink = new ConcurrentQueue<string>();
        var foreign = new ConcurrentQueue<string>();
        await using var app = await StartAsync(sink, foreign, ct);

        // A plain HttpClient stands in for whatever else might reach the
        // port. It carries no marker, which is the only thing that matters:
        // the path it chose is not consulted.
        using var stranger = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
        stranger.DefaultRequestHeaders.Add("User-Agent", "some-scanner/1.0");
        using var resp = await stranger.GetAsync(new Uri("/api/hello", UriKind.Relative), ct);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var found = Assert.Single(foreign);
        Assert.Contains("marker=(none)", found, StringComparison.Ordinal);
        // Everything a next investigation would ask for.
        Assert.Contains("GET /api/hello", found, StringComparison.Ordinal);
        Assert.Contains("ua=some-scanner/1.0", found, StringComparison.Ordinal);
        Assert.Contains("from 127.0.0.1:", found, StringComparison.Ordinal);
        Assert.Contains("conn=", found, StringComparison.Ordinal);
        Assert.Contains("fwd=(none)", found, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_Stranger_On_A_Path_Nobody_Asserts_On_Is_Still_Found()
    {
        // The limit of the previous approach, and the reason for this one.
        // Selecting flows by path makes a test survive foreign traffic,
        // which is right — and it also makes the traffic invisible. A
        // marker does not care where the stranger landed.
        var ct = TestContext.Current.CancellationToken;
        var sink = new ConcurrentQueue<string>();
        var foreign = new ConcurrentQueue<string>();
        await using var app = await StartAsync(sink, foreign, ct);

        using var stranger = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
        using var resp = await stranger.GetAsync(new Uri("/nothing-here", UriKind.Relative), ct);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);

        Assert.Contains(foreign, line => line.Contains("/nothing-here", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Both_Kinds_At_Once_Are_Told_Apart()
    {
        var ct = TestContext.Current.CancellationToken;
        var sink = new ConcurrentQueue<string>();
        var foreign = new ConcurrentQueue<string>();
        await using var app = await StartAsync(sink, foreign, ct);

        using var mine = ForeignArrivals.MarkedClient(app.Urls.First(), "mine");
        using var stranger = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
        _ = await mine.GetAsync(new Uri("/api/hello", UriKind.Relative), ct);
        _ = await stranger.GetAsync(new Uri("/api/hello", UriKind.Relative), ct);
        _ = await mine.GetAsync(new Uri("/api/hello", UriKind.Relative), ct);

        Assert.Equal(3, sink.Count);
        var found = Assert.Single(foreign);
        Assert.Contains("marker=(none)", found, StringComparison.Ordinal);
    }

    [Fact]
    public void A_Marked_Client_Carries_The_Header_On_Every_Request()
    {
        using var http = ForeignArrivals.MarkedClient("http://127.0.0.1:1/", "a-test");
        Assert.True(http.DefaultRequestHeaders.TryGetValues(
            ForeignArrivals.MarkerHeader, out var values));
        Assert.Equal("a-test", values!.Single());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_Client_Without_A_Marker_Is_Refused_Rather_Than_Unmarked(string marker)
    {
        // An empty marker would produce a client whose requests read as
        // foreign in every report — the failure mode the whole mechanism is
        // supposed to detect, manufactured by accident.
        Assert.Throws<ArgumentException>(
            () => ForeignArrivals.MarkedClient("http://127.0.0.1:1/", marker));
    }

    [Fact]
    public void The_Report_Goes_Somewhere_That_Outlives_The_Run()
    {
        // Not asserting the exact path — it depends on where the binary
        // sits. What matters is that it is a file somewhere, because the
        // known failure mode of "report, don't fail" is being overlooked,
        // and scrollback is the easiest thing in the world to overlook.
        Assert.False(string.IsNullOrWhiteSpace(ForeignArrivals.ReportPath));
        Assert.True(Path.IsPathRooted(ForeignArrivals.ReportPath));
    }
}
