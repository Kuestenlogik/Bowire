// Combined GraphQL sample for Bowire. One project, both stories:
//
//   * Embedded — the workbench is mounted at /bowire in this process and
//     the bundled graphql-catalogue.json seeds the Sources rail with this
//     host's /graphql endpoint, discovered via GraphQL introspection.
//   * Separate — it is a real HotChocolate GraphQL server, so an external
//     workbench (or `bowire --url graphql@http://localhost:5183/graphql`)
//     with the GraphQL plugin sees the same schema.
//
// Query + Mutation + Subscription, so the workbench has a runnable
// subscription target (bookAdded rides the WebSocket transport).
//
// Run:
//   dotnet run --project samples/Kuestenlogik.Bowire.Sample.GraphQL
//   → open http://localhost:5183/bowire

using HotChocolate.Subscriptions;
using Kuestenlogik.Bowire;
using Kuestenlogik.Bowire.Sources;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://localhost:5183");

builder.Services
    .AddGraphQLServer()
    .AddQueryType<Query>()
    .AddMutationType<Mutation>()
    .AddSubscriptionType<Subscription>()
    .AddInMemorySubscriptions();

builder.Services.AddBowire();
builder.Services.AddBowireCatalogue(builder.Configuration);

var app = builder.Build();
app.UseWebSockets();          // subscriptions ride on WebSockets
app.MapGraphQL();

app.MapBowire("/bowire");
app.MapGet("/", () => Results.Redirect("/bowire"));
await app.RunAsync();

sealed class Query
{
    private static readonly List<Book> s_books = new()
    {
        new(1, "The Pragmatic Programmer", "Andrew Hunt"),
        new(2, "Domain-Driven Design",      "Eric Evans"),
        new(3, "Refactoring",               "Martin Fowler"),
    };

    public IEnumerable<Book> Books() => s_books;
    public Book? BookById(int id) => s_books.FirstOrDefault(b => b.Id == id);
    internal static List<Book> All => s_books;
}

sealed class Mutation
{
    // Adds a book and publishes it to the `bookAdded` subscription stream.
    // ITopicEventSender is registered by AddInMemorySubscriptions and
    // injected into the resolver automatically.
    public async Task<Book> AddBook(string title, string author, ITopicEventSender sender)
    {
        var book = new Book(Query.All.Count + 1, title, author);
        Query.All.Add(book);
        await sender.SendAsync(nameof(Subscription.BookAdded), book);
        return book;
    }
}

sealed class Subscription
{
    // subscription { bookAdded { id title author } } — pushes every
    // newly-added book to connected clients over the WebSocket transport.
    [Subscribe]
    public Book BookAdded([EventMessage] Book book) => book;
}

sealed record Book(int Id, string Title, string Author);
