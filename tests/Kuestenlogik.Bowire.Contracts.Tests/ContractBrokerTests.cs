// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Text;
using System.Text.Json;
using Kuestenlogik.Bowire.Contracts;

namespace Kuestenlogik.Bowire.Contracts.Tests;

/// <summary>
/// The Pact Broker client: which URLs it builds, and what it does with a
/// broker that says no.
/// </summary>
/// <remarks>
/// <para>
/// Publishing is the one operation here that changes something outside this
/// machine, and a broker keys everything off the URL — a mis-escaped consumer
/// name publishes a contract under a participant nobody is verifying against,
/// and the call still returns 200. So the URL is the assertion.
/// </para>
/// <para>
/// Every test drives a stub handler; nothing reaches the network, which is
/// also the product rule — the broker path is gated behind an explicit
/// <c>--broker-url</c>.
/// </para>
/// </remarks>
public sealed class ContractBrokerTests
{
    /// <summary>Records what was sent and answers with a canned response.</summary>
    private sealed class StubHandler(
        HttpStatusCode status = HttpStatusCode.OK,
        string body = "{}") : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            Bodies.Add(request.Content is null
                ? ""
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
                ReasonPhrase = "stubbed",
            };
        }
    }

    private static PactContract Contract(string consumer = "checkout", string provider = "orders")
        => new()
        {
            Consumer = new PactParty { Name = consumer },
            Provider = new PactParty { Name = provider },
        };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- publish ----

    [Fact]
    public async Task Publishing_Puts_The_Contract_At_The_Documented_Url()
    {
        using var handler = new StubHandler();
        using var http = new HttpClient(handler);

        await ContractBroker.PublishAsync(
            http, "https://broker.example.com", Contract(), "1.2.3", tag: null, Ct);

        var req = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Put, req.Method);
        Assert.Equal(
            "https://broker.example.com/pacts/provider/orders/consumer/checkout/version/1.2.3",
            req.RequestUri!.ToString());
    }

    [Fact]
    public async Task A_Trailing_Slash_On_The_Broker_Url_Does_Not_Double_Up()
    {
        // Operators paste the broker URL from a browser, where it usually has
        // one; a doubled slash is a 404 on most brokers.
        using var handler = new StubHandler();
        using var http = new HttpClient(handler);

        await ContractBroker.PublishAsync(
            http, "https://broker.example.com/", Contract(), "1.0.0", tag: null, Ct);

        Assert.DoesNotContain("com//", handler.Requests[0].RequestUri!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Participant_Names_And_Versions_Are_Escaped_Into_The_Path()
    {
        // A team name with a space or a slash would otherwise silently publish
        // under a different participant — or a different path entirely.
        using var handler = new StubHandler();
        using var http = new HttpClient(handler);

        await ContractBroker.PublishAsync(
            http, "https://broker.example.com", Contract("check out", "orders/v2"), "1.0.0+ci", tag: null, Ct);

        // AbsoluteUri, not ToString(): Uri.ToString() hands back a *display*
        // form with %20 and friends decoded again, which would make this
        // assertion pass or fail for reasons that have nothing to do with what
        // went over the wire.
        var url = handler.Requests[0].RequestUri!.AbsoluteUri;
        Assert.Contains("provider/orders%2Fv2", url, StringComparison.Ordinal);
        Assert.Contains("consumer/check%20out", url, StringComparison.Ordinal);
        Assert.Contains("version/1.0.0%2Bci", url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_Contract_Itself_Travels_As_Camel_Cased_Json()
    {
        using var handler = new StubHandler();
        using var http = new HttpClient(handler);

        await ContractBroker.PublishAsync(
            http, "https://broker.example.com", Contract(), "1.0.0", tag: null, Ct);

        using var doc = JsonDocument.Parse(handler.Bodies[0]);
        Assert.Equal("checkout", doc.RootElement.GetProperty("consumer").GetProperty("name").GetString());
        Assert.Equal("orders", doc.RootElement.GetProperty("provider").GetProperty("name").GetString());
    }

    [Fact]
    public async Task A_Tag_Costs_A_Second_Call_To_The_Pacticipant_Endpoint()
    {
        using var handler = new StubHandler();
        using var http = new HttpClient(handler);

        await ContractBroker.PublishAsync(
            http, "https://broker.example.com", Contract(), "1.0.0", tag: "main", Ct);

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(
            "https://broker.example.com/pacticipants/checkout/versions/1.0.0/tags/main",
            handler.Requests[1].RequestUri!.ToString());
    }

    [Fact]
    public async Task No_Tag_Means_No_Second_Call()
        => await AssertSingleCall(tag: null);

    [Fact]
    public async Task An_Empty_Tag_Is_Treated_As_No_Tag()
        // A CLI that passes `--tag ""` through must not create a nameless tag.
        => await AssertSingleCall(tag: "");

    private static async Task AssertSingleCall(string? tag)
    {
        using var handler = new StubHandler();
        using var http = new HttpClient(handler);

        await ContractBroker.PublishAsync(
            http, "https://broker.example.com", Contract(), "1.0.0", tag, Ct);

        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task A_Rejected_Publish_Says_So_With_The_Status_And_The_Brokers_Words()
    {
        // The commonest real failure is a 409 for republishing a changed
        // contract under a version that already exists. An operator can only
        // act on that if the message carries the broker's own explanation.
        using var handler = new StubHandler(HttpStatusCode.Conflict, "pact already published with different content");
        using var http = new HttpClient(handler);

        var ex = await Assert.ThrowsAsync<ContractBrokerException>(() =>
            ContractBroker.PublishAsync(http, "https://broker.example.com", Contract(), "1.0.0", null, Ct));

        Assert.Contains("409", ex.Message, StringComparison.Ordinal);
        Assert.Contains("already published", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_Rejected_Tag_Is_Reported_Separately_From_The_Publish()
    {
        // The contract did land; only the tag failed. Saying "publish failed"
        // here would send someone re-publishing something that is already there.
        var calls = 0;
        using var handler = new SequencedHandler(_ =>
            ++calls == 1
                ? new HttpResponseMessage(HttpStatusCode.OK)
                : new HttpResponseMessage(HttpStatusCode.Forbidden)
                {
                    Content = new StringContent("read-only token"),
                });
        using var http = new HttpClient(handler);

        var ex = await Assert.ThrowsAsync<ContractBrokerException>(() =>
            ContractBroker.PublishAsync(http, "https://broker.example.com", Contract(), "1.0.0", "main", Ct));

        Assert.Contains("version tag", ex.Message, StringComparison.Ordinal);
        Assert.Contains("403", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_Very_Long_Broker_Error_Is_Truncated_Rather_Than_Dumped()
    {
        // Brokers answer errors with an HTML page often enough that the whole
        // body in an exception message makes the real cause unreadable.
        using var handler = new StubHandler(HttpStatusCode.InternalServerError, new string('x', 5_000));
        using var http = new HttpClient(handler);

        var ex = await Assert.ThrowsAsync<ContractBrokerException>(() =>
            ContractBroker.PublishAsync(http, "https://broker.example.com", Contract(), "1.0.0", null, Ct));

        Assert.True(ex.Message.Length < 400, $"message was {ex.Message.Length} chars");
        Assert.EndsWith("…", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Publishing_Without_An_Http_Client_Is_A_Programming_Error()
        => await Assert.ThrowsAsync<ArgumentNullException>(() =>
            ContractBroker.PublishAsync(null!, "https://broker.example.com", Contract(), "1.0.0", null, Ct));

    [Fact]
    public async Task Publishing_Without_A_Contract_Is_A_Programming_Error()
    {
        using var handler = new StubHandler();
        using var http = new HttpClient(handler);

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            ContractBroker.PublishAsync(http, "https://broker.example.com", null!, "1.0.0", null, Ct));
    }

    // ---- fetch ----

    [Fact]
    public async Task Fetching_Without_A_Tag_Asks_For_The_Latest()
    {
        using var handler = new StubHandler(HttpStatusCode.OK, """
            {"consumer":{"name":"checkout"},"provider":{"name":"orders"},"interactions":[]}
            """);
        using var http = new HttpClient(handler);

        var contract = await ContractBroker.FetchLatestAsync(
            http, "https://broker.example.com", "orders", tag: null, Ct);

        Assert.Equal("https://broker.example.com/pacts/provider/orders/latest",
            handler.Requests[0].RequestUri!.ToString());
        Assert.Equal("checkout", contract.Consumer.Name);
    }

    [Fact]
    public async Task Fetching_With_A_Tag_Asks_For_That_Environments_Latest()
    {
        // The whole point of tags: verify against what is in production, not
        // against whatever was published last.
        using var handler = new StubHandler(HttpStatusCode.OK, """{"provider":{"name":"orders"}}""");
        using var http = new HttpClient(handler);

        await ContractBroker.FetchLatestAsync(http, "https://broker.example.com", "orders", "production", Ct);

        Assert.Equal("https://broker.example.com/pacts/provider/orders/latest/production",
            handler.Requests[0].RequestUri!.ToString());
    }

    [Fact]
    public async Task A_Provider_Name_Is_Escaped_When_Fetching_Too()
    {
        using var handler = new StubHandler(HttpStatusCode.OK, """{"provider":{"name":"o"}}""");
        using var http = new HttpClient(handler);

        await ContractBroker.FetchLatestAsync(http, "https://broker.example.com", "orders v2", null, Ct);

        Assert.Contains("provider/orders%20v2", handler.Requests[0].RequestUri!.AbsoluteUri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_Unknown_Provider_Names_The_Url_It_Asked_For()
    {
        // A 404 here is nearly always a name mismatch, so the message has to
        // show the name that was actually used.
        using var handler = new StubHandler(HttpStatusCode.NotFound, "no pacts found");
        using var http = new HttpClient(handler);

        var ex = await Assert.ThrowsAsync<ContractBrokerException>(() =>
            ContractBroker.FetchLatestAsync(http, "https://broker.example.com", "typo-service", null, Ct));

        Assert.Contains("404", ex.Message, StringComparison.Ordinal);
        Assert.Contains("typo-service", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_Body_That_Is_Not_A_Contract_Is_Refused_Rather_Than_Returned_Empty()
    {
        // `null` deserialises from the literal "null" without throwing, and an
        // empty PactContract downstream would verify against nothing and pass.
        using var handler = new StubHandler(HttpStatusCode.OK, "null");
        using var http = new HttpClient(handler);

        var ex = await Assert.ThrowsAsync<ContractBrokerException>(() =>
            ContractBroker.FetchLatestAsync(http, "https://broker.example.com", "orders", null, Ct));

        Assert.Contains("empty", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Fetching_Without_An_Http_Client_Is_A_Programming_Error()
        => await Assert.ThrowsAsync<ArgumentNullException>(() =>
            ContractBroker.FetchLatestAsync(null!, "https://broker.example.com", "orders", null, Ct));

    /// <summary>A stub whose response depends on the call index.</summary>
    private sealed class SequencedHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }
}
