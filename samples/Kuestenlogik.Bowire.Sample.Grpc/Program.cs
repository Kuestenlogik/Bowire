// Combined gRPC sample for Bowire. This one project tells both stories
// at once:
//
//   * Embedded hosting — the full Bowire workbench is mounted at /bowire
//     in-process, and the catalogue (grpc-catalogue.json) seeds the
//     Sources rail with this very host so Greeter is discovered the
//     moment you open the page.
//   * Separate hosting — because it is a real gRPC server, it doubles as
//     a standalone target: point an external workbench or
//     `bowire --url grpc@http://localhost:5182` at it and get the same
//     surface.
//
// A single cleartext Kestrel port carries both: the default
// Http1AndHttp2 negotiates HTTP/2 (gRPC, prior-knowledge) and HTTP/1.1
// (the workbench UI) on the same socket — no dual-port juggling.
//
// Run:
//   dotnet run --project samples/Kuestenlogik.Bowire.Sample.Grpc
//   → open http://localhost:5182/bowire

using Bowire.Samples.Greeter;
using Grpc.Core;
using Kuestenlogik.Bowire;
using Kuestenlogik.Bowire.Sources;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://localhost:5182");

// The gRPC server surface. Reflection lets Bowire's gRPC plugin discover
// the service without a .proto upload.
builder.Services.AddGrpc();
builder.Services.AddGrpcReflection();

// Embedded Bowire workbench + catalogue-driven discovery. The catalogue
// provider (local, reading grpc-catalogue.json) points the Sources rail
// at this host over gRPC.
builder.Services.AddBowire();
builder.Services.AddBowireCatalogue(builder.Configuration);

var app = builder.Build();

app.MapGrpcService<GreeterService>();
app.MapGrpcReflectionService();

// Workbench mounted at /bowire — embedded-mode convention.
app.MapBowire("/bowire");
app.MapGet("/", () => Results.Redirect("/bowire"));

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
