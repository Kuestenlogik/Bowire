// Minimal embedded Bowire sample. Hosts:
//  - Bowire workbench at /bowire
//  - A handful of sample REST endpoints under /api/... so the operator
//    can see whether Bowire auto-discovers the host's own routes when
//    embedded (the open question driving this sample).
//
// Run:
//   dotnet run --project samples/Kuestenlogik.Bowire.Sample.Embedded --urls http://localhost:5181

using Kuestenlogik.Bowire;
using Kuestenlogik.Bowire.Interceptor;

var builder = WebApplication.CreateBuilder(args);

// Bowire core registrations. AddBowire() pulls in the workbench
// services; protocol-specific packages register themselves via
// their own AddBowire*Protocol() extensions in their assemblies.
builder.Services.AddBowire();
builder.Services.AddEndpointsApiExplorer();

var app = builder.Build();

// #153 — Transparent in-process interceptor. Every request flowing
// through this host (any client, any tool) is tee'd into the
// workbench's "Intercepted" rail: method, path, headers, request body,
// response status, response body, latency. Zero client-side setup,
// zero cert trust, zero separate process. When the operator opens a
// recording in the workbench, intercepted flows also auto-append as
// recording steps. The Bowire workbench's own /bowire/* surface is
// excluded by default so the rail doesn't observe itself.
app.UseBowireInterceptor();

// Sample host routes — these are what the operator wants to see
// discovered automatically by the embedded Bowire.
app.MapGet("/api/users", () => new[]
{
    new { Id = 1, Name = "Ada Lovelace", Role = "Pioneer" },
    new { Id = 2, Name = "Grace Hopper", Role = "Admiral" },
    new { Id = 3, Name = "Margaret Hamilton", Role = "Engineer" }
}).WithName("ListUsers").WithTags("Users");

app.MapGet("/api/users/{id:int}", (int id) =>
{
    return id switch
    {
        1 => Results.Ok(new { Id = 1, Name = "Ada Lovelace", Role = "Pioneer" }),
        2 => Results.Ok(new { Id = 2, Name = "Grace Hopper", Role = "Admiral" }),
        3 => Results.Ok(new { Id = 3, Name = "Margaret Hamilton", Role = "Engineer" }),
        _ => Results.NotFound(new { error = $"user {id} not found" })
    };
}).WithName("GetUser").WithTags("Users");

app.MapPost("/api/users", (UserCreate body) =>
    Results.Created($"/api/users/42", new { Id = 42, body.Name, body.Role }))
    .WithName("CreateUser").WithTags("Users");

app.MapGet("/api/products", () => new[]
{
    new { Sku = "WIDGET-001", Name = "Widget", Price = 9.99m },
    new { Sku = "GADGET-002", Name = "Gadget", Price = 19.99m }
}).WithName("ListProducts").WithTags("Products");

app.MapGet("/api/health", () => Results.Ok(new { status = "ok", server = "embedded-sample" }))
    .WithName("Health").WithTags("Ops");

// Geo data — the coordinate field on each entry is { lat, lon }, which
// is the exact shape Bowire's built-in Wgs84CoordinateDetector picks up.
// First time the operator invokes either of these endpoints from the
// workbench, the frame prober tags the lat/lon paths as
// coordinate.latitude / coordinate.longitude; the workbench's extension
// router then asks Kuestenlogik.Bowire.Map (referenced from this csproj)
// for a coordinate.wgs84 widget and mounts the MapLibre viewer over the
// response — no OpenAPI extension or x-bowire-* hint required.
var locations = new[]
{
    new Location("fra-airport",   "Frankfurt Airport",        "airport", new GeoPoint(50.0379, 8.5622)),
    new Location("munich-hbf",    "München Hauptbahnhof", "station", new GeoPoint(48.1402, 11.5582)),
    new Location("kiel-port",     "Hafen Kiel",               "port",    new GeoPoint(54.3233, 10.1396)),
    new Location("hamburg-hbf",   "Hamburg Hauptbahnhof",     "station", new GeoPoint(53.5527, 10.0067)),
    new Location("berlin-tegel",  "Berlin Tegel (THF)",       "airport", new GeoPoint(52.5597, 13.2877)),
    new Location("vienna-hbf",    "Wien Hauptbahnhof",        "station", new GeoPoint(48.1851, 16.3754)),
    new Location("zurich-hb",     "Zürich Hauptbahnhof", "station", new GeoPoint(47.3779, 8.5403)),
    new Location("rotterdam-port","Port of Rotterdam",        "port",    new GeoPoint(51.9496, 4.1453)),
};

app.MapGet("/api/locations", () => locations)
    .WithName("ListLocations").WithTags("Locations");

app.MapGet("/api/locations/{id}", (string id) =>
{
    var match = Array.Find(locations, l => string.Equals(l.Id, id, StringComparison.OrdinalIgnoreCase));
    return match is null
        ? Results.NotFound(new { error = $"location '{id}' not found" })
        : Results.Ok(match);
}).WithName("GetLocation").WithTags("Locations");

// Bowire workbench mounted at /bowire — embedded mode convention.
app.MapBowire("/bowire");

// Root redirect so a curious operator hitting / lands somewhere useful.
app.MapGet("/", () => Results.Redirect("/bowire"));

app.Run();

internal sealed record UserCreate(string Name, string Role);

// Geo records used by the /api/locations endpoints. The Coordinate
// member's { lat, lon } shape is what triggers Bowire's WGS84 auto-
// detection — the names are anchored, case-insensitive, and bounded
// by the [-90,90] / [-180,180] ranges the detector enforces.
internal sealed record Location(string Id, string Name, string Kind, GeoPoint Coordinate);
internal sealed record GeoPoint(double Lat, double Lon);
