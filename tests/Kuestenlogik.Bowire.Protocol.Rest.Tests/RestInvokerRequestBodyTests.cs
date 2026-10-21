// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Text;
using Kuestenlogik.Bowire.Models;
using Kuestenlogik.Bowire.Protocol.Rest;

namespace Kuestenlogik.Bowire.Protocol.Rest.Tests;

/// <summary>
/// Which verbs of a discovered operation carry the body fields the
/// operator filled in. The assertion is on the request the server
/// received, not on any intermediate shape.
/// </summary>
public sealed class RestInvokerRequestBodyTests
{
    /// <summary>Answers 200 and remembers the request body.</summary>
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public string? BodyText { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            if (request.Content is not null)
            {
                BodyText = await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            };
        }
    }

    private static BowireMethodInfo Operation(string verb) => new(
        Name: "op",
        FullName: verb + " /items",
        ClientStreaming: false,
        ServerStreaming: false,
        InputType: new BowireMessageInfo("In", "In",
        [
            new BowireFieldInfo("ids", 1, "string", "optional", false, true, null, null) { Source = "body" },
        ]),
        OutputType: new BowireMessageInfo("Out", "Out", []),
        MethodType: "Unary")
    { HttpMethod = verb, HttpPath = "/items" };

    [Theory]
    [InlineData("DELETE")]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    public async Task A_Declared_Body_Is_Sent_With_The_Verb(string verb)
    {
        // A bulk delete (`DELETE /items` with the ids in the body) is a
        // common API shape, and OpenAPI 3.1 allows a requestBody on DELETE.
        using var handler = new CapturingHandler();
        using var http = new HttpClient(handler);

        await RestInvoker.InvokeAsync(
            http, "http://api.example.com", Operation(verb),
            ["""{"ids":["a","b"]}"""], requestMetadata: null,
            TestContext.Current.CancellationToken);

        Assert.Equal(verb, handler.Request!.Method.Method);
        Assert.Equal("""{"ids":["a","b"]}""", handler.BodyText);
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("HEAD")]
    [InlineData("OPTIONS")]
    public async Task A_Verb_Without_Body_Semantics_Sends_None(string verb)
    {
        using var handler = new CapturingHandler();
        using var http = new HttpClient(handler);

        await RestInvoker.InvokeAsync(
            http, "http://api.example.com", Operation(verb),
            ["""{"ids":["a"]}"""], requestMetadata: null,
            TestContext.Current.CancellationToken);

        Assert.Null(handler.Request!.Content);
    }
}
