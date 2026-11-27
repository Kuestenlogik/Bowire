// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Text;
using Kuestenlogik.Bowire.Protocol.Rest;
using Kuestenlogik.Bowire.Testing;

namespace Kuestenlogik.Bowire.Protocol.Rest.Tests;

/// <summary>
/// #1055 — a request imported from Postman, Insomnia, Bruno, Yaak or a .http
/// file is saved with its URL as the service, the verb as the method and the
/// URL again as the server URL. The collection runner and <c>bowire test</c>
/// send exactly that; it has to reach the server as the request it was, not
/// be looked up as an OpenAPI operation that does not exist.
/// </summary>
public sealed class ImportedItemInvokeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_Imported_Item_Goes_To_Its_Url_With_Its_Verb_Headers_And_Body()
    {
        await using var server = await EchoServer.StartAsync();
        using var rest = new BowireRestProtocol();
        var url = server.BaseUrl + "/api/echo/json";

        // As the runner sends it: the service still carries the variable, the
        // server URL is the one with it substituted.
        var result = await rest.InvokeAsync(url, "{{host}}/api/echo/json", "POST", ["""{"hello":"bruno"}"""],
            false, new Dictionary<string, string> { ["Content-Type"] = "application/json", ["X-Imported"] = "yes" }, Ct);

        Assert.Equal("200", result.Status);
        Assert.Equal("POST /api/echo/json yes {\"hello\":\"bruno\"}", server.Last);
        Assert.Equal("""{"echo":true}""", result.Response);
    }

    [Fact]
    public async Task An_Item_With_A_Literal_Url_As_Service_Runs_Too()
    {
        await using var server = await EchoServer.StartAsync();
        using var rest = new BowireRestProtocol();
        var url = server.BaseUrl + "/items?limit=2";

        var result = await rest.InvokeAsync(url, url, "GET", ["{}"], false, null, Ct);

        Assert.Equal("200", result.Status);
        Assert.StartsWith("GET /items?limit=2", server.Last, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_A_Server_Url_The_Service_Is_The_Target()
    {
        await using var server = await EchoServer.StartAsync();
        using var rest = new BowireRestProtocol();

        var result = await rest.InvokeAsync("", server.BaseUrl + "/ping", "GET", [], false, null, Ct);

        Assert.Equal("200", result.Status);
        Assert.StartsWith("GET /ping", server.Last, StringComparison.Ordinal);
    }

    /// <summary>Answers every request with <c>{"echo":true}</c> and remembers the last one.</summary>
    private sealed class EchoServer : IAsyncDisposable
    {
        private readonly HttpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;

        public string BaseUrl { get; }
        public string Last { get; private set; } = "";

        private EchoServer(HttpListener listener, string baseUrl)
        {
            _listener = listener;
            BaseUrl = baseUrl;
            _loop = Task.Run(RunAsync);
        }

        public static Task<EchoServer> StartAsync()
        {
            HttpListener? bound = null;
            var port = LoopbackHost.OnAFreePort(p =>
            {
                bound = new HttpListener();
                bound.Prefixes.Add($"http://127.0.0.1:{p}/");
                bound.Start();
            });
            return Task.FromResult(new EchoServer(bound!, $"http://127.0.0.1:{port}"));
        }

        private async Task RunAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                HttpListenerContext context;
                try { context = await _listener.GetContextAsync().WaitAsync(_cts.Token); }
                catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException
                    or HttpListenerException or InvalidOperationException) { return; }

                var body = "";
                if (context.Request.HasEntityBody)
                {
                    using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
                    body = " " + await reader.ReadToEndAsync(_cts.Token);
                }
                var header = context.Request.Headers["X-Imported"];
                Last = context.Request.HttpMethod + " " + context.Request.RawUrl + (header is null ? "" : " " + header) + body;

                var payload = Encoding.UTF8.GetBytes("""{"echo":true}""");
                context.Response.ContentType = "application/json";
                context.Response.ContentLength64 = payload.Length;
                try
                {
                    await context.Response.OutputStream.WriteAsync(payload, _cts.Token);
                    context.Response.Close();
                }
                catch (HttpListenerException) { /* client gone */ }
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            try { _listener.Stop(); } catch (ObjectDisposedException) { }
            try { await _loop; } catch (OperationCanceledException) { }
            _listener.Close();
            _cts.Dispose();
        }
    }
}
