// Minimal embedded Bowire sample. Hosts:
//  - Bowire workbench at /bowire
//  - A handful of sample REST endpoints under /api/... so the operator
//    can see whether Bowire auto-discovers the host's own routes when
//    embedded (the open question driving this sample).
//
// Run:
//   dotnet run --project samples/Kuestenlogik.Bowire.Sample.Embedded --urls http://localhost:5181

using Kuestenlogik.Bowire;

var builder = WebApplication.CreateBuilder(args);

// Bowire core registrations. AddBowire() pulls in the workbench
// services; protocol-specific packages register themselves via
// their own AddBowire*Protocol() extensions in their assemblies.
builder.Services.AddBowire();
builder.Services.AddEndpointsApiExplorer();

var app = builder.Build();

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

// Bowire workbench mounted at /bowire — embedded mode convention.
app.MapBowire("/bowire");

// Root redirect so a curious operator hitting / lands somewhere useful.
app.MapGet("/", () => Results.Redirect("/bowire"));

app.Run();

internal sealed record UserCreate(string Name, string Role);
