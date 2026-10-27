// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;

namespace Kuestenlogik.Bowire.Mcp.Tests;

/// <summary>
/// bowire.import and bowire.export.http (#673, #674): the MCP face of the
/// Interop rpcs. The formats themselves are covered in the Interop tests;
/// these hold the tool contract — inputs, the .bww side file, error strings.
/// </summary>
public sealed class BowireMcpInteropTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"bowire-mcp-interop-{Guid.NewGuid():N}");

    public BowireMcpInteropTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* best-effort */ }
    }

    [Fact]
    public async Task Import_Returns_Collections_And_Writes_A_Workspace_File()
    {
        var source = Path.Combine(_dir, "orders.http");
        await File.WriteAllTextAsync(source, "### List orders\nGET https://api.example.com/orders\n", TestContext.Current.CancellationToken);
        var outPath = Path.Combine(_dir, "orders.bww");

        var json = await BowireMcpTools.Import(source, outPath: outPath);

        using var doc = JsonDocument.Parse(json);
        Assert.Equal(Path.GetFullPath(outPath), doc.RootElement.GetProperty("outPath").GetString());
        var result = doc.RootElement.GetProperty("result");
        Assert.Equal("http", result.GetProperty("format").GetString());
        Assert.Equal("List orders", result.GetProperty("collections")[0].GetProperty("items")[0].GetProperty("name").GetString());

        // The written workspace file reads straight into the export tool.
        var exported = await BowireMcpTools.ExportHttp(outPath);
        using var export = JsonDocument.Parse(exported);
        Assert.Contains("GET https://api.example.com/orders", export.RootElement.GetProperty("http").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Import_Of_A_Missing_Path_Returns_An_Error_String()
    {
        var result = await BowireMcpTools.Import(Path.Combine(_dir, "missing.http"));
        Assert.StartsWith("bowire.import: ", result, StringComparison.Ordinal);
        Assert.Contains("does not exist", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Import_Of_An_Unknown_Format_Returns_The_Reason()
    {
        var source = Path.Combine(_dir, "notes.txt");
        await File.WriteAllTextAsync(source, "just prose", TestContext.Current.CancellationToken);
        var result = await BowireMcpTools.Import(source);
        Assert.StartsWith("bowire.import: Not a format Bowire imports", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Export_Of_Something_That_Is_Not_A_Workspace_Returns_An_Error_String()
    {
        var source = Path.Combine(_dir, "bad.json");
        await File.WriteAllTextAsync(source, "{\"hello\":1}", TestContext.Current.CancellationToken);
        var result = await BowireMcpTools.ExportHttp(source);
        Assert.StartsWith("bowire.export.http: ", result, StringComparison.Ordinal);
    }
}
