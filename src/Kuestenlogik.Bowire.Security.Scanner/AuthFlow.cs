// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json.Serialization;

namespace Kuestenlogik.Bowire.Security.Scanner;

/// <summary>
/// A headless authentication flow (#190): an ordered chain of HTTP requests
/// (login → refresh → token), each optionally capturing values from its
/// response into named variables, ending with one variable that holds the
/// bearer token to inject into every subsequent probe.
///
/// This is the CI-facing slice of the auth-recording epic — a
/// <c>bowire scan --auth-flow flow.json</c> runs the chain once, extracts the
/// token, and injects it as an auth header ahead of the scan. Interactive
/// grants that need a browser (OAuth auth-code / device, SAML/OIDC web-flow)
/// and the workbench capture UI are tracked as follow-ups.
///
/// Secrets are never inlined: request fields substitute <c>{{env.NAME}}</c>
/// from the process environment (and <c>{{var}}</c> from earlier captures), so
/// a checked-in flow file references <c>{{env.CLIENT_SECRET}}</c> rather than
/// the secret itself.
/// </summary>
public sealed class AuthFlowDefinition
{
    /// <summary>Informational grant label (client_credentials / password / refresh_token / custom). Not interpreted — the steps describe the actual requests.</summary>
    [JsonPropertyName("grant")] public string? Grant { get; init; }

    /// <summary>Ordered requests to run. Captures from earlier steps are available as <c>{{var}}</c> in later ones.</summary>
    [JsonPropertyName("steps")] public IReadOnlyList<AuthStep> Steps { get; init; } = [];

    /// <summary>Captured variable that holds the token to inject. Defaults to <c>access_token</c>, then <c>token</c> when present.</summary>
    [JsonPropertyName("token")] public string? Token { get; init; }

    /// <summary>Header name to inject the token under. Default <c>Authorization</c>.</summary>
    [JsonPropertyName("injectHeader")] public string InjectHeader { get; init; } = "Authorization";

    /// <summary>Value prefix prepended to the token. Default <c>Bearer </c> (trailing space intentional). Set to empty for raw API-key headers.</summary>
    [JsonPropertyName("injectPrefix")] public string InjectPrefix { get; init; } = "Bearer ";
}

/// <summary>One request in an <see cref="AuthFlowDefinition"/>.</summary>
public sealed class AuthStep
{
    [JsonPropertyName("method")] public string Method { get; init; } = "POST";

    [JsonPropertyName("url")] public string Url { get; init; } = "";

    /// <summary>Request headers (values support <c>{{var}}</c> / <c>{{env.NAME}}</c>).</summary>
    [JsonPropertyName("headers")] public IReadOnlyDictionary<string, string>? Headers { get; init; }

    /// <summary>Form fields sent as <c>application/x-www-form-urlencoded</c> (the OAuth token-endpoint shape). Mutually exclusive with <see cref="Json"/> / <see cref="Body"/>.</summary>
    [JsonPropertyName("form")] public IReadOnlyDictionary<string, string>? Form { get; init; }

    /// <summary>Raw JSON body (sent as <c>application/json</c>).</summary>
    [JsonPropertyName("json")] public string? Json { get; init; }

    /// <summary>Raw body (sent with <see cref="ContentType"/> or <c>text/plain</c>).</summary>
    [JsonPropertyName("body")] public string? Body { get; init; }

    /// <summary>Content-Type override for <see cref="Body"/>.</summary>
    [JsonPropertyName("contentType")] public string? ContentType { get; init; }

    /// <summary>Values to extract from this step's response into flow variables.</summary>
    [JsonPropertyName("capture")] public IReadOnlyList<AuthCapture>? Capture { get; init; }
}

/// <summary>
/// Extracts one value from a step's response into a named flow variable. Exactly
/// one source (<see cref="Json"/> / <see cref="Regex"/> / <see cref="Header"/> /
/// <see cref="Cookie"/>) is used, checked in that order.
/// </summary>
public sealed class AuthCapture
{
    /// <summary>Variable name the captured value is stored under, referenced later as <c>{{var}}</c>.</summary>
    [JsonPropertyName("var")] public string Var { get; init; } = "";

    /// <summary>Dotted JSON path into the response body, e.g. <c>access_token</c>, <c>$.data.token</c>, <c>tokens[0].jwt</c>.</summary>
    [JsonPropertyName("json")] public string? Json { get; init; }

    /// <summary>Regex over the raw response body; group 1 (or the whole match if no group) is captured.</summary>
    [JsonPropertyName("regex")] public string? Regex { get; init; }

    /// <summary>Response header name to capture.</summary>
    [JsonPropertyName("header")] public string? Header { get; init; }

    /// <summary>Cookie name to capture from <c>Set-Cookie</c>.</summary>
    [JsonPropertyName("cookie")] public string? Cookie { get; init; }
}

/// <summary>The outcome of running an <see cref="AuthFlowDefinition"/>.</summary>
/// <param name="Token">The extracted token value.</param>
/// <param name="HeaderLine">A ready-to-inject <c>Name: value</c> auth header, e.g. <c>Authorization: Bearer eyJ…</c>.</param>
/// <param name="Variables">All variables captured across the flow (for diagnostics).</param>
public sealed record AuthFlowResult(string Token, string HeaderLine, IReadOnlyDictionary<string, string> Variables);
