// Combined OData v4 sample for Bowire. One project, both stories:
//
//   * Embedded — the workbench is mounted at /bowire and the bundled
//     odata-catalogue.json seeds the Sources rail with this host's /odata
//     endpoint; Bowire fetches the CSDL/EDMX and surfaces Categories +
//     Products as services.
//   * Separate — it is a real OData server, so point an external workbench
//     or `bowire --url odata@http://localhost:5188/odata` at it.
//
// The entity types + controllers live in Models.cs (they must be public
// for the EDM model / OData routing, so they sit in a namespace there).
//
// Run:
//   dotnet run --project samples/Kuestenlogik.Bowire.Sample.OData
//   → open http://localhost:5188/bowire

using Kuestenlogik.Bowire;
using Kuestenlogik.Bowire.Sample.OData;
using Kuestenlogik.Bowire.Sources;
using Microsoft.AspNetCore.OData;
using Microsoft.OData.ModelBuilder;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://localhost:5188");

var model = new ODataConventionModelBuilder();
model.EntitySet<Category>("Categories");
model.EntitySet<Product>("Products");

builder.Services.AddControllers()
    .AddOData(opt => opt
        .AddRouteComponents("odata", model.GetEdmModel())
        .Select().Filter().OrderBy().Count().Expand().SetMaxTop(100));

builder.Services.AddBowire();
builder.Services.AddBowireCatalogue(builder.Configuration);

var app = builder.Build();
app.MapControllers();

app.MapBowire("/bowire");
app.MapGet("/", () => Results.Redirect("/bowire"));
await app.RunAsync();
