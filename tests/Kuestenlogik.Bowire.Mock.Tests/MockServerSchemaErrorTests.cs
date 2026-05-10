// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Mock.Tests;

/// <summary>
/// Schema-mode startup errors when the matching plugin isn't installed.
/// Each schema kind (openapi / protobuf / graphql) needs an explicit
/// <see cref="IBowireMockSchemaSource"/> registration; the host throws
/// a clear "install plugin X" message rather than a null dereference
/// when the registration is missing. These tests don't supply any
/// SchemaSources so each kind hits the throw branch.
/// </summary>
public sealed class MockServerSchemaErrorTests : IDisposable
{
    private readonly string _tempDir;

    public MockServerSchemaErrorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "bowire-schema-err-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task OpenApiSchema_WithoutMatchingPlugin_ThrowsInstallHint()
    {
        // Touch a file so the path exists; the throw fires before the file
        // is actually read.
        var schemaPath = Path.Combine(_tempDir, "schema.yaml");
        await File.WriteAllTextAsync(schemaPath, "openapi: 3.0.0\n", TestContext.Current.CancellationToken);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            MockServer.StartAsync(
                new MockServerOptions { SchemaPath = schemaPath, Port = 0, Watch = false /* no SchemaSources */ },
                TestContext.Current.CancellationToken));

        Assert.Contains("openapi", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Kuestenlogik.Bowire.Protocol.Rest", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GrpcSchema_WithoutMatchingPlugin_ThrowsInstallHint()
    {
        var schemaPath = Path.Combine(_tempDir, "descriptors.binpb");
        await File.WriteAllBytesAsync(schemaPath, new byte[] { 0x00 }, TestContext.Current.CancellationToken);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            MockServer.StartAsync(
                new MockServerOptions { GrpcSchemaPath = schemaPath, Port = 0, Watch = false /* no SchemaSources */ },
                TestContext.Current.CancellationToken));

        Assert.Contains("protobuf", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Kuestenlogik.Bowire.Protocol.Grpc", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GraphQlSchema_WithoutMatchingPlugin_ThrowsInstallHint()
    {
        var schemaPath = Path.Combine(_tempDir, "schema.graphql");
        await File.WriteAllTextAsync(schemaPath, "type Query { ping: String }\n", TestContext.Current.CancellationToken);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            MockServer.StartAsync(
                new MockServerOptions { GraphQlSchemaPath = schemaPath, Port = 0, Watch = false /* no SchemaSources */ },
                TestContext.Current.CancellationToken));

        Assert.Contains("graphql", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Kuestenlogik.Bowire.Protocol.GraphQL", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ThreeSourcesAllSet_ExactlyOneCheckRejects()
    {
        // Belt-and-braces — covers the validation branch when more than
        // one schema source is supplied.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            MockServer.StartAsync(
                new MockServerOptions
                {
                    SchemaPath = "a.yaml",
                    GrpcSchemaPath = "b.binpb",
                    GraphQlSchemaPath = "c.graphql",
                    Port = 0,
                    Watch = false
                },
                TestContext.Current.CancellationToken));
        Assert.Contains("exactly one", ex.Message, StringComparison.Ordinal);
    }
}
