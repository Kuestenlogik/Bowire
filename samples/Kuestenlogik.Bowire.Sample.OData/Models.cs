// Entity types + OData controllers for the sample. They must be public
// (the EDM model and OData routing discover them by type), so they live
// in a namespace here rather than as top-level types in Program.cs.

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OData.Query;
using Microsoft.AspNetCore.OData.Routing.Controllers;

namespace Kuestenlogik.Bowire.Sample.OData;

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
