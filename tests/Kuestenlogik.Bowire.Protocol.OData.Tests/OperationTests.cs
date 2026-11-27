// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Kuestenlogik.Bowire.Protocol.OData;

namespace Kuestenlogik.Bowire.Protocol.OData.Tests;

/// <summary>
/// EDM functions and actions (#529): discovery lists them next to the CRUD
/// methods, and a call to one reaches the URL OData expects — a function as
/// a GET with its parameters in the segment, an action as a POST with them in
/// the body, an entity-bound one behind the entity's key.
/// </summary>
public sealed class OperationTests
{
    private const string MetadataXml = """
        <?xml version="1.0" encoding="utf-8"?>
        <edmx:Edmx xmlns:edmx="http://docs.oasis-open.org/odata/ns/edmx" Version="4.0">
          <edmx:DataServices>
            <Schema xmlns="http://docs.oasis-open.org/odata/ns/edm" Namespace="Demo">
              <EntityType Name="Product">
                <Key><PropertyRef Name="Id"/></Key>
                <Property Name="Id" Type="Edm.Int32" Nullable="false"/>
                <Property Name="Name" Type="Edm.String"/>
              </EntityType>
              <EntityType Name="Person">
                <Key><PropertyRef Name="UserName"/></Key>
                <Property Name="UserName" Type="Edm.String" Nullable="false"/>
              </EntityType>
              <Function Name="DiscountedPrice" IsBound="true">
                <Parameter Name="product" Type="Demo.Product"/>
                <Parameter Name="percent" Type="Edm.Decimal" Nullable="false"/>
                <ReturnType Type="Edm.Decimal"/>
              </Function>
              <Function Name="Friends" IsBound="true">
                <Parameter Name="person" Type="Demo.Person"/>
                <ReturnType Type="Collection(Demo.Person)"/>
              </Function>
              <Action Name="Restock" IsBound="true">
                <Parameter Name="products" Type="Collection(Demo.Product)"/>
                <Parameter Name="amount" Type="Edm.Int32" Nullable="false"/>
              </Action>
              <Function Name="Search">
                <Parameter Name="term" Type="Edm.String"/>
                <Parameter Name="id" Type="Edm.Guid"/>
                <ReturnType Type="Collection(Demo.Product)"/>
              </Function>
              <Action Name="Reset"/>
              <EntityContainer Name="DemoContainer">
                <EntitySet Name="Products" EntityType="Demo.Product"/>
                <EntitySet Name="People" EntityType="Demo.Person"/>
                <FunctionImport Name="Search" Function="Demo.Search" EntitySet="Products"/>
                <ActionImport Name="Reset" Action="Demo.Reset"/>
              </EntityContainer>
            </Schema>
          </edmx:DataServices>
        </edmx:Edmx>
        """;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Discovery_Lists_Bound_Operations_And_Imports()
    {
        await using var server = await RecordingServer.StartAsync(MetadataXml);
        using var protocol = new BowireODataProtocol();

        var services = await protocol.DiscoverAsync(server.BaseUrl, false, Ct);

        var products = services.Single(s => s.Name == "Products");
        var discounted = products.Methods.Single(m => m.Name == "DiscountedPrice");
        Assert.Equal("odata/Products/FUNCTION/Demo.DiscountedPrice", discounted.FullName);
        Assert.Equal("GET", discounted.HttpMethod);
        // The key to address the entity, then the parameter; not the binding parameter.
        Assert.Equal(["Id", "percent"], discounted.InputType.Fields.Select(f => f.Name));

        var restock = products.Methods.Single(m => m.Name == "Restock");
        Assert.Equal("odata/Products/ACTION_SET/Demo.Restock", restock.FullName);
        Assert.Equal("POST", restock.HttpMethod);
        Assert.Equal(["amount"], restock.InputType.Fields.Select(f => f.Name));

        Assert.Contains(services.Single(s => s.Name == "People").Methods, m => m.Name == "Friends");

        var imports = services.Single(s => s.Name == "DemoContainer");
        Assert.Equal(["Search", "Reset"], imports.Methods.Select(m => m.Name));
        Assert.Equal("odata/DemoContainer/FUNCTION_IMPORT/Search", imports.Methods[0].FullName);
    }

    [Fact]
    public async Task A_Bound_Function_Is_A_Get_Behind_The_Key()
    {
        var request = await CallAsync("odata/Products/FUNCTION/Demo.DiscountedPrice", """{"Id": 1, "percent": 15}""");
        Assert.Equal("GET /Products(1)/Demo.DiscountedPrice(percent=15)", request);
    }

    [Fact]
    public async Task A_String_Key_Is_Quoted_And_A_Function_Without_Parameters_Has_Empty_Parentheses()
    {
        var request = await CallAsync("odata/People/FUNCTION/Demo.Friends", """{"UserName": "alice"}""");
        Assert.Equal("GET /People('alice')/Demo.Friends()", request);
    }

    [Fact]
    public async Task A_Collection_Bound_Action_Is_A_Post_With_The_Parameters_As_Body()
    {
        var request = await CallAsync("odata/Products/ACTION_SET/Demo.Restock", """{"amount": 5}""");
        Assert.Equal("""POST /Products/Demo.Restock {"amount":5}""", request);
    }

    [Fact]
    public async Task A_Function_Import_Quotes_Strings_And_Leaves_Guids_Bare()
    {
        var request = await CallAsync("odata/DemoContainer/FUNCTION_IMPORT/Search",
            """{"term": "it's", "id": "6f9619ff-8b86-d011-b42d-00cf4fc964ff"}""");
        Assert.Equal("GET /Search(term='it''s',id=6f9619ff-8b86-d011-b42d-00cf4fc964ff)", WebUtility.UrlDecode(request));
    }

    [Fact]
    public async Task An_Action_Import_Is_A_Post()
    {
        var request = await CallAsync("odata/DemoContainer/ACTION_IMPORT/Reset", "{}");
        Assert.Equal("POST /Reset {}", request);
    }

    [Fact]
    public async Task Without_Discovery_A_Call_Goes_By_The_Json_Value()
    {
        // A flow or a recording calls without listing first: a JSON string
        // is quoted, a number is not, and "key" addresses the entity.
        await using var server = await RecordingServer.StartAsync(MetadataXml);
        using var protocol = new BowireODataProtocol();

        await protocol.InvokeAsync(server.BaseUrl, "Products", "odata/Products/FUNCTION/Demo.Rate",
            ["""{"key": 7, "stars": 4, "note": "good"}"""], false, null, Ct);

        Assert.Equal("GET /Products(7)/Demo.Rate(stars=4,note='good')", WebUtility.UrlDecode(server.Requests.Last()));
    }

    private static async Task<string> CallAsync(string method, string payload)
    {
        await using var server = await RecordingServer.StartAsync(MetadataXml);
        using var protocol = new BowireODataProtocol();
        await protocol.DiscoverAsync(server.BaseUrl, false, Ct);

        var result = await protocol.InvokeAsync(server.BaseUrl, method.Split('/')[1], method, [payload], false, null, Ct);

        Assert.Equal("OK", result.Status);
        return server.Requests.Last();
    }

    /// <summary>Answers <c>$metadata</c> with the model and anything else with an empty result, recording each request.</summary>
    private sealed class RecordingServer : IAsyncDisposable
    {
        private readonly HttpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;

        public string BaseUrl { get; }
        public ConcurrentQueue<string> Requests { get; } = new();

        private RecordingServer(HttpListener listener, string baseUrl, string metadata)
        {
            _listener = listener;
            BaseUrl = baseUrl;
            _loop = Task.Run(() => RunAsync(metadata, _cts.Token));
        }

        public static Task<RecordingServer> StartAsync(string metadata)
        {
            HttpListener? bound = null;
            var port = Kuestenlogik.Bowire.Testing.LoopbackHost.OnAFreePort(p =>
            {
                bound = new HttpListener();
                bound.Prefixes.Add($"http://127.0.0.1:{p}/");
                bound.Start();
            });
            return Task.FromResult(new RecordingServer(bound!, $"http://127.0.0.1:{port}", metadata));
        }

        private async Task RunAsync(string metadata, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                HttpListenerContext context;
                try { context = await _listener.GetContextAsync().WaitAsync(ct); }
                catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException
                    or HttpListenerException or InvalidOperationException) { return; }

                var path = context.Request.RawUrl ?? "";
                var isMetadata = path.EndsWith("/$metadata", StringComparison.Ordinal);
                if (!isMetadata)
                {
                    var body = "";
                    if (context.Request.HasEntityBody)
                    {
                        using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
                        body = " " + await reader.ReadToEndAsync(ct);
                    }
                    Requests.Enqueue(context.Request.HttpMethod + " " + path + body);
                }

                var payload = Encoding.UTF8.GetBytes(isMetadata ? metadata : """{"value":[]}""");
                context.Response.StatusCode = 200;
                context.Response.ContentType = isMetadata ? "application/xml" : "application/json";
                context.Response.ContentLength64 = payload.Length;
                try
                {
                    await context.Response.OutputStream.WriteAsync(payload, ct);
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
