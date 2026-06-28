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
/// Seeds eight fictional tactical entities — NATO-phonetic callsigns
/// over the DACH region — so the map widget shows a recognisable
/// tactical situation: friendly air picture, surface units, an air
/// defence battery, a maritime patrol vessel, and a reconnaissance
/// UAV. Operator: 'das beispiel ist für tactical vermutlich auch
/// nicht so gut mit hamburg-hbf usw.' and 'uuidIdentity ist auch
/// keine uuid in den messages.' Both fixed here.
/// </summary>
internal sealed class SeededSituationService : Situation.SituationBase
{
    // Eight fictional tactical entities spanning the DACH airspace /
    // landspace. Tuple shape keeps the seed table compact: (uuid,
    // callsign + role, lat, lon). UUIDs are real Guids (stable across
    // cycles so each callsign keeps its identity as it drifts), and
    // callsigns follow the NATO phonetic family with a role suffix so
    // the operator immediately reads them as tactical objects rather
    // than civilian transport hubs.
    private static readonly (string Uuid, string Name, double Lat, double Lon)[] Seed =
    [
        ("c5b3b5b6-1a2d-4e9b-8c0a-1f7a2d9c1a01", "Alpha-1 — Recon UAV",          50.0379,  8.5622),
        ("c5b3b5b6-1a2d-4e9b-8c0a-1f7a2d9c1a02", "Bravo-2 — Air Defence Btry",   48.1402, 11.5582),
        ("c5b3b5b6-1a2d-4e9b-8c0a-1f7a2d9c1a03", "Charlie-3 — Patrol Vessel",    54.3233, 10.1396),
        ("c5b3b5b6-1a2d-4e9b-8c0a-1f7a2d9c1a04", "Delta-4 — Mobile HQ",          53.5527, 10.0067),
        ("c5b3b5b6-1a2d-4e9b-8c0a-1f7a2d9c1a05", "Echo-5 — Fwd Observation",     52.5597, 13.2877),
        ("c5b3b5b6-1a2d-4e9b-8c0a-1f7a2d9c1a06", "Foxtrot-6 — Logistics Conv",   48.1851, 16.3754),
        ("c5b3b5b6-1a2d-4e9b-8c0a-1f7a2d9c1a07", "Golf-7 — AWACS Track",         47.3779,  8.5403),
        ("c5b3b5b6-1a2d-4e9b-8c0a-1f7a2d9c1a08", "Hotel-8 — Engineering Det",    48.6898,  9.2220),
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
    /// object, spaced ~400 ms apart. Each object's coordinate drifts
    /// by a small per-cycle delta so the operator sees actual movement
    /// on the map (pins reposition over time rather than restating the
    /// same coords). Loops until the caller cancels.
    /// Operator: 'wenn ich subscribe sehe ich in der karte keine
    /// änderungen. die positionen scheinen immer die gleichen zu
    /// bleiben, richtig?' — yes, the previous version re-emitted
    /// identical coords on every cycle.
    /// </summary>
    public override async Task SubscribeSituationObjectEvents(
        SubscribeSituationObjectEventsRequest request,
        IServerStreamWriter<SubscribeSituationObjectEventsResponse> responseStream,
        ServerCallContext context)
    {
        var tick = 0;
        while (!context.CancellationToken.IsCancellationRequested)
        {
            for (var i = 0; i < Seed.Length; i++)
            {
                if (context.CancellationToken.IsCancellationRequested) break;
                var (uuid, name, baseLat, baseLon) = Seed[i];
                // Sine-wave drift around the seed coordinate. The phase
                // is per-object (offset by index) so the eight pins
                // each follow their own little loop. Amplitude ~0.02
                // degrees ≈ 2 km at DACH latitudes — visible on the
                // map without flying off the seed point.
                var phase = (tick + i * 7) * Math.PI / 18.0;
                var dLat = 0.02 * Math.Sin(phase);
                var dLon = 0.02 * Math.Cos(phase);
                var frame = new SubscribeSituationObjectEventsResponse
                {
                    Header = new ResponseHeader { Success = true },
                };
                frame.SituationObjects.Add(BuildSymbol(uuid, name, baseLat + dLat, baseLon + dLon));
                await responseStream.WriteAsync(frame, context.CancellationToken).ConfigureAwait(false);
                try
                {
                    await Task.Delay(400, context.CancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { return; }
            }
            tick++;
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
