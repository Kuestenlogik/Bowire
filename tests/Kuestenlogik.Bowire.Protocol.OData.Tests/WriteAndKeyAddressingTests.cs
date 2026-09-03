// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Text;
using Kuestenlogik.Bowire.Protocol.OData;

namespace Kuestenlogik.Bowire.Protocol.OData.Tests;

/// <summary>
/// Regressions for the three write-path defects found by driving the
/// workbench against a real OData server:
///   1. key-by-real-name — the form submits the entity's own key property
///      ("Id"), not a literal "key", so GET_BY_KEY / DELETE silently
///      addressed the collection instead of the entity;
///   2. navigation properties leaked into the write template as strings,
///      producing a body every OData reader rejects;
///   3. the addressing key stayed in the PATCH body, where OData refuses
///      it (and the $-query helpers went along for the ride).
/// </summary>
public sealed class WriteAndKeyAddressingTests
{
    // Model with a navigation property — the shape that exposed defect 2.
    private const string MetadataXml = """
        <?xml version="1.0" encoding="utf-8"?>
        <edmx:Edmx xmlns:edmx="http://docs.oasis-open.org/odata/ns/edmx" Version="4.0">
          <edmx:DataServices>
            <Schema xmlns="http://docs.oasis-open.org/odata/ns/edm" Namespace="Demo">
              <EntityType Name="Category">
                <Key><PropertyRef Name="Id"/></Key>
                <Property Name="Id" Type="Edm.Int32" Nullable="false"/>
                <Property Name="Name" Type="Edm.String"/>
                <NavigationProperty Name="Products" Type="Collection(Demo.Product)"/>
              </EntityType>
              <EntityType Name="Product">
                <Key><PropertyRef Name="Id"/></Key>
                <Property Name="Id" Type="Edm.Int32" Nullable="false"/>
                <Property Name="Name" Type="Edm.String"/>
                <Property Name="Price" Type="Edm.Double"/>
                <NavigationProperty Name="Category" Type="Demo.Category"/>
              </EntityType>
              <EntityContainer Name="DemoContainer">
                <EntitySet Name="Categories" EntityType="Demo.Category"/>
                <EntitySet Name="Products" EntityType="Demo.Product"/>
              </EntityContainer>
            </Schema>
          </edmx:DataServices>
        </edmx:Edmx>
        """;

    private const string StringKeyMetadataXml = """
        <?xml version="1.0" encoding="utf-8"?>
        <edmx:Edmx xmlns:edmx="http://docs.oasis-open.org/odata/ns/edmx" Version="4.0">
          <edmx:DataServices>
            <Schema xmlns="http://docs.oasis-open.org/odata/ns/edm" Namespace="Demo">
              <EntityType Name="Person">
                <Key><PropertyRef Name="UserName"/></Key>
                <Property Name="UserName" Type="Edm.String" Nullable="false"/>
                <Property Name="City" Type="Edm.String"/>
              </EntityType>
              <EntityContainer Name="DemoContainer">
                <EntitySet Name="People" EntityType="Demo.Person"/>
              </EntityContainer>
            </Schema>
          </edmx:DataServices>
        </edmx:Edmx>
        """;

    [Fact]
    public async Task Write_Template_Excludes_Navigation_Properties()
    {
        await using var stub = await StubServer.StartAsync(MetadataXml, "application/xml");
        using var protocol = new BowireODataProtocol();

        var services = await protocol.DiscoverAsync(
            stub.BaseUrl + "/$metadata", showInternalServices: false, TestContext.Current.CancellationToken);

        var products = services.Single(s => s.Name == "Products");
        var post = products.Methods.Single(m => m.Name == "POST");
        var fieldNames = post.InputType.Fields.Select(f => f.Name).ToArray();

        Assert.Contains("Name", fieldNames);
        Assert.Contains("Price", fieldNames);
        // A navigation property is never a scalar in a write payload.
        Assert.DoesNotContain("Category", fieldNames);
    }

    [Fact]
    public async Task GetByKey_Addresses_The_Entity_Using_The_Real_Key_Property()
    {
        await using var stub = await StubServer.StartAsync("{\"value\":[]}", "application/json");
        using var protocol = new BowireODataProtocol();

        // What the workbench form submits: the declared key name, not "key".
        await protocol.InvokeAsync(
            stub.BaseUrl, "Products", "odata/Products/GET_BY_KEY",
            ["""{"Id": 2}"""], showInternalServices: false,
            metadata: null, TestContext.Current.CancellationToken);

        Assert.Equal("GET", stub.LastRequestMethod);
        Assert.Contains("/Products(2)", stub.LastRequestUrl, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Delete_Addresses_The_Entity_Using_The_Real_Key_Property()
    {
        await using var stub = await StubServer.StartAsync("", "application/json", statusCode: 204);
        using var protocol = new BowireODataProtocol();

        await protocol.InvokeAsync(
            stub.BaseUrl, "Products", "odata/Products/DELETE",
            ["""{"Id": 7}"""], showInternalServices: false,
            metadata: null, TestContext.Current.CancellationToken);

        Assert.Equal("DELETE", stub.LastRequestMethod);
        Assert.Contains("/Products(7)", stub.LastRequestUrl, StringComparison.Ordinal);
    }

    [Fact]
    public async Task String_Keys_Are_Quoted_In_The_Url()
    {
        await using var stub = await StubServer.StartAsync("{}", "application/json");
        using var protocol = new BowireODataProtocol();

        await protocol.InvokeAsync(
            stub.BaseUrl, "People", "odata/People/GET_BY_KEY",
            ["""{"UserName": "alice"}"""], showInternalServices: false,
            metadata: null, TestContext.Current.CancellationToken);

        Assert.Contains("/People('alice')", stub.LastRequestUrl, StringComparison.Ordinal);
        _ = StringKeyMetadataXml; // documents the model this mirrors
    }

    [Fact]
    public async Task Patch_Keeps_The_Key_Out_Of_The_Body()
    {
        // PATCH carries the whole entity, so the key can't be guessed from
        // the payload — the plugin uses the key name it recorded during
        // discovery. Discover first, exactly like the workbench does.
        await using var metaStub = await StubServer.StartAsync(MetadataXml, "application/xml");
        using var protocol = new BowireODataProtocol();
        await protocol.DiscoverAsync(
            metaStub.BaseUrl + "/$metadata", showInternalServices: false, TestContext.Current.CancellationToken);

        await using var stub = await StubServer.StartAsync("{}", "application/json", statusCode: 204);

        await protocol.InvokeAsync(
            stub.BaseUrl, "Products", "odata/Products/PATCH",
            ["""{"Id": 3, "Name": "Renamed", "$select": "Name"}"""],
            showInternalServices: false, metadata: null, TestContext.Current.CancellationToken);

        Assert.Equal("PATCH", stub.LastRequestMethod);
        Assert.Contains("/Products(3)", stub.LastRequestUrl, StringComparison.Ordinal);
        // Key lives in the URL; restating it (or a $-option) in the body
        // makes OData reject the whole document.
        Assert.DoesNotContain("\"Id\"", stub.LastRequestBody, StringComparison.Ordinal);
        Assert.DoesNotContain("$select", stub.LastRequestBody, StringComparison.Ordinal);
        Assert.Contains("\"Name\"", stub.LastRequestBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Post_Body_Survives_Untouched_Apart_From_Query_Helpers()
    {
        await using var stub = await StubServer.StartAsync("{}", "application/json", statusCode: 201);
        using var protocol = new BowireODataProtocol();

        await protocol.InvokeAsync(
            stub.BaseUrl, "Products", "odata/Products/POST",
            ["""{"Id": 0, "Name": "New", "Price": 9.5}"""],
            showInternalServices: false, metadata: null, TestContext.Current.CancellationToken);

        Assert.Equal("POST", stub.LastRequestMethod);
        // POST addresses the collection, so the key stays in the body —
        // servers that assign keys ignore it, servers that accept them need it.
        Assert.Contains("\"Id\"", stub.LastRequestBody, StringComparison.Ordinal);
        Assert.Contains("\"Name\"", stub.LastRequestBody, StringComparison.Ordinal);
    }

    // ---- minimal HttpListener stub (mirrors BowireODataProtocolTests) ----
    private sealed class StubServer : IAsyncDisposable
    {
        private readonly HttpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;

        public string BaseUrl { get; }
        public string LastRequestMethod { get; private set; } = "";
        public string LastRequestUrl { get; private set; } = "";
        public string LastRequestBody { get; private set; } = "";

        private StubServer(HttpListener listener, string baseUrl, string body, string contentType, int statusCode)
        {
            _listener = listener;
            BaseUrl = baseUrl;
            _loop = Task.Run(() => RunAsync(body, contentType, statusCode, _cts.Token));
        }

        public static Task<StubServer> StartAsync(string body, string contentType, int statusCode = 200)
        {
            for (var attempt = 0; attempt < 8; attempt++)
            {
                var port = GetFreePort();
                var prefix = $"http://127.0.0.1:{port}/";
                var listener = new HttpListener();
                listener.Prefixes.Add(prefix);
                try
                {
                    listener.Start();
                    return Task.FromResult(new StubServer(listener, prefix.TrimEnd('/'), body, contentType, statusCode));
                }
                catch (HttpListenerException)
                {
                    // Port grabbed between probe and Start — retry.
                }
            }
            throw new InvalidOperationException("Could not bind a free loopback port for the OData stub.");
        }

        private static int GetFreePort()
        {
            using var sock = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            sock.Start();
            var port = ((IPEndPoint)sock.LocalEndpoint).Port;
            sock.Stop();
            return port;
        }

        private async Task RunAsync(string body, string contentType, int statusCode, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                HttpListenerContext context;
                try { context = await _listener.GetContextAsync().WaitAsync(ct); }
                catch (OperationCanceledException) { return; }
                catch (ObjectDisposedException) { return; }
                catch (HttpListenerException) { return; }
                // The listener was stopped from under us. DisposeAsync
                // cancels and then calls Stop(), and the loop can be between
                // its `while (!ct.IsCancellationRequested)` check and this
                // call when that lands — GetContextAsync then reaches
                // BeginGetContext on a stopped listener and throws
                // "Please call the Start() method before calling this
                // method." Unhandled, it faulted the loop task, which
                // DisposeAsync rethrows at the `await using` and fails a test
                // that had already passed its assertions.
                catch (InvalidOperationException) { return; }

                LastRequestMethod = context.Request.HttpMethod;
                LastRequestUrl = context.Request.RawUrl ?? context.Request.Url?.ToString() ?? "";
                if (context.Request.HasEntityBody)
                {
                    using var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding ?? Encoding.UTF8);
                    LastRequestBody = await reader.ReadToEndAsync(ct);
                }
                else
                {
                    LastRequestBody = "";
                }

                context.Response.StatusCode = statusCode;
                context.Response.ContentType = contentType;
                var bytes = Encoding.UTF8.GetBytes(body);
                context.Response.ContentLength64 = bytes.Length;
                try
                {
                    await context.Response.OutputStream.WriteAsync(bytes, ct);
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
