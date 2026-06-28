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

// HTTP/2 + HTTP/1.1 on every listener. Without this Kestrel defaults
// to HTTP/1.1-only on http://localhost:5182 so any gRPC client probing
// the server gets back HTTP/2 error HTTP_1_1_REQUIRED, and Bowire's
// generic gRPC discovery fails before reaching server reflection. The
// landing page on `/` still works because it falls back to HTTP/1.1
// transparently. Operator: 'http://localhost:5182 sagt mir als source
// 0 services. fehlt grpc reflection im sample?'
builder.WebHost.ConfigureKestrel(options =>
{
    options.ConfigureEndpointDefaults(lo =>
    {
        lo.Protocols = HttpProtocols.Http1AndHttp2;
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
    /// Server-streaming variant — emits the same seeded list as a
    /// single frame, then closes. Bowire's gRPC plugin treats this as a
    /// one-shot stream which is exactly enough to drive the map widget
    /// from the response JSON.
    /// </summary>
    public override async Task SubscribeSituationObjectEvents(
        SubscribeSituationObjectEventsRequest request,
        IServerStreamWriter<SubscribeSituationObjectEventsResponse> responseStream,
        ServerCallContext context)
    {
        var frame = new SubscribeSituationObjectEventsResponse
        {
            Header = new ResponseHeader { Success = true },
        };

        foreach (var (uuid, name, lat, lon) in Seed)
        {
            frame.SituationObjects.Add(BuildSymbol(uuid, name, lat, lon));
        }

        await responseStream.WriteAsync(frame, context.CancellationToken).ConfigureAwait(false);
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
