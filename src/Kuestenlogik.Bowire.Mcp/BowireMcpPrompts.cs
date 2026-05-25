// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.ComponentModel;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Server;

namespace Kuestenlogik.Bowire.Mcp;

/// <summary>
/// MCP prompt surface for the Bowire-self adapter — canned AI
/// workflows the user (or the agent's host) picks from a menu and
/// the agent then runs against Bowire's tools. Each prompt renders
/// into a single user-role <see cref="ChatMessage"/> that wires the
/// arguments into a task description; the agent then drives the
/// matching <see cref="BowireMcpTools"/> calls.
///
/// <para>
/// Prompts are templates, not workflows: the language model still
/// chooses which tools to call and in what order. The descriptions
/// pick the tools the agent would naturally reach for given the task.
/// </para>
/// </summary>
[McpServerPromptType]
public sealed class BowireMcpPrompts
{
    private static ChatMessage UserText(string text)
        => new(ChatRole.User, text);

    [McpServerPrompt(Name = "replay-recording")]
    [Description("Replay a captured recording against its original target and report whether every response still matches. Useful as a smoke-check after a deploy or schema change.")]
    public static ChatMessage ReplayRecording(
        [Description("Recording id from bowire://recordings (or use the bowire.record.list tool to look it up).")] string recordingId,
        [Description("Optional: alternative server URL to replay against. Defaults to whatever the recording captured.")] string? targetUrl = null)
    {
        var lines = new List<string>
        {
            $"You have a Bowire recording with id `{recordingId}`. Replay it step-by-step and report whether each response matches the captured response.",
            "",
            "Tools you will need:",
            "- `bowire.record.list` (or read `bowire://recordings`) to confirm the recording exists and get the step count.",
            $"- `bowire.invoke` once per step. The recording's protocol + service + method come from `bowire://recordings/{recordingId}`.",
        };
        if (!string.IsNullOrWhiteSpace(targetUrl))
            lines.Add($"- Use `--url {targetUrl}` instead of the captured URL when invoking.");
        lines.Add("");
        lines.Add("Report: a per-step PASS/FAIL with a one-line diff for each FAIL. Total at the end. Stop after the first 5 failures and ask whether to continue.");
        return UserText(string.Join("\n", lines));
    }

    [McpServerPrompt(Name = "compare-responses")]
    [Description("Diff two recordings step-by-step — useful for catching regressions or drift between staging and prod captures.")]
    public static ChatMessage CompareResponses(
        [Description("Baseline recording id (the 'known good' one).")] string baselineId,
        [Description("Candidate recording id (the one you suspect changed).")] string candidateId)
    {
        var text = string.Join("\n", new[]
        {
            $"Compare Bowire recordings `{baselineId}` (baseline) and `{candidateId}` (candidate) step-by-step.",
            "",
            $"Read `bowire://recordings/{baselineId}` and `bowire://recordings/{candidateId}` first to confirm the step counts match. If they don't, surface that as the first finding — different lengths mean the candidate is a different scenario, not a drifted one.",
            "",
            "For matching step indices: diff response body, status code, and notable headers (Content-Type, Cache-Control). Treat trailing whitespace and key ordering as equivalent; report semantic deltas only.",
            "",
            "Output: a markdown table with columns Step | Field | Baseline | Candidate | Verdict. Append a one-paragraph summary of the dominant change type (schema drift, value drift, status drift)."
        });
        return UserText(text);
    }

    [McpServerPrompt(Name = "fuzz-method")]
    [Description("Run Bowire's fuzz scan against one method and summarise findings. Maps to the existing `bowire fuzz` CLI flow via the bowire.invoke tool.")]
    public static ChatMessage FuzzMethod(
        [Description("Server URL (must be on the allowlist).")] string url,
        [Description("Fully-qualified service name (e.g. `weather.WeatherService`).")] string service,
        [Description("Method name (e.g. `GetCurrentWeather`).")] string method,
        [Description("Optional: payload-class hint (string|number|bytes|json). Defaults to auto-pick.")] string? payloadClass = null)
    {
        var lines = new List<string>
        {
            $"Fuzz `{service}/{method}` on `{url}` and summarise the findings.",
            "",
            "1. Call `bowire.discover` against the URL to confirm the method exists and to find its field shape.",
            "2. Run `bowire.invoke` with a sequence of malformed inputs against each field. Cover at minimum: empty, very long, null, type-confusion (numeric where string expected), boundary integers, control characters.",
        };
        if (!string.IsNullOrWhiteSpace(payloadClass))
            lines.Add($"3. Constrain payloads to the `{payloadClass}` class — skip the type-confusion family.");
        lines.Add("");
        lines.Add("Report: a finding for each input class that produced a 500, a timeout, or a response that diverges suspiciously from the benign one (e.g. echoed unsanitised input). Order findings by severity. End with one paragraph of \"what to test next\".");
        return UserText(string.Join("\n", lines));
    }

    [McpServerPrompt(Name = "scan-service")]
    [Description("Run the Bowire security scanner against a URL and summarise SARIF findings. Operator workflow: scan → triage top severities → propose fixes.")]
    public static ChatMessage ScanService(
        [Description("Server URL (must be on the allowlist).")] string url,
        [Description("Optional scan profile: 'fast' (default rules only) or 'full' (all built-ins + Nuclei templates).")] string profile = "fast")
    {
        var text = string.Join("\n", new[]
        {
            $"Run `bowire scan --url {url} --profile {profile}` (via the bowire.invoke tool's CLI-pass-through, or via the dedicated bowire.scan tool when present) and summarise the SARIF findings.",
            "",
            "1. Run the scan and capture its SARIF JSON output.",
            "2. Group findings by rule id; collapse duplicates that touch the same endpoint into a single entry.",
            "3. Order groups by max severity within the group (critical → low).",
            "",
            "Report: a markdown section per severity tier. Within each section, one bullet per rule with (a) what the rule checks, (b) which endpoints it fired on, (c) the fix the rule's `helpUri` recommends. End with a top-3 prioritised TODO list."
        });
        return UserText(text);
    }

    [McpServerPrompt(Name = "mock-from-recording")]
    [Description("Spin up a Bowire mock server from a recording and walk the user through verifying it. Maps to bowire.mock.start + a follow-up bowire.invoke.")]
    public static ChatMessage MockFromRecording(
        [Description("Recording id from bowire://recordings.")] string recordingId,
        [Description("Optional: listen port for the mock server. 0 means OS-assigned (the tool will report the chosen port).")] int port = 0)
    {
        var text = string.Join("\n", new[]
        {
            $"Start a Bowire mock server that replays recording `{recordingId}` and verify it responds.",
            "",
            $"1. Call `bowire.mock.start` with the recording id `{recordingId}` and port `{port}`.",
            "2. Capture the returned mock handle and listen URL.",
            "3. Issue at least one matching `bowire.invoke` against the mock and report whether the response matches the corresponding recording step.",
            "4. If verification passes, print a sample curl/grpcurl command the operator can use to keep poking the mock manually.",
            "5. Remind the operator to call `bowire.mock.stop` with the handle when they're done."
        });
        return UserText(text);
    }

}
