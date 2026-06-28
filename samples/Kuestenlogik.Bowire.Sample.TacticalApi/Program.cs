// Minimal stand-alone gRPC server hosting Rheinmetall's TacticalAPI
// Situation service. The point of this sample is to give an operator a
// real endpoint to point Bowire's standalone Tool at: dial it, invoke
// GetSituationObjects, and watch the Wgs84CoordinateDetector pin every
// seeded DACH airfield / harbour / station on the MapLibre viewer.
//
// No Bowire workbench reference lives here — this is a pure SERVER. The
// Tool (separate process, http://localhost:5180) acts as the client.
//
// Run:
//   dotnet run --project samples/Kuestenlogik.Bowire.Sample.TacticalApi --urls http://localhost:5182
//
// Then point Bowire at http://localhost:5182 as a gRPC source. The
// TacticalApi plugin (shipped in Bundle.Workbench) matches the URL,
// surfaces the Situation service, and the response JSON's
// latitude_coordinate / longitude_coordinate fields drive the map
// widget — Wgs84CoordinateDetector's anchored regex accepts the
// upstream `*_coordinate` suffix exactly so DACH operators don't need
// any per-field configuration.

using Grpc.Core;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Rheinmetall.TacticalApi.V0;

var builder = WebApplication.CreateBuilder(args);

// HTTP/2 cleartext only on every listener. Plain http:// gRPC requires
// the client to start with HTTP/2 prior-knowledge — Kestrel's
// Http1AndHttp2 default only upgrades to HTTP/2 via TLS + ALPN, so a
// fresh HTTP/1.1 connection never negotiates h2c and the gRPC handshake
// fails. Going Http2 only kills the HTML landing page on `/` (browsers
// don't speak h2c without TLS) but unblocks Bowire's generic gRPC
// discovery from a plain `grpc@http://localhost:5182` URL.
builder.WebHost.ConfigureKestrel(options =>
{
    options.ConfigureEndpointDefaults(lo =>
    {
        lo.Protocols = HttpProtocols.Http2;
    });
});

// AddGrpc registers the routing / serialization stack the
// MapGrpcService<T>() call needs. The hosted service is plain
// HTTP/2 cleartext on the loopback so the Tool can dial in without
// TLS — for any real Rheinmetall deployment, swap in Kestrel TLS +
// MAGENTA / national PKI; this sample stays minimal so the operator
// can run it with a single `dotnet run`.
builder.Services.AddGrpc();
// Server Reflection — lets the operator dial the sample with a plain
// `grpc@http://localhost:5182` URL in Bowire and have the generic gRPC
// plugin auto-discover the Situation service. Without it, only the
// `tacticalapi@...` hint works (the TacticalApi plugin uses bundled
// descriptors instead of probing the server).
builder.Services.AddGrpcReflection();

var app = builder.Build();
app.MapGrpcService<SeededSituationService>();
app.MapGrpcReflectionService();

// Tiny landing page on `/` so a curious operator hitting the URL in a
// browser sees something useful rather than the "this server only
// speaks HTTP/2" gRPC handshake error.
app.MapGet("/", () => Results.Text(
    """
    Bowire TacticalAPI sample server.

    This host speaks gRPC on HTTP/2. Point Bowire's Tool at
    http://localhost:5182 as a gRPC source — the TacticalApi plugin will
    surface the Situation service. Invoke GetSituationObjects and the
    seeded DACH situation objects (airfields, harbours, stations) appear
    on the map.
    """, "text/plain"));

app.Run();

/// <summary>
/// In-process implementation of Rheinmetall's <c>Situation</c> service.
/// Seeds a handful of real DACH airfields, harbours, and stations so
/// the operator sees recognisable pins on the map widget. The shape
/// mirrors the test-fixture seed in
/// <c>Kuestenlogik.Bowire.Protocol.TacticalApi.Tests/Integration/InProcessSituationServerFixture.cs</c>,
/// extended with <see cref="GeoPoint"/> positions on every symbol so
/// Bowire's <c>Wgs84CoordinateDetector</c> has lat/lon pairs to grab.
/// </summary>
internal sealed class SeededSituationService : Situation.SituationBase
{
    // Eight DACH points spanning Germany / Austria / Switzerland. Tuple
    // shape keeps the seed table compact: (uuid, display name, lat,
    // lon). Coordinates are real WGS84 positions so the map widget zooms
    // onto a recognisable area when the operator invokes the method.
    private static readonly (string Uuid, string Name, double Lat, double Lon)[] Seed =
    [
        ("frankfurt-airport", "Frankfurt Airport (EDDF)",   50.0379,  8.5622),
        ("munich-hbf",        "Munchen Hauptbahnhof",       48.1402, 11.5582),
        ("kiel-port",         "Hafen Kiel",                 54.3233, 10.1396),
        ("hamburg-hbf",       "Hamburg Hauptbahnhof",       53.5527, 10.0067),
        ("berlin-tegel",      "Berlin Tegel (EDDT)",        52.5597, 13.2877),
        ("vienna-hbf",        "Wien Hauptbahnhof",          48.1851, 16.3754),
        ("zurich-hb",         "Zurich Hauptbahnhof",        47.3779,  8.5403),
        ("stuttgart-airport", "Stuttgart Airport (EDDS)",   48.6898,  9.2220),
    ];

    /// <summary>
    /// Returns every seeded <see cref="SituationObject"/>. Each one
    /// carries a <see cref="Symbol"/> with a <see cref="Point"/>
    /// location wrapped in <see cref="DataPropertyLocation"/>, and the
    /// inner <see cref="GeoPoint"/> sets <c>latitude_coordinate</c> +
    /// <c>longitude_coordinate</c> — the exact protobuf field names the
    /// detector's regex picks up via the optional <c>(coordinate)?</c>
    /// suffix.
    /// </summary>
    public override Task<GetSituationObjectsResponse> GetSituationObjects(
        GetSituationObjectsRequest request, ServerCallContext context)
    {
        var response = new GetSituationObjectsResponse
        {
            Header = new ResponseHeader { Success = true },
        };

        foreach (var (uuid, name, lat, lon) in Seed)
        {
            response.SituationObjects.Add(BuildSymbol(uuid, name, lat, lon));
        }

        return Task.FromResult(response);
    }

    /// <summary>
    /// Server-streaming variant — emits one frame per seeded situation
    /// object, spaced ~400 ms apart, so the operator sees the streaming
    /// path actually iterate in the workbench (pins land on the map
    /// one-by-one). Loops until the caller cancels the stream so the
    /// "Subscribe" semantics are real: re-emits the same eight objects
    /// every cycle with the seed shifted by one position, simulating
    /// a continuously updating tactical situation. Operator: 'bei
    /// subscribe situationobjects in der tacticalapi bekomme ich im
    /// test bei execute nur eine message zurück.'
    /// </summary>
    public override async Task SubscribeSituationObjectEvents(
        SubscribeSituationObjectEventsRequest request,
        IServerStreamWriter<SubscribeSituationObjectEventsResponse> responseStream,
        ServerCallContext context)
    {
        var offset = 0;
        while (!context.CancellationToken.IsCancellationRequested)
        {
            for (var i = 0; i < Seed.Length; i++)
            {
                if (context.CancellationToken.IsCancellationRequested) break;
                var (uuid, name, lat, lon) = Seed[(i + offset) % Seed.Length];
                var frame = new SubscribeSituationObjectEventsResponse
                {
                    Header = new ResponseHeader { Success = true },
                };
                frame.SituationObjects.Add(BuildSymbol(uuid, name, lat, lon));
                await responseStream.WriteAsync(frame, context.CancellationToken).ConfigureAwait(false);
                try
                {
                    await Task.Delay(400, context.CancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { return; }
            }
            offset++;
        }
    }

    // Symbol → DataPropertyLocation → SymbolLocation { point } → Point →
    // GeoPoint. The detector walks the JSON tree and picks up the lat /
    // lon pair at the GeoPoint level — every other layer is pass-through
    // protobuf wrappers TacticalAPI uses for change-tracking.
    private static SituationObject BuildSymbol(string uuid, string name, double lat, double lon)
    {
        var geoPoint = new GeoPoint
        {
            LatitudeCoordinate = lat,
            LongitudeCoordinate = lon,
        };

        return new SituationObject
        {
            Symbol = new Symbol
            {
                Identity = new Identity { UuidIdentity = uuid },
                Name = new DataPropertyString
                {
                    Content = name,
                },
                Location = new DataPropertyLocation
                {
                    Content = new SymbolLocation
                    {
                        Point = new Point
                        {
                            Name = name,
                            GeoPoint = geoPoint,
                        },
                    },
                },
            },
        };
    }
}
