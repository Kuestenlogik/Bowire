// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Grpc.Core;

namespace Kuestenlogik.Bowire.IntegrationTests.Services;

internal sealed class GreeterService : Greeter.GreeterBase
{
    public override Task<HelloReply> SayHello(HelloRequest request, ServerCallContext context)
    {
        return Task.FromResult(new HelloReply
        {
            Message = $"Hello {request.Name}!",
            Sequence = 1
        });
    }

    public override async Task SayHelloStream(HelloRequest request,
        IServerStreamWriter<HelloReply> responseStream, ServerCallContext context)
    {
        var count = request.Count > 0 ? request.Count : 3;
        for (var i = 1; i <= count; i++)
        {
            await responseStream.WriteAsync(new HelloReply
            {
                Message = $"Hello {request.Name} #{i}",
                Sequence = i
            });
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
    }

    // Client-streaming: drain every incoming HelloRequest, then reply once
    // with a concatenated greeting + the count of names received.
    public override async Task<HelloReply> CollectHellos(
        IAsyncStreamReader<HelloRequest> requestStream, ServerCallContext context)
    {
        var names = new List<string>();
        await foreach (var req in requestStream.ReadAllAsync())
            names.Add(req.Name);

        return new HelloReply
        {
            Message = $"Hello {string.Join(",", names)}!",
            Sequence = names.Count
        };
    }

    // Bidi: echo each incoming request back as a HelloReply. Sequence
    // numbering starts at 1 and increments per received request, so the
    // client can observe the gating between sends and reads.
    public override async Task ChatHellos(
        IAsyncStreamReader<HelloRequest> requestStream,
        IServerStreamWriter<HelloReply> responseStream, ServerCallContext context)
    {
        var seq = 0;
        await foreach (var req in requestStream.ReadAllAsync())
        {
            seq++;
            await responseStream.WriteAsync(new HelloReply
            {
                Message = $"Hello {req.Name}!",
                Sequence = seq
            });
        }
    }
}
