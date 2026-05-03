// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Kuestenlogik.Bowire.Protocol.GraphQL;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// Tests for the GraphQL introspection → BowireServiceInfo mapper. The
/// fixtures are minimal hand-built JSON shapes that mirror what a real
/// GraphQL server returns from the standard <c>__schema</c> introspection
/// query, just trimmed to the fields we actually consume.
/// </summary>
public class GraphQLSchemaMapperTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void Map_Surfaces_Query_Mutation_And_Subscription_As_Separate_Services()
    {
        var schema = Parse(/*lang=json,strict*/ """
        {
          "__schema": {
            "queryType": { "name": "Query" },
            "mutationType": { "name": "Mutation" },
            "subscriptionType": { "name": "Subscription" },
            "types": [
              { "kind": "OBJECT", "name": "Query",        "fields": [{ "name": "ping", "args": [], "type": { "kind": "SCALAR", "name": "String" } }], "inputFields": null, "enumValues": null },
              { "kind": "OBJECT", "name": "Mutation",     "fields": [{ "name": "noop", "args": [], "type": { "kind": "SCALAR", "name": "String" } }], "inputFields": null, "enumValues": null },
              { "kind": "OBJECT", "name": "Subscription", "fields": [{ "name": "tick", "args": [], "type": { "kind": "SCALAR", "name": "String" } }], "inputFields": null, "enumValues": null }
            ]
          }
        }
        """);

        var services = new GraphQLSchemaMapper().Map(schema, "http://localhost/graphql");

        Assert.Equal(3, services.Count);
        Assert.Equal(["Query", "Mutation", "Subscription"], services.Select(s => s.Name).ToArray());
        Assert.All(services, svc => Assert.Equal("graphql", svc.Source));
        Assert.All(services, svc => Assert.Equal("http://localhost/graphql", svc.OriginUrl));

        // Subscription methods are tagged as server-streaming so the UI shows
        // the right badge and the channel/stream code paths fire.
        var subscription = services.Single(s => s.Name == "Subscription");
        Assert.True(subscription.Methods[0].ServerStreaming);
        Assert.Equal("ServerStreaming", subscription.Methods[0].MethodType);
    }

    [Fact]
    public void Map_Unwraps_NonNull_And_List_Wrappers_Into_Required_And_Repeated()
    {
        var schema = Parse(/*lang=json,strict*/ """
        {
          "__schema": {
            "queryType": { "name": "Query" },
            "mutationType": null,
            "subscriptionType": null,
            "types": [
              {
                "kind": "OBJECT", "name": "Query",
                "fields": [{
                  "name": "search",
                  "args": [
                    {
                      "name": "id",
                      "type": { "kind": "NON_NULL", "ofType": { "kind": "SCALAR", "name": "ID" } }
                    },
                    {
                      "name": "tags",
                      "type": { "kind": "NON_NULL", "ofType": { "kind": "LIST", "ofType": { "kind": "NON_NULL", "ofType": { "kind": "SCALAR", "name": "String" } } } }
                    },
                    {
                      "name": "limit",
                      "type": { "kind": "SCALAR", "name": "Int" }
                    }
                  ],
                  "type": { "kind": "SCALAR", "name": "String" }
                }],
                "inputFields": null,
                "enumValues": null
              }
            ]
          }
        }
        """);

        var services = new GraphQLSchemaMapper().Map(schema, "http://localhost/graphql");
        var search = services.Single().Methods.Single();
        var args = search.InputType.Fields;

        var id = args.Single(f => f.Name == "id");
        Assert.Equal("string", id.Type);
        Assert.True(id.Required);
        Assert.False(id.IsRepeated);

        var tags = args.Single(f => f.Name == "tags");
        Assert.True(tags.Required);
        Assert.True(tags.IsRepeated);

        var limit = args.Single(f => f.Name == "limit");
        Assert.Equal("int32", limit.Type);
        Assert.False(limit.Required);
    }

    [Fact]
    public void Map_Resolves_Enum_Type_References_To_BowireEnumValues()
    {
        var schema = Parse(/*lang=json,strict*/ """
        {
          "__schema": {
            "queryType": { "name": "Query" },
            "mutationType": null,
            "subscriptionType": null,
            "types": [
              {
                "kind": "OBJECT", "name": "Query",
                "fields": [{
                  "name": "list",
                  "args": [{
                    "name": "order",
                    "type": { "kind": "ENUM", "name": "SortOrder" }
                  }],
                  "type": { "kind": "SCALAR", "name": "String" }
                }],
                "inputFields": null, "enumValues": null
              },
              {
                "kind": "ENUM", "name": "SortOrder",
                "fields": null, "inputFields": null,
                "enumValues": [
                  { "name": "ASC" },
                  { "name": "DESC" }
                ]
              }
            ]
          }
        }
        """);

        var services = new GraphQLSchemaMapper().Map(schema, "http://localhost/graphql");
        var order = services.Single().Methods.Single().InputType.Fields.Single();

        Assert.NotNull(order.EnumValues);
        Assert.Equal(["ASC", "DESC"], order.EnumValues!.Select(v => v.Name).ToArray());
    }

    [Fact]
    public void Map_Walks_OutputType_Into_Selectable_Field_Tree()
    {
        // Verifies that ResolveOutputType picks up the return type's
        // fields so the JS selection-set picker has something to render.
        // Each field becomes a BowireFieldInfo on the method's OutputType.
        var schema = Parse(/*lang=json,strict*/ """
        {
          "__schema": {
            "queryType": { "name": "Query" },
            "mutationType": null,
            "subscriptionType": null,
            "types": [
              {
                "kind": "OBJECT", "name": "Query",
                "fields": [{
                  "name": "getBook",
                  "args": [{ "name": "id", "type": { "kind": "NON_NULL", "ofType": { "kind": "SCALAR", "name": "ID" } } }],
                  "type": { "kind": "OBJECT", "name": "Book", "ofType": null }
                }],
                "inputFields": null, "enumValues": null
              },
              {
                "kind": "OBJECT", "name": "Book",
                "fields": [
                  { "name": "id",     "args": [], "type": { "kind": "NON_NULL", "ofType": { "kind": "SCALAR", "name": "ID" } } },
                  { "name": "title",  "args": [], "type": { "kind": "NON_NULL", "ofType": { "kind": "SCALAR", "name": "String" } } },
                  { "name": "author", "args": [], "type": { "kind": "SCALAR", "name": "String" } },
                  { "name": "year",   "args": [], "type": { "kind": "SCALAR", "name": "Int" } }
                ],
                "inputFields": null, "enumValues": null
              }
            ]
          }
        }
        """);

        var services = new GraphQLSchemaMapper().Map(schema, "http://localhost/graphql");
        var output = services.Single().Methods.Single().OutputType;

        Assert.Equal("Book", output.Name);
        Assert.Equal(["id", "title", "author", "year"], output.Fields.Select(f => f.Name).ToArray());
        // Scalar leaves get their JS-friendly type name
        Assert.Equal("string", output.Fields.Single(f => f.Name == "id").Type);
        Assert.Equal("int32",  output.Fields.Single(f => f.Name == "year").Type);
        // Source tag tells the JS picker which renderer to use
        Assert.All(output.Fields, f => Assert.Equal("selection", f.Source));
    }

    [Fact]
    public void Map_OutputType_UnwrapsListAndNonNullWrappers()
    {
        var schema = Parse(/*lang=json,strict*/ """
        {
          "__schema": {
            "queryType": { "name": "Query" },
            "mutationType": null,
            "subscriptionType": null,
            "types": [
              {
                "kind": "OBJECT", "name": "Query",
                "fields": [{
                  "name": "listBooks",
                  "args": [],
                  "type": {
                    "kind": "NON_NULL",
                    "ofType": {
                      "kind": "LIST",
                      "ofType": {
                        "kind": "NON_NULL",
                        "ofType": { "kind": "OBJECT", "name": "Book" }
                      }
                    }
                  }
                }],
                "inputFields": null, "enumValues": null
              },
              {
                "kind": "OBJECT", "name": "Book",
                "fields": [
                  { "name": "id", "args": [], "type": { "kind": "SCALAR", "name": "ID" } }
                ],
                "inputFields": null, "enumValues": null
              }
            ]
          }
        }
        """);

        var services = new GraphQLSchemaMapper().Map(schema, "http://localhost/graphql");
        var output = services.Single().Methods.Single().OutputType;

        // [Book!]! still resolves to the Book object's fields — repeated
        // is implied by the operation, not the picker, so the picker just
        // sees the inner type.
        Assert.Equal("Book", output.Name);
        Assert.Single(output.Fields);
        Assert.Equal("id", output.Fields[0].Name);
    }

    [Fact]
    public void Map_OutputType_HandlesCyclesViaVisitedSet()
    {
        // User → friends: [User] → friends: [User] → ... is a classic GraphQL
        // cycle. The mapper must terminate (depth cap + visited set) and
        // produce *some* tree, even if the deepest level is empty.
        var schema = Parse(/*lang=json,strict*/ """
        {
          "__schema": {
            "queryType": { "name": "Query" },
            "mutationType": null,
            "subscriptionType": null,
            "types": [
              {
                "kind": "OBJECT", "name": "Query",
                "fields": [{
                  "name": "me", "args": [],
                  "type": { "kind": "OBJECT", "name": "User" }
                }],
                "inputFields": null, "enumValues": null
              },
              {
                "kind": "OBJECT", "name": "User",
                "fields": [
                  { "name": "id",      "args": [], "type": { "kind": "SCALAR", "name": "ID" } },
                  { "name": "name",    "args": [], "type": { "kind": "SCALAR", "name": "String" } },
                  { "name": "friends", "args": [], "type": { "kind": "LIST", "ofType": { "kind": "OBJECT", "name": "User" } } }
                ],
                "inputFields": null, "enumValues": null
              }
            ]
          }
        }
        """);

        var services = new GraphQLSchemaMapper().Map(schema, "http://localhost/graphql");
        var output = services.Single().Methods.Single().OutputType;

        // Top level: User { id, name, friends }
        Assert.Equal("User", output.Name);
        Assert.Equal(["id", "name", "friends"], output.Fields.Select(f => f.Name).ToArray());

        var friends = output.Fields.Single(f => f.Name == "friends");
        Assert.True(friends.IsRepeated);
        Assert.Equal("message", friends.Type);
        // The visited-set entry for "User" was popped on the way out, so
        // the second level is allowed to re-enter — but the third level
        // (User again) hits the visited set OR depth cap and bails to an
        // empty leaf. Either outcome is acceptable; the key invariant is
        // that we don't infinite-recurse and we don't crash.
        Assert.NotNull(friends.MessageType);
    }

    [Fact]
    public void Map_OutputType_StopsAtScalarReturnTypes()
    {
        // Methods that return a bare scalar (e.g. `ping: String`) get an
        // empty selection tree — there's nothing to pick.
        var schema = Parse(/*lang=json,strict*/ """
        {
          "__schema": {
            "queryType": { "name": "Query" },
            "mutationType": null,
            "subscriptionType": null,
            "types": [
              {
                "kind": "OBJECT", "name": "Query",
                "fields": [{
                  "name": "ping", "args": [],
                  "type": { "kind": "SCALAR", "name": "String" }
                }],
                "inputFields": null, "enumValues": null
              }
            ]
          }
        }
        """);

        var services = new GraphQLSchemaMapper().Map(schema, "http://localhost/graphql");
        var output = services.Single().Methods.Single().OutputType;

        // Falls back to the placeholder GraphQLResponse — the JS picker
        // checks for empty fields[] and skips rendering the tree.
        Assert.Empty(output.Fields);
    }

    [Fact]
    public void Map_Recurses_Into_InputObject_Types_For_Nested_Forms()
    {
        var schema = Parse(/*lang=json,strict*/ """
        {
          "__schema": {
            "queryType": null,
            "mutationType": { "name": "Mutation" },
            "subscriptionType": null,
            "types": [
              {
                "kind": "OBJECT", "name": "Mutation",
                "fields": [{
                  "name": "createUser",
                  "args": [{
                    "name": "input",
                    "type": { "kind": "NON_NULL", "ofType": { "kind": "INPUT_OBJECT", "name": "UserInput" } }
                  }],
                  "type": { "kind": "SCALAR", "name": "String" }
                }],
                "inputFields": null, "enumValues": null
              },
              {
                "kind": "INPUT_OBJECT", "name": "UserInput",
                "fields": null,
                "inputFields": [
                  { "name": "name",  "type": { "kind": "NON_NULL", "ofType": { "kind": "SCALAR", "name": "String" } } },
                  { "name": "email", "type": { "kind": "SCALAR", "name": "String" } }
                ],
                "enumValues": null
              }
            ]
          }
        }
        """);

        var services = new GraphQLSchemaMapper().Map(schema, "http://localhost/graphql");
        var input = services.Single().Methods.Single().InputType.Fields.Single();

        Assert.Equal("message", input.Type);
        Assert.NotNull(input.MessageType);
        Assert.Equal("UserInput", input.MessageType!.Name);
        Assert.Equal(2, input.MessageType.Fields.Count);
        Assert.True(input.MessageType.Fields[0].Required);
        Assert.False(input.MessageType.Fields[1].Required);
    }
}
