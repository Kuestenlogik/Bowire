// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Models;
using Kuestenlogik.Bowire.Protocol.Rest;

namespace Kuestenlogik.Bowire.Protocol.Rest.Tests;

/// <summary>
/// #514 — a duplicate {service}::{method} key must not abort discovery.
/// An OpenAPI surface can legitimately produce the same operation twice
/// (one handler on several routes, an operation under multiple tags);
/// before the fix a raw ToDictionary threw and the operator saw
/// "0 services / Disconnected" with the cause only in the host log.
/// </summary>
public sealed class DuplicateOperationKeyTests
{
    private static BowireMethodInfo Method(string name) => new(
        Name: name,
        FullName: name,
        ClientStreaming: false,
        ServerStreaming: false,
        InputType: new BowireMessageInfo("In", "In", []),
        OutputType: new BowireMessageInfo("Out", "Out", []),
        MethodType: "Unary");

    [Fact]
    public async Task DiscoverAsync_With_Duplicate_Operation_Keys_Does_Not_Throw()
    {
        using var protocol = new BowireRestProtocol();

        // Two services named "Default", each exposing "GetPets" — exactly
        // the shape ASP.NET Core's AddOpenApi() produced in Sample.Rest.
        var services = new List<BowireServiceInfo>
        {
            new("Default", "rest", [Method("GetPets"), Method("GetPets")]),
        };

        var cache = typeof(BowireRestProtocol).GetMethod(
            "CacheEmbeddedSchemas",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(cache);

        var ex = Record.Exception(() => cache!.Invoke(protocol, [services]));
        Assert.Null(ex);

        await Task.CompletedTask;
    }
}
