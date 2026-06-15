// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Protocol.Pulsar;
using Xunit;

namespace Kuestenlogik.Bowire.Protocol.Pulsar.Tests;

/// <summary>
/// Unit-level coverage for the Pulsar plugin's pure helpers: URL
/// resolution into broker + admin endpoints, topic-name normalisation,
/// the admin-JSON parser, and the route encoding the discovery side
/// emits. The DotPulsar-driven produce/subscribe paths need a live
/// broker and live under the integration suite when Testcontainers
/// for Pulsar lands.
/// </summary>
public class PulsarPluginTests
{
    [Fact]
    public void Plugin_Identity_Is_Stable()
    {
        using var p = new BowirePulsarProtocol();
        Assert.Equal("Pulsar", p.Name);
        Assert.Equal("pulsar", p.Id);
        Assert.False(string.IsNullOrWhiteSpace(p.IconSvg));
    }

    [Theory]
    [InlineData("pulsar://localhost:6650", "pulsar://localhost:6650", "http://localhost:8080", false)]
    [InlineData("pulsar+ssl://broker.example.com:6651", "pulsar+ssl://broker.example.com:6651", "https://broker.example.com:8080", true)]
    [InlineData("http://localhost:8080", "pulsar://localhost:6650", "http://localhost:8080", false)]
    [InlineData("https://broker.example.com:8443", "pulsar+ssl://broker.example.com:6651", "https://broker.example.com:8443", true)]
    [InlineData("localhost", "pulsar://localhost:6650", "http://localhost:8080", false)]
    [InlineData("localhost:9999", "pulsar://localhost:9999", "http://localhost:8080", false)]
    public void Resolves_Endpoints_From_Various_Url_Shapes(
        string input, string expectedBroker, string expectedAdmin, bool expectedTls)
    {
        var e = PulsarConnectionHelper.Resolve(input);
        Assert.NotNull(e);
        Assert.Equal(expectedBroker, e!.BrokerUrl);
        Assert.Equal(expectedAdmin, e.AdminBaseUrl);
        Assert.Equal(expectedTls, e.UseTls);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ftp://x/foo")]
    public void Rejects_Unsupported_Shapes(string input)
    {
        Assert.Null(PulsarConnectionHelper.Resolve(input));
    }

    [Theory]
    [InlineData("my-topic", "persistent://public/default/my-topic")]
    [InlineData("tenant/ns/foo", "persistent://tenant/ns/foo")]
    [InlineData("persistent://t/n/already-qualified", "persistent://t/n/already-qualified")]
    [InlineData("non-persistent://t/n/np", "non-persistent://t/n/np")]
    [InlineData("", "")]
    public void Normalises_Topic_Name(string input, string expected)
    {
        Assert.Equal(expected, PulsarConnectionHelper.NormaliseTopicName(input));
    }

    [Theory]
    [InlineData("public/default,team-a/orders", new[] { "public/default", "team-a/orders" })]
    [InlineData(" public/default ,, team-a/orders ", new[] { "public/default", "team-a/orders" })]
    [InlineData("", new[] { "public/default" })]
    public void Parses_Namespaces_From_Setting_String(string input, string[] expected)
    {
        Assert.Equal(expected, BowirePulsarProtocol.ParseNamespaces(input));
    }

    [Fact]
    public void Parses_Topic_List_Json_Returns_Strings()
    {
        var topics = PulsarDiscovery.ParseTopicJson(
            "[\"persistent://public/default/a\",\"persistent://public/default/b\"]");
        Assert.Equal(2, topics.Count);
        Assert.Equal("persistent://public/default/a", topics[0]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-json")]
    [InlineData("{\"not\":\"an-array\"}")]
    public void Parse_Topic_Json_Returns_Empty_On_Bad_Input(string json)
    {
        Assert.Empty(PulsarDiscovery.ParseTopicJson(json));
    }

    [Theory]
    [InlineData("persistent://public/default/orders", "orders")]
    [InlineData("foo", "foo")]
    [InlineData("", "")]
    public void Short_Topic_Name_Strips_Persistent_Prefix(string input, string expected)
    {
        Assert.Equal(expected, PulsarDiscovery.ShortTopicName(input));
    }

    [Fact]
    public void Build_Topic_Methods_Emits_Produce_Plus_Subscribe()
    {
        var methods = PulsarDiscovery.BuildTopicMethods("persistent://public/default/orders");
        Assert.Equal(2, methods.Count);
        Assert.Contains(methods, m => m.Name == "produce" && !m.ServerStreaming);
        Assert.Contains(methods, m => m.Name == "subscribe" && m.ServerStreaming);
        // FullName encodes the topic so the invoke side doesn't need
        // a second discovery round-trip.
        Assert.All(methods, m => Assert.Contains("persistent://public/default/orders", m.FullName));
    }

    [Theory]
    [InlineData("pulsar/topic/persistent://public/default/orders/produce", "persistent://public/default/orders", "produce")]
    [InlineData("pulsar/topic/foo/subscribe", "foo", "subscribe")]
    public void Parses_Route_From_Method_FullName(string fullName, string expectedTopic, string expectedOp)
    {
        var route = BowirePulsarProtocol.ParseRoute(fullName);
        Assert.NotNull(route);
        Assert.Equal(expectedTopic, route!.Topic);
        Assert.Equal(expectedOp, route.Op);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-pulsar-route")]
    [InlineData("pulsar/topic/")]
    [InlineData("pulsar/topic/missing-op/")]
    public void Parse_Route_Returns_Null_For_Malformed_Method_Names(string method)
    {
        Assert.Null(BowirePulsarProtocol.ParseRoute(method));
    }

    [Fact]
    public void Settings_Carry_Defaults_For_Namespaces_And_From_Latest()
    {
        using var p = new BowirePulsarProtocol();
        Assert.Equal(2, p.Settings.Count);
        Assert.Contains(p.Settings, s => s.Key == "namespaces");
        Assert.Contains(p.Settings, s => s.Key == "subscribeFromLatest");
    }

    [Fact]
    public async Task DiscoverAsync_Returns_Empty_For_Unparseable_Url()
    {
        using var p = new BowirePulsarProtocol();
        var result = await p.DiscoverAsync("", showInternalServices: false, TestContext.Current.CancellationToken);
        Assert.Empty(result);
    }

    [Fact]
    public async Task InvokeAsync_Surfaces_Routing_Error_For_Bad_Method()
    {
        using var p = new BowirePulsarProtocol();
        var result = await p.InvokeAsync(
            "pulsar://localhost:6650",
            service: "ignored",
            method: "garbage",
            jsonMessages: ["{}"],
            showInternalServices: false,
            metadata: null,
            ct: TestContext.Current.CancellationToken);
        Assert.NotEqual("OK", result.Status);
        Assert.Contains("Unknown Pulsar route", result.Status);
    }
}
