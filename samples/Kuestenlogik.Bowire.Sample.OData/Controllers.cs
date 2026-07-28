// The OData controllers, one per entity set. Between them they implement
// exactly the five methods Bowire's OData plugin discovers per entity set
// — GET, GET_BY_KEY, POST, PATCH, DELETE — so every method in the Sources
// rail answers for real instead of 404/405-ing on Execute.
//
// The routing conventions do the wiring: an action called Get/Post/Patch/
// Delete with the right parameter shape (`int key`, `[FromBody] T`,
// `Delta<T>`) is matched against the EDM. `DiscountedPrice` is matched the
// same way against the bound function declared in Program.cs.
//
// All actions read and write the singleton NorthwindStore, so writes are
// visible to the next read.

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OData.Deltas;
using Microsoft.AspNetCore.OData.Formatter;
using Microsoft.AspNetCore.OData.Query;
using Microsoft.AspNetCore.OData.Results;
using Microsoft.AspNetCore.OData.Routing.Controllers;

namespace Kuestenlogik.Bowire.Sample.OData;

public class CategoriesController(NorthwindStore store) : ODataController
{
    [EnableQuery]
    // Snapshot, not the live list: OData enumerates the IQueryable while
    // it writes the response, so a concurrent write would throw
    // "Collection was modified" after the 200 headers are already sent.
    public IQueryable<Category> Get() => store.SnapshotCategories().AsQueryable();

    // SingleResult<T> is what makes an unknown key a clean 404 rather than
    // a null body — the framework materialises the query and decides.
    [EnableQuery]
    public SingleResult<Category> Get([FromRoute] int key)
        => SingleResult.Create(store.SnapshotCategories().Where(c => c.Id == key).AsQueryable());

    public IActionResult Post([FromBody] Category category)
    {
        if (category is null) return BadRequest();

        // The store hands out keys; a client-supplied Id would collide.
        store.Write(() =>
        {
            category.Id = store.NextCategoryId;
            store.Categories.Add(category);
        });
        return Created(category);   // 201 + Location: .../Categories(4)
    }

    public IActionResult Patch([FromRoute] int key, [FromBody] Delta<Category> delta)
    {
        if (delta is null) return BadRequest();
        var patched = store.Write(() =>
        {
            var category = store.Categories.Find(c => c.Id == key);
            if (category is null) return false;
            delta.Patch(category);
            category.Id = key;      // the URL owns the key, not the body
            return true;
        });
        return patched ? NoContent() : NotFound();   // 204, per OData's default Prefer handling
    }

    public IActionResult Delete([FromRoute] int key)
    {
        var deleted = store.Write(() =>
        {
            var category = store.Categories.Find(c => c.Id == key);
            if (category is null) return false;

            // Orphan the products rather than cascade-delete them: keeps the
            // remaining rows queryable and makes the effect obvious in a re-GET.
            foreach (var product in category.Products.ToList()) store.Unlink(product);
            store.Categories.Remove(category);
            return true;
        });
        return deleted ? NoContent() : NotFound();
    }
}

public class ProductsController(NorthwindStore store) : ODataController
{
    // Snapshot for the same reason as CategoriesController.Get().
    [EnableQuery]
    public IQueryable<Product> Get() => store.SnapshotProducts().AsQueryable();

    [EnableQuery]
    public SingleResult<Product> Get([FromRoute] int key)
        => SingleResult.Create(store.SnapshotProducts().Where(p => p.Id == key).AsQueryable());

    public IActionResult Post([FromBody] Product product)
    {
        if (product is null) return BadRequest();

        store.Write(() =>
        {
            product.Id = store.NextProductId;
            store.Products.Add(product);
            store.Link(product);    // hang it off the category it names
        });
        return Created(product);
    }

    public IActionResult Patch([FromRoute] int key, [FromBody] Delta<Product> delta)
    {
        if (delta is null) return BadRequest();
        var patched = store.Write(() =>
        {
            var product = store.Products.Find(p => p.Id == key);
            if (product is null) return false;
            delta.Patch(product);
            product.Id = key;
            store.Link(product);    // CategoryId may have moved
            return true;
        });
        return patched ? NoContent() : NotFound();
    }

    public IActionResult Delete([FromRoute] int key)
    {
        var deleted = store.Write(() =>
        {
            var product = store.Products.Find(p => p.Id == key);
            if (product is null) return false;
            store.Unlink(product);
            store.Products.Remove(product);
            return true;
        });
        return deleted ? NoContent() : NotFound();
    }

    /// <summary>
    /// Bound function declared on Product in Program.cs — the "actions" half
    /// of what an OData service advertises, next to plain CRUD:
    ///   GET /odata/Products(1)/Default.DiscountedPrice(percent=15)
    /// </summary>
    [HttpGet]
    public IActionResult DiscountedPrice([FromRoute] int key, [FromODataUri] decimal percent)
    {
        if (percent is < 0m or > 100m) return BadRequest();
        var product = store.Products.Find(p => p.Id == key);
        if (product is null) return NotFound();

        return Ok(decimal.Round(product.Price * (100m - percent) / 100m, 2));
    }
}
