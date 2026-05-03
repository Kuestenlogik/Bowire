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
}
