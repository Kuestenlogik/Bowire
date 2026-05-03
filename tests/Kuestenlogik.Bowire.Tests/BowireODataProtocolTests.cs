// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Text;
using Kuestenlogik.Bowire.Protocol.OData;

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// Tests for the OData protocol plugin's identity, $metadata-driven
/// discovery, and HTTP-verb routing in InvokeAsync. Uses a tiny in-process
/// <see cref="HttpListener"/> stub to serve canned <c>$metadata</c> XML and
/// echo invocation requests — keeps the tests pure-unit while still
/// exercising the EDM CSDL parser, the CRUD-method synthesis, and the
/// JSON-to-HTTP translation.
/// </summary>
public sealed class BowireODataProtocolTests
{
    // Trivial OData v4 EDM CSDL with one entity set "Products" carrying
    // an int32 key + string fields. Deliberately small so the test stays
    // readable; the parser path is the same as a 100-entity model.
    private const string MetadataXml = """
        <?xml version="1.0" encoding="utf-8"?>
        <edmx:Edmx xmlns:edmx="http://docs.oasis-open.org/odata/ns/edmx" Version="4.0">
          <edmx:DataServices>
            <Schema xmlns="http://docs.oasis-open.org/odata/ns/edm" Namespace="Demo">
              <EntityType Name="Product">
                <Key><PropertyRef Name="Id"/></Key>
                <Property Name="Id" Type="Edm.Int32" Nullable="false"/>
                <Property Name="Name" Type="Edm.String"/>
                <Property Name="Price" Type="Edm.Double"/>
                <Property Name="InStock" Type="Edm.Boolean"/>
                <Property Name="Created" Type="Edm.DateTimeOffset"/>
                <Property Name="Sku" Type="Edm.Guid"/>
                <Property Name="Quantity" Type="Edm.Int64"/>
              </EntityType>
              <EntityContainer Name="DemoContainer">
                <EntitySet Name="Products" EntityType="Demo.Product"/>
              </EntityContainer>
            </Schema>
          </edmx:DataServices>
        </edmx:Edmx>
        """;

    [Fact]
    public void Identity_Properties_Are_Stable()
    {
        var protocol = new BowireODataProtocol();

        Assert.Equal("OData", protocol.Name);
        Assert.Equal("odata", protocol.Id);
        Assert.NotNull(protocol.IconSvg);
        Assert.Contains("<svg", protocol.IconSvg, StringComparison.Ordinal);
    }

    [Fact]
    public void Implements_IBowireProtocol()
    {
        var protocol = new BowireODataProtocol();

        Assert.IsAssignableFrom<IBowireProtocol>(protocol);
    }

    [Fact]
    public void Initialize_Accepts_Null_Service_Provider()
    {
        var protocol = new BowireODataProtocol();

        protocol.Initialize(null);
    }

    [Fact]
    public async Task DiscoverAsync_Empty_Url_Returns_Empty()
    {
        var protocol = new BowireODataProtocol();

        var services = await protocol.DiscoverAsync(
            "", showInternalServices: false, TestContext.Current.CancellationToken);

        Assert.Empty(services);
    }

    [Fact]
    public async Task DiscoverAsync_Whitespace_Url_Returns_Empty()
    {
        var protocol = new BowireODataProtocol();

        var services = await protocol.DiscoverAsync(
            "   ", showInternalServices: false, TestContext.Current.CancellationToken);

        Assert.Empty(services);
    }

    [Fact]
    public async Task DiscoverAsync_Non_Http_Url_Returns_Empty()
    {
        var protocol = new BowireODataProtocol();

        var services = await protocol.DiscoverAsync(
            "ftp://example.com/foo", showInternalServices: false, TestContext.Current.CancellationToken);

        Assert.Empty(services);
    }

    [Fact]
    public async Task DiscoverAsync_Against_Stub_Returns_EntitySets_As_Services()
    {
        await using var stub = await StubServer.StartAsync(MetadataXml, "application/xml");
        var protocol = new BowireODataProtocol();

        var services = await protocol.DiscoverAsync(
            stub.BaseUrl, showInternalServices: false, TestContext.Current.CancellationToken);

        var svc = Assert.Single(services);
        Assert.Equal("Products", svc.Name);
        Assert.Equal("odata", svc.Source);
        Assert.Equal("odata", svc.Package);
        Assert.Equal(stub.BaseUrl.TrimEnd('/'), svc.OriginUrl);
        Assert.Contains("OData entity set", svc.Description!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DiscoverAsync_Synthesises_All_Crud_Methods()
    {
        await using var stub = await StubServer.StartAsync(MetadataXml, "application/xml");
        var protocol = new BowireODataProtocol();

        var services = await protocol.DiscoverAsync(
            stub.BaseUrl, showInternalServices: false, TestContext.Current.CancellationToken);

        var methods = services[0].Methods;
        Assert.Equal(5, methods.Count);
        Assert.Equal(
            ["GET", "GET_BY_KEY", "POST", "PATCH", "DELETE"],
            methods.Select(m => m.Name).ToArray());

        var get = methods.First(m => m.Name == "GET");
        Assert.Equal("GET", get.HttpMethod);
        Assert.Equal("/Products", get.HttpPath);
        Assert.Equal("Unary", get.MethodType);
        Assert.False(get.ServerStreaming);

        var post = methods.First(m => m.Name == "POST");
        Assert.Equal("POST", post.HttpMethod);
        Assert.Equal("/Products", post.HttpPath);

        var patch = methods.First(m => m.Name == "PATCH");
        Assert.Equal("PATCH", patch.HttpMethod);
        Assert.Equal("/Products({key})", patch.HttpPath);
    }

    [Fact]
    public async Task DiscoverAsync_Builds_Field_Schema_From_EntityType()
    {
        await using var stub = await StubServer.StartAsync(MetadataXml, "application/xml");
        var protocol = new BowireODataProtocol();

        var services = await protocol.DiscoverAsync(
            stub.BaseUrl, showInternalServices: false, TestContext.Current.CancellationToken);

        var post = services[0].Methods.First(m => m.Name == "POST");

        // POST input is the entity itself, all 7 properties.
        Assert.Equal(7, post.InputType.Fields.Count);
        Assert.Equal("Id", post.InputType.Fields[0].Name);

        // EDM type mapping: int32→int64, string→string, double→double,
        // bool→bool, datetime/guid→string, int64→int64.
        Assert.Equal("int64", post.InputType.Fields[0].Type);   // Edm.Int32
        Assert.Equal("string", post.InputType.Fields[1].Type);  // Edm.String
        Assert.Equal("double", post.InputType.Fields[2].Type);  // Edm.Double
        Assert.Equal("bool", post.InputType.Fields[3].Type);    // Edm.Boolean
        Assert.Equal("string", post.InputType.Fields[4].Type);  // Edm.DateTimeOffset
        Assert.Equal("string", post.InputType.Fields[5].Type);  // Edm.Guid
        Assert.Equal("int64", post.InputType.Fields[6].Type);   // Edm.Int64

        // Each field gets its EDM type echoed in the description.
        Assert.Contains("Type:", post.InputType.Fields[0].Description!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DiscoverAsync_GetByKey_Has_Required_Key_Field_Only()
    {
        await using var stub = await StubServer.StartAsync(MetadataXml, "application/xml");
        var protocol = new BowireODataProtocol();

        var services = await protocol.DiscoverAsync(
            stub.BaseUrl, showInternalServices: false, TestContext.Current.CancellationToken);

        var getByKey = services[0].Methods.First(m => m.Name == "GET_BY_KEY");

        // KeyInput message has only the entity's declared keys.
        var keyField = Assert.Single(getByKey.InputType.Fields);
        Assert.Equal("Id", keyField.Name);
        Assert.True(keyField.Required);
    }

    [Fact]
    public async Task DiscoverAsync_Url_Already_Ends_With_Metadata_Strips_From_Origin()
    {
        await using var stub = await StubServer.StartAsync(MetadataXml, "application/xml");
        var protocol = new BowireODataProtocol();

        var services = await protocol.DiscoverAsync(
            stub.BaseUrl + "/$metadata",
            showInternalServices: false,
            TestContext.Current.CancellationToken);

        var svc = Assert.Single(services);
        // Origin URL should not contain $metadata anymore.
        Assert.DoesNotContain("$metadata", svc.OriginUrl!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DiscoverAsync_NonSuccess_Status_Returns_Empty()
    {
        await using var stub = await StubServer.StartAsync(
            "<error/>", "application/xml", statusCode: 500);
        var protocol = new BowireODataProtocol();

        var services = await protocol.DiscoverAsync(
            stub.BaseUrl, showInternalServices: false, TestContext.Current.CancellationToken);

        Assert.Empty(services);
    }

    [Fact]
    public async Task DiscoverAsync_Invalid_Xml_Returns_Empty()
    {
        await using var stub = await StubServer.StartAsync(
            "not-valid-xml-at-all", "application/xml");
        var protocol = new BowireODataProtocol();

        var services = await protocol.DiscoverAsync(
            stub.BaseUrl, showInternalServices: false, TestContext.Current.CancellationToken);

        Assert.Empty(services);
    }

    [Fact]
    public async Task DiscoverAsync_No_Entity_Container_Returns_Empty()
    {
        const string emptyMetadata = """
            <?xml version="1.0" encoding="utf-8"?>
            <edmx:Edmx xmlns:edmx="http://docs.oasis-open.org/odata/ns/edmx" Version="4.0">
              <edmx:DataServices>
                <Schema xmlns="http://docs.oasis-open.org/odata/ns/edm" Namespace="Empty"/>
              </edmx:DataServices>
            </edmx:Edmx>
            """;
        await using var stub = await StubServer.StartAsync(emptyMetadata, "application/xml");
        var protocol = new BowireODataProtocol();

        var services = await protocol.DiscoverAsync(
            stub.BaseUrl, showInternalServices: false, TestContext.Current.CancellationToken);

        Assert.Empty(services);
    }

    [Fact]
    public async Task InvokeAsync_Get_Hits_Entity_Set_Path()
    {
        await using var stub = await StubServer.StartAsync(
            "{\"value\":[]}", "application/json");
        var protocol = new BowireODataProtocol();

        var result = await protocol.InvokeAsync(
            stub.BaseUrl, service: "Products",
            method: "odata/Products/GET",
            jsonMessages: ["{}"],
            showInternalServices: false,
            metadata: null,
            ct: TestContext.Current.CancellationToken);

        Assert.Equal("OK", result.Status);
        Assert.Equal("{\"value\":[]}", result.Response);
        Assert.Contains("/Products", stub.LastRequestUrl, StringComparison.Ordinal);
        Assert.Equal("GET", stub.LastRequestMethod);
        Assert.Equal("200", result.Metadata["httpStatus"]);
    }

    [Fact]
    public async Task InvokeAsync_With_Metadata_Suffix_In_BaseUrl_Strips_It()
    {
        await using var stub = await StubServer.StartAsync(
            "{}", "application/json");
        var protocol = new BowireODataProtocol();

        var result = await protocol.InvokeAsync(
            stub.BaseUrl + "/$metadata", service: "Products",
            method: "odata/Products/GET",
            jsonMessages: ["{}"],
            showInternalServices: false,
            metadata: null,
            ct: TestContext.Current.CancellationToken);

        Assert.Equal("OK", result.Status);
        Assert.DoesNotContain("$metadata", stub.LastRequestUrl, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InvokeAsync_GetByKey_Builds_Key_In_Path_Parens()
    {
        await using var stub = await StubServer.StartAsync(
            "{\"Id\":42}", "application/json");
        var protocol = new BowireODataProtocol();

        var result = await protocol.InvokeAsync(
            stub.BaseUrl, service: "Products",
            method: "odata/Products/GET_BY_KEY",
            jsonMessages: ["{\"key\":\"42\"}"],
            showInternalServices: false,
            metadata: null,
            ct: TestContext.Current.CancellationToken);

        Assert.Equal("OK", result.Status);
        Assert.Contains("/Products(42)", stub.LastRequestUrl, StringComparison.Ordinal);
        Assert.Equal("GET", stub.LastRequestMethod);
    }

    [Fact]
    public async Task InvokeAsync_Delete_Routes_To_Delete_Verb_With_Key()
    {
        await using var stub = await StubServer.StartAsync(
            "", "text/plain", statusCode: 204);
        var protocol = new BowireODataProtocol();

        var result = await protocol.InvokeAsync(
            stub.BaseUrl, service: "Products",
            method: "odata/Products/DELETE",
            jsonMessages: ["{\"key\":\"7\"}"],
            showInternalServices: false,
            metadata: null,
            ct: TestContext.Current.CancellationToken);

        Assert.Equal("DELETE", stub.LastRequestMethod);
        Assert.Contains("/Products(7)", stub.LastRequestUrl, StringComparison.Ordinal);
        Assert.Equal("204", result.Metadata["httpStatus"]);
    }

    [Fact]
    public async Task InvokeAsync_Post_Sends_Json_Body()
    {
        await using var stub = await StubServer.StartAsync(
            "{\"Id\":1}", "application/json", statusCode: 201);
        var protocol = new BowireODataProtocol();

        var result = await protocol.InvokeAsync(
            stub.BaseUrl, service: "Products",
            method: "odata/Products/POST",
            jsonMessages: ["{\"Name\":\"Widget\"}"],
            showInternalServices: false,
            metadata: null,
            ct: TestContext.Current.CancellationToken);

        Assert.Equal("POST", stub.LastRequestMethod);
        Assert.Equal("{\"Name\":\"Widget\"}", stub.LastRequestBody);
        Assert.Equal("OK", result.Status);
    }

    [Fact]
    public async Task InvokeAsync_Patch_Sends_Json_Body_With_Key()
    {
        await using var stub = await StubServer.StartAsync(
            "{}", "application/json");
        var protocol = new BowireODataProtocol();

        var _ = await protocol.InvokeAsync(
            stub.BaseUrl, service: "Products",
            method: "odata/Products/PATCH",
            jsonMessages: ["{\"key\":\"99\",\"Name\":\"Updated\"}"],
            showInternalServices: false,
            metadata: null,
            ct: TestContext.Current.CancellationToken);

        Assert.Equal("PATCH", stub.LastRequestMethod);
        Assert.Contains("/Products(99)", stub.LastRequestUrl, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvokeAsync_Filter_And_Select_Translate_To_Query_String()
    {
        await using var stub = await StubServer.StartAsync(
            "{\"value\":[]}", "application/json");
        var protocol = new BowireODataProtocol();

        var _ = await protocol.InvokeAsync(
            stub.BaseUrl, service: "Products",
            method: "odata/Products/GET",
            jsonMessages: ["{\"$filter\":\"Price gt 10\",\"$select\":\"Id,Name\"}"],
            showInternalServices: false,
            metadata: null,
            ct: TestContext.Current.CancellationToken);

        Assert.Contains("$filter=", stub.LastRequestUrl, StringComparison.Ordinal);
        Assert.Contains("$select=", stub.LastRequestUrl, StringComparison.Ordinal);
        // $filter is URL-encoded.
        Assert.Contains("Price%20gt%2010", stub.LastRequestUrl, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvokeAsync_Malformed_Json_Body_Falls_Through_To_Plain_Get()
    {
        await using var stub = await StubServer.StartAsync(
            "{}", "application/json");
        var protocol = new BowireODataProtocol();

        var result = await protocol.InvokeAsync(
            stub.BaseUrl, service: "Products",
            method: "odata/Products/GET",
            jsonMessages: ["this-is-not-json"],
            showInternalServices: false,
            metadata: null,
            ct: TestContext.Current.CancellationToken);

        // The catch swallows the JSON parse error and falls through to a
        // plain GET against /Products with no key/filter applied.
        Assert.Equal("OK", result.Status);
        Assert.Equal("GET", stub.LastRequestMethod);
    }

    [Fact]
    public async Task InvokeAsync_Returns_Http_Status_Label_On_Failure()
    {
        await using var stub = await StubServer.StartAsync(
            "boom", "text/plain", statusCode: 500);
        var protocol = new BowireODataProtocol();

        var result = await protocol.InvokeAsync(
            stub.BaseUrl, service: "Products",
            method: "odata/Products/GET",
            jsonMessages: ["{}"],
            showInternalServices: false,
            metadata: null,
            ct: TestContext.Current.CancellationToken);

        Assert.Contains("HTTP 500", result.Status, StringComparison.Ordinal);
        Assert.Equal("500", result.Metadata["httpStatus"]);
    }

    [Fact]
    public async Task InvokeStreamAsync_Returns_Empty_Sequence()
    {
        var protocol = new BowireODataProtocol();

        var produced = new List<string>();
        await foreach (var item in protocol.InvokeStreamAsync(
            "http://example.com",
            service: "Products",
            method: "odata/Products/GET",
            jsonMessages: ["{}"],
            showInternalServices: false,
            metadata: null,
            ct: TestContext.Current.CancellationToken))
        {
            produced.Add(item);
        }

        Assert.Empty(produced);
    }

    [Fact]
    public async Task OpenChannelAsync_Returns_Null_Because_OData_Has_No_Duplex()
    {
        var protocol = new BowireODataProtocol();

        var channel = await protocol.OpenChannelAsync(
            "http://example.com",
            service: "Products",
            method: "odata/Products/GET",
            showInternalServices: false,
            metadata: null,
            ct: TestContext.Current.CancellationToken);

        Assert.Null(channel);
    }

    // ---- minimal HttpListener stub ----
    //
    // Serves one canned body per request, captures the last request's verb /
    // path / body so the test can assert on what the protocol sent. Pure
    // localhost loopback — no integration harness, no external dependencies.
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

                LastRequestMethod = context.Request.HttpMethod;
                // Use RawUrl so percent-encoded query strings stay encoded;
                // .Url.ToString() helpfully (and unhelpfully) decodes them.
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
                await context.Response.OutputStream.WriteAsync(bytes, ct);
                context.Response.Close();
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            try { _listener.Stop(); } catch { /* best-effort */ }
            try { _listener.Close(); } catch { /* best-effort */ }
            try { await _loop; } catch { /* best-effort */ }
            _cts.Dispose();
        }
    }
}
