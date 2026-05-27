// OData v4 Northwind-style sample for the Bowire OData plugin demo.
// Exposes /odata with $metadata; Bowire fetches the CSDL/EDMX and
// surfaces Categories + Products as Bowire services.

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OData;
using Microsoft.AspNetCore.OData.Query;
using Microsoft.AspNetCore.OData.Routing.Controllers;
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

var app = builder.Build();
app.MapControllers();
await app.RunAsync();

public class Category
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}

public class Product
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public decimal Price { get; set; }
    public int CategoryId { get; set; }
}

public class CategoriesController : ODataController
{
    private static readonly List<Category> s_categories = new()
    {
        new() { Id = 1, Name = "Beverages" },
        new() { Id = 2, Name = "Confections" },
    };

    [EnableQuery]
    public IActionResult Get() => Ok(s_categories);
}

public class ProductsController : ODataController
{
    private static readonly List<Product> s_products = new()
    {
        new() { Id = 1, Name = "Chai", Price = 18m, CategoryId = 1 },
        new() { Id = 2, Name = "Chang", Price = 19m, CategoryId = 1 },
        new() { Id = 3, Name = "Chocolate", Price = 12m, CategoryId = 2 },
    };

    [EnableQuery]
    public IActionResult Get() => Ok(s_products);
}
