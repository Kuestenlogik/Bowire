// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Kuestenlogik.Bowire.Security.Scanner;
using Kuestenlogik.Bowire.Security.Scanner.Cli;

namespace Kuestenlogik.Bowire.Tests.Security;

/// <summary>
/// <c>bowire scan --schema</c> (#1016).
/// </summary>
/// <remarks>
/// <para>
/// The other commands name a schema file with <c>--schema</c>; the scan had
/// its own spelling, <c>--grpc-descriptor-set</c>, for the one kind it
/// reads. A pipeline that names its contract once had to name it twice. The
/// scan takes <c>--schema</c> now and the old option is gone from it.
/// </para>
/// <para>
/// What a scan does with the set is unchanged and is the reason it travels
/// as the plugin's marker rather than as an uploaded schema: the set names
/// what may be called and is never evidence of what the server exposes, so
/// the reflection check keeps asking the server with nothing in hand.
/// </para>
/// </remarks>
public sealed class ScanSchemaOptionTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "bowire-scan-schema-" + Guid.NewGuid().ToString("N"));

    public ScanSchemaOptionTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
        GC.SuppressFinalize(this);
    }

    private string File_(string name, string content)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static async Task<(int Code, string Stderr)> ScanAsync(params string[] args)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var parsed = new ScanCliCommand().Build().Parse(args);
        Assert.Empty(parsed.Errors);
        var code = await parsed.InvokeAsync(
            new System.CommandLine.InvocationConfiguration { Output = stdout, Error = stderr },
            TestContext.Current.CancellationToken);
        return (code, stderr.ToString());
    }

    [Fact]
    public void Scan_Takes_Schema_And_No_Longer_The_Old_Option()
    {
        var scan = new ScanCliCommand().Build();

        Assert.Empty(scan.Parse(["--target", "http://127.0.0.1:1", "--schema", "a.protoset", "--schema", "b.pb"]).Errors);
        Assert.NotEmpty(scan.Parse(["--target", "http://127.0.0.1:1", "--grpc-descriptor-set", "a.protoset"]).Errors);
    }

    [Fact]
    public async Task A_Proto_Is_Refused_Before_Anything_Is_Probed()
    {
        var (code, stderr) = await ScanAsync(
            "--target", "http://127.0.0.1:1", "--schema", File_("api.proto", "syntax = \"proto3\";"));

        Assert.Equal(2, code);
        Assert.Contains("api.proto", stderr, StringComparison.Ordinal);
        Assert.Contains("descriptor_set_out", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_OpenApi_Document_Is_Refused_Rather_Than_Accepted_And_Ignored()
    {
        // The scan reads no OpenAPI document. Taking the file and doing
        // nothing with it would let a pipeline believe it had been used.
        var (code, stderr) = await ScanAsync(
            "--target", "http://127.0.0.1:1", "--schema", File_("petstore.json", """{"openapi":"3.0.0"}"""));

        Assert.Equal(2, code);
        Assert.Contains("petstore.json", stderr, StringComparison.Ordinal);
        Assert.Contains("OpenAPI", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_File_That_Is_Not_A_Descriptor_Set_Is_Refused_With_Its_Path()
    {
        var (code, stderr) = await ScanAsync(
            "--target", "http://127.0.0.1:1", "--schema", File_("api.protoset", "syntax = \"proto3\";"));

        Assert.Equal(2, code);
        Assert.Contains("api.protoset", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void No_Schema_Means_No_Marker()
        => Assert.Null(ScanCommand.ProtocolMetadataFor(new ScanOptions { Target = "http://x" }));

    [Fact]
    public void One_Set_Travels_As_The_Bare_Path_The_Marker_Always_Took()
    {
        var metadata = ScanCommand.ProtocolMetadataFor(
            new ScanOptions { Target = "http://x", GrpcDescriptorSetPaths = ["/sets/api.protoset"] });

        Assert.Equal("/sets/api.protoset", Assert.Single(metadata!).Value);
        Assert.Equal(BowireMetadataKeys.GrpcDescriptorSet, Assert.Single(metadata!).Key);
    }

    [Fact]
    public void Several_Sets_Travel_As_A_List()
    {
        var metadata = ScanCommand.ProtocolMetadataFor(new ScanOptions
        {
            Target = "http://x",
            GrpcDescriptorSetPaths = ["/sets/a.protoset", "/sets/b.pb"],
        });

        using var marker = JsonDocument.Parse(metadata![BowireMetadataKeys.GrpcDescriptorSet]);
        Assert.Equal(
            ["/sets/a.protoset", "/sets/b.pb"],
            marker.RootElement.GetProperty("paths").EnumerateArray().Select(p => p.GetString()));
    }
}
