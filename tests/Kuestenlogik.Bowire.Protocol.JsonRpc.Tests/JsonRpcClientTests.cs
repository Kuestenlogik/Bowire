// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Text;
using System.Text.Json;
using Kuestenlogik.Bowire.Protocol.JsonRpc;

namespace Kuestenlogik.Bowire.Protocol.JsonRpc.Tests;

/// <summary>
/// <see cref="JsonRpcClient"/> against a canned HTTP answer: how a reply
/// is matched to the request it answers.
/// </summary>
public sealed class JsonRpcClientTests
{
    private static readonly Uri Endpoint = new("http://jsonrpc.test/rpc");

    [Fact]
    public async Task A_Reply_To_Another_Request_Is_Refused()
    {
        // #866 — the spec has the response carry the request's id. A
        // reply with a different id answers some other call (a proxy or
        // pooled connection mixing them up), and its result must not be
        // shown as this call's.
        using var reply = new CannedReply("""{"jsonrpc":"2.0","id":99,"result":"someone else's"}""");
        using var http = new HttpClient(reply, disposeHandler: false);
        var client = new JsonRpcClient(http, Endpoint);

        var ex = await Assert.ThrowsAsync<JsonRpcException>(
            () => client.CallAsync("ping", parameters: null, TestContext.Current.CancellationToken));

        Assert.Contains("99", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_Reply_With_The_Request_Id_Is_Accepted()
    {
        // The first call of a client carries id 1.
        using var reply = new CannedReply("""{"jsonrpc":"2.0","id":1,"result":"pong"}""");
        using var http = new HttpClient(reply, disposeHandler: false);
        var client = new JsonRpcClient(http, Endpoint);

        var result = await client.CallAsync("ping", parameters: null, TestContext.Current.CancellationToken);

        Assert.Equal("pong", result.GetString());
    }

    [Fact]
    public async Task An_Error_With_A_Null_Id_Still_Surfaces_As_The_Error()
    {
        // The spec answers a request it could not read (parse error,
        // invalid request) with id null; that error belongs to this call.
        using var reply = new CannedReply(
            """{"jsonrpc":"2.0","id":null,"error":{"code":-32600,"message":"Invalid Request"}}""");
        using var http = new HttpClient(reply, disposeHandler: false);
        var client = new JsonRpcClient(http, Endpoint);

        var ex = await Assert.ThrowsAsync<JsonRpcException>(
            () => client.CallAsync("ping", parameters: null, TestContext.Current.CancellationToken));

        Assert.Equal(-32600, ex.Code);
    }

    private sealed class CannedReply(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
    }
}
