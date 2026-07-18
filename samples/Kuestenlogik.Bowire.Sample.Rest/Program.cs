// Combined REST sample for Bowire. One project, both stories:
//
//   * Embedded — the workbench is mounted at /bowire and the bundled
//     rest-catalogue.json seeds the Sources rail with this host; the REST
//     plugin discovers the surface by fetching /openapi/v1.json.
//   * Separate — it is a real REST server, so point an external workbench
//     or `bowire --url rest@http://localhost:5181` at it.
//
// An in-memory pet store: /pets, /pets/{id}, POST /pets, DELETE /pets/{id}.
//
// Run:
//   dotnet run --project samples/Kuestenlogik.Bowire.Sample.Rest
//   → open http://localhost:5181/bowire

using Kuestenlogik.Bowire;
using Kuestenlogik.Bowire.Sources;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://localhost:5181");
builder.Services.AddOpenApi();

builder.Services.AddBowire();
builder.Services.AddBowireCatalogue(builder.Configuration);

var app = builder.Build();
app.MapOpenApi();

var pets = new List<Pet>
{
    new(1, "Tigerlilly", "cat"),
    new(2, "Rex", "dog"),
    new(3, "Comet", "hamster"),
};
var nextId = pets.Count + 1;

app.MapGet("/pets", () => pets);
app.MapGet("/pets/{id:int}", (int id) =>
    pets.FirstOrDefault(p => p.Id == id) is { } pet
        ? Results.Ok(pet)
        : Results.NotFound());
app.MapPost("/pets", (PetInput input) =>
{
    var pet = new Pet(nextId++, input.Name, input.Species);
    pets.Add(pet);
    return Results.Created($"/pets/{pet.Id}", pet);
});
app.MapDelete("/pets/{id:int}", (int id) =>
    pets.RemoveAll(p => p.Id == id) > 0 ? Results.NoContent() : Results.NotFound());

app.MapBowire("/bowire");
app.MapGet("/", () => Results.Redirect("/bowire"));
await app.RunAsync();

sealed record Pet(int Id, string Name, string Species);
sealed record PetInput(string Name, string Species);
