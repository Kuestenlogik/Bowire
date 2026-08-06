// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;
using Kuestenlogik.Bowire.Mocking;
using Kuestenlogik.Bowire.Security.Scanner;

namespace Kuestenlogik.Bowire.App.Cli;

/// <summary>
/// Scanner-backed <see cref="IAuthFlowCapturer"/> (#563): runs an
/// <c>AuthFlowDefinition</c> via <see cref="AuthFlowRunner"/> and returns the
/// captured credential. This is the adapter that keeps the OUTBOUND
/// flow-execution — which lives in the optional
/// <c>Kuestenlogik.Bowire.Security.Scanner</c> sibling — reachable from Core's
/// auth-recording endpoint and the MCP tools without Core depending on the
/// Scanner. Registered in the standalone tool's host; embedded hosts that don't
/// register it fall back to static-credential capture only.
/// </summary>
internal sealed class AuthFlowCapturer : IAuthFlowCapturer
{
    public async Task<AuthFlowCaptureResult> CaptureAsync(string flowJson, CancellationToken ct = default)
    {
        AuthFlowDefinition flow;
        try
        {
            flow = AuthFlowRunner.Parse(flowJson);
        }
        catch (Exception ex) when (ex is AuthFlowException or JsonException or ArgumentException)
        {
            // Parse throws JsonException for malformed JSON and ArgumentException
            // for an empty document; surface all as a Core-visible capture error.
            throw new AuthFlowCaptureException(ex.Message, ex);
        }

        using var http = new HttpClient();
        AuthFlowResult result;
        try
        {
            result = await AuthFlowRunner.RunAsync(flow, http, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;   // the caller cancelled — not a flow failure, don't swallow it
        }
        catch (Exception ex) when (
            ex is AuthFlowException or HttpRequestException or InvalidOperationException
               or UriFormatException or IOException or TaskCanceledException or FormatException)
        {
            // A relative/malformed step URL (InvalidOperationException /
            // UriFormatException), the HttpClient timeout (TaskCanceledException),
            // and transport faults (HttpRequestException / IOException) all become
            // the Core-visible capture error so no surface leaks a raw 500 / stack.
            throw new AuthFlowCaptureException("Auth flow failed: " + ex.Message, ex);
        }

        // Fail closed (the documented contract): a captured empty/blank token
        // would arm #562's gate in presence-only mode — never return one.
        if (string.IsNullOrWhiteSpace(result.Token))
            throw new AuthFlowCaptureException("Auth flow captured an empty token.");

        return new AuthFlowCaptureResult(result.Token, SchemeFromPrefix(flow.InjectPrefix), flow.InjectHeader);
    }

    // The flow's inject prefix tells us how the token is presented: "Bearer " →
    // bearer, "Basic " → basic, empty → a raw api-key header.
    private static string SchemeFromPrefix(string prefix)
    {
        var trimmed = prefix.Trim();
        if (string.Equals(trimmed, "Bearer", StringComparison.OrdinalIgnoreCase)) return "bearer";
        if (string.Equals(trimmed, "Basic", StringComparison.OrdinalIgnoreCase)) return "basic";
        return string.IsNullOrEmpty(trimmed) ? "apikey" : "bearer";
    }
}
