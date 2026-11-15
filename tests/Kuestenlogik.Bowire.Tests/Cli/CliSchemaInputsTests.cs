// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.CommandLine;
using Kuestenlogik.Bowire.App.Cli;
using Kuestenlogik.Bowire.Cli;
using Kuestenlogik.Bowire.Auth;
using Kuestenlogik.Bowire.Testing;
using Kuestenlogik.Bowire.Tests.Plugins;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
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

    // The plugins the tool carries, as a command gets them: they declare the
    // kinds and read the files (#1018).
    private static readonly BowireProtocolRegistry Plugins = BowireProtocolRegistry.Discover();

    private static BowireProtocolRegistry Only(params IBowireProtocol[] protocols)
    {
        var registry = new BowireProtocolRegistry();
        foreach (var protocol in protocols) registry.Register(protocol);
        return registry;
    }

    private const string HarborSdl = """
        type Ship { id: ID! name: String! flag: String }
        type Query { ship(id: ID!): Ship }
        """;

    private const string CalculatorWsdl = """
        <definitions xmlns="http://schemas.xmlsoap.org/wsdl/"
                     xmlns:soap="http://schemas.xmlsoap.org/wsdl/soap/"
                     xmlns:tns="http://example.com/calc/wsdl"
                     targetNamespace="http://example.com/calc/wsdl">
          <message name="AddIn"><part name="a" type="tns:int"/></message>
          <portType name="Calculator">
            <operation name="Add"><input message="tns:AddIn"/></operation>
          </portType>
          <binding name="CalculatorSoap" type="tns:Calculator">
            <operation name="Add"><soap:operation soapAction="http://example.com/calc/Add"/></operation>
          </binding>
        </definitions>
        """;

    private const string PeopleEdmx = """
        <edmx:Edmx xmlns:edmx="http://docs.oasis-open.org/odata/ns/edmx" Version="4.0">
          <edmx:DataServices>
            <Schema xmlns="http://docs.oasis-open.org/odata/ns/edm" Namespace="Demo">
              <EntityType Name="Person">
                <Key><PropertyRef Name="UserName"/></Key>
                <Property Name="UserName" Type="Edm.String" Nullable="false"/>
              </EntityType>
              <EntityContainer Name="DemoContainer">
                <EntitySet Name="People" EntityType="Demo.Person"/>
              </EntityContainer>
            </Schema>
          </edmx:DataServices>
        </edmx:Edmx>
        """;

    private const string HarborOpenRpc = """
        {"openrpc":"1.3.2","info":{"title":"Harbor","version":"1.0.0"},
         "methods":[{"name":"ship.get","params":[{"name":"id","schema":{"type":"integer"}}]}]}
        """;

    private const string LightsAsyncApi = """
        asyncapi: 3.0.0
        info:
          title: Lights
          version: 1.0.0
        servers:
          production:
            host: 'broker.local:1883'
            protocol: mqtt
        channels:
          lightTurnOn:
            address: 'light/turn/on'
            messages:
              turnOn:
                payload:
                  type: object
        operations:
          sendTurnOn:
            action: send
            channel:
              $ref: '#/channels/lightTurnOn'
        """;

    private const string PetstoreOpenApi = """
        openapi: 3.0.0
        info:
          title: Petstore
          version: "1"
        paths: {}
        """;

    // ---- what a file is ----

    public static TheoryData<string, string, string> Files => new()
    {
        // The extension settles it.
        { "harbor.graphql", HarborSdl, "graphql-sdl" },
        { "calculator.wsdl", CalculatorWsdl, "wsdl" },
        { "people.edmx", PeopleEdmx, "edmx" },
        { "a.proto", "syntax = \"proto3\";", SchemaUploadStore.ProtoKind },
        // Several kinds share the extension: the content decides.
        { "harbor.json", HarborOpenRpc, "openrpc" },
        { "lights.yaml", LightsAsyncApi, "asyncapi" },
        { "petstore.yaml", PetstoreOpenApi, SchemaUploadStore.OpenApiKind },
        { "petstore.json", """{"openapi":"3.0.0"}""", SchemaUploadStore.OpenApiKind },
        // No kind declares the extension: the content decides there too.
        { "service.xml", CalculatorWsdl, "wsdl" },
        { "schema.txt", HarborSdl, "graphql-sdl" },
    };

    [Theory]
    [MemberData(nameof(Files))]
    public async Task A_File_Is_The_Kind_The_Upload_Route_Would_Make_It(string name, string content, string expected)
    {
        // The table --schema read a kind from knew three of these and called
        // every .yaml OpenAPI, so an AsyncAPI document went to the REST
        // plugin and SDL was an unknown extension.
        using var stderr = new StringWriter();
        var (schemas, code) = await CliSchemaInputs.ReadAsync([File_(name, content)], stderr, Plugins);

        Assert.Equal("", stderr.ToString());
        Assert.Equal(0, code);
        var one = Assert.Single(schemas!);
        Assert.Equal(expected, one.Kind);
        Assert.Equal(name, one.SourceName);
    }

    [Fact]
    public async Task A_File_Nobody_Reads_Is_Refused_With_What_The_Loaded_Plugins_Read()
    {
        using var stderr = new StringWriter();
        var (schemas, code) = await CliSchemaInputs.ReadAsync([File_("notes.txt", "just notes")], stderr, Plugins);

        Assert.Null(schemas);
        Assert.Equal(2, code);
        Assert.Contains("notes.txt", stderr.ToString(), StringComparison.Ordinal);
        Assert.Contains("GraphQL SDL (.graphql", stderr.ToString(), StringComparison.Ordinal);
        Assert.Contains("gRPC descriptor set (.protoset", stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_Refusal_Names_Only_What_Is_Loaded()
    {
        // With the gRPC plugin alone there is nobody to read SDL, and the
        // sentence does not offer it.
        using var stderr = new StringWriter();
        var (schemas, code) = await CliSchemaInputs.ReadAsync(
            [File_("harbor.graphql", HarborSdl)], stderr, Only(new Kuestenlogik.Bowire.Protocol.Grpc.BowireGrpcProtocol()));

        Assert.Null(schemas);
        Assert.Equal(2, code);
        Assert.Contains("gRPC descriptor set", stderr.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("GraphQL", stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_File_Its_Plugin_Cannot_Read_Stops_The_Command_With_The_Readers_Reason()
    {
        using var stderr = new StringWriter();
        var (schemas, code) = await CliSchemaInputs.ReadAsync(
            [File_("broken.graphql", "type Query { ship(: Ship }")], stderr, Plugins);

        Assert.Null(schemas);
        Assert.Equal(2, code);
        Assert.Contains("broken.graphql", stderr.ToString(), StringComparison.Ordinal);
        Assert.Contains("Not GraphQL SDL", stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_Kind_The_Command_Does_Not_Use_Is_Refused_Rather_Than_Accepted_And_Ignored()
    {
        // `list` and `describe` are gRPC-only; taking SDL there and doing
        // nothing with it would let a pipeline believe it had been used.
        using var stderr = new StringWriter();
        var (schemas, code) = await CliSchemaInputs.ReadAsync(
            [File_("harbor.graphql", HarborSdl)], stderr, Plugins,
            CliSchemaInputs.Taken.Only(SchemaUploadStore.ProtoSetKind));

        Assert.Null(schemas);
        Assert.Equal(2, code);
        Assert.Contains("does not use GraphQL SDL", stderr.ToString(), StringComparison.Ordinal);
        Assert.Contains("It takes: gRPC descriptor set", stderr.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("OpenAPI", stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task What_A_Plugin_Reads_Is_Listed_Through_It_Once_The_Scope_Is_Open()
    {
        using var stderr = new StringWriter();
        var (schemas, _) = await CliSchemaInputs.ReadAsync(
            [File_("harbor.graphql", HarborSdl), File_("lights.yaml", LightsAsyncApi)], stderr, Plugins);

        using (SchemaUploadStore.EnterExplicit(schemas!))
        {
            var listed = UploadedSchemaImports.GetServices(Plugins).Select(s => s.Source + ":" + s.Name).ToList();
            Assert.Contains("graphql:Query", listed);
            Assert.Contains("asyncapi:lightTurnOn", listed);
        }
    }

    // ---- reading ----

    [Fact]
    public async Task No_Schema_Given_Means_Read_What_Is_Stored()
    {
        using var stderr = new StringWriter();

        Assert.Null((await CliSchemaInputs.ReadAsync(null, stderr, Plugins)).Schemas);
        Assert.Null((await CliSchemaInputs.ReadAsync([], stderr, Plugins)).Schemas);
        Assert.Equal(0, (await CliSchemaInputs.ReadAsync([], stderr, Plugins)).ExitCode);
        Assert.Empty(stderr.ToString());
    }

    [Fact]
    public async Task A_File_Is_Read_With_Its_Kind_And_Its_Name()
    {
        using var stderr = new StringWriter();
        var (schemas, code) = await CliSchemaInputs.ReadAsync(
            [File_("petstore.json", """{"openapi":"3.0.0"}""")], stderr, Plugins);

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
            [File_("a.json"), File_("b.yaml", "openapi: 3.0.0")], stderr, Plugins);

        Assert.Equal(["a.json", "b.yaml"], schemas!.Select(s => s.SourceName));
    }

    [Theory]
    [InlineData("notes.txt", "It takes: ")]
    [InlineData("missing.json", "")]
    public async Task A_Path_That_Cannot_Be_Used_Names_Itself_And_Stops_The_Command(
        string name, string expectedFragment)
    {
        // Exit 2, not 1: this is a usage error, and CI scripts branch on it.
        using var stderr = new StringWriter();
        var path = name == "notes.txt" ? File_(name, "hello") : Path.Combine(_dir, name);

        var (schemas, code) = await CliSchemaInputs.ReadAsync([path], stderr, Plugins);

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
        var (schemas, code) = await CliSchemaInputs.ReadAsync([File_("empty.json", "   ")], stderr, Plugins);

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
            [File_("a.proto", "syntax = \"proto3\";")], stderr, Plugins, CliSchemaInputs.Taken.ForCalls);

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
            [Binary("api.protoset", bytes)], stderr, Plugins, CliSchemaInputs.Taken.ForCalls);

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
            [File_("api.pb", "syntax = \"proto3\";")], stderr, Plugins);

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
            [Binary("api.protoset", DescriptorSet("shop/money.proto"))], stderr, Plugins);

        Assert.Null(schemas);
        Assert.Equal(2, code);
        Assert.Contains("shop/money.proto", stderr.ToString(), StringComparison.Ordinal);
        Assert.Contains("--include_imports", stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_Its_Plugin_A_Descriptor_Set_Is_Not_Something_The_Command_Reads()
    {
        // A host with no gRPC plugin has nothing that would use the set.
        // Passing the bytes on unread, as this did before the kinds came
        // from the plugins, accepted a file and did nothing with it.
        using var stderr = new StringWriter();
        var (schemas, code) = await CliSchemaInputs.ReadAsync(
            [Binary("api.pb", DescriptorSet())], stderr, Only());

        Assert.Null(schemas);
        Assert.Equal(2, code);
        Assert.Contains("api.pb", stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_OpenApi_Document_Is_Refused_By_A_Command_That_Reads_None()
    {
        using var stderr = new StringWriter();
        var (schemas, code) = await CliSchemaInputs.ReadAsync(
            [File_("petstore.json", "{\"openapi\":\"3.0.0\"}")], stderr, Plugins, CliSchemaInputs.Taken.Only(SchemaUploadStore.ProtoSetKind));

        Assert.Null(schemas);
        Assert.Equal(2, code);
        Assert.Contains("petstore.json", stderr.ToString(), StringComparison.Ordinal);
        Assert.Contains("does not use OpenAPI", stderr.ToString(), StringComparison.Ordinal);
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
            [File_("a.proto", "syntax = \"proto3\";")], stderr, Plugins);

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
            [File_("petstore.json", """{"openapi":"3.0.0"}""")], stderr, Plugins);

        Assert.NotNull(schemas);
        Assert.Empty(SchemaUploadStore.GetAll(SchemaUploadStore.OpenApiKind));

        using (SchemaUploadStore.EnterExplicit(schemas!))
        {
            Assert.Single(SchemaUploadStore.GetAll(SchemaUploadStore.OpenApiKind));
        }

        Assert.Empty(SchemaUploadStore.GetAll(SchemaUploadStore.OpenApiKind));
    }

    // ---- through the commands ----

    private static async Task<(int Code, string Stdout, string Stderr)> BowireAsync(params string[] args)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var code = await BowireCli.RunAsync(
            args, new ConfigurationBuilder().Build(), TestPluginLoaders.None(), stdout, stderr,
            TestContext.Current.CancellationToken);
        return (code, stdout.ToString(), stderr.ToString());
    }

    [Fact]
    public async Task A_Call_With_A_Schema_Of_Another_Plugin_Goes_To_That_Plugin()
    {
        // No --protocol: SDL says the call is GraphQL. Before, the call
        // stayed on the gRPC path and failed to connect as gRPC.
        var sent = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls(LoopbackHost.AnyPort);
        await using var app = builder.Build();
        app.MapPost("/graphql", async (HttpContext ctx) =>
        {
            using var reader = new StreamReader(ctx.Request.Body);
            var body = await reader.ReadToEndAsync(ctx.RequestAborted);
            ctx.Response.ContentType = "application/json";
            // Refuses introspection the way a production server does.
            if (body.Contains("__schema", StringComparison.Ordinal))
            {
                await ctx.Response.WriteAsync("""{ "errors": [{ "message": "introspection disabled" }] }""");
                return;
            }
            // Every plugin probes the URL before the call; the operation is
            // the one request that names the field.
            if (body.Contains("ship", StringComparison.Ordinal)) sent.TrySetResult(body);
            await ctx.Response.WriteAsync("""{ "data": { "ship": { "id": "101", "name": "Aurora", "flag": "DE" } } }""");
        });
        await app.StartAsync(TestContext.Current.CancellationToken);
        var url = LoopbackHost.BaseAddress(app.Services) + "/graphql";

        var (code, stdout, stderr) = await BowireAsync(
            "call", "Query/ship", "--url", url, "--schema", File_("harbor.graphql", HarborSdl),
            "-d", """{"id":"101"}""");

        Assert.True(code == 0, stderr);
        Assert.Contains("Aurora", stdout, StringComparison.Ordinal);
        var operation = await sent.Task.WaitAsync(TestContext.Current.CancellationToken);
        // Built from the file: the declared variable type and the fields of
        // the return type, where no schema gives String and __typename.
        Assert.Contains("$id: ID!", operation, StringComparison.Ordinal);
        Assert.Contains("flag", operation, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("list")]
    [InlineData("describe")]
    public async Task The_Grpc_Only_Commands_Refuse_A_Schema_They_Would_Not_Use(string command)
    {
        string[] args = command == "describe"
            ? ["describe", "Query", "--schema", File_("harbor.graphql", HarborSdl)]
            : ["list", "--schema", File_("harbor.graphql", HarborSdl)];

        var (code, _, stderr) = await BowireAsync(args);

        Assert.Equal(2, code);
        Assert.Contains("does not use GraphQL SDL", stderr, StringComparison.Ordinal);
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
