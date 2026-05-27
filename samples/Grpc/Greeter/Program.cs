// gRPC Greeter sample for the Bowire gRPC plugin demo. Hosts both
// the classic unary SayHello plus a server-streaming SayHelloStream
// so the workbench's streaming preview has something to consume.
// Server Reflection is on so the plugin discovers the service without
// the user having to upload the .proto file.

using Bowire.Samples.Greeter;
using Grpc.Core;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://localhost:5182");
builder.Services.AddGrpc();
builder.Services.AddGrpcReflection();

var app = builder.Build();
app.MapGrpcService<GreeterService>();
app.MapGrpcReflectionService();
await app.RunAsync();

sealed class GreeterService : Greeter.GreeterBase
{
    public override Task<HelloReply> SayHello(HelloRequest request, ServerCallContext context)
        => Task.FromResult(new HelloReply { Message = $"Hello, {request.Name}!" });

    public override async Task SayHelloStream(HelloRequest request,
        IServerStreamWriter<HelloReply> responseStream, ServerCallContext context)
    {
        for (var i = 1; i <= 5 && !context.CancellationToken.IsCancellationRequested; i++)
        {
            await responseStream.WriteAsync(new HelloReply { Message = $"Hello #{i}, {request.Name}!" });
            await Task.Delay(TimeSpan.FromMilliseconds(500), context.CancellationToken);
        }
    }
}
