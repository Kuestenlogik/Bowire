// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Text;
using Kuestenlogik.Bowire.App;
using Kuestenlogik.Bowire.Mocking;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// #191 — Pact-style contract testing. Covers the pure model
/// (recording → contract projection, status mapping, header filtering,
/// path resolution) and the provider-side verifier end-to-end against a
/// self-hosted loopback provider (contract held / status mismatch / body
/// shape drift).
/// </summary>
public sealed class ContractTests
{
    // ---- model: FromRecording ----

    [Fact]
    public void FromRecording_ProjectsRestStepsIntoInteractions()
    {
        var rec = new BowireRecording { Id = "r", Name = "orders", RecordingFormatVersion = 2 };
        rec.Steps.Add(new BowireRecordingStep
        {
            Id = "s1", Protocol = "rest", HttpVerb = "GET", HttpPath = "/orders/42",
            Status = "OK", Response = "{\"id\":42,\"status\":\"shipped\"}",
        });
        rec.Steps.Add(new BowireRecordingStep
        {
            Id = "s2", Protocol = "rest", HttpVerb = "POST", HttpPath = "/orders",
            Body = "{\"item\":\"widget\"}", Status = "Created", Response = "{\"id\":43}",
        });

        var contract = PactContract.FromRecording(rec, "orders-consumer", "order-service");

        Assert.Equal("orders-consumer", contract.Consumer.Name);
        Assert.Equal("order-service", contract.Provider.Name);
        Assert.Equal(2, contract.Interactions.Count);
        Assert.Equal("GET", contract.Interactions[0].Request.Method);
        Assert.Equal("/orders/42", contract.Interactions[0].Request.Path);
        Assert.Equal(200, contract.Interactions[0].Response.Status);
        Assert.Equal(201, contract.Interactions[1].Response.Status);
        Assert.NotNull(contract.Interactions[1].Request.Body);
        Assert.Equal("3.0.0", contract.Metadata.PactSpecification.Version);
    }

    [Fact]
    public void FromRecording_SkipsNonHttpSteps()
    {
        var rec = new BowireRecording { Id = "r", Name = "mixed", RecordingFormatVersion = 2 };
        rec.Steps.Add(new BowireRecordingStep { Id = "g", Protocol = "grpc", Service = "S", Method = "M", Status = "OK" });
        rec.Steps.Add(new BowireRecordingStep { Id = "h", Protocol = "rest", HttpVerb = "GET", HttpPath = "/ping", Status = "OK" });

        var contract = PactContract.FromRecording(rec, "c", "p");

        Assert.Single(contract.Interactions);
        Assert.Equal("/ping", contract.Interactions[0].Request.Path);
    }

    [Theory]
    [InlineData("OK", 200)]
    [InlineData("Created", 201)]
    [InlineData("NoContent", 204)]
    [InlineData("NotFound", 404)]
    [InlineData("InternalServerError", 500)]
    [InlineData("503", 503)]
    [InlineData("", 200)]
    [InlineData("gibberish", 200)]
    public void StatusCodeOf_MapsNamesNumbersAndFallsBackTo200(string status, int expected)
        => Assert.Equal(expected, PactContract.StatusCodeOf(status));

    [Fact]
    public void FilterHttpHeaders_DropsPseudoAndGrpcHeaders()
    {
        var meta = new Dictionary<string, string>
        {
            [":path"] = "/x",
            ["grpc-timeout"] = "5S",
            ["Authorization"] = "Bearer t",
            ["X-Trace"] = "abc",
        };
        var headers = PactContract.FilterHttpHeaders(meta);
        Assert.NotNull(headers);
        Assert.Equal(2, headers!.Count);
        Assert.True(headers.ContainsKey("Authorization"));
        Assert.True(headers.ContainsKey("X-Trace"));
    }

    [Fact]
    public void PathOf_FallsBackToServerUrlPath()
    {
        var step = new BowireRecordingStep { ServerUrl = "https://api.example.com/v1/orders?page=2" };
        Assert.Equal("/v1/orders?page=2", PactContract.PathOf(step));

        var rel = new BowireRecordingStep { HttpPath = "orders/1" };
        Assert.Equal("/orders/1", PactContract.PathOf(rel)); // leading slash normalised
    }

    // ---- verifier end-to-end ----

    [Fact]
    public async Task Verify_ProviderHonoursContract_AllHold()
    {
        using var provider = new LoopbackProvider((method, path) =>
            method == "GET" && path == "/orders/42"
                // total changes (value may vary), shape holds → structural match passes
                ? (200, "{\"id\":42,\"total\":99.5,\"status\":\"delivered\"}")
                : (404, "{}"));

        var contract = Contract("GET", "/orders/42", 200, "{\"id\":42,\"total\":19.99,\"status\":\"shipped\"}");

        using var http = new HttpClient();
        using var sw = new StringWriter();
        var report = await ContractVerifier.VerifyAsync(http, contract, provider.Url, sw, TestContext.Current.CancellationToken);

        Assert.Equal(0, report.FailedTests);
        Assert.Equal(2, report.TotalAssertions); // status + body
        Assert.Equal(2, report.PassedAssertions);
    }

    [Fact]
    public async Task Verify_ProviderDropsField_Fails()
    {
        using var provider = new LoopbackProvider((_, _) =>
            (200, "{\"id\":42,\"total\":99.5}")); // 'status' dropped

        var contract = Contract("GET", "/orders/42", 200, "{\"id\":42,\"status\":\"shipped\"}");

        using var http = new HttpClient();
        using var sw = new StringWriter();
        var report = await ContractVerifier.VerifyAsync(http, contract, provider.Url, sw, TestContext.Current.CancellationToken);

        Assert.Equal(1, report.FailedTests);
        var body = report.Tests[0].Assertions.First(a => a.Path == "body");
        Assert.False(body.Passed);
        Assert.Contains("status", body.Error ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public async Task Verify_StatusMismatch_Fails()
    {
        using var provider = new LoopbackProvider((_, _) => (500, "{}"));
        var contract = Contract("GET", "/orders/42", 200, null);

        using var http = new HttpClient();
        using var sw = new StringWriter();
        var report = await ContractVerifier.VerifyAsync(http, contract, provider.Url, sw, TestContext.Current.CancellationToken);

        Assert.Equal(1, report.FailedTests);
        var status = report.Tests[0].Assertions.First(a => a.Path == "status");
        Assert.False(status.Passed);
        Assert.Equal("200", status.Expected);
        Assert.Equal("500", status.ActualText);
    }

    // ---- helpers ----

    private static PactContract Contract(string method, string path, int status, string? responseBody)
    {
        var c = new PactContract
        {
            Consumer = new PactParty { Name = "c" },
            Provider = new PactParty { Name = "p" },
        };
        c.Interactions.Add(new PactInteraction
        {
            Description = $"{method} {path}",
            Request = new PactRequest { Method = method, Path = path },
            Response = new PactResponse
            {
                Status = status,
                Body = responseBody is null ? null : System.Text.Json.Nodes.JsonNode.Parse(responseBody),
            },
        });
        return c;
    }

    private sealed class LoopbackProvider : IDisposable
    {
        private readonly HttpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;

        public string Url { get; }

        public LoopbackProvider(Func<string, string, (int Status, string Body)> responder)
        {
            var port = FreePort();
            Url = $"http://127.0.0.1:{port}";
            _listener = new HttpListener();
            _listener.Prefixes.Add(Url + "/");
            _listener.Start();
            _loop = Task.Run(async () =>
            {
                while (!_cts.IsCancellationRequested)
                {
                    HttpListenerContext ctx;
                    try { ctx = await _listener.GetContextAsync().WaitAsync(_cts.Token); }
                    catch { return; }
                    try
                    {
                        var (status, body) = responder(ctx.Request.HttpMethod, ctx.Request.Url!.AbsolutePath);
                        ctx.Response.StatusCode = status;
                        ctx.Response.ContentType = "application/json";
                        var bytes = Encoding.UTF8.GetBytes(body);
                        ctx.Response.ContentLength64 = bytes.Length;
                        await ctx.Response.OutputStream.WriteAsync(bytes, _cts.Token);
                    }
                    catch { /* best-effort */ }
                    finally { try { ctx.Response.OutputStream.Close(); } catch { } }
                }
            });
        }

        private static int FreePort()
        {
            using var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            l.Start();
            return ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { _listener.Stop(); } catch { }
            try { ((IDisposable)_listener).Dispose(); } catch { }
            try { _loop.Wait(TimeSpan.FromSeconds(2)); } catch { }
            _cts.Dispose();
        }
    }
}
