// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.CommandLine;
using Kuestenlogik.Bowire.App.Cli;
using Kuestenlogik.Bowire.Cli;
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
    [InlineData("api.protoset", SchemaUploadStore.ProtoSetKind)]
    [InlineData("api.PB", SchemaUploadStore.ProtoSetKind)]
    [InlineData("api.desc", SchemaUploadStore.ProtoSetKind)]
    [InlineData("api.binpb", SchemaUploadStore.ProtoSetKind)]
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
    [InlineData("notes.txt", "expected a .proto")]
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
    public async Task A_Proto_Is_Sent_To_Its_Descriptor_Set_Where_Messages_Are_Marshalled()
    {
        // list / describe / call / test marshal messages, and a .proto gives
        // names without layouts. Refused with a pointer at the compiled set,
        // which the same option takes, rather than accepted and quietly
        // ignored.
        using var stderr = new StringWriter();
        var (schemas, code) = await CliSchemaInputs.ReadAsync(
            [File_("a.proto", "syntax = \"proto3\";")], stderr, allowProto: false);

        Assert.Null(schemas);
        Assert.Equal(2, code);
        Assert.Contains("--schema api.protoset", stderr.ToString(), StringComparison.Ordinal);
        Assert.Contains("descriptor_set_out", stderr.ToString(), StringComparison.Ordinal);
    }

    // ---- descriptor sets (#991) ----

    private static byte[] DescriptorSet(params string[] imports)
    {
        var file = new Google.Protobuf.Reflection.FileDescriptorProto
        {
            Name = "ping.proto",
            Package = "demo",
            Syntax = "proto3",
            MessageType = { new Google.Protobuf.Reflection.DescriptorProto { Name = "Ping" } },
            Service =
            {
                new Google.Protobuf.Reflection.ServiceDescriptorProto
                {
                    Name = "Pinger",
                    Method =
                    {
                        new Google.Protobuf.Reflection.MethodDescriptorProto
                        {
                            Name = "Send", InputType = ".demo.Ping", OutputType = ".demo.Ping",
                        },
                    },
                },
            },
        };
        // An import that is used: a field of a type only the import defines.
        // One that is merely named resolves without it and is harmless.
        file.Dependency.AddRange(imports);
        if (imports.Length > 0)
        {
            file.MessageType[0].Field.Add(new Google.Protobuf.Reflection.FieldDescriptorProto
            {
                Name = "amount", Number = 1,
                Label = Google.Protobuf.Reflection.FieldDescriptorProto.Types.Label.Optional,
                Type = Google.Protobuf.Reflection.FieldDescriptorProto.Types.Type.Message,
                TypeName = ".shop.Money",
            });
        }
        return Google.Protobuf.MessageExtensions.ToByteArray(
            new Google.Protobuf.Reflection.FileDescriptorSet { File = { file } });
    }

    private string Binary(string name, byte[] bytes)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Fact]
    public async Task A_Descriptor_Set_Is_Read_As_Bytes_Where_A_Proto_Is_Refused()
    {
        using var stderr = new StringWriter();
        var bytes = DescriptorSet();
        var (schemas, code) = await CliSchemaInputs.ReadAsync(
            [Binary("api.protoset", bytes)], stderr, allowProto: false);

        Assert.Equal(0, code);
        var one = Assert.Single(schemas!);
        Assert.Equal(SchemaUploadStore.ProtoSetKind, one.Kind);
        Assert.Equal(bytes, one.Bytes);
        Assert.Empty(stderr.ToString());
    }

    [Fact]
    public async Task A_File_Named_Like_A_Descriptor_Set_That_Is_Not_One_Stops_The_Command()
    {
        using var stderr = new StringWriter();
        var (schemas, code) = await CliSchemaInputs.ReadAsync(
            [File_("api.pb", "syntax = \"proto3\";")], stderr, descriptorSets: Reader);

        Assert.Null(schemas);
        Assert.Equal(2, code);
        Assert.Contains("api.pb", stderr.ToString(), StringComparison.Ordinal);
        Assert.Contains("descriptor_set_out", stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_Descriptor_Set_Without_Its_Imports_Is_Refused_Before_The_Run()
    {
        // Otherwise it lists its services and fails on the first message
        // that uses an imported type, far from the protoc line that caused it.
        using var stderr = new StringWriter();
        var (schemas, code) = await CliSchemaInputs.ReadAsync(
            [Binary("api.protoset", DescriptorSet("shop/money.proto"))], stderr, descriptorSets: Reader);

        Assert.Null(schemas);
        Assert.Equal(2, code);
        Assert.Contains("shop/money.proto", stderr.ToString(), StringComparison.Ordinal);
        Assert.Contains("--include_imports", stderr.ToString(), StringComparison.Ordinal);
    }

    // The gRPC plugin, as the tool passes it: what reads a set before the run.
    private static readonly IBowireUploadedSchemaReader Reader =
        new Kuestenlogik.Bowire.Protocol.Grpc.BowireGrpcProtocol();

    [Fact]
    public async Task Without_A_Reader_A_Descriptor_Set_Is_Passed_On_Unread()
    {
        // A host with no gRPC plugin has nothing to read the set with, and
        // nothing that would use it either: the bytes go through as given.
        using var stderr = new StringWriter();
        var (schemas, code) = await CliSchemaInputs.ReadAsync(
            [File_("api.pb", "not a set")], stderr);

        Assert.Equal(0, code);
        Assert.Equal(SchemaUploadStore.ProtoSetKind, Assert.Single(schemas!).Kind);
    }

    [Fact]
    public async Task An_OpenApi_Document_Is_Refused_By_A_Command_That_Reads_None()
    {
        using var stderr = new StringWriter();
        var (schemas, code) = await CliSchemaInputs.ReadAsync(
            [File_("petstore.json", "{\"openapi\":\"3.0.0\"}")], stderr, allowOpenApi: false);

        Assert.Null(schemas);
        Assert.Equal(2, code);
        Assert.Contains("petstore.json", stderr.ToString(), StringComparison.Ordinal);
        Assert.Contains("descriptor set", stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Test_Takes_Schema_Files_Like_The_Other_Commands()
    {
        var cmd = BowireCli.BuildTestCommand(new ConfigurationBuilder().Build());
        var parsed = cmd.Parse(["flow.json", "--schema", "api.protoset", "--schema", "other.pb"]);

        Assert.Empty(parsed.Errors);
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
