// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Net;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Kuestenlogik.Bowire.Tests;

public sealed class BowireHttpClientFactoryTests
{
    [Fact]
    public void Create_With_Null_Config_Returns_Working_Client()
    {
        // Test paths that skip Initialize should still get a valid HttpClient.
        using var http = BowireHttpClientFactory.Create(config: null, pluginId: "rest");
        Assert.NotNull(http);
    }

    [Fact]
    public void Create_With_Custom_Timeout_Honours_It()
    {
        using var http = BowireHttpClientFactory.Create(config: null, pluginId: "rest", timeout: TimeSpan.FromMinutes(5));
        Assert.Equal(TimeSpan.FromMinutes(5), http.Timeout);
    }

    [Fact]
    public void CreateHandler_Returns_Handler_With_Custom_Validation_Callback()
    {
        // The callback exists — it's invoked at request time, so we can't
        // exercise it here without an actual TLS handshake. Just confirm
        // wiring.
        using var handler = BowireHttpClientFactory.CreateHandler(config: null, pluginId: "rest");
        Assert.NotNull(handler.ServerCertificateCustomValidationCallback);
    }

    [Fact]
    public void Validation_Callback_Trusts_Loopback_When_Global_Flag_True()
    {
        // The relaxed path should fire for https://localhost when the global
        // Bowire:TrustLocalhostCert is set, regardless of whether the OS
        // trust store accepted the cert. Simulate the "OS rejected" case by
        // passing SslPolicyErrors.RemoteCertificateNameMismatch.
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Bowire:TrustLocalhostCert"] = "true"
            })
            .Build();

        using var handler = BowireHttpClientFactory.CreateHandler(config, pluginId: "rest");

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://localhost:5000/api");
        var trusted = handler.ServerCertificateCustomValidationCallback!(
            request, null, null, System.Net.Security.SslPolicyErrors.RemoteCertificateNameMismatch);

        Assert.True(trusted);
    }

    [Fact]
    public void Validation_Callback_Rejects_Production_Url_Even_When_Flag_True()
    {
        // Defence in depth: TrustLocalhostCert=true on a misconfigured host
        // should NOT relax validation against an actual production hostname.
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Bowire:TrustLocalhostCert"] = "true"
            })
            .Build();

        using var handler = BowireHttpClientFactory.CreateHandler(config, pluginId: "rest");

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.example.com/things");
        var trusted = handler.ServerCertificateCustomValidationCallback!(
            request, null, null, System.Net.Security.SslPolicyErrors.RemoteCertificateChainErrors);

        Assert.False(trusted);
    }

    [Fact]
    public void Validation_Callback_Honours_Per_Plugin_Override()
    {
        // Per-plugin "false" beats global "true" — the SignalR plugin
        // example from the docs.
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Bowire:TrustLocalhostCert"] = "true",
                ["Bowire:rest:TrustLocalhostCert"] = "false"
            })
            .Build();

        using var handler = BowireHttpClientFactory.CreateHandler(config, pluginId: "rest");

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://localhost:5000/api");
        var trusted = handler.ServerCertificateCustomValidationCallback!(
            request, null, null, System.Net.Security.SslPolicyErrors.RemoteCertificateNameMismatch);

        Assert.False(trusted);
    }

    [Fact]
    public void Validation_Callback_Always_Accepts_When_Os_Trusts_The_Cert()
    {
        // Even with TrustLocalhostCert=false, a cert that the OS already
        // accepted (SslPolicyErrors.None) should pass through. The relaxed
        // path is only for the OS-failed branch.
        var config = new ConfigurationBuilder().Build();

        using var handler = BowireHttpClientFactory.CreateHandler(config, pluginId: "rest");

        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.example.com/things");
        var trusted = handler.ServerCertificateCustomValidationCallback!(
            request, null, null, System.Net.Security.SslPolicyErrors.None);

        Assert.True(trusted);
    }

    // ---- SocketsHttpHandler variant (gRPC HTTP/2 path) ----

    [Fact]
    public void CreateSocketsHttpHandler_Returns_Handler_With_Ssl_Validation_Callback()
    {
        using var handler = BowireHttpClientFactory.CreateSocketsHttpHandler(
            config: null, pluginId: "grpc", serverUrl: "https://localhost:5001");
        Assert.NotNull(handler.SslOptions.RemoteCertificateValidationCallback);
        Assert.True(handler.EnableMultipleHttp2Connections);
    }

    [Fact]
    public void SocketsHttpHandler_Trusts_Loopback_When_Flag_True()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Bowire:TrustLocalhostCert"] = "true"
            })
            .Build();

        using var handler = BowireHttpClientFactory.CreateSocketsHttpHandler(
            config, pluginId: "grpc", serverUrl: "https://localhost:5001");

        var trusted = handler.SslOptions.RemoteCertificateValidationCallback!(
            new object(), null, null, System.Net.Security.SslPolicyErrors.RemoteCertificateChainErrors);

        Assert.True(trusted);
    }

    [Fact]
    public void SocketsHttpHandler_Rejects_Production_Url_Even_When_Flag_True()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Bowire:TrustLocalhostCert"] = "true"
            })
            .Build();

        using var handler = BowireHttpClientFactory.CreateSocketsHttpHandler(
            config, pluginId: "grpc", serverUrl: "https://api.example.com:443");

        var trusted = handler.SslOptions.RemoteCertificateValidationCallback!(
            new object(), null, null, System.Net.Security.SslPolicyErrors.RemoteCertificateChainErrors);

        Assert.False(trusted);
    }

    [Fact]
    public void SocketsHttpHandler_Bypasses_Validation_When_Os_Trusts_The_Cert()
    {
        using var handler = BowireHttpClientFactory.CreateSocketsHttpHandler(
            config: null, pluginId: "grpc", serverUrl: "https://api.example.com:443");

        var trusted = handler.SslOptions.RemoteCertificateValidationCallback!(
            new object(), null, null, System.Net.Security.SslPolicyErrors.None);

        Assert.True(trusted);
    }
}
