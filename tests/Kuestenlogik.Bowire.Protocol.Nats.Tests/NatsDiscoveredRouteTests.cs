// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Kuestenlogik.Bowire.Models;

namespace Kuestenlogik.Bowire.Protocol.Nats.Tests;

/// <summary>
/// #1001 — the workbench sends a discovered method's <c>Name</c>, and the
/// plugin has to arrive at the same operation as the method's
/// <c>FullName</c> names. Discovery gave the subscribe, publish and request
/// of a subject the same name, the subject, which invoke parses as a
/// publish: a discovered request never waited for its reply. JetStream and
/// Services methods arrived as bare <c>info</c> / <c>consume</c> / endpoint
/// names, which only their service turns into a route.
/// </summary>
public sealed class NatsDiscoveredRouteTests
{
    private static string Route(string method)
    {
        var mi = typeof(BowireNatsProtocol).GetMethod("ParseRoute", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("ParseRoute not found");
        // The route is a record struct: its text names every component.
        return mi.Invoke(null, [method])!.ToString()!;
    }

    private static List<BowireServiceInfo> BuildCoreServices(params string[] subjects)
    {
        var type = typeof(BowireNatsProtocol).Assembly.GetType("Kuestenlogik.Bowire.Protocol.Nats.NatsDiscovery")!;
        var mi = type.GetMethod("BuildServices", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
        return (List<BowireServiceInfo>)mi.Invoke(null, [new HashSet<string>(subjects, StringComparer.Ordinal), "nats://h:4222"])!;
    }

    private static readonly BowireNatsProtocol s_plugin = new();

    [Fact]
    public void Each_Core_Method_Name_Reaches_The_Operation_Its_Full_Name_Names()
    {
        var service = Assert.Single(BuildCoreServices("orders.created"));

        Assert.Equal(3, service.Methods.Select(m => m.Name).Distinct(StringComparer.Ordinal).Count());
        Assert.All(service.Methods, m =>
            Assert.Equal(Route(m.FullName), Route(s_plugin.ResolveMethodName(service.Name, m.Name))));
        var request = service.Methods.Single(m => m.FullName.EndsWith("/request", StringComparison.Ordinal));
        Assert.Contains("Op = request", Route(s_plugin.ResolveMethodName(service.Name, request.Name)), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("info", "nats/jetstream/ORDERS/info")]
    [InlineData("consume", "nats/jetstream/ORDERS/consume")]
    [InlineData("orders.new", "nats/jetstream/ORDERS/publish/orders.new")]
    public void JetStream_Names_Become_Routes_Under_Their_Stream(string name, string fullName)
    {
        // Names and full names as NatsJetStreamDiscovery emits them.
        Assert.Equal(fullName, s_plugin.ResolveMethodName("JetStream:ORDERS", name));
    }

    [Theory]
    [InlineData("hello", "nats/services/echo/hello")]
    [InlineData("ping", "nats/services/echo/ping")]
    public void Services_Names_Become_Routes_Under_Their_Service(string name, string fullName)
    {
        Assert.Equal(fullName, s_plugin.ResolveMethodName("Service:echo", name));
    }

    [Theory]
    [InlineData("orders", "nats/orders.created/request")]
    [InlineData("JetStream:ORDERS", "nats/jetstream/ORDERS/info")]
    [InlineData("orders", "orders.created")]
    [InlineData("orders", "orders.created/publish")]
    public void A_Route_A_Subject_And_An_Operation_Name_Pass_Through(string service, string method)
    {
        // A route maps to itself, and a bare subject — what a request saved
        // before #1001 holds — still means what it meant.
        Assert.Equal(method, s_plugin.ResolveMethodName(service, method));
    }

    [Fact]
    public void A_Service_Qualified_Name_Is_Reduced_To_The_Name()
    {
        Assert.Equal("orders.created/request", s_plugin.ResolveMethodName("orders", "orders/orders.created/request"));
    }
}
