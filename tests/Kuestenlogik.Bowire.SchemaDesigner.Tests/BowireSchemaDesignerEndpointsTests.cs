// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Kuestenlogik.Bowire.SchemaDesigner.Tests;

/// <summary>
/// <c>POST /api/schema/graph</c> — the Schema Designer rail's whole HTTP
/// surface, and what the workbench actually calls.
/// </summary>
/// <remarks>
/// <see cref="SchemaGraphBuilder"/> was well covered and the endpoint
/// around it was not covered at all, which is the shape a coverage report
/// flags and a reviewer misses: the part with the decisions in it — what
/// an undecodable descriptor does, what an empty body does, what reaches
/// the wire — had no test on it.
/// </remarks>
public sealed class BowireSchemaDesignerEndpointsTests
{
    private const string Pkg = "harbor.v1";

    private static async Task<IHost> BuildHost()
    {
        var host = new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer()
                   .Configure(app =>
                   {
                       app.UseRouting();
                       // Through the contribution, so the seam the host
                       // discovers is the one under test.
                       app.UseEndpoints(e =>
                           new BowireSchemaDesignerEndpointContribution().MapEndpoints(e, string.Empty));
                   })
                   .ConfigureServices(s => s.AddRouting());
            })
            .Build();
        await host.StartAsync(TestContext.Current.CancellationToken);
        return host;
    }

    private static async Task<(HttpStatusCode Status, JsonElement Body)> PostAsync(object? request)
    {
        using var host = await BuildHost();
        using var client = host.GetTestClient();
        var response = await client.PostAsJsonAsync(
            new Uri("/api/schema/graph", UriKind.Relative), request, TestContext.Current.CancellationToken);
        var json = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(json);
        return (response.StatusCode, doc.RootElement.Clone());
    }

    [Fact]
    public async Task A_Descriptor_Set_Comes_Back_As_Nodes_And_Edges()
    {
        var descriptor = Convert.ToBase64String(Set(File(
            Message("Vessel", Scalar("name", 1), Reference("berth", 2, $".{Pkg}.Berth")),
            Message("Berth", Scalar("code", 1)))));

        var (status, body) = await PostAsync(new { descriptors = new[] { descriptor } });

        Assert.Equal(HttpStatusCode.OK, status);
        var ids = body.GetProperty("nodes").EnumerateArray()
            .Select(n => n.GetProperty("id").GetString()).ToList();
        Assert.Contains($"{Pkg}.Vessel", ids);
        Assert.Contains($"{Pkg}.Berth", ids);
        // The reference field is what makes this a graph rather than a list.
        Assert.Contains(body.GetProperty("edges").EnumerateArray(),
            e => e.GetProperty("from").GetString() == $"{Pkg}.Vessel"
              && e.GetProperty("to").GetString() == $"{Pkg}.Berth");
    }

    [Fact]
    public async Task A_Kind_Travels_As_A_Name_Rather_Than_A_Number()
    {
        // Projected deliberately: a reordered enum must not silently change
        // what a client reads.
        var descriptor = Convert.ToBase64String(Set(File(Message("Vessel", Scalar("name", 1)))));

        var (_, body) = await PostAsync(new { descriptors = new[] { descriptor } });

        var kind = body.GetProperty("nodes")[0].GetProperty("kind");
        Assert.Equal(JsonValueKind.String, kind.ValueKind);
        // Lower-case by contract. Asserted as "holds no capital" rather
        // than by round-tripping the case, which would compare a value
        // with itself and pass whatever the endpoint sent.
        Assert.DoesNotContain(kind.GetString()!, c => char.IsUpper(c));
        Assert.NotEmpty(kind.GetString()!);
    }

    [Fact]
    public async Task An_Undecodable_Descriptor_Is_Skipped_Not_Fatal()
    {
        // One unreadable service must not blank the graph for every other
        // service on the page — the reason the handler skips rather than
        // refuses.
        var good = Convert.ToBase64String(Set(File(Message("Vessel", Scalar("name", 1)))));

        var (status, body) = await PostAsync(new { descriptors = new[] { "!!! not base64 !!!", good } });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Single(body.GetProperty("nodes").EnumerateArray(),
            n => n.GetProperty("id").GetString() == $"{Pkg}.Vessel");
    }

    [Fact]
    public async Task Base64_That_Decodes_To_Nonsense_Is_Skipped_Too()
    {
        // Decodable but not a FileDescriptorSet: the builder has to survive
        // it, because "valid base64" says nothing about the bytes inside.
        var garbage = Convert.ToBase64String([0xDE, 0xAD, 0xBE, 0xEF, 0x01, 0x02]);

        var (status, body) = await PostAsync(new { descriptors = new[] { garbage } });

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Empty(body.GetProperty("nodes").EnumerateArray());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"descriptors":null}""")]
    [InlineData("""{"descriptors":[]}""")]
    [InlineData("""{"descriptors":["",""]}""")]
    public async Task A_Request_With_Nothing_In_It_Is_An_Empty_Graph(string rawBody)
    {
        // Every one of these reaches the rail on a first paint, before any
        // service has been discovered. None is an error.
        using var host = await BuildHost();
        using var client = host.GetTestClient();
        using var content = new StringContent(rawBody, System.Text.Encoding.UTF8, "application/json");

        var response = await client.PostAsync(
            new Uri("/api/schema/graph", UriKind.Relative), content, TestContext.Current.CancellationToken);
        var json = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(json);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(doc.RootElement.GetProperty("nodes").EnumerateArray());
        Assert.Empty(doc.RootElement.GetProperty("edges").EnumerateArray());
    }

    [Fact]
    public async Task Well_Known_Types_Stay_Out_Unless_Asked_For()
    {
        // google/protobuf/* is referenced from everywhere and would
        // dominate the picture, so absent means false.
        var descriptor = Convert.ToBase64String(Set(
            File(Message("Vessel", Reference("at", 1, ".google.protobuf.Timestamp"))),
            WellKnownFile()));

        var (_, without) = await PostAsync(new { descriptors = new[] { descriptor } });
        var (_, with) = await PostAsync(new { descriptors = new[] { descriptor }, includeWellKnown = true });

        var idsWithout = without.GetProperty("nodes").EnumerateArray()
            .Select(n => n.GetProperty("id").GetString()!).ToList();
        var idsWith = with.GetProperty("nodes").EnumerateArray()
            .Select(n => n.GetProperty("id").GetString()!).ToList();

        Assert.DoesNotContain(idsWithout, id => id.StartsWith("google.protobuf.", StringComparison.Ordinal));
        Assert.Contains(idsWith, id => id.StartsWith("google.protobuf.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Several_Descriptor_Sets_Become_One_Graph()
    {
        // The workbench posts what it holds, which is one set per
        // discovered service.
        var first = Convert.ToBase64String(Set(File(Message("Vessel", Scalar("name", 1)))));
        var second = Convert.ToBase64String(Set(FileNamed("harbor/v1/berth.proto", Message("Berth", Scalar("code", 1)))));

        var (_, body) = await PostAsync(new { descriptors = new[] { first, second } });

        var ids = body.GetProperty("nodes").EnumerateArray()
            .Select(n => n.GetProperty("id").GetString()).ToList();
        Assert.Contains($"{Pkg}.Vessel", ids);
        Assert.Contains($"{Pkg}.Berth", ids);
    }

    // ---- descriptor construction helpers ----

    private static byte[] Set(params FileDescriptorProto[] files)
    {
        var set = new FileDescriptorSet();
        set.File.AddRange(files);
        return set.ToByteArray();
    }

    private static FileDescriptorProto File(params DescriptorProto[] messages)
        => FileNamed("harbor/v1/harbor.proto", messages);

    private static FileDescriptorProto FileNamed(string name, params DescriptorProto[] messages)
    {
        var file = new FileDescriptorProto { Name = name, Package = Pkg };
        file.MessageType.AddRange(messages);
        return file;
    }

    private static FileDescriptorProto WellKnownFile()
    {
        var file = new FileDescriptorProto
        {
            Name = "google/protobuf/timestamp.proto",
            Package = "google.protobuf",
        };
        file.MessageType.Add(Message("Timestamp", Scalar("seconds", 1)));
        return file;
    }

    private static DescriptorProto Message(string name, params FieldDescriptorProto[] fields)
    {
        var message = new DescriptorProto { Name = name };
        message.Field.AddRange(fields);
        return message;
    }

    private static FieldDescriptorProto Scalar(string name, int number) => new()
    {
        Name = name,
        Number = number,
        Type = FieldDescriptorProto.Types.Type.String,
        Label = FieldDescriptorProto.Types.Label.Optional,
    };

    private static FieldDescriptorProto Reference(string name, int number, string type) => new()
    {
        Name = name,
        Number = number,
        Type = FieldDescriptorProto.Types.Type.Message,
        TypeName = type,
        Label = FieldDescriptorProto.Types.Label.Optional,
    };
}
