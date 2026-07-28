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

using Kuestenlogik.Bowire;
using Kuestenlogik.Bowire.Sample.GraphQL;
using Kuestenlogik.Bowire.Sources;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://localhost:5183");

builder.Services
    .AddGraphQLServer()
    .AddQueryType<Query>()
    .AddMutationType<Mutation>()
    .AddSubscriptionType<Subscription>()
    .AddInMemorySubscriptions()
    .AddHttpRequestInterceptor<IntrospectionInterceptor>();

builder.Services.AddBowire();
builder.Services.AddBowireCatalogue(builder.Configuration);

var app = builder.Build();
app.UseWebSockets();          // subscriptions ride on WebSockets
app.MapGraphQL();

app.MapBowire("/bowire");
app.MapGet("/", () => Results.Redirect("/bowire"));
await app.RunAsync();
