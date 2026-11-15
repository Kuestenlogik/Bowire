// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Interop.V1;

namespace Kuestenlogik.Bowire.Interop.Tests;

/// <summary>
/// The importers against collections the apps wrote (#940).
/// </summary>
/// <remarks>
/// The fixtures beside these were written by hand from the formats'
/// specifications, so they hold what the specification made us expect.
/// <c>Fixtures/exported</c> holds what Insomnia and Bruno write: the
/// collections of their own end-to-end tests, unchanged. Its README names
/// where each came from.
/// </remarks>
public sealed class ExportedCollectionsTests
{
    private static string Exported(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "exported", name);

    private static async Task<ImportCollectionResponse> ImportAsync(string fixture)
    {
        var request = new ImportCollectionRequest();
        request.Files.Add(await InteropSources.ReadAsync(Exported(fixture), TestContext.Current.CancellationToken));
        return InteropRpc.ImportCollection(request);
    }

    private static IEnumerable<string> Details(ImportCollectionResponse r, ReportEntry.Types.Kind kind, string subject) =>
        r.Report.Entries.Where(e => e.Kind == kind && e.Subject == subject).Select(e => e.Detail);

    // ---- Insomnia ----

    [Fact]
    public async Task An_Insomnia_Collection_Imports_Its_Http_Requests()
    {
        var r = await ImportAsync("insomnia/smoke-test-collection.yaml");

        Assert.Equal("insomnia", r.Format);
        var collection = Assert.Single(r.Collections);
        Assert.Equal("Smoke tests", collection.Name);
        Assert.Equal(10, collection.Items.Count);
        Assert.All(collection.Items, i => Assert.Equal("rest", i.Protocol));
        Assert.All(collection.Items, i => Assert.False(string.IsNullOrWhiteSpace(i.Service)));
        // The folder's request keeps its folder.
        Assert.Equal(["FolderWithEnv"], collection.Items.Single(i => i.Name == "sends request with pre-request script").Folders);
    }

    [Fact]
    public async Task An_Insomnia_Socket_Io_Request_Is_Not_Imported_As_An_Http_Get()
    {
        // In v5 a request has no type field. A Socket.IO request has a URL
        // and headers like an HTTP one, so it came across as a GET to the
        // Socket.IO server and was reported as mapped.
        var ex = await Assert.ThrowsAsync<Grpc.Core.RpcException>(() => ImportAsync("insomnia/socket-io.yaml"));

        Assert.Contains("Socket.IO", ex.Status.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("insomnia/grpc.yaml", 5)]
    [InlineData("insomnia/websockets.yaml", 6)]
    [InlineData("insomnia/socket-io.yaml", 1)]
    public async Task An_Insomnia_Collection_Without_Http_Requests_Is_Refused_With_What_It_Holds(string fixture, int requests)
    {
        // "no request collection found in the export" was the answer, to a
        // file that is a request collection with five requests in it.
        var ex = await Assert.ThrowsAsync<Grpc.Core.RpcException>(() => ImportAsync(fixture));

        Assert.Contains("no HTTP request", ex.Status.Detail, StringComparison.Ordinal);
        Assert.Contains($"({requests})", ex.Status.Detail, StringComparison.Ordinal);
        Assert.Contains("add its URL as a source", ex.Status.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("environment.insomnia.rest/5.0", "global environment")]
    [InlineData("mock.insomnia.rest/5.0", "mock server")]
    [InlineData("spec.insomnia.rest/5.0", "design document")]
    public void An_Insomnia_File_That_Is_No_Collection_Is_Refused_As_What_It_Is(string type, string named)
    {
        // Every one of them was called a design document.
        var request = new ImportCollectionRequest();
        request.Files.Add(new SourceFile { Path = "export.yaml", Content = $"type: {type}\nname: Shop\n" });

        var ex = Assert.Throws<Grpc.Core.RpcException>(() => InteropRpc.ImportCollection(request));

        Assert.Contains("not a request collection", ex.Status.Detail, StringComparison.Ordinal);
        Assert.Contains(named, ex.Status.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_Insomnia_Response_Tag_Says_What_To_Write_Instead()
    {
        var r = await ImportAsync("insomnia/chained-responses.yaml");

        Assert.Equal(3, Assert.Single(r.Collections).Items.Count);
        Assert.Contains(
            "{% response %} stays as written; rewrite it as {{response.path}} chaining in Bowire",
            Details(r, ReportEntry.Types.Kind.Approximated, "second"));
    }

    [Fact]
    public async Task Insomnia_Auth_That_Has_No_Equivalent_Is_Reported_And_The_Request_Kept()
    {
        var r = await ImportAsync("insomnia/auth-types.yaml");

        var items = Assert.Single(r.Collections).Items;
        Assert.Equal(["sends request with basic authentication", "sends request with oauth1"], items.Select(i => i.Name));
        Assert.Contains(
            Details(r, ReportEntry.Types.Kind.Skipped, "sends request with oauth1"),
            d => d.StartsWith("oauth1 auth", StringComparison.Ordinal));
    }

    // ---- Bruno, Bru format ----

    [Fact]
    public async Task A_Bruno_Collection_Imports_Every_Request_File()
    {
        var r = await ImportAsync("bruno-bru");

        Assert.Equal("bruno", r.Format);
        var collection = Assert.Single(r.Collections);
        Assert.Equal("bruno-testbench", collection.Name);

        var requestFiles = Directory.EnumerateFiles(Exported("bruno-bru"), "*.bru", SearchOption.AllDirectories)
            .Count(f => !Path.GetFileName(f).Equals("folder.bru", StringComparison.OrdinalIgnoreCase)
                && !Path.GetFileName(f).Equals("collection.bru", StringComparison.OrdinalIgnoreCase)
                && !f.Contains(Path.DirectorySeparatorChar + "environments" + Path.DirectorySeparatorChar, StringComparison.Ordinal));
        Assert.Equal(requestFiles, collection.Items.Count);
        Assert.Equal(["Local", "Prod"], r.Environments.Select(e => e.Name));
    }

    [Fact]
    public async Task A_Folder_Imports_The_Same_Whatever_Order_Its_Files_Arrive_In()
    {
        // The order a directory is listed in is the file system's. Linux
        // listed Prod.bru before Local.bru, and the environments came out in
        // that order there and in the other on Windows.
        var files = await InteropSources.ReadAsync(Exported("bruno-bru"), TestContext.Current.CancellationToken);
        var forwards = new ImportCollectionRequest();
        forwards.Files.Add(files);
        var backwards = new ImportCollectionRequest();
        backwards.Files.Add(Enumerable.Reverse(files));

        var a = InteropRpc.ImportCollection(forwards);
        var b = InteropRpc.ImportCollection(backwards);

        Assert.Equal(a.Environments.Select(e => e.Name), b.Environments.Select(e => e.Name));
        Assert.Equal(a.Collections[0].Items.Select(i => i.Name), b.Collections[0].Items.Select(i => i.Name));
    }

    [Fact]
    public async Task Bruno_Annotations_Are_Not_Read_As_Headers_Or_Variables()
    {
        // collection.bru as the app writes it: @description('''…''') above a
        // header, over several lines, and @number / @object above a variable.
        var r = await ImportAsync("bruno-bru");

        var item = r.Collections[0].Items.Single(i => i.Name == "Bearer Auth 200" && i.Folders.Contains("via auth"));
        Assert.Equal("again", item.Metadata["check"]);
        Assert.Equal("collection-header-value", item.Metadata["collection-header"]);
        Assert.DoesNotContain(item.Metadata.Keys, k => k.Contains('@', StringComparison.Ordinal) || k.Contains("asd", StringComparison.Ordinal));

        var local = r.Environments.Single(e => e.Name == "Local");
        Assert.Equal("100", local.Vars["coll_num"]);
        Assert.Equal("""{"scope":"collection"}""", local.Vars["coll_obj"]);
        Assert.DoesNotContain(local.Vars.Keys, k => k.StartsWith('@'));
    }

    [Fact]
    public async Task Bruno_Auth_Inherits_From_The_Collection_Unless_The_Request_Says_None()
    {
        var r = await ImportAsync("bruno-bru");
        var items = r.Collections[0].Items;

        // auth: inherit → the collection's bearer token.
        Assert.Equal("Bearer {{bearer_auth_token}}", items.Single(i => i.Name == "inherit Bearer Auth 200").Metadata["Authorization"]);
        // auth: digest is configured on the environment in Bowire, and the
        // collection's bearer token must not stand in for it.
        var digest = items.Single(i => i.Name == "Digest Auth 200");
        Assert.DoesNotContain("Authorization", digest.Metadata.Keys);
        Assert.Contains(
            Details(r, ReportEntry.Types.Kind.Skipped, "auth / digest / Digest Auth 200"),
            d => d.StartsWith("digest auth", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_Bruno_Graphql_Request_Carries_Its_Variables()
    {
        var r = await ImportAsync("bruno-bru");

        var mutation = r.Collections[0].Items.Single(i => i.Name == "mutation");
        Assert.Equal("POST", mutation.Method);
        using var body = System.Text.Json.JsonDocument.Parse(mutation.Messages[0]);
        Assert.StartsWith("mutation create($id: String!)", body.RootElement.GetProperty("query").GetString(), StringComparison.Ordinal);
        Assert.Equal("1", body.RootElement.GetProperty("variables").GetProperty("id").GetString());
    }

    // ---- Bruno, OpenCollection YAML ----

    [Fact]
    public async Task A_Bruno_OpenCollection_Folder_Imports_Its_Requests_And_Environments()
    {
        var r = await ImportAsync("bruno-opencollection");

        Assert.Equal("opencollection", r.Format);
        var collection = Assert.Single(r.Collections);
        Assert.Equal("bruno-yml-testbench", collection.Name);
        Assert.Equal(
            ["delayed headers", "delayed body", "typed vars", "form-urlencoded", "multipart"],
            collection.Items.Select(i => i.Name));
        Assert.Equal(["response-time"], collection.Items[0].Folders);
        Assert.Equal(["Local", "Prod"], r.Environments.Select(e => e.Name));
        Assert.All(collection.Items, i => Assert.False(string.IsNullOrWhiteSpace(i.Service)));
    }
}
