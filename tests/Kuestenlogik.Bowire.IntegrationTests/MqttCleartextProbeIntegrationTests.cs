// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Sockets;
using Kuestenlogik.Bowire.Protocol.Mqtt;
using Kuestenlogik.Bowire.Security.Scanner;
using MQTTnet;
using MQTTnet.Server;

namespace Kuestenlogik.Bowire.IntegrationTests;

/// <summary>
/// The MQTT cleartext probe against a live in-process broker
/// (Kuestenlogik/Bowire.VulnDb#25).
/// </summary>
/// <remarks>
/// <para>
/// The point of this probe is the case its sibling passes cleanly: a broker
/// that correctly demands credentials and then takes them over plaintext.
/// <c>MqttAuthProbe</c> asks whether anyone may in and is satisfied; every
/// username and password still crosses the wire in the open, because MQTT puts
/// them in the CONNECT packet as plain fields.
/// </para>
/// <para>
/// Driven against a real MQTTnet broker rather than a stub, because the finding
/// rests on an observation — a plaintext CONNECT that actually completed — and
/// a stub would only prove the scheme string was parsed.
/// </para>
/// </remarks>
public sealed class MqttCleartextProbeIntegrationTests : IAsyncLifetime
{
    private MqttServer? _broker;
    private int _brokerPort;

    public async ValueTask InitializeAsync()
    {
        _brokerPort = FindFreeTcpPort();
        var factory = new MqttServerFactory();
        _broker = factory.CreateMqttServer(
            new MqttServerOptionsBuilder()
                .WithDefaultEndpoint()
                .WithDefaultEndpointPort(_brokerPort)
                .Build());
        await _broker.StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_broker is not null)
        {
            await _broker.StopAsync();
            _broker.Dispose();
        }
    }

    [Fact]
    public async Task PlaintextBroker_WithACredentialExpected_IsReported()
    {
        var ct = TestContext.Current.CancellationToken;
        var probe = new MqttCleartextProbe();
        var protocol = new BowireMqttProtocol();

        var findings = await probe.RunAsync(
            $"mqtt://localhost:{_brokerPort}", protocol, ["Authorization: Bearer x"], ct);

        var f = Assert.Single(findings);
        Assert.Equal(ScanFindingStatus.Vulnerable, f.Status);
        Assert.Equal("BWR-OWASP-API8-MQTT-CLEARTEXT", f.Template.Recording.Vulnerability?.Id);
        // CWE-319 rather than a broken-auth code: the credential is correct and
        // required, it is the transport that gives it away.
        Assert.Equal("CWE-319", f.Template.Recording.Vulnerability?.Cwe);
    }

    [Fact]
    public async Task WithNoCredentialExpected_NothingIsReported()
    {
        // A public telemetry broker on plaintext is a design decision, not a
        // finding — there is no credential for the link to expose. Same gate
        // MqttAuthProbe uses, and the reason both stay quiet on an ordinary
        // scan of something that was never meant to be private.
        var ct = TestContext.Current.CancellationToken;
        var probe = new MqttCleartextProbe();
        var protocol = new BowireMqttProtocol();

        var findings = await probe.RunAsync(
            $"mqtt://localhost:{_brokerPort}", protocol, [], ct);

        Assert.Empty(findings);
    }

    [Fact]
    public async Task AnMqttsTargetIsLeftToTheTlsInspector()
    {
        // Addressed over TLS, so this probe has nothing to say — whether the
        // certificate is any good is SslProbeExecutor's question. Asserted
        // because the alternative failure is silent and expensive: a probe that
        // reported every mqtts:// target would train people to ignore it.
        var ct = TestContext.Current.CancellationToken;
        var probe = new MqttCleartextProbe();
        var protocol = new BowireMqttProtocol();

        var findings = await probe.RunAsync(
            $"mqtts://localhost:{_brokerPort}", protocol, ["Authorization: Bearer x"], ct);

        Assert.Empty(findings);
    }

    [Fact]
    public async Task AnHttpTargetIsNotAnMqttBroker()
    {
        // The scheme guard exists so an ordinary HTTP scan never opens a broker
        // socket, the same way MqttAuthProbe's does.
        var ct = TestContext.Current.CancellationToken;
        var probe = new MqttCleartextProbe();
        var protocol = new BowireMqttProtocol();

        var findings = await probe.RunAsync(
            "https://example.invalid/api", protocol, ["Authorization: Bearer x"], ct);

        Assert.Empty(findings);
    }

    private static int FindFreeTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
