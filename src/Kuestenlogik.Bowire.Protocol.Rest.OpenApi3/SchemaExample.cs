// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Nodes;
using Microsoft.OpenApi;

namespace Kuestenlogik.Bowire.Protocol.Rest.OpenApi3;

/// <summary>
/// The one place that reads the deprecated <see cref="IOpenApiSchema.Example"/>.
/// </summary>
internal static class SchemaExample
{
    /// <summary>
    /// The schema's singular <c>example</c>, or <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// Microsoft.OpenApi 3.9+ marks <see cref="IOpenApiSchema.Example"/> obsolete in favour of
    /// <see cref="IOpenApiSchema.Examples"/>, but its reader (verified on 3.10.2 and 3.11.0) still
    /// puts an OpenAPI 3.0 document's <c>example:</c> only into the deprecated property, so
    /// <c>Examples</c> stays empty. Dropping this read loses the schema examples of every
    /// OpenAPI 3.0 API (OpenApiSampleGeneratorTests.Generate_Example_Wins_Over_Type_Driven_Guess
    /// fails). No replacement carries the data, so CS0618 is suppressed for this one read only.
    /// Remove the suppression when upstream folds a 3.0 <c>example:</c> into <c>Examples</c>.
    /// </remarks>
    public static JsonNode? Singular(IOpenApiSchema schema)
    {
#pragma warning disable CS0618 // No replacement carries a 3.0 `example:` yet, see remarks.
        return schema.Example;
#pragma warning restore CS0618
    }
}
