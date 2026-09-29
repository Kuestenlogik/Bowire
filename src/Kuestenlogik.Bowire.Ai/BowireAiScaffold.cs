// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using System.Text.Json.Serialization;
using Kuestenlogik.Bowire.Scaffold;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Kuestenlogik.Bowire.Ai;

/// <summary>What a model is asked, and who answered.</summary>
/// <param name="Spec">The spec, normalized.</param>
/// <param name="Source"><c>ai</c> when a model wrote it, <c>parser</c> when the fallback did.</param>
/// <param name="Model">Which model, as <c>provider:model</c>; null for the parser.</param>
/// <param name="Notes">What was assumed or went wrong on the way, one line each.</param>
public sealed record ScaffoldProposal(
    [property: JsonPropertyName("spec")] ScaffoldSpec Spec,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("model")] string? Model,
    [property: JsonPropertyName("notes")] IReadOnlyList<string> Notes);

/// <summary>A model the scaffolder may ask, in the order it asks them.</summary>
/// <param name="Label"><c>provider:model</c>, for the notes.</param>
/// <param name="Client">The client.</param>
/// <param name="Owned">Disposed after the call when the scaffolder built it for this call.</param>
public sealed record ScaffoldModel(string Label, IChatClient Client, IDisposable? Owned = null);

/// <summary>
/// The AI half of scaffolding (#177): a sentence becomes a
/// <see cref="ScaffoldSpec"/>. The model only ever writes the spec — the
/// files come from the checked-in templates — so a small local model is
/// enough, and a different model changes at most the field list, never the
/// shape of the code.
/// </summary>
/// <remarks>
/// <b>Local first, cloud as fallback, parser last.</b> The configured
/// provider is asked when it is local. When it is a cloud provider, a local
/// Ollama or LM Studio that answers the probe is asked first and the cloud
/// provider only when the local one fails. <c>Bowire:Ai:Scaffold:PreferLocal
/// = false</c> asks the configured provider only. When no model gives a
/// usable spec, <see cref="ScaffoldIntentParser"/> does.
/// </remarks>
public static class BowireAiScaffold
{
    private static readonly JsonSerializerOptions s_read = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private static readonly HttpClient s_probe = new() { Timeout = TimeSpan.FromMilliseconds(400) };

    /// <summary>How long one model may take for one spec.</summary>
    public static readonly TimeSpan ModelTimeout = TimeSpan.FromSeconds(90);

    internal const string SystemPrompt = """
        You turn a one-sentence description of a service into an entity spec for a code scaffolder.
        Respond ONLY with one JSON object, no prose, no code fence:
        {"entity": "<singular noun, PascalCase>", "protocol": "rest" | "grpc", "fields": [{"name": "<camelCase>", "type": "string" | "int" | "long" | "double" | "bool" | "datetime" | "uuid", "required": true | false}]}
        Rules:
        - Do not include an id field; every entity gets one.
        - Use only the types listed.
        - Mark a field required only when the description says so or it is plainly essential to the entity.
        - Between 1 and 20 fields. Keep the names the description uses.
        - Use the protocol the description names; otherwise the one the user message gives as default.
        """;

    /// <summary>
    /// Ask <paramref name="models"/> in order, then fall back to the parser.
    /// Never throws for a model's failure — that becomes a note.
    /// </summary>
    public static async Task<ScaffoldProposal> ProposeAsync(
        IReadOnlyList<ScaffoldModel> models, string intent, string? protocol, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(models);
        ArgumentException.ThrowIfNullOrWhiteSpace(intent);
        var notes = new List<string>();
        foreach (var model in models)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(ModelTimeout);
                var response = await model.Client.GetResponseAsync(
                    [
                        new ChatMessage(ChatRole.System, SystemPrompt),
                        new ChatMessage(ChatRole.User, $"Default protocol: {protocol ?? "rest"}\nDescription: {intent}"),
                    ],
                    cancellationToken: timeout.Token).ConfigureAwait(false);
                var (spec, errors) = TryParseSpec(response.Text, protocol);
                if (spec is not null) return new ScaffoldProposal(spec, "ai", model.Label, notes);
                notes.Add($"{model.Label} answered, but not with a usable spec: {string.Join("; ", errors)}");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
#pragma warning disable CA1031 // any provider failure moves on to the next model
            catch (Exception ex)
#pragma warning restore CA1031
            {
                notes.Add($"{model.Label} failed: {(ex is OperationCanceledException ? "no answer within " + ModelTimeout.TotalSeconds + " s" : ex.Message)}");
            }
            finally
            {
                model.Owned?.Dispose();
            }
        }
        var (parsed, parserNotes) = ScaffoldIntentParser.Parse(intent, protocol);
        notes.AddRange(parserNotes);
        var (normalized, _) = parsed.Normalize();
        return new ScaffoldProposal(normalized ?? parsed, "parser", null, notes);
    }

    /// <summary>
    /// The spec in a model's answer — the first <c>{</c> to the last
    /// <c>}</c>, so a stray sentence or code fence around it does not
    /// matter — normalized; or why there is none.
    /// </summary>
    internal static (ScaffoldSpec? Spec, IReadOnlyList<string> Errors) TryParseSpec(string? text, string? protocol)
    {
        var raw = (text ?? string.Empty).Trim();
        var first = raw.IndexOf('{', StringComparison.Ordinal);
        var last = raw.LastIndexOf('}');
        if (first < 0 || last <= first) return (null, ["no JSON object in the answer"]);
        ScaffoldSpec? spec;
        try
        {
            spec = JsonSerializer.Deserialize<ScaffoldSpec>(raw[first..(last + 1)], s_read);
        }
        catch (JsonException ex)
        {
            return (null, ["the JSON does not read as a spec: " + ex.Message]);
        }
        if (spec is null) return (null, ["empty answer"]);
        if (!string.IsNullOrWhiteSpace(protocol)) spec = spec with { Protocol = protocol };
        else if (string.IsNullOrWhiteSpace(spec.Protocol)) spec = spec with { Protocol = "rest" };
        return spec.Normalize();
    }

    /// <summary>
    /// The models to ask, local first: the configured provider when it is
    /// local; otherwise a local server that answers the probe, then the
    /// configured cloud provider.
    /// </summary>
    public static async Task<IReadOnlyList<ScaffoldModel>> ModelsAsync(
        BowireAiOptions configured, IChatClient? configuredClient,
        IEnumerable<IBowireAiProviderFactory> factories, bool preferLocal, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(configured);
        ArgumentNullException.ThrowIfNull(factories);
        var models = new List<ScaffoldModel>();
        var configuredLocal = configured.ProviderId is "ollama" or "lmstudio";
        var configuredLabel = configured.ProviderId + ":" + configured.Model;

        if (!configuredLocal && preferLocal && configured.AutoDetectLocal)
        {
            var local = await ProbeLocalAsync(ct).ConfigureAwait(false);
            if (local is { } found)
            {
                var factory = factories.FirstOrDefault(f => f.Matches(found.Provider));
                var built = factory?.Build(new BowireAiOptions { ProviderId = found.Provider, Endpoint = found.Endpoint, Model = found.Model });
                if (built is { Client: { } client })
                {
#pragma warning disable CA2000 // ownership moves to the ScaffoldModel; ProposeAsync disposes it after the call
                    models.Add(new ScaffoldModel(found.Provider + ":" + found.Model, client, new Both(client, built.Value.Inner)));
#pragma warning restore CA2000
                }
            }
        }
        if (configuredClient is not null) models.Add(new ScaffoldModel(configuredLabel, configuredClient));
        return models;
    }

    // Ollama's /api/tags, then LM Studio's /v1/models: the first that
    // answers with a model wins.
    private static async Task<(string Provider, string Endpoint, string Model)?> ProbeLocalAsync(CancellationToken ct)
    {
        foreach (var (provider, endpoint, path, listProp, nameProp) in new[]
        {
            ("ollama", "http://127.0.0.1:11434", "/api/tags", "models", "name"),
            ("lmstudio", "http://127.0.0.1:1234", "/v1/models", "data", "id"),
        })
        {
            try
            {
                using var resp = await s_probe.GetAsync(new Uri(endpoint + path), ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) continue;
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
                if (!doc.RootElement.TryGetProperty(listProp, out var list) || list.ValueKind != JsonValueKind.Array) continue;
                foreach (var m in list.EnumerateArray())
                {
                    if (m.TryGetProperty(nameProp, out var n) && n.GetString() is { Length: > 0 } name)
                        return (provider, endpoint, name);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
#pragma warning disable CA1031 // nothing listening is the common answer
            catch (Exception)
#pragma warning restore CA1031
            {
            }
        }
        return null;
    }

    private sealed class Both(IDisposable a, IDisposable? b) : IDisposable
    {
        public void Dispose()
        {
            a.Dispose();
            b?.Dispose();
        }
    }

    /// <summary>
    /// <c>POST {basePath}/api/ai/scaffold</c> — <c>{ intent, protocol }</c>
    /// in, a <see cref="ScaffoldProposal"/> out. Answers even without any
    /// model: then the parser wrote the spec, and <c>source</c> says so.
    /// </summary>
    public static IEndpointRouteBuilder MapBowireAiScaffoldEndpoints(this IEndpointRouteBuilder endpoints, string basePath = "/bowire")
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapPost($"{basePath}/api/ai/scaffold", async (HttpContext ctx, ScaffoldParseRequest? request) =>
        {
            if (string.IsNullOrWhiteSpace(request?.Intent))
                return Results.BadRequest(new { errors = s_intentRequired });
            var sp = ctx.RequestServices;
            var runtime = sp.GetService<BowireAiRuntime>();
            var options = runtime?.Options ?? sp.GetService<BowireAiOptions>() ?? new BowireAiOptions();
            var preferLocal = sp.GetService<IConfiguration>()?.GetValue<bool?>("Bowire:Ai:Scaffold:PreferLocal") ?? true;
            var models = await ModelsAsync(options, runtime?.Current, sp.GetServices<IBowireAiProviderFactory>(), preferLocal, ctx.RequestAborted);
            var proposal = await ProposeAsync(models, request.Intent, string.IsNullOrWhiteSpace(request.Protocol) ? null : request.Protocol, ctx.RequestAborted);
            return Results.Ok(proposal);
        }).ExcludeFromDescription();
        return endpoints;
    }

    private static readonly string[] s_intentRequired = ["intent is required"];
}
