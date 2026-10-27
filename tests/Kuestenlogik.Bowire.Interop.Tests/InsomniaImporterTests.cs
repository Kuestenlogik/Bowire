// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using Kuestenlogik.Bowire.Interop.V1;

namespace Kuestenlogik.Bowire.Interop.Tests;

/// <summary>
/// The Insomnia importer (#674) against a v4 export (JSON) and a v5
/// collection (YAML), through the rpc handler the service, CLI and MCP share.
/// </summary>
public sealed class InsomniaImporterTests
{
    private static ImportCollectionResponse Import(string fixture, string? format = null)
    {
        var request = new ImportCollectionRequest { Format = format ?? "" };
        request.Files.Add(new SourceFile
        {
            Path = fixture,
            Content = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", fixture)),
        });
        return InteropRpc.ImportCollection(request);
    }

    private static IEnumerable<string> Details(ImportCollectionResponse r, ReportEntry.Types.Kind kind, string subject) =>
        r.Report.Entries.Where(e => e.Kind == kind && e.Subject == subject).Select(e => e.Detail);

    [Fact]
    public void V4_Is_Sniffed_And_Its_Workspace_Becomes_One_Collection()
    {
        var r = Import("insomnia-v4.json");

        Assert.Equal("insomnia", r.Format);
        var collection = Assert.Single(r.Collections);
        Assert.Equal("Shop API", collection.Name);
        // Folder first (sort key), then the workspace-level requests; gRPC is skipped.
        Assert.Equal(["List orders", "Create order", "Login", "List bucket"], collection.Items.Select(i => i.Name));
        Assert.Equal(["Orders"], collection.Items[0].Folders);
    }

    [Fact]
    public void V4_Templates_Query_And_Headers_Map_To_Bowire()
    {
        var items = Import("insomnia-v4.json").Collections[0].Items;

        var list = items.Single(i => i.Name == "List orders");
        Assert.Equal("GET", list.Method);
        // {{ _.base_url }} → {{base_url}}; the disabled parameter stays out.
        Assert.Equal("{{base_url}}/orders?page=1", list.Service);
        Assert.Equal("application/json", list.Metadata["Accept"]);

        var create = items.Single(i => i.Name == "Create order");
        Assert.Contains("\"ref\": \"{{uuid}}\"", create.Messages[0], StringComparison.Ordinal);
        Assert.Contains("\"at\": \"{{timestamp}}\"", create.Messages[0], StringComparison.Ordinal);
        Assert.Equal("Bearer {{token}}", create.Metadata["Authorization"]);
    }

    [Fact]
    public void V4_Auth_Forms_And_Bodies_Come_Across_Or_Are_Reported()
    {
        var r = Import("insomnia-v4.json");
        var items = r.Collections[0].Items;

        var login = items.Single(i => i.Name == "Login");
        Assert.Equal("user={{user}}&grant=pass%20word", login.Messages[0]);
        Assert.Equal("application/x-www-form-urlencoded", login.Metadata["Content-Type"]);
        Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("alice:s3cr3t")), login.Metadata["Authorization"]);
        Assert.Contains(Details(r, ReportEntry.Types.Kind.Approximated, "Login"), d => d.Contains("urlencoded", StringComparison.Ordinal));
        Assert.Contains(r.Report.Credentials, c => c.Subject == "Login" && c.Field == "Authorization header");

        Assert.Contains(Details(r, ReportEntry.Types.Kind.Skipped, "List bucket"), d => d.StartsWith("iam auth", StringComparison.Ordinal));
        Assert.Contains(Details(r, ReportEntry.Types.Kind.Skipped, "Get stock"), d => d.StartsWith("gRPC request", StringComparison.Ordinal));
        Assert.Contains(Details(r, ReportEntry.Types.Kind.Skipped, "Orders / List orders"), d => d == "disabled query parameter debug");
        Assert.Contains(Details(r, ReportEntry.Types.Kind.Skipped, "Orders / Create order"), d => d.StartsWith("pre-request script", StringComparison.Ordinal));
        Assert.Contains(Details(r, ReportEntry.Types.Kind.Approximated, "Orders / Create order"), d => d.StartsWith("{% response %}", StringComparison.Ordinal));
        Assert.Contains(Details(r, ReportEntry.Types.Kind.Skipped, "api_spec"), d => d.Contains("schema source", StringComparison.Ordinal));
        Assert.Contains(Details(r, ReportEntry.Types.Kind.Skipped, "Shop API"), d => d.StartsWith("cookie jar", StringComparison.Ordinal));
    }

    [Fact]
    public void V4_Sub_Environments_Inherit_The_Base_And_Private_Ones_Are_Flagged()
    {
        var r = Import("insomnia-v4.json");

        Assert.Equal(["Staging", "Local secrets"], r.Environments.Select(e => e.Name));
        var staging = r.Environments[0];
        Assert.Equal("https://staging.example.com", staging.Vars["base_url"]);
        Assert.Equal("v2", staging.Vars["api.version"]);
        Assert.Equal("{{staging_token}}", staging.Vars["token"]);
        // Folder variables have nowhere else to go.
        Assert.Equal("acme", staging.Vars["tenant"]);

        var local = r.Environments[1];
        Assert.Equal("https://api.example.com", local.Vars["base_url"]);
        Assert.Contains(r.Report.Credentials, c => c.Subject == "Local secrets" && c.Field == "private variable token");
    }

    [Fact]
    public void V5_Yaml_Folders_Inherit_Auth_And_Graphql_Becomes_A_Post()
    {
        var r = Import("insomnia-v5.yaml");

        Assert.Equal("insomnia", r.Format);
        var collection = Assert.Single(r.Collections);
        Assert.Equal("Weather", collection.Name);

        var forecast = collection.Items.Single(i => i.Name == "Forecast for a city");
        Assert.Equal("{{base}}/forecast/{{city}}?days=3", forecast.Service);
        Assert.Equal(["Forecasts"], forecast.Folders);
        // The folder's API key header reaches the request.
        Assert.Equal("{{weather_key}}", forecast.Metadata["X-Api-Key"]);
        Assert.Contains(Details(r, ReportEntry.Types.Kind.Skipped, "Forecasts / Forecast for a city"), d => d.StartsWith("after-response script", StringComparison.Ordinal));

        var gql = collection.Items.Single(i => i.Name == "Current (GraphQL)");
        Assert.Equal("POST", gql.Method);
        Assert.Contains("\"query\"", gql.Messages[0], StringComparison.Ordinal);
        Assert.Equal("application/json", gql.Metadata["Content-Type"]);
        // An explicit "none" stops the folder's auth.
        Assert.False(gql.Metadata.ContainsKey("X-Api-Key"));

        Assert.Contains(Details(r, ReportEntry.Types.Kind.Skipped, "Live feed"), d => d.StartsWith("WebSocket request", StringComparison.Ordinal));

        var prod = Assert.Single(r.Environments);
        Assert.Equal("Production", prod.Name);
        Assert.Equal("Kiel", prod.Vars["city"]);
        Assert.Equal("{{prod_key}}", prod.Vars["weather_key"]);
    }

    [Fact]
    public void A_Design_Document_Is_Refused_With_The_Reason()
    {
        var request = new ImportCollectionRequest();
        request.Files.Add(new SourceFile { Path = "spec.yaml", Content = "type: spec.insomnia.rest/5.0\nname: Shop\n" });
        var ex = Assert.Throws<Grpc.Core.RpcException>(() => InteropRpc.ImportCollection(request));
        Assert.Contains("not a request collection", ex.Status.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void An_Imported_Collection_Exports_As_Http()
    {
        var imported = Import("insomnia-v4.json");
        var export = new ExportHttpRequest();
        export.Collections.Add(imported.Collections);
        export.Environments.Add(imported.Environments);

        var result = InteropRpc.ExportHttp(export);

        Assert.Contains("GET {{base_url}}/orders?page=1", result.Http, StringComparison.Ordinal);
        Assert.Contains("\"Staging\"", result.EnvJson, StringComparison.Ordinal);
    }
}
