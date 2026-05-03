// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Protocol.GraphQL;

/// <summary>
/// The standard GraphQL introspection query. Returns the full schema:
/// query / mutation / subscription root types, every type's fields with
/// their argument types, and enum values. The shape matches the
/// <c>__Schema</c> definition in the GraphQL spec section "Introspection".
/// </summary>
internal static class GraphQLIntrospectionQuery
{
    public const string Query = """
        query IntrospectionQuery {
          __schema {
            queryType { name }
            mutationType { name }
            subscriptionType { name }
            types {
              kind
              name
              description
              fields(includeDeprecated: true) {
                name
                description
                args {
                  name
                  description
                  type { ...TypeRef }
                  defaultValue
                }
                type { ...TypeRef }
                isDeprecated
                deprecationReason
              }
              inputFields {
                name
                description
                type { ...TypeRef }
                defaultValue
              }
              enumValues(includeDeprecated: true) {
                name
                description
                isDeprecated
              }
            }
          }
        }

        fragment TypeRef on __Type {
          kind
          name
          ofType {
            kind
            name
            ofType {
              kind
              name
              ofType {
                kind
                name
                ofType {
                  kind
                  name
                  ofType {
                    kind
                    name
                    ofType {
                      kind
                      name
                      ofType { kind name }
                    }
                  }
                }
              }
            }
          }
        }
        """;
}
