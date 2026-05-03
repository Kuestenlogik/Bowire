// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Sockets;
using Kuestenlogik.Bowire.Models;
using Kuestenlogik.Bowire.Protocol.Rest;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;

namespace Kuestenlogik.Bowire.IntegrationTests;

/// <summary>
/// End-to-end test for the REST plugin's multipart/form-data path: a
/// file-upload BowireMethodInfo with a binary "file" field + a plain
/// "description" field. The Kestrel host echoes back what it received
/// (filename, content type, file bytes, description) so the test can
/// assert the wire shape.
/// </summary>
public class MultipartRestIntegrationTests
{
    [Fact]
    public async Task RestInvoker_BinaryAndPlainFormFields_ReachServerAsMultipart()
    {
        var port = GetFreeTcpPort();
        var url = $"http://127.0.0.1:{port}";

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls(url);
        builder.Logging.ClearProviders();

        await using var app = builder.Build();
        app.MapPost("/upload", async (HttpRequest req) =>
        {
            if (!req.HasFormContentType) return Results.BadRequest("not a form request");
            var form = await req.ReadFormAsync();
            var description = form["description"].ToString();
            var file = form.Files.Count > 0 ? form.Files[0] : null;
            if (file is null)
            {
                return Results.Json(new
                {
                    description,
                    fileSeen = false,
                    contentType = req.ContentType
                });
            }
            using var ms = new MemoryStream();
            await file.CopyToAsync(ms);
            var bytes = ms.ToArray();
            return Results.Json(new
            {
                description,
                fileSeen = true,
                filename = file.FileName,
                fileBytes = Convert.ToBase64String(bytes),
                contentType = req.ContentType ?? string.Empty
            });
        });
        await app.StartAsync(TestContext.Current.CancellationToken);

        try
        {
            var fields = new List<BowireFieldInfo>
            {
                new(Name: "file", Number: 1, Type: "string", Label: "required", IsMap: false, IsRepeated: false, MessageType: null, EnumValues: null)
                {
                    Source = "formdata",
                    IsBinary = true,
                    Required = true
                },
                new(Name: "description", Number: 2, Type: "string", Label: "optional", IsMap: false, IsRepeated: false, MessageType: null, EnumValues: null)
                {
                    Source = "formdata"
                }
            };

            var methodInfo = new BowireMethodInfo(
                Name: "Upload",
                FullName: "POST /upload",
                ClientStreaming: false,
                ServerStreaming: false,
                InputType: new BowireMessageInfo("UploadRequest", "UploadRequest", fields),
                OutputType: new BowireMessageInfo("UploadResponse", "UploadResponse", []),
                MethodType: "Unary")
            {
                HttpMethod = "POST",
                HttpPath = "/upload"
            };

            var fileBytes = System.Text.Encoding.UTF8.GetBytes("Hello, multipart!");
            var fileBase64 = Convert.ToBase64String(fileBytes);
            var requestJson = $$"""
                {
                    "file": { "filename": "greeting.txt", "data": "{{fileBase64}}" },
                    "description": "Test upload"
                }
                """;

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

            var result = await RestInvoker.InvokeAsync(
                http, url, methodInfo,
                jsonMessages: [requestJson],
                requestMetadata: null,
                ct: TestContext.Current.CancellationToken);

            Assert.Equal("OK", result.Status);
            Assert.NotNull(result.Response);
            Assert.Contains("\"fileSeen\":true", result.Response!, StringComparison.Ordinal);
            Assert.Contains("\"filename\":\"greeting.txt\"", result.Response!, StringComparison.Ordinal);
            Assert.Contains($"\"fileBytes\":\"{fileBase64}\"", result.Response!, StringComparison.Ordinal);
            Assert.Contains("\"description\":\"Test upload\"", result.Response!, StringComparison.Ordinal);
            Assert.Contains("multipart/form-data", result.Response!, StringComparison.Ordinal);
        }
        finally
        {
            await app.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task RestInvoker_PlainStringFormField_ReachesServerAsTextPart()
    {
        // Bare base64 strings (no { filename, data } wrapper) on a binary
        // field still travel; same for non-binary form fields. Sanity-check
        // the text-only multipart shape so OpenAPI specs that declare
        // `multipart/form-data` with all-text properties (Slack-style POST
        // forms) still round-trip cleanly.
        var port = GetFreeTcpPort();
        var url = $"http://127.0.0.1:{port}";

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls(url);
        builder.Logging.ClearProviders();

        await using var app = builder.Build();
        app.MapPost("/post-message", async (HttpRequest req) =>
        {
            var form = await req.ReadFormAsync();
            return Results.Json(new
            {
                channel = form["channel"].ToString(),
                text = form["text"].ToString(),
                contentType = req.ContentType ?? string.Empty
            });
        });
        await app.StartAsync(TestContext.Current.CancellationToken);

        try
        {
            var fields = new List<BowireFieldInfo>
            {
                new(Name: "channel", Number: 1, Type: "string", Label: "required", IsMap: false, IsRepeated: false, MessageType: null, EnumValues: null) { Source = "formdata" },
                new(Name: "text", Number: 2, Type: "string", Label: "required", IsMap: false, IsRepeated: false, MessageType: null, EnumValues: null) { Source = "formdata" }
            };

            var methodInfo = new BowireMethodInfo(
                Name: "PostMessage",
                FullName: "POST /post-message",
                ClientStreaming: false,
                ServerStreaming: false,
                InputType: new BowireMessageInfo("PostMessageRequest", "PostMessageRequest", fields),
                OutputType: new BowireMessageInfo("PostMessageResponse", "PostMessageResponse", []),
                MethodType: "Unary")
            {
                HttpMethod = "POST",
                HttpPath = "/post-message"
            };

            var requestJson = """{"channel":"#general","text":"Hello team"}""";

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

            var result = await RestInvoker.InvokeAsync(
                http, url, methodInfo,
                jsonMessages: [requestJson],
                requestMetadata: null,
                ct: TestContext.Current.CancellationToken);

            Assert.Equal("OK", result.Status);
            Assert.Contains("\"channel\":\"#general\"", result.Response!, StringComparison.Ordinal);
            Assert.Contains("\"text\":\"Hello team\"", result.Response!, StringComparison.Ordinal);
            Assert.Contains("multipart/form-data", result.Response!, StringComparison.Ordinal);
        }
        finally
        {
            await app.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    private static int GetFreeTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
