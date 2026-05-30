// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Kuestenlogik.Bowire.Protocol.JsonRpc;

namespace Kuestenlogik.Bowire.Protocol.JsonRpc.Tests;

/// <summary>
/// Coverage for <see cref="BowireJsonRpcProtocol"/> — the parts that
/// work without a network: identity, scheme rejection, parameter
/// decoding, OpenRPC-document extraction. Wire-level invocation is
/// exercised by the integration suite against an in-process Kestrel
/// JSON-RPC stub.
/// </summary>
public sealed class BowireJsonRpcProtocolTests
{
    [Fact]
    public void Identity_Pins_Id_Name_And_Icon()
    {
        var p = new BowireJsonRpcProtocol();
        Assert.Equal("jsonrpc", p.Id);
        Assert.Equal("JSON-RPC", p.Name);
        Assert.False(string.IsNullOrEmpty(p.IconSvg));
    }

    [Fact]
    public void Implements_IBowireProtocol()
        => Assert.IsAssignableFrom<IBowireProtocol>(new BowireJsonRpcProtocol());

    [Fact]
    public async Task DiscoverAsync_Empty_Url_Returns_Empty()
    {
        var p = new BowireJsonRpcProtocol();
        Assert.Empty(await p.DiscoverAsync("", false, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DiscoverAsync_Whitespace_Url_Returns_Empty()
    {
        var p = new BowireJsonRpcProtocol();
        Assert.Empty(await p.DiscoverAsync("   ", false, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DiscoverAsync_Non_Http_Scheme_Returns_Empty()
    {
        // Only http:// / https:// are accepted — every other plugin's
        // scheme (ws://, mqtt://, kafka://, ...) must be passed through.
        var p = new BowireJsonRpcProtocol();
        Assert.Empty(await p.DiscoverAsync("ws://example.com", false, TestContext.Current.CancellationToken));
        Assert.Empty(await p.DiscoverAsync("ftp://example.com", false, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DiscoverAsync_Unreachable_Endpoint_Returns_Empty_Without_Throwing()
    {
        // Port 1 is reserved and never listens; transport-level failure
        // returns an empty service tree so Bowire's dispatcher can try
        // the next plugin.
        var p = new BowireJsonRpcProtocol();
        p.Initialize(null);
        var result = await p.DiscoverAsync("http://127.0.0.1:1", false, TestContext.Current.CancellationToken);
        Assert.Empty(result);
    }

    [Fact]
    public async Task InvokeAsync_Unreachable_Endpoint_Returns_Error_Status()
    {
        var p = new BowireJsonRpcProtocol();
        p.Initialize(null);
        var result = await p.InvokeAsync(
            "http://127.0.0.1:1",
            "Methods", "anyMethod",
            jsonMessages: ["{}"],
            showInternalServices: false,
            metadata: null,
            ct: TestContext.Current.CancellationToken);
        Assert.NotEqual("OK", result.Status);
        Assert.Null(result.Response);
    }

    [Fact]
    public async Task InvokeAsync_Invalid_Url_Surfaces_Parse_Status()
    {
        var p = new BowireJsonRpcProtocol();
        p.Initialize(null);
        var result = await p.InvokeAsync(
            "not a url at all",
            "Methods", "anything",
            jsonMessages: ["{}"],
            showInternalServices: false,
            metadata: null,
            ct: TestContext.Current.CancellationToken);
        Assert.Contains("Could not parse", result.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvokeStreamAsync_Yields_Nothing()
    {
        // JSON-RPC 2.0 has no streaming primitive — explicit doc.
        var p = new BowireJsonRpcProtocol();
        var count = 0;
        await foreach (var _ in p.InvokeStreamAsync(
            "http://example.com",
            "Methods", "anything",
            jsonMessages: ["{}"],
            showInternalServices: false,
            metadata: null,
            ct: TestContext.Current.CancellationToken))
        {
            count++;
        }
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task OpenChannelAsync_Returns_Null()
    {
        var p = new BowireJsonRpcProtocol();
        var ch = await p.OpenChannelAsync(
            "http://example.com",
            "Methods", "anything",
            showInternalServices: false,
            metadata: null,
            ct: TestContext.Current.CancellationToken);
        Assert.Null(ch);
    }

    // -------- ParseParameters --------

    [Fact]
    public void ParseParameters_Empty_List_Returns_Null()
    {
        Assert.Null(BowireJsonRpcProtocol.ParseParameters([]));
    }

    [Fact]
    public void ParseParameters_Whitespace_Returns_Null()
    {
        Assert.Null(BowireJsonRpcProtocol.ParseParameters(["   "]));
    }

    [Fact]
    public void ParseParameters_Invalid_Json_Returns_Null()
    {
        Assert.Null(BowireJsonRpcProtocol.ParseParameters(["{not json"]));
    }

    [Fact]
    public void ParseParameters_Scalar_Returns_Null()
    {
        // Spec: params must be object (named) or array (positional).
        // A bare string / number / bool isn't valid and gets dropped.
        Assert.Null(BowireJsonRpcProtocol.ParseParameters(["\"scalar\""]));
        Assert.Null(BowireJsonRpcProtocol.ParseParameters(["42"]));
        Assert.Null(BowireJsonRpcProtocol.ParseParameters(["true"]));
    }

    [Fact]
    public void ParseParameters_Object_Is_Kept()
    {
        var result = BowireJsonRpcProtocol.ParseParameters([
            """{"name":"world","count":3}"""
        ]);
        Assert.NotNull(result);
        Assert.Equal(JsonValueKind.Object, result.Value.ValueKind);
        Assert.Equal("world", result.Value.GetProperty("name").GetString());
    }

    [Fact]
    public void ParseParameters_Array_Is_Kept()
    {
        var result = BowireJsonRpcProtocol.ParseParameters([
            """[1, "two", 3]"""
        ]);
        Assert.NotNull(result);
        Assert.Equal(JsonValueKind.Array, result.Value.ValueKind);
        Assert.Equal(3, result.Value.GetArrayLength());
    }

    // -------- ExtractMethodsFromOpenRpc --------

    [Fact]
    public void ExtractMethodsFromOpenRpc_Non_Object_Returns_Empty()
    {
        var doc = JsonSerializer.SerializeToElement("scalar");
        Assert.Empty(BowireJsonRpcProtocol.ExtractMethodsFromOpenRpc(doc));
    }

    [Fact]
    public void ExtractMethodsFromOpenRpc_Missing_Methods_Returns_Empty()
    {
        var doc = JsonSerializer.SerializeToElement(new { openrpc = "1.2.6", info = new { title = "demo" } });
        Assert.Empty(BowireJsonRpcProtocol.ExtractMethodsFromOpenRpc(doc));
    }

    [Fact]
    public void ExtractMethodsFromOpenRpc_Maps_Methods_With_Params()
    {
        // Minimal but realistic OpenRPC document — two methods, the
        // first with two named params (one required), the second with
        // no params.
        var doc = JsonSerializer.SerializeToElement(new
        {
            openrpc = "1.2.6",
            info = new { title = "demo", version = "1.0" },
            methods = new object[]
            {
                new
                {
                    name = "user.get",
                    summary = "Look up a user",
                    description = "Returns the user record",
                    @params = new object[]
                    {
                        new { name = "userId", required = true, schema = new { type = "string" } },
                        new { name = "expand", schema = new { type = "boolean" } }
                    },
                    result = new { name = "user", schema = new { type = "object" } }
                },
                new
                {
                    name = "ping",
                    summary = "Liveness probe",
                    @params = Array.Empty<object>(),
                    result = new { name = "pong", schema = new { type = "string" } }
                }
            }
        });

        var methods = BowireJsonRpcProtocol.ExtractMethodsFromOpenRpc(doc);
        Assert.Equal(2, methods.Count);

        var get = methods.Single(m => m.Name == "user.get");
        Assert.Equal("Methods/user.get", get.FullName);
        Assert.Equal("Look up a user", get.Summary);
        Assert.False(get.ClientStreaming);
        Assert.False(get.ServerStreaming);
        Assert.Equal(2, get.InputType.Fields.Count);

        var userId = get.InputType.Fields.Single(f => f.Name == "userId");
        Assert.True(userId.Required);
        Assert.Equal("required", userId.Label);
        Assert.Equal("string", userId.Type);

        var expand = get.InputType.Fields.Single(f => f.Name == "expand");
        Assert.False(expand.Required);
        Assert.Equal("optional", expand.Label);
        Assert.Equal("boolean", expand.Type);

        var ping = methods.Single(m => m.Name == "ping");
        Assert.Empty(ping.InputType.Fields);
    }

    [Fact]
    public void ExtractMethodsFromOpenRpc_Skips_Methods_Without_Name()
    {
        var doc = JsonSerializer.SerializeToElement(new
        {
            methods = new object[]
            {
                new { name = "real" },
                new { summary = "no name here" },
                "scalar entry"
            }
        });
        var methods = BowireJsonRpcProtocol.ExtractMethodsFromOpenRpc(doc);
        Assert.Single(methods);
        Assert.Equal("real", methods[0].Name);
    }

    [Fact]
    public void ExtractMethodsFromOpenRpc_Param_Without_Schema_Defaults_To_String()
    {
        var doc = JsonSerializer.SerializeToElement(new
        {
            methods = new object[]
            {
                new { name = "free", @params = new object[] { new { name = "anything" } } }
            }
        });
        var field = BowireJsonRpcProtocol.ExtractMethodsFromOpenRpc(doc)[0]
            .InputType.Fields[0];
        Assert.Equal("string", field.Type);
        Assert.False(field.Required);
    }

    [Fact]
    public void ExtractMethodsFromOpenRpc_Array_Type_Flags_IsRepeated()
    {
        var doc = JsonSerializer.SerializeToElement(new
        {
            methods = new object[]
            {
                new
                {
                    name = "bulk",
                    @params = new object[]
                    {
                        new { name = "ids", schema = new { type = "array" } }
                    }
                }
            }
        });
        var field = BowireJsonRpcProtocol.ExtractMethodsFromOpenRpc(doc)[0]
            .InputType.Fields[0];
        Assert.True(field.IsRepeated);
        Assert.Equal("array", field.Type);
    }
}
