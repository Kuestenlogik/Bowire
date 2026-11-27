// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Grpc.Core;
using Grpc.Reflection.V1Alpha;

namespace Kuestenlogik.Bowire.Protocol.Grpc.Tests;

/// <summary>
/// #1063 — a server without the reflection service can answer
/// <c>Unimplemented</c> while the request is still being written. The write
/// then fails with a <c>Cancelled</c> from the closed HTTP/2 stream; the
/// server's own answer is what the caller has to see. Timing-dependent on the
/// wire (about 1 call in 200 under load), so pinned here with a call whose
/// write fails that way by construction.
/// </summary>
public sealed class GrpcReflectionEarlyAnswerTests
{
    private sealed class FailingWriter(RpcException failure) : IClientStreamWriter<ServerReflectionRequest>
    {
        public WriteOptions? WriteOptions { get; set; }
        public Task WriteAsync(ServerReflectionRequest message) => Task.FromException(failure);
        public Task WriteAsync(ServerReflectionRequest message, CancellationToken cancellationToken) => Task.FromException(failure);
        public Task CompleteAsync() => Task.CompletedTask;
    }

    private sealed class AnsweringReader(RpcException? answer) : IAsyncStreamReader<ServerReflectionResponse>
    {
        public ServerReflectionResponse Current => new();
        public Task<bool> MoveNext(CancellationToken cancellationToken)
            => answer is null ? Task.FromResult(false) : Task.FromException<bool>(answer);
    }

    private static AsyncDuplexStreamingCall<ServerReflectionRequest, ServerReflectionResponse> Call(RpcException? answer)
        => new(
            new FailingWriter(new RpcException(new Status(StatusCode.Cancelled, ""))),
            new AnsweringReader(answer),
            Task.FromResult(new Metadata()),
            () => Status.DefaultSuccess,
            () => [],
            () => { });

    [Fact]
    public async Task A_Write_Cut_Short_By_The_Servers_Answer_Reports_That_Answer()
    {
        using var call = Call(new RpcException(new Status(StatusCode.Unimplemented, "Service is unimplemented.")));

        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            GrpcReflectionClient.SendAsync(call, new ServerReflectionRequest { ListServices = "" }, TestContext.Current.CancellationToken));

        Assert.Equal(StatusCode.Unimplemented, ex.StatusCode);
    }

    [Fact]
    public async Task Without_An_Answer_The_Failed_Write_Stands()
    {
        using var call = Call(answer: null);

        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            GrpcReflectionClient.SendAsync(call, new ServerReflectionRequest { ListServices = "" }, TestContext.Current.CancellationToken));

        Assert.Equal(StatusCode.Cancelled, ex.StatusCode);
    }

    [Fact]
    public async Task A_Caller_Who_Cancelled_Gets_Cancelled()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        using var call = Call(new RpcException(new Status(StatusCode.Unimplemented, "")));

        var ex = await Assert.ThrowsAsync<RpcException>(() =>
            GrpcReflectionClient.SendAsync(call, new ServerReflectionRequest { ListServices = "" }, cts.Token));

        Assert.Equal(StatusCode.Cancelled, ex.StatusCode);
    }
}
