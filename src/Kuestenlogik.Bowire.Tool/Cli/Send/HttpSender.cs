// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using Kuestenlogik.Bowire.App.Configuration;

namespace Kuestenlogik.Bowire.App.Cli.Send;

/// <summary>What <c>bowire send http</c> was asked to do.</summary>
internal sealed class HttpSendOptions
{
    public IReadOnlyList<string> Tokens { get; init; } = [];
    public string? Data { get; init; }
    public IReadOnlyList<string> Vars { get; init; } = [];
    public IReadOnlyList<string> VarFiles { get; init; } = [];
    public bool Compact { get; init; }
    public bool Offline { get; init; }
    public bool Negotiate { get; init; }
    public string? Ntlm { get; init; }
    public string? Digest { get; init; }
    public string DefaultScheme { get; init; } = "http";
}

/// <summary>
/// Sends the request of <c>bowire send http</c> (#678), or prints it with
/// <c>--offline</c>.
/// </summary>
/// <remarks>
/// No plugin registry, no discovery: the request goes out through the client
/// <c>BowireHttpClientFactory</c> builds for the REST plugin, so the proxy,
/// the CA bundle, the loopback-certificate rule and the challenge-response
/// schemes are the ones every other HTTP request of Bowire follows.
/// </remarks>
internal static class HttpSender
{
    /// <summary>Run the command.</summary>
    /// <param name="options">The parsed command line.</param>
    /// <param name="io">Where the body and the diagnostics go.</param>
    /// <param name="createClient">Makes the client; never called with <c>--offline</c>.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>0 for 2xx and 3xx, 2 for 4xx and 5xx or a usage error, 1 when nothing could be sent.</returns>
    public static async Task<int> RunAsync(
        HttpSendOptions options, CommandIo io, Func<HttpMessageInvoker> createClient, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(createClient);

        var (method, url, items) = HttpSendRequest.SplitPositionals(options.Tokens);
        if (url is null)
        {
            io.ErrLine("Usage: bowire send http [METHOD] <url> [items…]");
            return 2;
        }

        var vars = ReadVars(options, io);
        if (vars is null) return 2;

        string? data = options.Data;
        if (data is not null && data.StartsWith('@'))
        {
            var path = data[1..];
            if (!File.Exists(path))
            {
                io.ErrLine($"File not found: {path}");
                return 2;
            }
            data = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
        }

        var (request, error) = HttpSendRequest.Build(method, url, items, data, vars, options.DefaultScheme);
        if (request is null)
        {
            io.ErrLine(error!);
            return 2;
        }

        // Some APIs refuse a request without one (GitHub's among them).
        request.DefaultHeader("User-Agent", "Bowire/" + BowireVersionReport.AppVersion());

        if (options.Offline)
        {
            io.Out_(request.Render());
            return 0;
        }

        using var message = request.ToMessage();
        var auth = AuthMarker(options);
        if (auth is not null) Kuestenlogik.Bowire.Auth.BowireHttpAuth.Attach(message, auth);

        using var client = createClient();
        var watch = Stopwatch.StartNew();
        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(message, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            io.ErrLine($"{request.Method} {request.Url} failed: {ex.Message}");
            return 1;
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            io.ErrLine($"{request.Method} {request.Url} timed out: {ex.Message}");
            return 1;
        }

        using (response)
        {
            var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            watch.Stop();
            var status = (int)response.StatusCode;
            io.ErrLine(string.Create(CultureInfo.InvariantCulture,
                $"HTTP/{response.Version} {status} {response.ReasonPhrase}  {watch.ElapsedMilliseconds}ms"));
            WriteBody(io, response.Content.Headers.ContentType, bytes, options.Compact);
            return status is >= 200 and < 400 ? 0 : 2;
        }
    }

    private static Dictionary<string, string>? ReadVars(HttpSendOptions options, CommandIo io)
    {
        var pairs = new List<string>();
        foreach (var file in options.VarFiles)
        {
            try
            {
                pairs.AddRange(FlowTestRunner.ReadEnvFileLines(file));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                io.ErrLine($"Failed to read --env-file '{file}': {ex.Message}");
                return null;
            }
        }
        pairs.AddRange(options.Vars);
        return FlowTestRunner.MergeEnv(pairs);
    }

    /// <summary>
    /// The credentials as the metadata marker every HTTP path of Bowire reads,
    /// so <c>send</c> authenticates exactly as <c>call</c> does.
    /// </summary>
    private static Dictionary<string, string>? AuthMarker(HttpSendOptions options)
    {
        var cli = new CliCommandOptions();
        BowireCli.ApplyHttpAuth(options.Negotiate, options.Ntlm, options.Digest, cli);
        return CliHandler.BuildMetadata(cli);
    }

    /// <summary>
    /// JSON indented (one line with <c>--compact</c>), other text as it came,
    /// and a binary body as its size only, so it does not garble the terminal.
    /// </summary>
    private static void WriteBody(CommandIo io, MediaTypeHeaderValue? type, byte[] bytes, bool compact)
    {
        if (bytes.Length == 0) return;
        var media = type?.MediaType ?? string.Empty;
        if (!IsText(media, bytes))
        {
            io.ErrLine($"[{bytes.Length} bytes of {(media.Length > 0 ? media : "binary data")} not printed]");
            return;
        }
        var encoding = System.Text.Encoding.UTF8;
        if (type?.CharSet is { Length: > 0 } charset)
        {
            try { encoding = System.Text.Encoding.GetEncoding(charset.Trim('"')); }
            catch (ArgumentException) { /* an unknown charset reads as UTF-8 */ }
        }
        var text = encoding.GetString(bytes);
        if (IsJson(media))
        {
            try
            {
                using var doc = JsonDocument.Parse(text);
                io.OutLine(JsonSerializer.Serialize(doc.RootElement, compact ? Compact : Indented));
                return;
            }
            catch (JsonException)
            {
                // Labelled JSON but is not: print it as it came.
            }
        }
        io.Out_(text);
        if (!text.EndsWith('\n')) io.OutLine();
    }

    // Relaxed: this is terminal output, not HTML, so a quote stays a quote
    // and an umlaut an umlaut instead of " and ü.
    private static readonly JsonSerializerOptions Compact = new()
    {
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
    private static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static bool IsJson(string media) =>
        media.Equals("application/json", StringComparison.OrdinalIgnoreCase)
        || media.EndsWith("+json", StringComparison.OrdinalIgnoreCase);

    private static bool IsText(string media, byte[] bytes)
    {
        if (media.Length == 0) return Array.IndexOf(bytes, (byte)0) < 0;
        return media.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
            || IsJson(media)
            || media.EndsWith("+xml", StringComparison.OrdinalIgnoreCase)
            || media is "application/xml" or "application/javascript" or "application/x-www-form-urlencoded"
                or "application/x-ndjson" or "application/yaml";
    }
}
