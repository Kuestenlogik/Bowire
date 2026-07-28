// Combined OData v4 sample for Bowire. One project, both stories:
//
//   * Embedded — the workbench is mounted at /bowire and the bundled
//     odata-catalogue.json seeds the Sources rail with this host's /odata
//     endpoint; Bowire fetches the CSDL/EDMX and surfaces Categories +
//     Products as services.
//   * Separate — it is a real OData server, so point an external workbench
//     or `bowire --url odata@http://localhost:5188/odata` at it.
//
// The entity types + the in-memory store live in Models.cs and the
// controllers in Controllers.cs (they must be public for the EDM model /
// OData routing, so they sit in a namespace there). Between them the two
// controllers implement all five methods the plugin discovers per entity
// set — GET, GET_BY_KEY, POST, PATCH, DELETE — plus a bound function.
//
// Run:
//   dotnet run --project samples/Kuestenlogik.Bowire.Sample.OData
//   → open http://localhost:5188/bowire

using Kuestenlogik.Bowire;
using Kuestenlogik.Bowire.Sample.OData;
using Kuestenlogik.Bowire.Sources;
using Microsoft.AspNetCore.OData;
using Microsoft.OData.ModelBuilder;
using Microsoft.OData.UriParser;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://localhost:5188");

// One store for the whole host, so a POST is still there on the next GET.
builder.Services.AddSingleton<NorthwindStore>();

var model = new ODataConventionModelBuilder();
model.EntitySet<Category>("Categories");
var products = model.EntitySet<Product>("Products");

// A bound function, so the EDM advertises an operation and not only CRUD:
//   GET /odata/Products(1)/Default.DiscountedPrice(percent=15)
var discount = products.EntityType.Function("DiscountedPrice").Returns<decimal>();
discount.Parameter<decimal>("percent");

var edm = model.GetEdmModel();

builder.Services.AddControllers()
    .AddOData(opt => opt
        // UnqualifiedODataUriResolver also accepts the operation without its
        // namespace — .../Products(1)/DiscountedPrice(percent=15) — which is
        // what most clients (and most people typing a URL) reach for first.
        .AddRouteComponents("odata", edm, services => services
            .AddSingleton<ODataUriResolver>(_ => new UnqualifiedODataUriResolver { EnableCaseInsensitive = true }))
        .Select().Filter().OrderBy().Count().Expand().SetMaxTop(100));

builder.Services.AddBowire();
builder.Services.AddBowireCatalogue(builder.Configuration);

var app = builder.Build();

app.MapControllers();

app.MapBowire("/bowire");
app.MapGet("/", () => Results.Redirect("/bowire"));
await app.RunAsync();
