// Entity types + the in-memory store behind them. They must be public
// (the EDM model and OData routing discover them by type), so they live
// in a namespace here rather than as top-level types in Program.cs.
// The controllers that expose them live in Controllers.cs.
//
// Category <-> Product is a real, two-ended relation, so $expand has
// something to expand in both directions:
//   /odata/Products?$expand=Category
//   /odata/Categories?$expand=Products

namespace Kuestenlogik.Bowire.Sample.OData;

public class Category
{
    public int Id { get; set; }
    public string Name { get; set; } = "";

    /// <summary>Collection navigation property — the target of $expand=Products.</summary>
    public ICollection<Product> Products { get; } = [];
}

public class Product
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public decimal Price { get; set; }

    /// <summary>
    /// Foreign key; the convention builder pairs it with <see cref="Category"/>.
    /// Nullable, because deleting a category orphans its products rather
    /// than taking them with it.
    /// </summary>
    public int? CategoryId { get; set; }

    /// <summary>Single-valued navigation property — the target of $expand=Category.</summary>
    public Category? Category { get; set; }
}

/// <summary>
/// The seeded Northwind slice this sample serves. Registered as a
/// singleton in Program.cs so every request sees the same rows: POST /
/// PATCH / DELETE really do change what the next GET returns, which is
/// what makes the write methods worth clicking in the workbench.
/// </summary>
public sealed class NorthwindStore
{
    /// <summary>
    /// Guards every read and write. The store is a DI singleton handing
    /// out raw lists, and OData serialises a query result LAZILY — the
    /// formatter is still walking Products while another request adds to
    /// it, which throws "Collection was modified" mid-response, after the
    /// 200 headers are already on the wire. Readers take a snapshot
    /// instead (see Snapshot* below); writers hold the lock.
    /// </summary>
    private readonly System.Threading.Lock _gate = new();

    public List<Category> Categories { get; } =
    [
        new() { Id = 1, Name = "Beverages" },
        new() { Id = 2, Name = "Confections" },
    ];

    public List<Product> Products { get; } =
    [
        new() { Id = 1, Name = "Chai", Price = 18m, CategoryId = 1 },
        new() { Id = 2, Name = "Chang", Price = 19m, CategoryId = 1 },
        new() { Id = 3, Name = "Chocolate", Price = 12m, CategoryId = 2 },
    ];

    public NorthwindStore()
    {
        foreach (var product in Products) Link(product);
    }

    /// <summary>Next free key — there is no database here to hand one out.</summary>
    public int NextCategoryId => Categories.Count == 0 ? 1 : Categories.Max(c => c.Id) + 1;

    /// <inheritdoc cref="NextCategoryId"/>
    public int NextProductId => Products.Count == 0 ? 1 : Products.Max(p => p.Id) + 1;

    /// <summary>
    /// Point a product at the category its CategoryId names, and fix up the
    /// other end of the relation. Both ends have to be set — an $expand walks
    /// the object graph, not the foreign key.
    /// </summary>
    public void Link(Product product)
    {
        Detach(product);
        product.Category = Categories.Find(c => c.Id == product.CategoryId);
        product.Category?.Products.Add(product);
    }

    /// <summary>
    /// Orphan a product: off its category's collection and with the foreign
    /// key cleared, so a deleted category leaves nothing dangling behind.
    /// </summary>
    public void Unlink(Product product)
    {
        Detach(product);
        product.Category = null;
        product.CategoryId = null;
    }

    /// <summary>Take a product out of whichever collection currently holds it.</summary>
    private void Detach(Product product)
    {
        foreach (var category in Categories) category.Products.Remove(product);
    }

    /// <summary>
    /// Materialised copies for the query path. OData enumerates the
    /// IQueryable while writing the response, so handing it the live list
    /// races every concurrent write.
    /// </summary>
    public List<Product> SnapshotProducts()
    {
        lock (_gate) return [.. Products];
    }

    /// <inheritdoc cref="SnapshotProducts"/>
    public List<Category> SnapshotCategories()
    {
        lock (_gate) return [.. Categories];
    }

    /// <summary>Run a mutation under the store lock.</summary>
    public T Write<T>(Func<T> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        lock (_gate) return mutate();
    }

    /// <inheritdoc cref="Write{T}"/>
    public void Write(Action mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        lock (_gate) mutate();
    }
}
