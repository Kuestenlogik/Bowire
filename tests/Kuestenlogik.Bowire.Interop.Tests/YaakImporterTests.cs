// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Interop.Formats;
using Kuestenlogik.Bowire.Interop.V1;

namespace Kuestenlogik.Bowire.Interop.Tests;

/// <summary>
/// The Yaak importer (#939) against exports Yaak wrote: <c>Fixtures/exported/yaak</c>
/// holds files from the official Yaak CLI, see its README for how each was made.
/// </summary>
public sealed class YaakImporterTests
{
    private static string Exported(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "exported", "yaak", name);

    private static async Task<ImportCollectionResponse> ImportAsync(string fixture)
    {
        var request = new ImportCollectionRequest();
        request.Files.Add(await InteropSources.ReadAsync(Exported(fixture), TestContext.Current.CancellationToken));
        return InteropRpc.ImportCollection(request);
    }

    private static IEnumerable<string> Details(ImportCollectionResponse r, ReportEntry.Types.Kind kind, string subject) =>
        r.Report.Entries.Where(e => e.Kind == kind && e.Subject == subject).Select(e => e.Detail);

    [Fact]
    public async Task A_Yaak_Export_Is_Recognised_By_Its_Content()
    {
        var r = await ImportAsync("harbor.json");

        Assert.Equal("yaak", r.Format);
        var collection = Assert.Single(r.Collections);
        Assert.Equal("Harbor API", collection.Name);
        Assert.Equal(7, collection.Items.Count);
    }

    [Fact]
    public async Task Folders_Become_Paths_In_Yaak_Order()
    {
        var items = (await ImportAsync("harbor.json")).Collections[0].Items;

        Assert.Equal(["Ships"], items.Single(i => i.Name == "List ships").Folders);
        Assert.Equal(["Ships", "Berths"], items.Single(i => i.Name == "Book berth").Folders);
        Assert.Empty(items.Single(i => i.Name == "Delete ship").Folders);
    }

    [Fact]
    public async Task Templates_Become_Bowire_Variables_And_Query_Parameters_Are_Appended()
    {
        var r = await ImportAsync("harbor.json");
        var items = r.Collections[0].Items;

        var list = items.Single(i => i.Name == "List ships");
        Assert.Equal("GET", list.Method);
        Assert.Equal("{{baseUrl}}/ships?limit=20", list.ServerUrl);
        Assert.Contains("disabled query parameter draft", Details(r, ReportEntry.Types.Kind.Skipped, "Ships / List ships"));

        var chained = items.Single(i => i.Name == "Ship by id (chained)");
        Assert.Equal("{{uuid}}", chained.Metadata["X-Trace"]);
        // Response chaining has no counterpart that can be written for the caller.
        Assert.Contains("${[ response.body.path(", chained.ServerUrl, StringComparison.Ordinal);
        Assert.Contains(Details(r, ReportEntry.Types.Kind.Approximated, "Ship by id (chained)"),
            d => d.Contains("{{response.path}}", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Headers_And_Auth_Are_Inherited_From_Folders_And_The_Workspace()
    {
        var items = (await ImportAsync("harbor.json")).Collections[0].Items;

        // Workspace header and bearer auth, folder header, request's own body type.
        var create = items.Single(i => i.Name == "Create ship");
        Assert.Equal("bowire-fixture", create.Metadata["X-Client"]);
        Assert.Equal("ships", create.Metadata["X-Folder"]);
        Assert.Equal("Bearer {{token}}", create.Metadata["Authorization"]);
        Assert.Equal("application/json", create.Metadata["Content-Type"]);
        Assert.Contains("Nordstern", create.Messages[0], StringComparison.Ordinal);

        // The request's own API key replaces the inherited bearer token.
        var delete = items.Single(i => i.Name == "Delete ship");
        Assert.Equal("{{apiKey}}", delete.Metadata["X-Api-Key"]);
        Assert.False(delete.Metadata.ContainsKey("Authorization"));
        Assert.False(delete.Metadata.ContainsKey("X-Folder"));
    }

    [Fact]
    public async Task Bodies_Change_Shape_Or_Are_Left_Out_With_A_Report_Line()
    {
        var r = await ImportAsync("harbor.json");
        var items = r.Collections[0].Items;

        var book = items.Single(i => i.Name == "Book berth");
        Assert.Equal("ship=101&from=2026-12-01", book.Messages[0]);
        Assert.Contains("form fields were joined into a urlencoded body", Details(r, ReportEntry.Types.Kind.Approximated, "Ships / Berths / Book berth"));
        // Basic auth with a variable in it cannot be encoded ahead of time.
        Assert.Contains(Details(r, ReportEntry.Types.Kind.Approximated, "Ships / Berths / Book berth"),
            d => d.Contains("Basic auth with variables", StringComparison.Ordinal));

        Assert.Contains(Details(r, ReportEntry.Types.Kind.Skipped, "Ships / Berths / Upload manifest"),
            d => d.Contains("multipart", StringComparison.Ordinal));

        var graphQl = items.Single(i => i.Name == "GraphQL ships");
        Assert.Equal("""{"query":"query { ships { name } }","variables":{"limit":5}}""", graphQl.Messages[0]);
    }

    [Fact]
    public async Task Sub_Environments_Are_Laid_Over_Their_Base_And_Private_Values_Are_Reported()
    {
        var r = await ImportAsync("harbor.json");

        // Global is a sub-environment of its own, Staging one of Global.
        Assert.Equal(["Global", "Staging"], r.Environments.Select(e => e.Name).Order(StringComparer.Ordinal));
        Assert.Equal("https://harbor.example.com/api", r.Environments.Single(e => e.Name == "Global").Vars["baseUrl"]);
        var staging = r.Environments.Single(e => e.Name == "Staging");
        Assert.Equal("https://staging.harbor.example.com/api", staging.Vars["baseUrl"]);
        Assert.Equal("dev-token", staging.Vars["token"]);
        Assert.False(staging.Vars.ContainsKey("disabledVar"));
        // Reported once, under Global, which declares it.
        Assert.Single(r.Report.Entries, e => e.Detail == "disabled variable disabledVar");
        Assert.Contains("disabled variable disabledVar", Details(r, ReportEntry.Types.Kind.Skipped, "Global"));
        Assert.Contains(r.Report.Credentials, c => c.Subject == "Staging" && c.Field == "private variable token");
    }

    [Fact]
    public async Task A_Bruno_Collection_Through_Yaak_Keeps_Its_Folders_Environments_And_Auth()
    {
        var r = await ImportAsync("from-bruno.json");

        var collection = Assert.Single(r.Collections);
        Assert.Equal(15, collection.Items.Count);
        Assert.All(collection.Items, i => Assert.Equal("rest", i.Protocol));
        // The base environment is laid under each sub-environment, not listed on its own.
        Assert.Equal(["Local", "Prod"], r.Environments.Select(e => e.Name).Order(StringComparer.Ordinal));
        Assert.True(r.Environments.All(e => e.Vars.Count >= 7));
        Assert.Contains(r.Report.Entries, e => e.Kind == ReportEntry.Types.Kind.Skipped
            && e.Detail.StartsWith("digest auth", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_Export_With_Only_Grpc_And_WebSocket_Requests_Is_Refused_With_What_It_Holds()
    {
        var ex = await Assert.ThrowsAsync<Grpc.Core.RpcException>(() => ImportAsync("grpc-websocket.json"));

        Assert.Contains("no HTTP request", ex.Status.Detail, StringComparison.Ordinal);
        Assert.Contains("(11)", ex.Status.Detail, StringComparison.Ordinal);
        Assert.Contains("add its URL as a source", ex.Status.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("${[ base ]}", "{{base}}")]
    [InlineData("${[uuid.v4()]}", "{{uuid}}")]
    [InlineData("${[ timestamp.iso8601() ]}", "{{timestamp}}")]
    [InlineData("${[ timestamp.unix() ]} ${[ timestamp.unixMillis() ]}", "{{now}} {{nowMs}}")]
    [InlineData("no template", "no template")]
    public void Templates(string yaak, string bowire)
    {
        var unmapped = new List<string>();
        Assert.Equal(bowire, YaakTemplates.ToBowire(yaak, unmapped));
        Assert.Empty(unmapped);
    }

    [Fact]
    public void An_Unknown_Function_Stays_And_Is_Named()
    {
        var unmapped = new List<string>();
        Assert.Equal("${[ prompt.text(label='x') ]}", YaakTemplates.ToBowire("${[ prompt.text(label='x') ]}", unmapped));
        Assert.Equal(["prompt.text"], unmapped);
    }
}
