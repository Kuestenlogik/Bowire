// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.CommandLine;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Kuestenlogik.Bowire.App.Cli;
using Kuestenlogik.Bowire.App.Cli.Send;
using Microsoft.Extensions.Configuration;

namespace Kuestenlogik.Bowire.Tests.Cli;

/// <summary>
/// <c>bowire send http</c> (#678): the item notation, the method and URL
/// rules, the request it builds, <c>--offline</c>, and the command line that
/// reaches it. Requests go to a handler the test owns or to a socket it
/// accepts on, so every test runs in parallel.
/// </summary>
public sealed class SendHttpTests
{
    private static readonly Dictionary<string, string> NoVars = new(StringComparer.Ordinal);

    [Theory]
    [InlineData("X-Id:7", "Header", "X-Id", "7")]
    [InlineData("Authorization:Bearer a=b", "Header", "Authorization", "Bearer a=b")]
    [InlineData("q==a:b", "Query", "q", "a:b")]
    [InlineData("name=ada", "StringField", "name", "ada")]
    [InlineData("name=", "StringField", "name", "")]
    [InlineData("age:=36", "JsonField", "age", "36")]
    [InlineData("tags:=[\"a\",\"b\"]", "JsonField", "tags", "[\"a\",\"b\"]")]
    [InlineData("file@./a b.png", "File", "file", "./a b.png")]
    [InlineData(@"a\=b=c", "StringField", "a=b", "c")]
    [InlineData(@"x\:y:z", "Header", "x:y", "z")]
    [InlineData(@"back\\slash=1", "StringField", @"back\slash", "1")]
    public void Item_IsDecidedByTheFirstSeparator(string token, string kind, string name, string value)
    {
        var item = HttpRequestItem.TryParse(token);
        Assert.NotNull(item);
        Assert.Equal(Enum.Parse<HttpRequestItemKind>(kind), item.Kind);
        Assert.Equal(name, item.Name);
        Assert.Equal(value, item.Value);
    }

    [Theory]
    [InlineData("api.example.com/pets")]
    [InlineData("=value")]
    [InlineData(":3000/pets")]
    [InlineData("")]
    public void Token_WithoutANamedSeparator_IsNoItem(string token)
        => Assert.Null(HttpRequestItem.TryParse(token));

    [Theory]
    [InlineData(new[] { "https://api/pets/1" }, null, "https://api/pets/1", 0)]
    [InlineData(new[] { "post", "https://api/x", "name=ada" }, "post", "https://api/x", 1)]
    [InlineData(new[] { "PROPFIND", "https://dav/x" }, "PROPFIND", "https://dav/x", 0)]
    [InlineData(new[] { "get", "localhost:8080/x" }, "get", "localhost:8080/x", 0)]
    [InlineData(new[] { "delete", ":3000/pets/1" }, "delete", ":3000/pets/1", 0)]
    [InlineData(new[] { "patch", "api.example.com/pets/1", "name=x" }, "patch", "api.example.com/pets/1", 1)]
    [InlineData(new[] { "localhost", "name=ada" }, null, "localhost", 1)]
    [InlineData(new[] { "api", "X-Id:abc" }, null, "api", 1)]
    public void Positionals_SplitIntoMethodUrlAndItems(string[] tokens, string? method, string url, int items)
    {
        var (m, u, rest) = HttpSendRequest.SplitPositionals(tokens);
        Assert.Equal(method, m);
        Assert.Equal(url, u);
        Assert.Equal(items, rest.Count);
    }

    [Theory]
    [InlineData(":3000/pets", "http", "http://localhost:3000/pets")]
    [InlineData(":/pets", "http", "http://localhost/pets")]
    [InlineData(":", "http", "http://localhost")]
    [InlineData("api.example.com/pets", "https", "https://api.example.com/pets")]
    [InlineData("http://api/x", "https", "http://api/x")]
    public void Url_IsCompletedLikeHttpie(string url, string scheme, string expected)
        => Assert.Equal(expected, HttpSendRequest.CompleteUrl(url, scheme));

    [Fact]
    public void BodyItems_MakeAJsonPost()
    {
        // The ticket's acceptance line: `post https://api/x name=ada age:=36`.
        var request = Build(null, "https://api/x", "name=ada", "age:=36", "tags:=[1,2]", "ok:=true");

        Assert.Equal("POST", request.Method);
        Assert.Equal("application/json", request.ContentType);
        Assert.Equal("""{"name":"ada","age":36,"tags":[1,2],"ok":true}""", request.Body);
        Assert.Contains(request.Headers, h => h.Key == "Accept" && h.Value.StartsWith("application/json", StringComparison.Ordinal));
    }

    [Fact]
    public void NoBody_IsAGet_AndQueryItemsJoinTheUrl()
    {
        var request = Build(null, "https://api/pets?limit=5", "q==a b", "tag==x&y");

        Assert.Equal("GET", request.Method);
        Assert.Null(request.Body);
        Assert.Equal("https://api/pets?limit=5&q=a%20b&tag=x%26y", request.Url.AbsoluteUri);
    }

    [Fact]
    public void Method_IsUpperCased_AndAnyTokenWorks()
        => Assert.Equal("PROPFIND", Build("propfind", "https://dav/x").Method);

    [Fact]
    public void LaterHeader_ReplacesAnEarlierOne()
    {
        var request = Build(null, "https://api/x", "X-Id:1", "x-id:2");
        var header = Assert.Single(request.Headers, h => h.Key.Equals("X-Id", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("2", header.Value);
    }

    [Fact]
    public void ContentTypeItem_NamesTheBodyType()
    {
        var request = Build(null, "https://api/x", "Content-Type:application/merge-patch+json", "name=ada");
        Assert.Equal("application/merge-patch+json", request.ContentType);
        Assert.DoesNotContain(request.Headers, h => h.Key == "Content-Type");
    }

    [Fact]
    public void Variables_AreResolved_AfterTheItemIsSplit()
    {
        // A value holding a separator stays a value: `{{token}}` is "a=b".
        var vars = new Dictionary<string, string>(StringComparer.Ordinal) { ["token"] = "a=b", ["host"] = "api" };
        var (request, error) = HttpSendRequest.Build(null, "https://{{host}}/x",
            ["Authorization:Bearer {{token}}", "who={{token}}"], null, vars, "http");

        Assert.Null(error);
        Assert.Equal("https://api/x", request!.Url.AbsoluteUri);
        Assert.Contains(request.Headers, h => h.Key == "Authorization" && h.Value == "Bearer a=b");
        Assert.Equal("""{"who":"a=b"}""", request.Body);
    }

    [Theory]
    [InlineData("nonsense", "is not a request item")]
    [InlineData("age:=thirty", "is not valid JSON")]
    public void UnusableItem_IsAnError_NotHalfARequest(string item, string message)
    {
        var (request, error) = HttpSendRequest.Build(null, "https://api/x", [item], null, NoVars, "http");
        Assert.Null(request);
        Assert.Contains(message, error, StringComparison.Ordinal);
    }

    [Fact]
    public void DataAndBodyItems_CannotBeCombined()
    {
        var (request, error) = HttpSendRequest.Build(null, "https://api/x", ["name=ada"], "{}", NoVars, "http");
        Assert.Null(request);
        Assert.Contains("cannot be combined", error, StringComparison.Ordinal);
    }

    [Fact]
    public void NonHttpUrl_IsAnError()
    {
        var (request, error) = HttpSendRequest.Build(null, "ftp://files/x", [], null, NoVars, "http");
        Assert.Null(request);
        Assert.Contains("not an http or https URL", error, StringComparison.Ordinal);
    }

    [Fact]
    public void FileItem_MakesAMultipartForm_WithTheTextFields()
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory("bowire-send-").FullName, "a.txt");
        File.WriteAllText(path, "hello");

        var request = Build(null, "https://api/up", $"file@{path}", "note=hi");

        Assert.Equal("POST", request.Method);
        Assert.StartsWith("multipart/form-data; boundary=", request.ContentType, StringComparison.Ordinal);
        Assert.Collection(request.Form,
            p => Assert.Equal(("file", path), (p.Name, p.FilePath!)),
            p => Assert.Equal(("note", "hi"), (p.Name, p.Value!)));
        var rendered = request.Render();
        Assert.Contains("filename=\"a.txt\"", rendered, StringComparison.Ordinal);
        Assert.Contains($"[5 bytes from {path}]", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void FileItem_WithRawJson_IsAnError()
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory("bowire-send-").FullName, "a.txt");
        File.WriteAllText(path, "x");
        var (request, error) = HttpSendRequest.Build(null, "https://api/up", [$"file@{path}", "n:=1"], null, NoVars, "http");
        Assert.Null(request);
        Assert.Contains("multipart form cannot carry", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Offline_PrintsTheRequest_AndNeverOpensASocket()
    {
        // A listener on the URL's port: had anything connected, the
        // connection would be pending when the command returns. The client
        // factory throws as well, so neither route to the wire is open.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var rc = await HttpSender.RunAsync(
            new HttpSendOptions
            {
                Tokens = ["post", $"http://127.0.0.1:{port}/x", "name={{who}}", "age:=36"],
                Vars = ["who=ada"],
                Offline = true,
            },
            new CommandIo(stdout, stderr),
            () => throw new InvalidOperationException("--offline must not create a client"),
            TestContext.Current.CancellationToken);

        Assert.Equal(0, rc);
        Assert.False(listener.Pending());
        var text = stdout.ToString();
        Assert.StartsWith("POST /x HTTP/1.1\nHost: 127.0.0.1:" + port + "\n", text, StringComparison.Ordinal);
        Assert.Contains("User-Agent: Bowire/", text, StringComparison.Ordinal);
        Assert.EndsWith("\n\n{\"name\":\"ada\",\"age\":36}\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Send_WithNoOtherArguments_PrintsTheBody()
    {
        HttpRequestMessage? seen = null;
        var (rc, stdout, stderr) = await RunAgainst(
            new HttpSendOptions { Tokens = ["https://api.example.com/pets/1"] },
            request =>
            {
                seen = request;
                return Json(HttpStatusCode.OK, """{"id":1,"name":"Jürgen \"J\""}""");
            });

        Assert.Equal(0, rc);
        Assert.Equal(HttpMethod.Get, seen!.Method);
        Assert.Null(seen.Content);
        // Indented, and readable: neither the quote nor the umlaut is escaped.
        Assert.Equal("{\n  \"id\": 1,\n  \"name\": \"Jürgen \\\"J\\\"\"\n}", stdout.TrimEnd().ReplaceLineEndings("\n"));
        Assert.StartsWith("HTTP/1.1 200 OK", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Send_PostsTheJsonBody_WithItsContentType()
    {
        string? body = null;
        string? type = null;
        var (rc, stdout, _) = await RunAgainst(
            new HttpSendOptions { Tokens = ["post", "https://api/x", "name=ada", "age:=36"], Compact = true },
            request =>
            {
                body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                type = request.Content.Headers.ContentType!.ToString();
                return Json(HttpStatusCode.Created, """{ "ok" : true }""");
            });

        Assert.Equal(0, rc);
        Assert.Equal("""{"name":"ada","age":36}""", body);
        // RFC 8259: JSON is UTF-8 and takes no charset parameter.
        Assert.Equal("application/json", type);
        Assert.Equal("""{"ok":true}""", stdout.TrimEnd());
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, 2)]
    [InlineData(HttpStatusCode.InternalServerError, 2)]
    [InlineData(HttpStatusCode.NoContent, 0)]
    [InlineData(HttpStatusCode.NotModified, 0)]
    public async Task ExitCode_FollowsTheStatus(HttpStatusCode status, int expected)
    {
        var (rc, _, stderr) = await RunAgainst(
            new HttpSendOptions { Tokens = ["https://api/x"] },
            _ => new HttpResponseMessage(status));
        Assert.Equal(expected, rc);
        Assert.Contains(((int)status).ToString(System.Globalization.CultureInfo.InvariantCulture), stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BinaryBody_IsNotPrinted()
    {
        var (rc, stdout, stderr) = await RunAgainst(
            new HttpSendOptions { Tokens = ["https://api/logo.png"] },
            _ =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([0x89, 0x50, 0x00, 0x47]) };
                response.Content.Headers.ContentType = new("image/png");
                return response;
            });
        Assert.Equal(0, rc);
        Assert.Empty(stdout);
        Assert.Contains("[4 bytes of image/png not printed; -o <file> saves them]", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnreachableServer_ExitsOne()
    {
        var (rc, _, stderr) = await RunAgainst(
            new HttpSendOptions { Tokens = ["https://api/x"] },
            _ => throw new HttpRequestException("connection refused"));
        Assert.Equal(1, rc);
        Assert.Contains("connection refused", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Digest_TravelsAsTheAuthMarker_NotAsAHeader()
    {
        HttpRequestMessage? seen = null;
        var (rc, _, _) = await RunAgainst(
            new HttpSendOptions { Tokens = ["https://api/x"], Digest = "ada:secret" },
            request =>
            {
                seen = request;
                return new HttpResponseMessage(HttpStatusCode.OK);
            });
        Assert.Equal(0, rc);
        Assert.True(seen!.Options.TryGetValue(Kuestenlogik.Bowire.Auth.BowireHttpAuth.OptionKey, out var auth));
        Assert.Equal("ada", auth.User);
        Assert.DoesNotContain(seen.Headers, h => h.Key.StartsWith("__bowire", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CommandLine_ReachesARealServer_OverTheFactoryClient()
    {
        // End to end through the parser and BowireHttpClientFactory: a socket
        // the test accepts on, one request read, one response written.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var ct = TestContext.Current.CancellationToken;
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(ct);
            var stream = client.GetStream();
            var request = await ReadRequestAsync(stream, ct);
            var body = Encoding.UTF8.GetBytes("""{"pong":true}""");
            await stream.WriteAsync(Encoding.ASCII.GetBytes(
                $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n"), ct);
            await stream.WriteAsync(body, ct);
            return request;
        }, ct);

        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var rc = await SendCommand.BuildHttp(new ConfigurationBuilder().Build())
            .Parse(["put", $"http://127.0.0.1:{port}/echo", "name=ada", "--compact"], BowireCli.Parsing())
            .InvokeAsync(new InvocationConfiguration { Output = stdout, Error = stderr }, ct);

        var seen = await server;
        Assert.Equal(0, rc);
        Assert.StartsWith("PUT /echo HTTP/1.1\r\n", seen, StringComparison.Ordinal);
        Assert.EndsWith("\r\n\r\n{\"name\":\"ada\"}", seen, StringComparison.Ordinal);
        Assert.Equal("""{"pong":true}""", stdout.ToString().TrimEnd());
    }

    [Fact]
    public async Task DataFromAFile_ReachesTheCommandAsWritten()
    {
        // #974: with response files on, the parser replaced `@file` with the
        // file's tokens and the body arrived as {x:me}.
        var path = Path.Combine(Directory.CreateTempSubdirectory("bowire-send-").FullName, "body.json");
        await File.WriteAllTextAsync(path, """{"x": "{{who}}"}""", TestContext.Current.CancellationToken);
        using var stdout = new StringWriter();

        var rc = await SendCommand.BuildHttp(new ConfigurationBuilder().Build())
            .Parse(["put", ":1/x", "-d", "@" + path, "--var", "who=me", "--offline"], BowireCli.Parsing())
            .InvokeAsync(new InvocationConfiguration { Output = stdout, Error = TextWriter.Null },
                TestContext.Current.CancellationToken);

        Assert.Equal(0, rc);
        Assert.EndsWith("\n\n{\"x\": \"me\"}\n", stdout.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void CallData_FromAFile_IsNotExpandedByTheParser()
    {
        // #974 for `call`: the option sees the token, and CallImplAsync reads the file.
        var call = BowireCli.BuildCallCommand(new ConfigurationBuilder().Build());
        var parsed = call.Parse(["svc/Method", "-d", "@request.json"], BowireCli.Parsing());
        Assert.Empty(parsed.Errors);
        Assert.Equal(["@request.json"], parsed.GetValue<string[]>("-d") ?? []);
    }

    [Fact]
    public void Help_ListsTheProtocols_AndTheItemNotation()
    {
        var send = SendCommand.Build(new ConfigurationBuilder().Build());
        Assert.Contains(send.Subcommands, c => c.Name == "http");

        var http = send.Subcommands.Single(c => c.Name == "http");
        var help = string.Join('\n', http.Arguments.Select(a => a.Description).Concat(http.Options.Select(o => o.Name)));
        foreach (var expected in new[] { "Header:value", "param==value", "field=value", "field:=json", "field@file",
                     "--offline", "--var", "--env-file", "--compact", "--include", "--output", "--negotiate", "--ntlm", "--digest" })
            Assert.Contains(expected, help, StringComparison.Ordinal);
    }

    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0xFF];

    private static HttpResponseMessage Binary(HttpStatusCode status, byte[] bytes)
    {
        var response = new HttpResponseMessage(status) { Content = new ByteArrayContent(bytes) };
        response.Content.Headers.ContentType = new("image/png");
        response.Headers.ETag = new("\"v1\"");
        return response;
    }

    [Fact]
    public async Task Include_PrintsTheStatusLineAndHeaders_BeforeTheBody()
    {
        // #975
        var (rc, stdout, _) = await RunAgainst(
            new HttpSendOptions { Tokens = [":5000/pets/1"], Include = true, Compact = true },
            _ =>
            {
                var response = Json(HttpStatusCode.Created, """{"id":1}""");
                response.Headers.Location = new Uri("https://api/pets/1");
                return response;
            });

        Assert.Equal(0, rc);
        var lines = stdout.ReplaceLineEndings("\n").Split('\n');
        Assert.Equal("HTTP/1.1 201 Created", lines[0]);
        Assert.Contains("Location: https://api/pets/1", lines);
        Assert.Contains("Content-Type: application/json; charset=utf-8", lines);
        var blank = Array.IndexOf(lines, string.Empty);
        Assert.True(blank > 0);
        Assert.Equal("""{"id":1}""", lines[blank + 1]);
    }

    [Fact]
    public async Task Output_WritesTheExactBytes_AndNothingToStdout()
    {
        // #975: `bowire send http :5000/logo.png -o logo.png`.
        var dir = Directory.CreateTempSubdirectory("bowire-send-");
        try
        {
            var path = Path.Combine(dir.FullName, "logo.png");
            var (rc, stdout, stderr) = await RunAgainst(
                new HttpSendOptions { Tokens = [":5000/logo.png"], Output = path },
                _ => Binary(HttpStatusCode.OK, Png));

            Assert.Equal(0, rc);
            Assert.Equal(Png, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
            Assert.Empty(stdout);
            Assert.Contains($"10 bytes written to {path}", stderr, StringComparison.Ordinal);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Output_StillWritesAnErrorBody_AndExitsTwo()
    {
        var dir = Directory.CreateTempSubdirectory("bowire-send-");
        try
        {
            var path = Path.Combine(dir.FullName, "error.json");
            var (rc, _, _) = await RunAgainst(
                new HttpSendOptions { Tokens = [":5000/x"], Output = path },
                _ => Json(HttpStatusCode.NotFound, """{"error":"nope"}"""));

            Assert.Equal(2, rc);
            Assert.Equal("""{"error":"nope"}""", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task OutputDash_WritesTheRawBytesToStdout_AfterTheHead()
    {
        using var raw = new MemoryStream();
        var (rc, stdout, _) = await RunAgainst(
            new HttpSendOptions { Tokens = [":5000/logo.png"], Output = "-", Include = true },
            _ => Binary(HttpStatusCode.OK, Png), raw);

        Assert.Equal(0, rc);
        Assert.Equal(Png, raw.ToArray());
        Assert.StartsWith("HTTP/1.1 200 OK", stdout, StringComparison.Ordinal);
        Assert.Contains("ETag: \"v1\"", stdout, StringComparison.Ordinal);
    }

    private static HttpSendRequest Build(string? method, string url, params string[] items)
    {
        var (request, error) = HttpSendRequest.Build(method, url, items, null, NoVars, "http");
        Assert.Null(error);
        return request!;
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static async Task<(int Rc, string Stdout, string Stderr)> RunAgainst(
        HttpSendOptions options, Func<HttpRequestMessage, HttpResponseMessage> respond, Stream? rawStdout = null)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var rc = await HttpSender.RunAsync(options, new CommandIo(stdout, stderr),
            () => new HttpClient(new FakeHandler(respond)), TestContext.Current.CancellationToken,
            rawStdout is null ? null : () => rawStdout);
        return (rc, stdout.ToString(), stderr.ToString());
    }

    private static async Task<string> ReadRequestAsync(NetworkStream stream, CancellationToken ct)
    {
        // Headers up to the blank line, then exactly Content-Length bytes.
        var buffer = new List<byte>();
        var chunk = new byte[4096];
        int headerEnd;
        while ((headerEnd = IndexOf(buffer, "\r\n\r\n"u8.ToArray())) < 0)
        {
            var n = await stream.ReadAsync(chunk, ct);
            if (n == 0) break;
            buffer.AddRange(chunk.AsSpan(0, n).ToArray());
        }
        var head = Encoding.ASCII.GetString(buffer.GetRange(0, headerEnd).ToArray());
        var lengthLine = head.Split("\r\n").FirstOrDefault(l => l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
        var length = lengthLine is null ? 0 : int.Parse(lengthLine["Content-Length:".Length..].Trim(), System.Globalization.CultureInfo.InvariantCulture);
        while (buffer.Count < headerEnd + 4 + length)
        {
            var n = await stream.ReadAsync(chunk, ct);
            if (n == 0) break;
            buffer.AddRange(chunk.AsSpan(0, n).ToArray());
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static int IndexOf(List<byte> haystack, byte[] needle)
    {
        for (var i = 0; i + needle.Length <= haystack.Count; i++)
        {
            var match = true;
            for (var j = 0; j < needle.Length && match; j++) match = haystack[i + j] == needle[j];
            if (match) return i;
        }
        return -1;
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }
}
