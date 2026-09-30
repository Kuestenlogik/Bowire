// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using Kuestenlogik.Bowire.Auth;
using Kuestenlogik.Bowire.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Kuestenlogik.Bowire.Tests.Net;

/// <summary>
/// The acceptance checks of #680 on real sockets: a call is observed at the
/// configured proxy, a bypassed host never reaches it, and a server whose
/// chain ends in a private CA validates exactly when the bundle is supplied.
/// </summary>
[Collection(BowireNetworkCollectionDefinition.Name)]
public sealed class BowireNetworkWireTests : IAsyncDisposable
{
    private readonly IBowireUserStore _originalStore;
    private readonly string _root;

    public BowireNetworkWireTests()
    {
        _originalStore = BowireUserContext.Current;
        _root = Path.Combine(Path.GetTempPath(), "bowire-network-wire-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        BowireUserContext.Current = new TempUserStore(_root);
        BowireNetworkPolicy.UseConfiguration(null);
    }

    public ValueTask DisposeAsync()
    {
        BowireUserContext.Current = _originalStore;
        BowireNetworkPolicy.Invalidate();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { /* best effort */ }
        return ValueTask.CompletedTask;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_Rest_Call_Is_Observed_At_The_Manual_Proxy()
    {
        await using var proxy = FakeProxy.Start();
        BowireNetworkSettingsStore.Save(new BowireNetworkSettings { ProxyUrl = proxy.Url }, null);

        using var client = BowireHttpClientFactory.Create(null, "rest", TimeSpan.FromSeconds(10));
        using var response = await client.GetAsync(new Uri("http://api.example.test/orders?id=7"), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("proxied", await response.Content.ReadAsStringAsync(Ct));
        Assert.Contains("GET http://api.example.test/orders?id=7 HTTP/1.1", proxy.RequestLines);
    }

    [Fact]
    public async Task A_Host_On_The_Bypass_List_Never_Reaches_The_Proxy()
    {
        await using var proxy = FakeProxy.Start();
        BowireNetworkSettingsStore.Save(new BowireNetworkSettings { ProxyUrl = proxy.Url, NoProxy = ".internal.test" }, null);

        // Bypassed: goes direct, where the made-up name does not resolve — and
        // the proxy never sees a request line for it.
        using var client = BowireHttpClientFactory.Create(null, "rest", TimeSpan.FromSeconds(10));
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(new Uri("http://billing.internal.test/"), Ct));
        Assert.Empty(proxy.RequestLines);

        // Not bypassed: the same client goes through the proxy.
        using var response = await client.GetAsync(new Uri("http://api.example.test/"), Ct);
        Assert.Equal("proxied", await response.Content.ReadAsStringAsync(Ct));
        Assert.Single(proxy.RequestLines);
    }

    [Fact]
    public async Task A_Server_Behind_A_Private_Ca_Validates_Only_With_The_Bundle()
    {
        using var ca = BowireNetworkPolicyTests.CreateCa();
        using var leaf = BowireNetworkPolicyTests.CreateLeaf(ca, "localhost");
        await using var target = await StartTarget(leaf);
        var url = new UriBuilder(target.Urls.First()) { Host = "localhost" }.Uri;

        using (var strict = BowireHttpClientFactory.Create(null, "rest", TimeSpan.FromSeconds(10)))
        {
            var ex = await Assert.ThrowsAsync<HttpRequestException>(() => strict.GetAsync(url, Ct));
            Assert.IsType<AuthenticationException>(ex.InnerException);
        }

        var bundle = Path.Combine(_root, "corp-ca.pem");
        await File.WriteAllTextAsync(bundle, ca.ExportCertificatePem(), Ct);
        BowireNetworkSettingsStore.Save(new BowireNetworkSettings { CaBundle = bundle }, null);

        using var trusting = BowireHttpClientFactory.Create(null, "rest", TimeSpan.FromSeconds(10));
        using var response = await trusting.GetAsync(url, Ct);
        Assert.Equal("direct", await response.Content.ReadAsStringAsync(Ct));
    }

    private static async Task<WebApplication> StartTarget(System.Security.Cryptography.X509Certificates.X509Certificate2? certificate)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0, l =>
        {
            if (certificate is not null) l.UseHttps(certificate);
        }));
        var app = builder.Build();
        app.MapGet("/", () => "direct");
        await app.StartAsync(Ct);
        return app;
    }

    /// <summary>A forward proxy that records each request line and answers for the target.</summary>
    private sealed class FakeProxy : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;

        public List<string> RequestLines { get; } = [];
        public string Url => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";

        private FakeProxy()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            _loop = Task.Run(AcceptAsync);
        }

        public static FakeProxy Start() => new();

        private async Task AcceptAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
                catch (OperationCanceledException) { return; }
                catch (ObjectDisposedException) { return; }
                using (client)
                {
                    var stream = client.GetStream();
                    using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
                    var line = await reader.ReadLineAsync(_stop.Token);
                    if (line is null) continue;
                    lock (RequestLines) RequestLines.Add(line);
                    while (!string.IsNullOrEmpty(await reader.ReadLineAsync(_stop.Token))) { }
                    var body = "proxied";
                    var reply = $"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(reply), _stop.Token);
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _listener.Stop();
            _listener.Dispose();
            try { await _loop; } catch (OperationCanceledException) { }
            _stop.Dispose();
        }
    }

    private sealed class TempUserStore(string root) : IBowireUserStore
    {
        public string GetUserPath(string filename) => Path.Combine(root, filename);
    }
}
