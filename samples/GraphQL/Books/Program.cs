// GraphQL Books sample for the Bowire GraphQL plugin demo. HotChocolate
// publishes the schema at /graphql which Bowire fetches via the
// standard introspection query.

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://localhost:5183");
builder.Services
    .AddGraphQLServer()
    .AddQueryType<Query>()
    .AddMutationType<Mutation>();

var app = builder.Build();
app.MapGraphQL();
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
    public Book AddBook(string title, string author)
    {
        var b = new Book(Query.All.Count + 1, title, author);
        Query.All.Add(b);
        return b;
    }
}

record Book(int Id, string Title, string Author);
