// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using Kuestenlogik.Bowire.Interop.V1;

namespace Kuestenlogik.Bowire.Interop.Tests;

/// <summary>
/// Bruno collections (#674): a folder of .bru files, and OpenCollection YAML
/// in its folder layout and as one bundled file. Read through
/// <see cref="InteropSources"/> and the rpc handler, as the CLI does.
/// </summary>
public sealed class BrunoImporterTests
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    private static async Task<ImportCollectionResponse> ImportAsync(string fixture, params SourceFile[] extra)
    {
        var request = new ImportCollectionRequest();
        request.Files.Add(await InteropSources.ReadAsync(Fixture(fixture), TestContext.Current.CancellationToken));
        request.Files.Add(extra);
        return InteropRpc.ImportCollection(request);
    }

    private static IEnumerable<string> Details(ImportCollectionResponse r, ReportEntry.Types.Kind kind, string subject) =>
        r.Report.Entries.Where(e => e.Kind == kind && e.Subject == subject).Select(e => e.Detail);

    [Fact]
    public async Task A_Bru_Folder_Becomes_One_Collection_In_Folder_And_Seq_Order()
    {
        var r = await ImportAsync("bruno");

        Assert.Equal("bruno", r.Format);
        var collection = Assert.Single(r.Collections);
        Assert.Equal("Petstore", collection.Name);
        // Root requests by seq, then the folder's by seq; the folder's display name comes from folder.bru.
        Assert.Equal(["Search", "Login", "Create pet", "Get pet"], collection.Items.Select(i => i.Name));
        Assert.Equal(["Pet store"], collection.Items.Single(i => i.Name == "Get pet").Folders);
    }

    [Fact]
    public async Task Bru_Requests_Inherit_Headers_And_Auth_And_Fill_Path_Parameters()
    {
        var r = await ImportAsync("bruno");
        var items = r.Collections[0].Items;

        var get = items.Single(i => i.Name == "Get pet");
        Assert.Equal("GET", get.Method);
        Assert.Equal("{{baseUrl}}/pets/42?verbose=true", get.Service);
        Assert.Equal("bowire-test", get.Metadata["X-Client"]);
        Assert.Equal("pets", get.Metadata["X-Folder"]);
        // auth: inherit → the collection's bearer token.
        Assert.Equal("Bearer {{token}}", get.Metadata["Authorization"]);
        Assert.Contains(Details(r, ReportEntry.Types.Kind.Skipped, "Pet store / Get pet"), d => d == "disabled query parameter debug");
        Assert.Contains(Details(r, ReportEntry.Types.Kind.Skipped, "Pet store / Get pet"), d => d.StartsWith("assertions", StringComparison.Ordinal));

        var create = items.Single(i => i.Name == "Create pet");
        Assert.Equal("{\n  \"name\": \"Rex\",\n  \"tag\": \"dog\"\n}", create.Messages[0]);
        Assert.Equal("application/json", create.Metadata["Content-Type"]);
        Assert.Equal("{{uuid}}", create.Metadata["X-Request-Id"]);
        Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("admin:hunter2")), create.Metadata["Authorization"]);
        Assert.Contains(r.Report.Credentials, c => c.Subject == "Pet store / Create pet" && c.Field == "Authorization header");
        Assert.Contains(Details(r, ReportEntry.Types.Kind.Skipped, "Pet store / Create pet"), d => d.StartsWith("pre-request script", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Bru_Graphql_Forms_And_Query_Api_Keys_Map()
    {
        var r = await ImportAsync("bruno");
        var items = r.Collections[0].Items;

        var search = items.Single(i => i.Name == "Search");
        Assert.Equal("POST", search.Method);
        Assert.Contains("\"query\":\"query Find($q: String) {", search.Messages[0], StringComparison.Ordinal);
        Assert.Contains("\"variables\":{\"q\":\"rex\"}", search.Messages[0], StringComparison.Ordinal);
        // auth: none stops the collection's bearer token.
        Assert.False(search.Metadata.ContainsKey("Authorization"));

        var login = items.Single(i => i.Name == "Login");
        Assert.Equal("{{baseUrl}}/login?api_key={{apiKey}}", login.Service);
        Assert.Equal("user=alice&note=two%0Alines", login.Messages[0]);
        Assert.Equal("application/x-www-form-urlencoded", login.Metadata["Content-Type"]);
    }

    [Fact]
    public async Task Bru_Environments_Carry_Variables_And_Name_Their_Secrets()
    {
        var r = await ImportAsync("bruno", new SourceFile { Path = ".env", Content = "SHOP_TOKEN=not-imported\n" });

        var local = Assert.Single(r.Environments);
        Assert.Equal("Local", local.Name);
        Assert.Equal("http://localhost:3000", local.Vars["baseUrl"]);
        Assert.False(local.Vars.ContainsKey("old"));
        Assert.Equal("", local.Vars["token"]);
        // Collection variables have nowhere else to go.
        Assert.Equal("v1", local.Vars["apiVersion"]);
        Assert.Contains(Details(r, ReportEntry.Types.Kind.Approximated, "Local"), d => d.StartsWith("secret variable token", StringComparison.Ordinal));
        Assert.Contains(Details(r, ReportEntry.Types.Kind.Skipped, ".env"), d => d.StartsWith("the .env file is not imported", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OpenCollection_Folder_Layout_Maps_Like_Bru()
    {
        var r = await ImportAsync("opencollection");

        Assert.Equal("opencollection", r.Format);
        var collection = Assert.Single(r.Collections);
        Assert.Equal("Weather OC", collection.Name);
        Assert.Equal(["Current", "Upload", "Daily forecast"], collection.Items.Select(i => i.Name));

        var daily = collection.Items.Single(i => i.Name == "Daily forecast");
        Assert.Equal("{{base}}/forecast/{{city}}?days=3", daily.Service);
        Assert.Equal(["Forecasts"], daily.Folders);
        Assert.Equal("forecasts", daily.Metadata["X-Folder"]);
        Assert.Equal("{{weatherKey}}", daily.Metadata["X-Api-Key"]);
        Assert.Contains(Details(r, ReportEntry.Types.Kind.Skipped, "Forecasts / Daily forecast"), d => d.StartsWith("tests", StringComparison.Ordinal));

        var current = collection.Items.Single(i => i.Name == "Current");
        Assert.Contains("\"variables\":{\"unit\":\"C\"}", current.Messages[0], StringComparison.Ordinal);
        Assert.False(current.Metadata.ContainsKey("X-Api-Key"));

        Assert.Contains(Details(r, ReportEntry.Types.Kind.Skipped, "Upload"), d => d.StartsWith("multipart form body", StringComparison.Ordinal));

        var prod = r.Environments.Single(e => e.Name == "Production");
        // extends: Base fills what Production does not set.
        Assert.Equal("https://weather.example.com", prod.Vars["base"]);
        Assert.Equal("Hamburg", prod.Vars["city"]);
        Assert.Equal("", prod.Vars["weatherKey"]);
        Assert.Equal("Kiel", r.Environments.Single(e => e.Name == "Base").Vars["city"]);
    }

    [Fact]
    public async Task OpenCollection_Bundled_File_Reads_The_Tree_And_Selected_Body()
    {
        var r = await ImportAsync("opencollection-bundled.yml");

        Assert.Equal("opencollection", r.Format);
        var collection = Assert.Single(r.Collections);
        Assert.Equal("Bundled", collection.Name);

        var list = collection.Items.Single(i => i.Name == "List users");
        Assert.Equal(["Admin"], list.Folders);
        Assert.Equal("Bearer {{adminToken}}", list.Metadata["Authorization"]);
        Assert.False(list.Metadata.ContainsKey("X-Debug"));

        var ping = collection.Items.Single(i => i.Name == "Ping");
        Assert.Equal("{\"n\":1}", ping.Messages[0]);
        Assert.Contains(Details(r, ReportEntry.Types.Kind.Skipped, "Admin / Live"), d => d.StartsWith("WebSocket request", StringComparison.Ordinal));
        Assert.Equal("http://localhost:8080", Assert.Single(r.Environments).Vars["host"]);
    }
}
