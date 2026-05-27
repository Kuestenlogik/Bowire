// Tiny REST sample for the Bowire REST plugin demo. Exposes an
// in-memory pet store with /pets, /pets/{id}, plus a POST /pets to
// add new entries. Built on minimal APIs + the .NET 10 OpenAPI
// generator so Bowire's REST plugin can discover the surface by
// fetching /openapi/v1.json.

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://localhost:5181");
builder.Services.AddOpenApi();

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

await app.RunAsync();

record Pet(int Id, string Name, string Species);
record PetInput(string Name, string Species);
