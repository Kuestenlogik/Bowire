// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Sockets;
using System.Text;
using Kuestenlogik.Bowire.Mocking;
using Kuestenlogik.Bowire.Security;
using Kuestenlogik.Bowire.Security.Scanner;

namespace Kuestenlogik.Bowire.Tests.Security;

/// <summary>
/// #491 (#35 Phase 2g) — the raw-socket transport pass.
/// </summary>
public sealed class NetworkProbeExecutorTests
{
    private static BowireRecordingStep Step(string address, params string[] messages)
    {
        var step = new BowireRecordingStep
        {
            Id = "probe-1",
            Protocol = "network",
            Service = address,
            Method = "TCP",
            MethodType = "Unary",
            Status = "OK",
            Metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["read-size"] = "1024" },
        };
        foreach (var m in messages) step.Messages.Add(m);
        return step;
    }

    // The listener is started once and STAYS started; the port comes off the
    // live socket. Binding a probe socket, reading its port and closing it
    // before the real listener binds is the pattern behind the flaky
    // McpDiscoveryWireTests (#556) — another process can take the port in the
    // gap. Not repeating it here.
    private static (TcpListener Listener, string Address) StartListener()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        return (listener, $"127.0.0.1:{port}");
    }

    [Fact]
    public void Unescape_Expands_The_Sequences_Templates_Write_Literally()
    {
        // A PING that goes out as the literal characters backslash-r
        // backslash-n gets no answer, and the probe would then report a
        // wide-open Redis as clean.
        Assert.Equal("PING\r\n", NetworkProbeExecutor.Unescape(@"PING\r\n"));
        Assert.Equal("a\tb", NetworkProbeExecutor.Unescape(@"a\tb"));
        Assert.Equal("\0", NetworkProbeExecutor.Unescape(@"\0"));
        Assert.Equal("A", NetworkProbeExecutor.Unescape(@"\x41"));
        Assert.Equal(@"\", NetworkProbeExecutor.Unescape(@"\\"));
    }

    [Fact]
    public void Unescape_Keeps_An_Unknown_Escape_Intact()
    {
        // Swallowing it would silently corrupt the payload.
        Assert.Equal(@"\q", NetworkProbeExecutor.Unescape(@"\q"));
        Assert.Equal(@"C:\path", NetworkProbeExecutor.Unescape(@"C:\path"));
    }

    [Fact]
    public void DecodePayload_Reads_Hex_Inputs()
    {
        Assert.Equal("PING"u8.ToArray(), NetworkProbeExecutor.DecodePayload("hex:50494e47"));
    }

    [Fact]
    public void DecodePayload_Refuses_Malformed_Hex()
    {
        Assert.Throws<InvalidOperationException>(() => NetworkProbeExecutor.DecodePayload("hex:zzz"));
    }

    [Fact]
    public void ParseAddress_Splits_Host_And_Port()
    {
        Assert.Equal(("127.0.0.1", 6379), NetworkProbeExecutor.ParseAddress("127.0.0.1:6379"));
        Assert.Equal(("example.com", 11211), NetworkProbeExecutor.ParseAddress("example.com:11211"));
    }

    [Fact]
    public void ParseAddress_Refuses_A_Bare_Host_Rather_Than_Guessing()
    {
        // {{Hostname}} omits the port on 80/443, so a template that relies on
        // it gives us no port at all. Guessing would probe a service the
        // template never named.
        var ex = Assert.Throws<InvalidOperationException>(() => NetworkProbeExecutor.ParseAddress("example.com"));
        Assert.Contains("port", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParseAddress_Refuses_An_Unresolved_Placeholder()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => NetworkProbeExecutor.ParseAddress("{{Hostname}}"));
        Assert.Contains("placeholder", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DecodeResponse_Survives_A_Binary_Banner()
    {
        // UTF-8 decoding would turn invalid sequences into replacement
        // characters and destroy the very bytes a word matcher hunts for.
        var raw = new byte[] { 0x00, 0xFF, 0x41, 0x80 };
        var text = NetworkProbeExecutor.DecodeResponse(raw);

        Assert.Equal(4, text.Length);
        Assert.Equal('A', text[2]);
        Assert.DoesNotContain('\uFFFD', text);
    }

    [Fact]
    public async Task Sends_The_Payload_And_Matches_The_Reply()
    {
        var (listener, address) = StartListener();
        using (listener)
        {
            var served = Task.Run(async () =>
            {
                var ct = TestContext.Current.CancellationToken;
                using var client = await listener.AcceptTcpClientAsync(ct);
                await using var stream = client.GetStream();
                var buffer = new byte[64];
                var read = await stream.ReadAsync(buffer, ct);
                var request = Encoding.ASCII.GetString(buffer, 0, read);
                var reply = request.StartsWith("PING\r\n", StringComparison.Ordinal) ? "+PONG\r\n" : "-ERR\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(reply), ct);
                await stream.FlushAsync(ct);
            }, TestContext.Current.CancellationToken);

            var response = await NetworkProbeExecutor.ExecuteAsync(
                Step(address, @"PING\r\n"), timeoutSeconds: 5, ct: TestContext.Current.CancellationToken);
            await served;

            Assert.Contains("PONG", response.Body, StringComparison.Ordinal);

            // The predicate an unauthenticated-Redis template translates to.
            Assert.True(AttackPredicateEvaluator.Evaluate(
                new AttackPredicate { BodyContains = "+PONG" }, response));
        }
    }

    // Deliberately NOT tested here: "a refused connection surfaces as an
    // error". Writing it means binding a port, reading it, releasing it and
    // then connecting to the gap — and if another process takes the port in
    // between, the connect succeeds and the test fails for reasons having
    // nothing to do with the code. That is precisely the race behind #556, and
    // adding a second instance of it while that issue is open would be
    // indefensible. The behaviour it would assert is .NET's own (ConnectAsync
    // throws SocketException) and the scan loop wraps every transport probe in
    // a catch that turns it into ScanFinding.Error regardless.
}
