// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Kuestenlogik.Bowire.Models;
using Kuestenlogik.Bowire.Protocol.Rest;
using Kuestenlogik.Bowire.Testing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Kuestenlogik.Bowire.Protocol.Rest.OpenApi2.Tests;

/// <summary>
/// An embedding host's REST API, discovered from the OpenAPI document the
/// host generates (#993).
/// </summary>
/// <remarks>
/// <para>
/// Embedded discovery used to walk ApiExplorer and reflect over the body
/// types. That gives the shape the CLR types have. The document gives the
/// shape the host publishes, which differs as soon as anything sits between
/// the two: here the host's JSON naming policy, so that the two sources
/// disagree about one field's name and a test can tell which one answered.
/// </para>
/// <para>
/// The plugin is asked directly, with the host's service provider, the way
/// <c>MapBowire()</c> initialises it.
/// </para>
/// </remarks>
public sealed class EmbeddedOpenApiDocumentTests
{
    // `CustomerName` in the CLR; `customer_name` on the wire and in the
    // document, because the host serialises snake case. Reflection over the
    // type camel-cases the property name and arrives at `customerName`,
    // which no client of this API ever sends.
    private sealed record Order(string CustomerName, int Quantity);

    // The registry finds an adapter by scanning the assemblies already
    // loaded, and nothing in this class touches the adapter's assembly
    // before discovery runs. A host gets it loaded by AddBowire(); here it
    // is named. It is the only adapter this test assembly references, so
    // naming it changes nothing for the other classes.
    public EmbeddedOpenApiDocumentTests() => BowireOpenApiAdapterRegistry.Register(new OpenApi2Adapter());

    private static async Task<WebApplication> StartAsync(bool openApi)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls(LoopbackHost.AnyPort);
        builder.Logging.ClearProviders();
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.ConfigureHttpJsonOptions(o =>
            o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower);
        if (openApi) builder.Services.AddOpenApi();

        var app = builder.Build();
        app.MapPost("/orders", (Order order) => Results.Ok(order)).WithTags("Orders").WithName("PlaceOrder");

        await app.StartAsync(TestContext.Current.CancellationToken);
        return app;
    }

    private static async Task<List<BowireServiceInfo>> DiscoverAsync(WebApplication app)
    {
        using var plugin = new BowireRestProtocol();
        plugin.Initialize(app.Services);
        // No server URL: embedded, the host itself is the subject.
        return await plugin.DiscoverAsync("", showInternalServices: false, TestContext.Current.CancellationToken);
    }

    private static HashSet<string> FieldNames(BowireMessageInfo? message)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        void Walk(BowireMessageInfo? m, int depth)
        {
            if (m is null || depth > 6) return;
            foreach (var field in m.Fields)
            {
                names.Add(field.Name);
                Walk(field.MessageType, depth + 1);
            }
        }
        Walk(message, 0);
        return names;
    }

    [Fact]
    public async Task A_Host_With_OpenApi_Is_Discovered_From_Its_Document()
    {
        await using var app = await StartAsync(openApi: true);

        var services = await DiscoverAsync(app);

        var orders = Assert.Single(services, s => s.Name == "Orders");
        var place = Assert.Single(orders.Methods);
        var fields = FieldNames(place.InputType);
        Assert.Contains("customer_name", fields);
        Assert.DoesNotContain("customerName", fields);
        Assert.Contains("quantity", fields);
    }

    [Fact]
    public async Task A_Host_Without_OpenApi_Falls_Back_To_ApiExplorer()
    {
        await using var app = await StartAsync(openApi: false);

        var services = await DiscoverAsync(app);

        var orders = Assert.Single(services, s => s.Name == "Orders");
        var fields = FieldNames(Assert.Single(orders.Methods).InputType);
        // The CLR's view of the type: all ApiExplorer has to go on.
        Assert.Contains("customerName", fields);
        Assert.DoesNotContain("customer_name", fields);
    }

    [Fact]
    public async Task A_Method_From_The_Document_Is_Callable_Against_The_Host()
    {
        // Discovery and invocation meet in the plugin's embedded cache. A
        // document-sourced method that could be listed and not called would
        // be the .proto problem over again.
        var ct = TestContext.Current.CancellationToken;
        await using var app = await StartAsync(openApi: true);
        using var plugin = new BowireRestProtocol();
        plugin.Initialize(app.Services);
        var orders = Assert.Single(await plugin.DiscoverAsync("", false, ct), s => s.Name == "Orders");
        var place = Assert.Single(orders.Methods);

        var result = await plugin.InvokeAsync(
            LoopbackHost.BaseAddress(app.Services), orders.Name, place.Name,
            ["""{"customer_name":"acme","quantity":3}"""],
            showInternalServices: false, metadata: null, ct);

        Assert.Contains("\"customer_name\":\"acme\"", (result.Response ?? "").Replace(" ", "", StringComparison.Ordinal)
            .Replace("\r", "", StringComparison.Ordinal).Replace("\n", "", StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Fact]
    public async Task No_Generator_Means_No_Documents()
    {
        await using var app = await StartAsync(openApi: false);

        var documents = await EmbeddedOpenApiDocument.ReadAllAsync(app.Services, TestContext.Current.CancellationToken);

        Assert.Empty(documents);
    }

    [Fact]
    public async Task The_Document_Is_Read_Without_The_Host_Mapping_It()
    {
        // No MapOpenApi() anywhere in this class: the document is generated
        // in-process, so the host need not publish it for Bowire to read it.
        await using var app = await StartAsync(openApi: true);

        var documents = await EmbeddedOpenApiDocument.ReadAllAsync(app.Services, TestContext.Current.CancellationToken);

        var (name, json) = Assert.Single(documents);
        Assert.Equal("v1", name);
        Assert.Contains("\"/orders\"", json, StringComparison.Ordinal);
    }
}
