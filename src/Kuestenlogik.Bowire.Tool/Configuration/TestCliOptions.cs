// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.App.Configuration;

/// <summary>
/// Typed configuration for <c>bowire test</c>. Bound from the
/// <c>Bowire:Test</c> section of the shared configuration stack.
/// The collection-file path is positional, not a key — extracted
/// separately by <c>BowireCli</c>.
/// </summary>
/// <remarks>
/// <para>
/// Config shape:
/// </para>
/// <code>
/// {
///   "Bowire": {
///     "Test": {
///       "ReportPath": "./test-report.html"
///     }
///   }
/// }
/// </code>
/// </remarks>
internal sealed class TestCliOptions
{
    /// <summary>Path to the test-collection JSON file (positional arg).</summary>
    public string? CollectionPath { get; set; }

    /// <summary>Optional HTML report output (<c>--report</c>).</summary>
    public string? ReportPath { get; set; }

    /// <summary>
    /// Optional JUnit XML report output (<c>--junit</c>). The format is the
    /// de-facto CI standard consumed natively by Jenkins, GitLab CI, Azure
    /// DevOps, and GitHub Actions test reporters; complementary to the
    /// human-readable <see cref="ReportPath"/>.
    /// </summary>
    public string? JUnitPath { get; set; }

    /// <summary>
    /// Optional SARIF 2.1.0 report output (<c>--sarif</c>). Uploading it
    /// via <c>github/codeql-action/upload-sarif</c> lights failures up in
    /// the GitHub Code Scanning tab; GitLab + Azure DevOps ingest the same
    /// format.
    /// </summary>
    public string? SarifPath { get; set; }

    /// <summary>
    /// Emit GitHub Actions <c>::error</c> workflow-command annotations for
    /// every failure (<c>--annotations</c>). Default off — the plain TTY
    /// output stays clean outside CI.
    /// </summary>
    public bool Annotations { get; set; }

    /// <summary>
    /// #171 — re-capture snapshot baselines instead of diffing
    /// (<c>--update-snapshots</c>). Flow codepath only.
    /// </summary>
    public bool UpdateSnapshots { get; set; }

    /// <summary>
    /// #181 — failure threshold gating the exit code (<c>--fail-on</c>):
    /// <c>any</c> (default — non-zero on any failed assertion / step) or
    /// <c>never</c> (always exit 0; run + report only, e.g. a
    /// non-blocking pre-merge signal). Reports are written regardless.
    /// </summary>
    public string FailOn { get; set; } = "any";

    /// <summary>
    /// v2.2 (#test-pillar T2) — fallback server URL for Flow steps that
    /// don't carry their own <c>serverUrl</c>. Ignored for the legacy
    /// test-collection codepath (which already supports a per-collection
    /// <c>serverUrl</c>); applies only when the runner dispatches to
    /// <see cref="FlowTestRunner"/>.
    /// </summary>
    public string? BaseUrl { get; set; }

    /// <summary>
    /// v2.2 — <c>--env KEY=VALUE</c> repeats from the CLI. Populate the
    /// Flow runner's <c>{{var}}</c> / <c>${var}</c> resolver. Empty for
    /// the legacy codepath.
    /// </summary>
    public IReadOnlyList<string> EnvOverrides { get; set; } = Array.Empty<string>();

    /// <summary>
    /// #181 — <c>--env-file</c> repeats: dotenv-style files whose
    /// KEY=VALUE lines seed the Flow resolver before the
    /// <see cref="EnvOverrides"/> repeats are applied on top. Flow
    /// codepath only.
    /// </summary>
    public IReadOnlyList<string> EnvFiles { get; set; } = Array.Empty<string>();

    /// <summary>
    /// #208 Phase 5 — <c>--keyring</c>: resolve <c>{{keyring.service/account}}</c>
    /// refs from the runner's OS credential store (Windows Credential
    /// Manager / macOS Keychain / libsecret) so secrets never live in the
    /// flow file or an <c>--env-file</c>. Off by default; Flow codepath only.
    /// </summary>
    public bool Keyring { get; set; }

    /// <summary>
    /// #208 Phase 5 — <c>--ai-seed</c>: deterministic seed for
    /// <c>{{ai.*}}</c> refs. When set, each ai ref resolves to a stable
    /// seed-derived value (no model call) so CI runs are reproducible.
    /// Flow codepath only.
    /// </summary>
    public string? AiSeed { get; set; }
}
