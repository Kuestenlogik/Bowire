// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.CommandLine;
using Kuestenlogik.Bowire.App.Cli;
using Kuestenlogik.Bowire.Auth;
using Microsoft.Extensions.Configuration;

namespace Kuestenlogik.Bowire.Tests.Cli;

/// <summary>
/// <c>--schema</c> — naming a schema file on the command line (#654).
/// </summary>
/// <remarks>
/// The workbench stores an uploaded schema because the person comes back to
/// it; a CLI run names the file, because in CI it is in the repository and a
/// pipeline should not depend on what somebody uploaded on another machine.
/// </remarks>
public sealed class CliSchemaInputsTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "bowire-cli-schema-" + Guid.NewGuid().ToString("N"));

    /// <summary>
    /// Storage of this class's own: the assertions below read the store with
    /// no scope open to prove the scope is what does the work, and without
    /// this they would read the developer's own <c>~/.bowire</c> — green on a
    /// clean machine, red on one that has ever uploaded a schema.
    /// </summary>
    private readonly IDisposable _userScope;

    public CliSchemaInputsTests()
    {
        Directory.CreateDirectory(_dir);
        _userScope = BowireUserContext.Enter(new DefaultBowireUserStore(_dir));
    }

    public void Dispose()
    {
        _userScope.Dispose();
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
        GC.SuppressFinalize(this);
    }

    private string File_(string name, string content = "{}")
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    // ---- what an extension says ----

    [Theory]
    [InlineData("a.proto", SchemaUploadStore.ProtoKind)]
    [InlineData("A.PROTO", SchemaUploadStore.ProtoKind)]
    [InlineData("petstore.json", SchemaUploadStore.OpenApiKind)]
    [InlineData("petstore.yaml", SchemaUploadStore.OpenApiKind)]
    [InlineData("petstore.YML", SchemaUploadStore.OpenApiKind)]
    public void The_Extension_Decides_What_A_File_Is(string name, string expected)
        => Assert.Equal(expected, CliSchemaInputs.KindOf(name));

    [Theory]
    [InlineData("notes.txt")]
    [InlineData("schema")]
    [InlineData("schema.graphql")]
    public void Anything_Else_Is_Refused_Rather_Than_Guessed(string name)
    {
        // The workbench's drop zone can fall back to "probably OpenAPI"
        // because a person is watching the result. A pipeline is not.
        Assert.Null(CliSchemaInputs.KindOf(name));
    }

    // ---- reading ----

    [Fact]
    public async Task No_Schema_Given_Means_Read_What_Is_Stored()
    {
        using var stderr = new StringWriter();

        Assert.Null((await CliSchemaInputs.ReadAsync(null, stderr)).Schemas);
        Assert.Null((await CliSchemaInputs.ReadAsync([], stderr)).Schemas);
        Assert.Equal(0, (await CliSchemaInputs.ReadAsync([], stderr)).ExitCode);
        Assert.Empty(stderr.ToString());
    }

    [Fact]
    public async Task A_File_Is_Read_With_Its_Kind_And_Its_Name()
    {
        using var stderr = new StringWriter();
        var (schemas, code) = await CliSchemaInputs.ReadAsync(
            [File_("petstore.json", """{"openapi":"3.0.0"}""")], stderr);

        Assert.Equal(0, code);
        var one = Assert.Single(schemas!);
        Assert.Equal(SchemaUploadStore.OpenApiKind, one.Kind);
        Assert.Equal("petstore.json", one.SourceName);
        Assert.Contains("openapi", one.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Several_Files_Keep_The_Order_They_Were_Given()
    {
        using var stderr = new StringWriter();
        var (schemas, _) = await CliSchemaInputs.ReadAsync(
            [File_("a.json"), File_("b.yaml", "openapi: 3.0.0")], stderr);

        Assert.Equal(["a.json", "b.yaml"], schemas!.Select(s => s.SourceName));
    }

    [Theory]
    [InlineData("notes.txt", "expected .proto")]
    [InlineData("missing.json", "")]
    public async Task A_Path_That_Cannot_Be_Used_Names_Itself_And_Stops_The_Command(
        string name, string expectedFragment)
    {
        // Exit 2, not 1: this is a usage error, and CI scripts branch on it.
        using var stderr = new StringWriter();
        var path = name == "notes.txt" ? File_(name, "hello") : Path.Combine(_dir, name);

        var (schemas, code) = await CliSchemaInputs.ReadAsync([path], stderr);

        Assert.Null(schemas);
        Assert.Equal(2, code);
        Assert.Contains(name, stderr.ToString(), StringComparison.Ordinal);
        if (expectedFragment.Length > 0)
            Assert.Contains(expectedFragment, stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_Empty_File_Is_Refused_Rather_Than_Discovering_Nothing()
    {
        // Silently finding nothing is the failure this whole ticket was about.
        using var stderr = new StringWriter();
        var (schemas, code) = await CliSchemaInputs.ReadAsync([File_("empty.json", "   ")], stderr);

        Assert.Null(schemas);
        Assert.Equal(2, code);
        Assert.Contains("empty", stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_Proto_Is_Sent_To_Descriptor_Set_Where_That_Is_The_Right_Flag()
    {
        // list / describe / call already take a compiled FileDescriptorSet,
        // which carries more than a .proto does. Two ways in one place is a
        // choice with a wrong answer, so this one is refused with a pointer
        // rather than accepted and quietly ignored.
        using var stderr = new StringWriter();
        var (schemas, code) = await CliSchemaInputs.ReadAsync(
            [File_("a.proto", "syntax = \"proto3\";")], stderr, allowProto: false);

        Assert.Null(schemas);
        Assert.Equal(2, code);
        Assert.Contains("--descriptor-set", stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_Proto_Is_Accepted_Where_It_Is_Read_Directly()
    {
        using var stderr = new StringWriter();
        var (schemas, code) = await CliSchemaInputs.ReadAsync(
            [File_("a.proto", "syntax = \"proto3\";")], stderr);

        Assert.Equal(0, code);
        Assert.Equal(SchemaUploadStore.ProtoKind, Assert.Single(schemas!).Kind);
    }

    // ---- it hands back data, and that is the point ----

    [Fact]
    public async Task Reading_Does_Not_Open_The_Scope_Itself()
    {
        // The bug this shape exists to prevent, and it shipped once before
        // an end-to-end run caught it: EnterExplicit sets an AsyncLocal, and
        // a value set inside an `async` method is discarded when that method
        // returns. A helper that opened the scope handed its caller a
        // disposable and none of the effect — every command silently ignored
        // --schema while looking correct.
        //
        // So the contract is that reading returns data. The caller opens the
        // scope in the method that goes on to do the work.
        using var stderr = new StringWriter();
        var (schemas, _) = await CliSchemaInputs.ReadAsync(
            [File_("petstore.json", """{"openapi":"3.0.0"}""")], stderr);

        Assert.NotNull(schemas);
        Assert.Empty(SchemaUploadStore.GetAll(SchemaUploadStore.OpenApiKind));

        using (SchemaUploadStore.EnterExplicit(schemas!))
        {
            Assert.Single(SchemaUploadStore.GetAll(SchemaUploadStore.OpenApiKind));
        }

        Assert.Empty(SchemaUploadStore.GetAll(SchemaUploadStore.OpenApiKind));
    }

    // ---- the grammar ----

    [Theory]
    [InlineData("discover")]
    [InlineData("list")]
    [InlineData("describe")]
    public void The_Commands_That_Read_A_Schema_Accept_The_Flag(string command)
    {
        var cfg = new ConfigurationBuilder().Build();
        Command cmd = command switch
        {
            "discover" => BowireCli.BuildDiscoverCommand(cfg),
            "list" => BowireCli.BuildListCommand(cfg),
            _ => BowireCli.BuildDescribeCommand(cfg),
        };

        // describe takes a positional target; the flag has to survive beside it.
        var argv = command == "describe"
            ? new[] { "svc/Method", "--schema", "a.json", "--schema", "b.yaml" }
            : ["--schema", "a.json", "--schema", "b.yaml"];

        Assert.Empty(cmd.Parse(argv).Errors);
    }
}
