// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using Kuestenlogik.Bowire.Endpoints;
using Kuestenlogik.Bowire.Projects;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Kuestenlogik.Bowire.IntegrationTests;

/// <summary>
/// Installing a plugin from a package the operator already has, over the
/// same <c>/api/plugins/install</c> the workbench posts to.
/// </summary>
/// <remarks>
/// <para>
/// The CLI has taken <c>--file</c> since it shipped and the UI had no
/// counterpart, so a <c>.nupkg</c> in hand meant leaving the workbench for
/// a terminal. A browser never hands out a path, so the UI uploads the
/// bytes and the server runs the same CLI against a temp copy.
/// </para>
/// <para>
/// What is asserted here is everything that happens before the child
/// process: the request shapes that are refused, and the reason each is
/// refused with. The success path ends in <c>Process.Start</c>, which
/// needs a real <c>bowire</c> and a real package — that half is covered by
/// driving the actual workbench, not from here.
/// </para>
/// </remarks>
public sealed class BowirePluginFileInstallTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "bowire-file-install-" + Guid.NewGuid().ToString("N"));
    private readonly IBowirePathResolver _previous = BowirePaths.Current;

    public BowirePluginFileInstallTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "plugins"));
        BowirePaths.Current = new BowirePathResolver(
            name => name == BowirePathResolver.DataDirVariable ? _root : null,
            () => _root);
    }

    public void Dispose()
    {
        BowirePaths.Current = _previous;
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static async Task<IHost> BuildHost()
    {
        var host = new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer()
                   .Configure(app =>
                   {
                       app.UseRouting();
                       app.UseEndpoints(e => e.MapBowirePluginEndpoints(basePath: string.Empty));
                   })
                   .ConfigureServices(s => s.AddRouting());
            })
            .Build();
        await host.StartAsync(TestContext.Current.CancellationToken);
        return host;
    }

    // MultipartFormDataContent takes ownership of what is added to it and
    // disposes its parts with itself, which CA2000 cannot see through the
    // Add call. The caller disposes the multipart, so the part goes too.
#pragma warning disable CA2000
    private static MultipartFormDataContent Upload(string fileName, byte[] bytes, string field = "package")
    {
        var content = new MultipartFormDataContent();
        var part = new ByteArrayContent(bytes);
        part.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(part, field, fileName);
        return content;
    }

    /// <summary>A multipart body with a plain field and no file part at all.</summary>
    private static MultipartFormDataContent FieldOnly(string field, string value)
    {
        var content = new MultipartFormDataContent();
        content.Add(new StringContent(value), field);
        return content;
    }
#pragma warning restore CA2000

    private static async Task<(HttpStatusCode Status, JsonElement Body)> PostAsync(
        IHost host, HttpContent content)
    {
        using var client = host.GetTestClient();
        var response = await client.PostAsync(
            new Uri("/api/plugins/install", UriKind.Relative), content, TestContext.Current.CancellationToken);
        var json = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var doc = JsonDocument.Parse(json);
        return (response.StatusCode, doc.RootElement.Clone());
    }

    [Fact]
    public async Task A_Multipart_Post_Without_A_File_Says_What_Is_Missing()
    {
        using var host = await BuildHost();
        // A multipart body whose only part is a plain field, so both the
        // named lookup and the fallback come up empty.
        using var content = FieldOnly("prerelease", "true");

        var (status, body) = await PostAsync(host, content);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("A package file is required", body.GetProperty("title").GetString());
        // The reply names the field, so a caller building the request by
        // hand is not left guessing.
        Assert.Contains("package", body.GetProperty("detail").GetString()!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("plugin.txt")]
    [InlineData("plugin.dll")]
    [InlineData("plugin")]
    public async Task A_Package_That_Is_Not_A_Nupkg_Or_Zip_Is_Refused(string fileName)
    {
        using var host = await BuildHost();
        using var content = Upload(fileName, [1, 2, 3, 4]);

        var (status, body) = await PostAsync(host, content);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("Unsupported package type", body.GetProperty("title").GetString());
        // Both halves of the mismatch, so the message is actionable.
        var detail = body.GetProperty("detail").GetString()!;
        Assert.Contains(".nupkg", detail, StringComparison.Ordinal);
        Assert.Contains(".zip", detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_Empty_File_Is_Refused_Rather_Than_Handed_To_The_Installer()
    {
        using var host = await BuildHost();
        using var content = Upload("plugin.nupkg", []);

        var (status, body) = await PostAsync(host, content);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("A package file is required", body.GetProperty("title").GetString());
    }

    [Fact]
    public async Task The_Extension_Decides_Regardless_Of_How_It_Is_Cased()
    {
        // A file picker on Windows hands back whatever case the disk has.
        // The rejection above must not fire on `.NUPKG`, so this one gets
        // past validation and dies at the child process instead, which is
        // a different status and a different title.
        using var host = await BuildHost();
        using var content = Upload("Plugin.NUPKG", [1, 2, 3, 4]);

        var (status, body) = await PostAsync(host, content);

        Assert.NotEqual(HttpStatusCode.BadRequest, status);
        Assert.NotEqual("Unsupported package type", body.TryGetProperty("title", out var t) ? t.GetString() : null);
    }

    [Fact]
    public async Task A_Json_Post_Still_Needs_A_Package_Id()
    {
        // The upload branch keys off the content type, so the feed route
        // has to behave exactly as it did.
        using var host = await BuildHost();
        using var content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");

        var (status, body) = await PostAsync(host, content);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("packageId is required", body.GetProperty("title").GetString());
    }
}
