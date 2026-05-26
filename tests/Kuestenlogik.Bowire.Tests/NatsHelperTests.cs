// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using System.Text;
using Kuestenlogik.Bowire.Models;
using Kuestenlogik.Bowire.Protocol.Nats;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// Unit tests for the synchronous helpers behind the NATS protocol
/// plugin: server-URL normalisation, payload-display fallback chain,
/// and the subject-tree builder. The async discovery / pub-sub paths
/// need a live <c>nats-server</c> and are covered separately by the
/// Testcontainers integration suite — these helpers don't talk to
/// the network at all so they belong in the fast unit project.
/// </summary>
public sealed class NatsHelperTests
{
    private static readonly Assembly s_pluginAsm = typeof(BowireNatsProtocol).Assembly;

    // ---- NatsConnectionHelper.NormaliseServerUrl ----

    [Theory]
    [InlineData("nats://localhost:4222", "nats://localhost:4222")]
    [InlineData("tls://broker.example:4222", "tls://broker.example:4222")]
    [InlineData("ws://broker.example:8080", "ws://broker.example:8080")]
    [InlineData("wss://broker.example:8080", "wss://broker.example:8080")]
    [InlineData("http://broker.example:4222", "nats://broker.example:4222")]
    [InlineData("https://broker.example:4222", "tls://broker.example:4222")]
    [InlineData("localhost:4222", "nats://localhost:4222")]
    [InlineData("broker.example", "nats://broker.example:4222")]
    public void NormaliseServerUrl_Maps_To_Canonical_Form(string input, string expected)
    {
        var result = Invoke<string?>("NatsConnectionHelper", "NormaliseServerUrl", input);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void NormaliseServerUrl_Empty_Or_Whitespace_Returns_Null(string input)
    {
        var result = Invoke<string?>("NatsConnectionHelper", "NormaliseServerUrl", input);
        Assert.Null(result);
    }

    [Fact]
    public void BuildOptions_Sets_Url_And_Timeouts()
    {
        var opts = Invoke<NATS.Client.Core.NatsOpts>(
            "NatsConnectionHelper", "BuildOptions", "nats://localhost:4222");
        Assert.Equal("nats://localhost:4222", opts.Url);
        Assert.Equal(TimeSpan.FromSeconds(10), opts.CommandTimeout);
        Assert.Equal(TimeSpan.FromSeconds(15), opts.ConnectTimeout);
        Assert.StartsWith("bowire-", opts.Name, StringComparison.Ordinal);
    }

    // ---- NatsPayloadHelper.PayloadToDisplayString ----

    [Fact]
    public void PayloadToDisplayString_Empty_Returns_Empty_String()
    {
        var s = Invoke<string>("NatsPayloadHelper", "PayloadToDisplayString", Array.Empty<byte>());
        Assert.Equal("", s);
    }

    [Fact]
    public void PayloadToDisplayString_Json_Gets_Pretty_Printed()
    {
        var bytes = Encoding.UTF8.GetBytes("""{"a":1,"b":"two"}""");
        var s = Invoke<string>("NatsPayloadHelper", "PayloadToDisplayString", bytes);
        // Indented serialiser inserts newline + 2-space indent.
        Assert.Contains("\"a\"", s, StringComparison.Ordinal);
        Assert.Contains("\n", s, StringComparison.Ordinal);
    }

    [Fact]
    public void PayloadToDisplayString_Plain_Text_Returns_Raw()
    {
        var bytes = Encoding.UTF8.GetBytes("hello world");
        var s = Invoke<string>("NatsPayloadHelper", "PayloadToDisplayString", bytes);
        Assert.Equal("hello world", s);
    }

    [Fact]
    public void PayloadToDisplayString_Binary_Returns_Hex_Dump()
    {
        // Bytes with embedded NUL + control chars are forced down the
        // hex-dump branch (LooksLikeText rejects them).
        var bytes = new byte[] { 0x00, 0x01, 0x02, 0xFE, 0xFF };
        var s = Invoke<string>("NatsPayloadHelper", "PayloadToDisplayString", bytes);
        Assert.Contains("[binary: 5 bytes]", s, StringComparison.Ordinal);
        Assert.Contains("00 01 02 FE FF", s, StringComparison.Ordinal);
    }

    [Fact]
    public void PayloadToDisplayString_Long_Binary_Truncates_With_Annotation()
    {
        // 300 bytes — beyond the 256-byte cap; the dump should
        // annotate the suppressed remainder. Use control-byte 0x07
        // (BEL) so LooksLikeText rejects the payload and we fall
        // into the hex branch instead of seeing 300 BEL characters.
        var bytes = new byte[300];
        for (var i = 0; i < bytes.Length; i++) bytes[i] = 0x07;
        var s = Invoke<string>("NatsPayloadHelper", "PayloadToDisplayString", bytes);
        Assert.Contains("[binary: 300 bytes]", s, StringComparison.Ordinal);
        Assert.Contains("... (44 more bytes)", s, StringComparison.Ordinal);
    }

    // ---- NatsDiscovery.BuildServices ----

    [Fact]
    public void BuildServices_Empty_Subject_Set_Returns_Empty_List()
    {
        var services = BuildServices([], "nats://localhost:4222");
        Assert.Empty(services);
    }

    [Fact]
    public void BuildServices_Single_Token_Subjects_Land_In_Root_Service()
    {
        var services = BuildServices(["health"], "nats://localhost:4222");
        var root = Assert.Single(services);
        Assert.Equal("(root)", root.Name);
        // Three methods per subject: subscribe, publish, request.
        Assert.Equal(3, root.Methods.Count);
        Assert.Contains(root.Methods, m => m.FullName.EndsWith("/subscribe", StringComparison.Ordinal));
        Assert.Contains(root.Methods, m => m.FullName.EndsWith("/publish", StringComparison.Ordinal));
        Assert.Contains(root.Methods, m => m.FullName.EndsWith("/request", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildServices_Groups_By_First_Token()
    {
        var services = BuildServices(
            ["orders.created", "orders.updated", "users.signup"],
            "nats://localhost:4222");

        Assert.Equal(2, services.Count);

        var orders = services.Single(s => s.Name == "orders");
        // 2 subjects * 3 methods each.
        Assert.Equal(6, orders.Methods.Count);
        Assert.Equal("nats", orders.Source);

        var users = services.Single(s => s.Name == "users");
        Assert.Equal(3, users.Methods.Count);
    }

    [Fact]
    public void BuildServices_Subscribe_Method_Is_ServerStreaming()
    {
        var services = BuildServices(["x.y"], "nats://localhost:4222");
        var sub = services.Single().Methods.Single(
            m => m.FullName == "nats/x.y/subscribe");
        Assert.Equal("ServerStreaming", sub.MethodType);
        Assert.True(sub.ServerStreaming);
        Assert.False(sub.ClientStreaming);
    }

    [Fact]
    public void BuildServices_Publish_And_Request_Methods_Are_Unary()
    {
        var services = BuildServices(["x.y"], "nats://localhost:4222");
        var methods = services.Single().Methods;
        foreach (var name in new[] { "nats/x.y/publish", "nats/x.y/request" })
        {
            var m = methods.Single(x => x.FullName == name);
            Assert.Equal("Unary", m.MethodType);
            Assert.False(m.ServerStreaming);
            Assert.False(m.ClientStreaming);
        }
    }

    [Fact]
    public void BuildServices_PublishInput_Has_Required_Payload_Field()
    {
        var services = BuildServices(["x.y"], "nats://localhost:4222");
        var publish = services.Single().Methods.Single(
            m => m.FullName == "nats/x.y/publish");
        var payload = publish.InputType?.Fields?.SingleOrDefault(f => f.Name == "payload");
        Assert.NotNull(payload);
        Assert.True(payload!.Required);
    }

    [Fact]
    public void BuildServices_SubscribeInput_Is_Empty()
    {
        // Subscriptions take no body; the input message is just a
        // placeholder so the form-renderer has something to bind.
        var services = BuildServices(["x.y"], "nats://localhost:4222");
        var sub = services.Single().Methods.Single(
            m => m.FullName == "nats/x.y/subscribe");
        Assert.Empty(sub.InputType?.Fields ?? []);
    }

    [Fact]
    public void BuildServices_Skips_Empty_Subject_Set_Even_When_Origin_Is_Set()
    {
        var services = BuildServices([], "nats://anywhere");
        Assert.Empty(services);
    }

    // ---- BowireNatsProtocol public-surface contract ----

    [Fact]
    public void Protocol_Identity_And_IconSvg_Are_Stable()
    {
        var p = new BowireNatsProtocol();
        Assert.Equal("nats", p.Id);
        Assert.Equal("NATS", p.Name);
        Assert.Contains("<svg", p.IconSvg, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DiscoverAsync_Empty_Url_Returns_Empty()
    {
        var p = new BowireNatsProtocol();
        var result = await p.DiscoverAsync(
            "",
            showInternalServices: false,
            ct: TestContext.Current.CancellationToken);
        Assert.Empty(result);
    }

    [Fact]
    public async Task InvokeAsync_Empty_Url_Surfaces_Validation_Error()
    {
        var p = new BowireNatsProtocol();
        var result = await p.InvokeAsync(
            "",
            service: "(root)",
            method: "nats/health/publish",
            jsonMessages: ["{}"],
            showInternalServices: false,
            metadata: null,
            ct: TestContext.Current.CancellationToken);
        Assert.NotEqual("OK", result.Status);
    }

    [Fact]
    public async Task OpenChannelAsync_Returns_Null_In_Phase1()
    {
        // Phase 1 of the plugin intentionally exposes no channel
        // surface — pub/sub + req/reply already cover the workbench's
        // invoke buttons. JetStream comes later.
        var p = new BowireNatsProtocol();
        var ch = await p.OpenChannelAsync(
            "nats://localhost:4222",
            service: "x",
            method: "x.y",
            showInternalServices: false,
            ct: TestContext.Current.CancellationToken);
        Assert.Null(ch);
    }

    [Fact]
    public void Protocol_Surfaces_AutoInterpretJson_And_ScanDuration_Settings()
    {
        var p = new BowireNatsProtocol();
        Assert.Contains(p.Settings, s => s.Key == "autoInterpretJson");
        Assert.Contains(p.Settings, s => s.Key == "scanDuration");
    }

    // ---- ResolveSubjectAndOp (private) ----

    [Theory]
    [InlineData("nats/orders.created/request", "orders.created", "request")]
    [InlineData("nats/health/publish", "health", "publish")]
    [InlineData("orders.created", "orders.created", "publish")]
    [InlineData("", "", "publish")]
    public void ResolveSubjectAndOp_Strips_Prefix_And_Defaults_To_Publish(
        string input, string expectedSubject, string expectedOp)
    {
        var resolver = typeof(BowireNatsProtocol).GetMethod(
            "ResolveSubjectAndOp",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("ResolveSubjectAndOp not found");
        // The method returns a (string, string) tuple — boxed as
        // ValueTuple<string,string> here.
        var tuple = (ValueTuple<string, string>)(resolver.Invoke(null, [input])
            ?? throw new InvalidOperationException("resolver returned null"));
        Assert.Equal(expectedSubject, tuple.Item1);
        Assert.Equal(expectedOp, tuple.Item2);
    }

    // ---- reflection helpers ----

    private static T Invoke<T>(string typeName, string methodName, params object?[] args)
    {
        var type = s_pluginAsm.GetType($"Kuestenlogik.Bowire.Protocol.Nats.{typeName}")
            ?? throw new InvalidOperationException($"Type {typeName} not found");
        var method = type.GetMethod(methodName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException($"Method {typeName}.{methodName} not found");
        return (T)method.Invoke(null, args)!;
    }

    private static List<BowireServiceInfo> BuildServices(string[] subjects, string originUrl)
    {
        var set = new HashSet<string>(subjects, StringComparer.Ordinal);
        return Invoke<List<BowireServiceInfo>>("NatsDiscovery", "BuildServices", set, originUrl);
    }
}
