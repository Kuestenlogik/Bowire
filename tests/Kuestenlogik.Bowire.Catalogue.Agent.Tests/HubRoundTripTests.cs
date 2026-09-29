// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using Kuestenlogik.Bowire.AgentHub;
using Kuestenlogik.Bowire.Sources;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Kuestenlogik.Bowire.Catalogue.Agent.Tests;

/// <summary>
/// #128 — the provider against a real hub rather than a stub: what an agent
/// registers is what the catalogue lists, token included.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "Test scope")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5399:HttpClient created without enabling CheckCertificateRevocationList", Justification = "Loopback-only test traffic")]
public sealed class HubRoundTripTests
{
    [Fact]
    public async Task What_an_agent_registers_is_what_the_provider_lists()
    {
        var ct = TestContext.Current.CancellationToken;
        var b = WebApplication.CreateSlimBuilder();
        b.Logging.ClearProviders();
        b.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0, l => l.Protocols = HttpProtocols.Http1));
        b.Configuration["Bowire:Hub:Token"] = "bootstrap";
        await using var hub = b.Build();
        hub.MapBowireHub();
        await hub.StartAsync(ct);
        var hubUrl = hub.Urls.First();

        using (var http = new HttpClient())
        using (var request = new HttpRequestMessage(HttpMethod.Post, new Uri(hubUrl + "/hub/agents")))
        {
            request.Content = JsonContent.Create(new BowireAgentRegistration(
                "orders", "node-1", "http://node-1:5080/bowire", Tags: ["env:prod"],
                Entries: [new BowireCatalogueEntry("http://node-1:5080", "orders", ["grpc"])]));
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer bootstrap");
            (await http.SendAsync(request, ct)).EnsureSuccessStatusCode();
        }

        var provider = new AgentCatalogueProvider(
            () => new BowireAgentCatalogueOptions { HubUrl = hubUrl, BootstrapToken = "bootstrap" },
            () => new HttpClient());
        var entry = Assert.Single(await provider.FetchAsync(ct));
        Assert.Equal("http://node-1:5080", entry.Url);
        Assert.Equal(["grpc"], entry.Protocols);
    }
}
