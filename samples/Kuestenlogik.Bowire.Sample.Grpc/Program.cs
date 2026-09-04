// Combined gRPC sample for Bowire. This one project tells both stories
// at once:
//
//   * Embedded hosting — the full Bowire workbench is mounted at /bowire
//     in-process, and the catalogue (grpc-catalogue.json) seeds the
//     Sources rail with this very host — over both transports — so
//     Greeter is discovered the moment you open the page.
//   * Separate hosting — because it is a real gRPC server, it doubles as
//     a standalone target: point an external workbench or
//     `bowire --url grpc@http://localhost:5183` at it and get the same
//     surface.
//
// Two cleartext Kestrel ports carry it. One would be nicer, but Kestrel
// only picks HTTP/2 on a shared port through the TLS ALPN handshake — a
// plaintext `Http1AndHttp2` endpoint answers HTTP/1.1 only and rejects
// the h2c preface with GOAWAY(HTTP_1_1_REQUIRED). So:
//
//   :5182  HTTP/1.1  the workbench UI, and gRPC-Web (which rides HTTP/1.1)
//   :5183  h2c       native gRPC — the transport all four call types need
//
// A TLS endpoint would fold both onto one port, at the price of making
// the sample depend on `dotnet dev-certs https --trust`.
//
// The Greeter covers all four gRPC call types — unary, server
// streaming, client streaming, duplex — plus the bits that make gRPC
// gRPC and not "JSON over HTTP/2": request metadata, response trailers
// and non-OK status codes.
//
// Run:
//   dotnet run --project samples/Kuestenlogik.Bowire.Sample.Grpc
//   → open http://localhost:5182/bowire
//
// Hardened shape — the one production is supposed to look like:
//   dotnet run --project samples/Kuestenlogik.Bowire.Sample.Grpc -- --hardened
//
// That drops the embedded workbench and, with it, Server Reflection: the
// plugin's MapBowire() hook is what maps the reflection endpoint here.
// What is left is a bare gRPC server that will not tell a caller what it
// hosts — so a client needs a descriptor set built from greeter.proto:
//
//   protoc --descriptor_set_out=greeter.protoset --include_imports \
//          -I samples/Kuestenlogik.Bowire.Sample.Grpc/Protos greeter.proto
//   bowire call grpc@http://localhost:5183 bowire.samples.greeter.Greeter/SayHello \
//          -d '{"name":"world"}' --grpc-descriptor-set greeter.protoset

using Bowire.Samples.Greeter;
using Grpc.Core;
using Kuestenlogik.Bowire;
using Kuestenlogik.Bowire.Sources;
using Microsoft.AspNetCore.Server.Kestrel.Core;

var builder = WebApplication.CreateBuilder(args);

// `--hardened` runs the sample the way a real deployment runs: no
// reflection, no workbench, just the service. It exists because the
// reflection-off case is the one a client library is most likely to get
// wrong, and it could not be reproduced from this sample before.
var hardened = args.Contains("--hardened", StringComparer.Ordinal);
builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenLocalhost(5182, listen => listen.Protocols = HttpProtocols.Http1);
    options.ListenLocalhost(5183, listen => listen.Protocols = HttpProtocols.Http2);
});

// The gRPC server surface. Reflection lets Bowire's gRPC plugin discover
// the service without a .proto upload. AddGrpcReflection() is idempotent
// and AddBowire() calls it too — spelled out here so the server half
// stands on its own if you lift it out of the sample.
builder.Services.AddGrpc();
if (!hardened) builder.Services.AddGrpcReflection();

// Embedded Bowire workbench + catalogue-driven discovery. The catalogue
// provider (local, reading grpc-catalogue.json) points the Sources rail
// at this host over gRPC.
if (!hardened)
{
    builder.Services.AddBowire();
    builder.Services.AddBowireCatalogue(builder.Configuration);
}

var app = builder.Build();

// gRPC-Web bridges the gRPC pipeline onto plain HTTP/1.1 — the shape a
// browser or an Envoy-fronted service sees. Enabling it makes
// `bowire --url grpcweb@http://localhost:5182` a runnable target on the
// same port the UI is served from. Unary and server streaming work over
// either transport; client streaming and duplex need native HTTP/2 on
// :5183, because HTTP/1.1 carries no trailers (docs/protocols/grpc.md).
//
// DefaultEnabled rather than per-endpoint `.EnableGrpcWeb()`: the
// reflection endpoint is mapped by MapBowire() below (see the note
// there), so there is no convention builder here to hang the opt-in
// off — and grpcweb@ discovery needs reflection on the web transport
// too. A host that maps every gRPC service itself can drop the options
// object and write `app.MapGrpcService<T>().EnableGrpcWeb()` instead.
if (!hardened) app.UseGrpcWeb(new GrpcWebOptions { DefaultEnabled = true });

app.MapGrpcService<GreeterService>();

// Workbench mounted at /bowire — embedded-mode convention. This also
// maps gRPC reflection: in embedded mode MapBowire() runs the gRPC
// plugin's IBowireProtocolServices hook, which calls
// MapGrpcReflectionService() for us. Mapping it here as well makes the
// route ambiguous and every reflection call 500s, so the sample leaves
// it to the plugin.
if (!hardened)
{
    app.MapBowire("/bowire");
    app.MapGet("/", () => Results.Redirect("/bowire"));
}

await app.RunAsync();

sealed class GreeterService : Greeter.GreeterBase
{
    // Request metadata the calls look for. Set it in the workbench's
    // Headers panel and every reply echoes it back in `caller`.
    private const string CallerHeader = "x-caller";

    public override Task<HelloReply> SayHello(HelloRequest request, ServerCallContext context)
    {
        // Trailers are written up front so they ride along on the fault
        // path too. The gRPC plugin surfaces trailers as `_trailer:*`
        // entries on the invoke result (GrpcInvoker's RpcException
        // branch), so the InvalidArgument case below is where they
        // become visible in the workbench.
        context.ResponseTrailers.Add("x-greeter-version", "1.0");

        // Non-OK status: an empty name is the caller's mistake, so it
        // comes back as InvalidArgument rather than a 200-with-excuse.
        // The exception's own metadata is merged into the trailers.
        if (string.IsNullOrWhiteSpace(request.Name))
            throw new RpcException(
                new Status(StatusCode.InvalidArgument, "name must not be empty."),
                new Metadata { { "x-greeter-hint", "set 'name' to any non-empty string" } });

        var caller = context.RequestHeaders.GetValue(CallerHeader) ?? "anonymous";
        context.ResponseTrailers.Add("x-greeter-caller", caller);

        return Task.FromResult(Greet(request, caller));
    }

    // Read-only by name and by behaviour: no trailers, no validation, no
    // state. It answers an empty request, which is what makes it usable
    // as the probe target for `bowire scan`'s gRPC auth check.
    public override Task<HelloReply> GetGreeting(GetGreetingRequest request, ServerCallContext context)
    {
        var caller = context.RequestHeaders.GetValue(CallerHeader) ?? "anonymous";
        return Task.FromResult(new HelloReply
        {
            Message = request.Language switch
            {
                Language.German => "Hallo!",
                Language.French => "Bonjour !",
                _ => "Hello!",
            },
            Language = request.Language == Language.Unspecified ? Language.English : request.Language,
            Caller = caller,
        });
    }

    public override async Task SayHelloStream(HelloRequest request,
        IServerStreamWriter<HelloReply> responseStream, ServerCallContext context)
    {
        var caller = context.RequestHeaders.GetValue(CallerHeader) ?? "anonymous";
        var language = Effective(request.Language);

        for (var i = 1; i <= 5 && !context.CancellationToken.IsCancellationRequested; i++)
        {
            await responseStream.WriteAsync(new HelloReply
            {
                Message = $"{Salutation(language)} #{i}, {request.Name}!",
                Language = language,
                Caller = caller
            });
            await Task.Delay(TimeSpan.FromMilliseconds(500), context.CancellationToken);
        }
    }

    // Client streaming. The workbench queues N messages and ships them
    // in one go; this single summary lands after the request stream
    // completes.
    public override async Task<HelloSummary> SayHelloBatch(
        IAsyncStreamReader<HelloRequest> requestStream, ServerCallContext context)
    {
        var summary = new HelloSummary();

        await foreach (var request in requestStream.ReadAllAsync(context.CancellationToken))
        {
            summary.Greeted++;
            summary.Names.Add(string.IsNullOrWhiteSpace(request.Name) ? "(nobody)" : request.Name);
            // Last explicit language on the stream wins — enough to show
            // the enum surviving a client-streamed batch.
            if (request.Language != Language.Unspecified)
                summary.Language = request.Language;
        }

        summary.Language = Effective(summary.Language);
        summary.Message = summary.Greeted == 0
            ? "Nobody to greet."
            : $"{Salutation(summary.Language)}, {string.Join(" & ", summary.Names)}! "
              + $"({summary.Greeted} in one batch)";

        return summary;
    }

    // Duplex. Each reply is written the moment its request arrives, so
    // both directions really do interleave rather than the server
    // draining the request stream first.
    public override async Task Converse(IAsyncStreamReader<HelloRequest> requestStream,
        IServerStreamWriter<HelloReply> responseStream, ServerCallContext context)
    {
        var caller = context.RequestHeaders.GetValue(CallerHeader) ?? "anonymous";
        var turn = 0;

        await foreach (var request in requestStream.ReadAllAsync(context.CancellationToken))
        {
            turn++;
            var language = Effective(request.Language);
            await responseStream.WriteAsync(new HelloReply
            {
                Message = string.IsNullOrWhiteSpace(request.Name)
                    ? $"Turn {turn}: who's there?"
                    : $"{Salutation(language)}, {request.Name}! (turn {turn})",
                Language = language,
                Caller = caller
            });
        }

        // A closing frame so the channel visibly winds down instead of
        // just going quiet. Skipped when the client hung up first.
        if (!context.CancellationToken.IsCancellationRequested)
            await responseStream.WriteAsync(new HelloReply
            {
                Message = $"Goodbye — {turn} turn(s).",
                Language = Language.English,
                Caller = caller
            });
    }

    // proto3 enums always carry a zero value; treat it as English so an
    // unset `language` is still a valid request.
    private static Language Effective(Language language)
        => language == Language.Unspecified ? Language.English : language;

    private static string Salutation(Language language) => language switch
    {
        Language.German => "Hallo",
        Language.French => "Bonjour",
        _ => "Hello"
    };

    private static HelloReply Greet(HelloRequest request, string caller)
    {
        var language = Effective(request.Language);
        var message = $"{Salutation(language)}, {request.Name}!";

        // The nested Caller message with an observable effect, so it is
        // worth filling in the form the plugin renders for it.
        if (request.Caller is { Priority: 1 })
            message = message.ToUpperInvariant();
        if (request.Caller is { Team.Length: > 0 })
            message += $" (via {request.Caller.Team})";

        return new HelloReply { Message = message, Language = language, Caller = caller };
    }
}
