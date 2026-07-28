// The GraphQL schema this sample serves: one query root, one mutation
// root, one subscription root, over a tiny in-memory book list.
//
// These types live here rather than at the bottom of Program.cs on
// purpose. Below top-level statements they can only be declared without
// a namespace, and a type without a namespace has to stay `internal`
// (CA1050 is an error in this repo) — which HotChocolate's schema
// builder cannot resolve: it reported
//   "Unable to infer or resolve a schema type from the type reference
//    `Query (Output)`"
// for all three roots and the host threw SchemaException on every
// request. A file with a real namespace lets them be public, which is
// what the builder needs.

using HotChocolate.AspNetCore;
using HotChocolate.Execution;
using HotChocolate.Subscriptions;
using Microsoft.AspNetCore.Http;

namespace Kuestenlogik.Bowire.Sample.GraphQL;

/// <summary>
/// Re-enables GraphQL introspection, which HotChocolate disables outside
/// Development. Discovery is introspection: without this, the workbench
/// gets HC0046 "Introspection is not allowed for the current request"
/// and shows zero services. A published sample runs in Production, so
/// the environment default would silently break the thing the sample
/// exists to demonstrate.
/// </summary>
public sealed class IntrospectionInterceptor : DefaultHttpRequestInterceptor
{
    public override ValueTask OnCreateAsync(
        HttpContext context,
        IRequestExecutor requestExecutor,
        OperationRequestBuilder requestBuilder,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requestBuilder);

        requestBuilder.AllowIntrospection();
        return base.OnCreateAsync(context, requestExecutor, requestBuilder, cancellationToken);
    }
}

public sealed class Query
{
    private static readonly List<Book> s_books =
    [
        new(1, "The Pragmatic Programmer", "Andrew Hunt"),
        new(2, "Domain-Driven Design",      "Eric Evans"),
        new(3, "Refactoring",               "Martin Fowler"),
    ];

    public IEnumerable<Book> Books() => s_books;
    public Book? BookById(int id) => s_books.FirstOrDefault(b => b.Id == id);
    internal static List<Book> All => s_books;
}

public sealed class Mutation
{
    // Adds a book and publishes it to the `bookAdded` subscription stream.
    // ITopicEventSender is registered by AddInMemorySubscriptions and
    // injected into the resolver automatically.
    public async Task<Book> AddBook(string title, string author, ITopicEventSender sender)
    {
        ArgumentNullException.ThrowIfNull(sender);

        var book = new Book(Query.All.Count + 1, title, author);
        Query.All.Add(book);
        await sender.SendAsync(nameof(Subscription.BookAdded), book);
        return book;
    }
}

public sealed class Subscription
{
    // subscription { bookAdded { id title author } } — pushes every
    // newly-added book to connected clients over the WebSocket transport.
    [Subscribe]
    public Book BookAdded([EventMessage] Book book) => book;
}

public sealed record Book(int Id, string Title, string Author);
