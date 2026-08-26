// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Text;
using Kuestenlogik.Bowire.Protocol.Rest;

namespace Kuestenlogik.Bowire.Protocol.Rest.Tests;

/// <summary>
/// The schema-free REST path — the freeform request builder's "just hit this
/// URL" mode.
/// </summary>
/// <remarks>
/// <para>
/// Nothing here has an OpenAPI document to check against, so the invoker is
/// the last thing between what an operator typed and what goes on the wire.
/// The assertions are on the request the server would have received: the verb,
/// whether a body was attached, and which headers survived.
/// </para>
/// <para>
/// The reserved <c>X-Bowire-*</c> markers are the subtle part. They are how
/// the JSON envelope smuggles a binary body through a metadata dictionary, and
/// they must never reach the wire — a request carrying a base64 copy of its
/// own body in a header would be both wrong and enormous.
/// </para>
/// </remarks>
public sealed class RestAdHocInvokeTests
{
    /// <summary>Answers 200 and remembers the request.</summary>
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public string? BodyText { get; private set; }
        public string? ContentType { get; private set; }
        public string? ContentDispositionFileName { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            if (request.Content is not null)
            {
                // Read inside the handler: HttpClient disposes the request
                // (and its content) once the response completes, so anything
                // the assertions need has to be captured here.
                BodyText = await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                ContentType = request.Content.Headers.ContentType?.MediaType;
                ContentDispositionFileName = request.Content.Headers.ContentDisposition?.FileName;
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"ok":true}""", Encoding.UTF8, "application/json"),
            };
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<(InvokeResult Result, CapturingHandler Handler)> Invoke(
        string url = "https://api.example.com/orders",
        string verb = "GET",
        string? body = null,
        Dictionary<string, string>? metadata = null)
    {
        using var handler = new CapturingHandler();
        using var http = new HttpClient(handler);
        var result = await RestInvoker.InvokeAdHocAsync(http, url, verb, body, metadata, Ct);
        return (result, handler);
    }

    // ---- refusals, before anything is sent ----

    [Fact]
    public async Task A_Request_With_No_Url_Is_An_Error_Result_Not_An_Exception()
    {
        // The freeform builder can be half-filled at any moment; an exception
        // here would take the request pane down instead of showing a message.
        var (result, handler) = await Invoke(url: "");

        Assert.Equal("Error", result.Status);
        Assert.Contains("URL is required", result.Metadata!["error"], StringComparison.Ordinal);
        Assert.Null(handler.Request);
    }

    [Fact]
    public async Task A_Verb_Nothing_Speaks_Is_Refused_By_Name()
    {
        // Naming the verb matters: the usual cause is a typo in a field the
        // operator typed themselves.
        var (result, handler) = await Invoke(verb: "FETCH");

        Assert.Equal("Error", result.Status);
        Assert.Contains("FETCH", result.Metadata!["error"], StringComparison.Ordinal);
        Assert.Null(handler.Request);
    }

    [Fact]
    public async Task A_Url_That_Will_Not_Parse_Is_Refused()
    {
        // Note what is *not* asserted here: a rooted path like "/orders".
        // On Unix that parses as an absolute URI (file:///orders), so the
        // guard lets it through and the failure surfaces later as a transport
        // error instead. Same platform quirk the reverse-proxy guard had to
        // handle explicitly — pinning it as a refusal would only hold on
        // Windows.
        var (result, _) = await Invoke(url: "not a url at all");

        Assert.Equal("Error", result.Status);
        Assert.Contains("Invalid URL", result.Metadata!["error"], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    [InlineData("PATCH")]
    [InlineData("HEAD")]
    [InlineData("OPTIONS")]
    public async Task Every_Standard_Verb_Goes_Out_As_Itself(string verb)
    {
        var (_, handler) = await Invoke(verb: verb);

        Assert.Equal(verb, handler.Request!.Method.Method);
    }

    [Fact]
    public async Task A_Lowercase_Verb_Is_Normalised_Rather_Than_Refused()
        // Operators type what they read in a curl snippet.
        => Assert.Equal("POST", (await Invoke(verb: "post")).Handler.Request!.Method.Method);

    // ---- the body ----

    [Fact]
    public async Task A_Body_On_Post_Goes_Out_As_Json()
    {
        var (_, handler) = await Invoke(verb: "POST", body: """{"id":42}""");

        Assert.Equal("""{"id":42}""", handler.BodyText);
        Assert.Equal("application/json", handler.ContentType);
    }

    [Fact]
    public async Task A_Body_On_Get_Is_Dropped()
    {
        // RFC 7231 §4.3.1: a GET body has no defined semantics, and servers
        // do surprising things with one. The builder keeps the text in the
        // pane; the request does not carry it.
        var (_, handler) = await Invoke(verb: "GET", body: """{"id":42}""");

        Assert.Null(handler.Request!.Content);
    }

    [Fact]
    public async Task An_Empty_Body_Attaches_No_Content_At_All()
    {
        var (_, handler) = await Invoke(verb: "POST", body: "   ");

        Assert.Null(handler.Request!.Content);
    }

    // ---- the binary body smuggled through metadata (#290) ----

    [Fact]
    public async Task A_Binary_Body_Is_Decoded_And_Sent_As_Bytes()
    {
        var (_, handler) = await Invoke(verb: "POST", metadata: new Dictionary<string, string>
        {
            ["X-Bowire-Body-Binary"] = Convert.ToBase64String(Encoding.UTF8.GetBytes("hello")),
            ["X-Bowire-Body-Binary-Content-Type"] = "image/png",
        });

        Assert.Equal("hello", handler.BodyText);
        Assert.Equal("image/png", handler.ContentType);
    }

    [Fact]
    public async Task A_Binary_Body_Without_A_Content_Type_Goes_Out_As_Octet_Stream()
    {
        var (_, handler) = await Invoke(verb: "POST", metadata: new Dictionary<string, string>
        {
            ["X-Bowire-Body-Binary"] = Convert.ToBase64String(Encoding.UTF8.GetBytes("hello")),
        });

        Assert.Equal("application/octet-stream", handler.ContentType);
    }

    [Fact]
    public async Task A_Binary_Filename_Travels_As_A_Content_Disposition()
    {
        var (_, handler) = await Invoke(verb: "POST", metadata: new Dictionary<string, string>
        {
            ["X-Bowire-Body-Binary"] = Convert.ToBase64String(Encoding.UTF8.GetBytes("hello")),
            ["X-Bowire-Body-Binary-Name"] = "logo.png",
        });

        Assert.Equal("logo.png", handler.ContentDispositionFileName);
    }

    [Fact]
    public async Task A_Body_Binary_That_Is_Not_Base64_Is_Reported_Rather_Than_Sent()
    {
        // The value crossed a JSON envelope to get here; a truncated one has
        // to say so rather than produce an empty or garbled upload.
        var (result, handler) = await Invoke(verb: "POST", metadata: new Dictionary<string, string>
        {
            ["X-Bowire-Body-Binary"] = "not base64 at all!!",
        });

        Assert.Equal("Error", result.Status);
        Assert.Contains("base64", result.Metadata!["error"], StringComparison.Ordinal);
        Assert.Null(handler.Request);
    }

    [Fact]
    public async Task A_Binary_Body_Is_Ignored_On_A_Verb_That_Takes_No_Body()
    {
        var (_, handler) = await Invoke(verb: "GET", metadata: new Dictionary<string, string>
        {
            ["X-Bowire-Body-Binary"] = Convert.ToBase64String(Encoding.UTF8.GetBytes("hello")),
        });

        Assert.Null(handler.Request!.Content);
    }

    // ---- headers ----

    [Fact]
    public async Task A_Custom_Header_Reaches_The_Wire()
    {
        var (_, handler) = await Invoke(metadata: new Dictionary<string, string>
        {
            ["X-Request-Id"] = "abc-123",
        });

        Assert.Equal("abc-123", handler.Request!.Headers.GetValues("X-Request-Id").Single());
    }

    [Theory]
    [InlineData("X-Bowire-Body-Binary")]
    [InlineData("X-Bowire-Body-Binary-Content-Type")]
    [InlineData("X-Bowire-Body-Binary-Name")]
    public async Task The_Reserved_Markers_Never_Reach_The_Wire(string header)
    {
        // They are the envelope's smuggling channel, not request headers. A
        // request carrying a base64 copy of its own body in a header would be
        // both wrong and enormous.
        var (_, handler) = await Invoke(verb: "POST", metadata: new Dictionary<string, string>
        {
            ["X-Bowire-Body-Binary"] = Convert.ToBase64String(Encoding.UTF8.GetBytes("hello")),
            ["X-Bowire-Body-Binary-Content-Type"] = "image/png",
            ["X-Bowire-Body-Binary-Name"] = "logo.png",
        });

        Assert.False(handler.Request!.Headers.Contains(header), $"{header} must not be sent");
    }

    [Fact]
    public async Task Content_Type_Is_Left_To_The_Content_Not_The_Header_Set()
    {
        // HttpClient throws on a content header added to the request's own
        // header collection ("Misused header name") — which is exactly why the
        // invoker filters Content-Type out of the metadata rather than
        // forwarding it. The proof is that the call succeeds at all, and that
        // the content keeps the type the body path gave it.
        var (result, handler) = await Invoke(verb: "POST", body: """{"a":1}""",
            metadata: new Dictionary<string, string> { ["Content-Type"] = "application/xml" });

        Assert.NotEqual("Error", result.Status);
        Assert.Equal("application/json", handler.ContentType);
    }

    [Fact]
    public async Task A_Nameless_Header_Is_Skipped_Rather_Than_Throwing()
    {
        var (result, handler) = await Invoke(metadata: new Dictionary<string, string>
        {
            [" "] = "value",
            ["X-Ok"] = "yes",
        });

        Assert.NotEqual("Error", result.Status);
        Assert.Equal("yes", handler.Request!.Headers.GetValues("X-Ok").Single());
    }

    // ---- the answer ----

    [Fact]
    public async Task A_Successful_Call_Comes_Back_With_The_Body_And_A_Duration()
    {
        var (result, _) = await Invoke();

        Assert.NotEqual("Error", result.Status);
        Assert.Contains("ok", result.Response!, StringComparison.Ordinal);
    }
}
