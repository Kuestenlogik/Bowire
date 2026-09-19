// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using System.Text.Json;

namespace Kuestenlogik.Bowire.Protocol.GraphQL.Tests;

/// <summary>
/// The <c>graphql-multipart-request-spec</c> body (#713).
/// </summary>
/// <remarks>
/// Three parts in one form: <c>operations</c> with a null standing where
/// each file goes, <c>map</c> saying which part fills which hole, then the
/// files. The indirection exists because a file cannot sit inside JSON, and
/// getting the null-and-map pair wrong is the failure mode — a server walks
/// the map and expects to find something to replace.
/// </remarks>
public sealed class GraphQLMultipartRequestTests
{
    private static JsonElement Vars(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static GraphQLUpload Upload(string path, string name = "a.txt", string? type = "text/plain")
        => new(path, name, type, Encoding.UTF8.GetBytes("hello"));

    private static async Task<(JsonDocument Operations, JsonDocument Map, List<string> PartNames)> ReadAsync(
        HttpContent content)
    {
        var form = Assert.IsType<MultipartFormDataContent>(content);
        JsonDocument? operations = null, map = null;
        var names = new List<string>();

        foreach (var part in form)
        {
            var name = part.Headers.ContentDisposition?.Name?.Trim('"') ?? "";
            names.Add(name);
            if (name == "operations") operations = JsonDocument.Parse(await part.ReadAsStringAsync());
            else if (name == "map") map = JsonDocument.Parse(await part.ReadAsStringAsync());
        }

        Assert.NotNull(operations);
        Assert.NotNull(map);
        return (operations!, map!, names);
    }

    [Fact]
    public async Task The_File_Slot_Is_Null_And_The_Map_Points_At_It()
    {
        // The pair that makes the spec work. A slot that is absent rather
        // than null, or a map naming a path that is not there, both leave
        // the server with nothing to replace.
        using var content = GraphQLMultipartRequest.Build(
            "mutation Up($file: Upload!) { upload(file: $file) }",
            Vars("""{"file":null}"""), "Up", [Upload("variables.file")]);

        var (operations, map, names) = await ReadAsync(content);
        using var _1 = operations;
        using var _2 = map;

        Assert.Equal(JsonValueKind.Null, operations.RootElement.GetProperty("variables").GetProperty("file").ValueKind);
        Assert.Equal("variables.file", map.RootElement.GetProperty("0")[0].GetString());
        Assert.Equal(["operations", "map", "0"], names);
    }

    [Fact]
    public async Task The_Operation_And_Its_Name_Ride_In_The_Operations_Part()
    {
        using var content = GraphQLMultipartRequest.Build(
            "mutation Up($file: Upload!) { upload(file: $file) }",
            Vars("""{"file":null,"note":"chart"}"""), "Up", [Upload("variables.file")]);

        var (operations, map, _) = await ReadAsync(content);
        using var _1 = operations;
        using var _2 = map;

        Assert.Contains("upload(file: $file)", operations.RootElement.GetProperty("query").GetString()!, StringComparison.Ordinal);
        Assert.Equal("Up", operations.RootElement.GetProperty("operationName").GetString());
        // Variables that are not files survive untouched.
        Assert.Equal("chart", operations.RootElement.GetProperty("variables").GetProperty("note").GetString());
    }

    [Fact]
    public async Task Several_Files_Get_One_Part_Each_Numbered_By_The_Map()
    {
        using var content = GraphQLMultipartRequest.Build(
            "mutation Up($a: Upload!, $b: Upload!) { up(a: $a, b: $b) }",
            Vars("""{"a":null,"b":null}"""), "Up",
            [Upload("variables.a", "one.txt"), Upload("variables.b", "two.txt")]);

        var (operations, map, names) = await ReadAsync(content);
        using var _1 = operations;
        using var _2 = map;

        Assert.Equal("variables.a", map.RootElement.GetProperty("0")[0].GetString());
        Assert.Equal("variables.b", map.RootElement.GetProperty("1")[0].GetString());
        Assert.Equal(["operations", "map", "0", "1"], names);
    }

    [Fact]
    public async Task A_File_Inside_A_List_Grows_The_List_To_Fit()
    {
        // variables.files.2 means the list has three slots, and the two
        // before it are nulls the server fills from its own parts or leaves
        // empty. Indexing into a list that is not there would throw.
        using var content = GraphQLMultipartRequest.Build(
            "mutation Up($files: [Upload!]!) { up(files: $files) }",
            null, "Up", [Upload("variables.files.2")]);

        var (operations, map, _) = await ReadAsync(content);
        using var _1 = operations;
        using var _2 = map;

        var list = operations.RootElement.GetProperty("variables").GetProperty("files");
        Assert.Equal(JsonValueKind.Array, list.ValueKind);
        Assert.Equal(3, list.GetArrayLength());
        Assert.Equal("variables.files.2", map.RootElement.GetProperty("0")[0].GetString());
    }

    [Fact]
    public void An_Unknown_Media_Type_Becomes_Octet_Stream_Rather_Than_A_Guess()
    {
        // Guessing from the extension would be wrong often enough to matter,
        // and a server that cares reads its own magic bytes anyway.
        using var content = GraphQLMultipartRequest.Build(
            "mutation Up { up }", null, null, [Upload("variables.file", "x.bin", type: null)]);

        var form = Assert.IsType<MultipartFormDataContent>(content);
        var filePart = form.Last();
        Assert.Equal("application/octet-stream", filePart.Headers.ContentType?.MediaType);
    }

    // ---- reading the caller's message ----

    [Fact]
    public void A_Message_Without_Files_Declares_No_Uploads()
    {
        Assert.Empty(GraphQLMultipartRequest.Uploads("""{"query":"query A { a }"}"""));
        Assert.Empty(GraphQLMultipartRequest.Uploads(""));
        Assert.Empty(GraphQLMultipartRequest.Uploads(null));
        Assert.Empty(GraphQLMultipartRequest.Uploads("not json"));
    }

    [Fact]
    public void An_Upload_Is_Read_With_Its_Path_Name_And_Bytes()
    {
        var uploads = GraphQLMultipartRequest.Uploads("""
            {"query":"mutation Up { up }","files":[
              {"variablePath":"variables.file","name":"chart.png","contentType":"image/png","base64":"aGVsbG8="}
            ]}
            """);

        var one = Assert.Single(uploads);
        Assert.Equal("variables.file", one.VariablePath);
        Assert.Equal("chart.png", one.FileName);
        Assert.Equal("image/png", one.ContentType);
        Assert.Equal("hello", Encoding.UTF8.GetString(one.Content));
    }

    [Fact]
    public void A_Half_Filled_Entry_Is_Skipped_Rather_Than_Failing_The_Operation()
    {
        // A file picker that was opened and abandoned should not fail the
        // call. The variable stays null and the server's own "expected a
        // file here" is the clearer message.
        var uploads = GraphQLMultipartRequest.Uploads("""
            {"files":[
              {"variablePath":"variables.a"},
              {"base64":"aGVsbG8="},
              {"variablePath":"variables.c","base64":"not valid base64!!"},
              {"variablePath":"variables.d","base64":"aGVsbG8="}
            ]}
            """);

        var one = Assert.Single(uploads);
        Assert.Equal("variables.d", one.VariablePath);
    }

    [Fact]
    public void A_File_Without_A_Name_Still_Has_One_On_The_Wire()
    {
        // multipart requires a filename on the part; omitting it makes some
        // servers reject the whole form.
        var one = Assert.Single(GraphQLMultipartRequest.Uploads("""
            {"files":[{"variablePath":"variables.a","base64":"aGVsbG8="}]}
            """));

        Assert.Equal("file", one.FileName);
    }
}
