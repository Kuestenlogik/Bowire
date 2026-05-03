// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.App.Configuration;

/// <summary>
/// Typed configuration for the gRPC-centric CLI subcommands
/// (<c>bowire list</c>, <c>describe</c>, <c>call</c>). Bound from the
/// <c>Bowire:Cli</c> section of the shared configuration stack —
/// <c>appsettings.json</c> + env + CLI flags — so a project-local
/// config can pin the server URL and plaintext toggle while one-off
/// invocations still retype flags to override.
/// </summary>
/// <remarks>
/// <para>
/// Config shape:
/// </para>
/// <code>
/// {
///   "Bowire": {
///     "Cli": {
///       "Url": "https://localhost:5001",
///       "Plaintext": false,
///       "Verbose": false,
///       "Compact": false
///     }
///   }
/// }
/// </code>
/// <para>
/// Not bound from config (they're per-invocation and either positional
/// or repeated): <see cref="Target"/>, <see cref="Data"/>,
/// <see cref="Headers"/>. <c>BowireCli</c>
/// extracts these from the raw args alongside the typed binding.
/// </para>
/// </remarks>
internal sealed class CliCommandOptions
{
    /// <summary>gRPC endpoint. Defaults to <c>https://localhost:5001</c>.</summary>
    public string Url { get; set; } = "https://localhost:5001";

    /// <summary>Downgrade to plaintext (no TLS). Set by <c>-plaintext</c> or <c>--plaintext</c>.</summary>
    public bool Plaintext { get; set; }

    /// <summary>Print method names in addition to service names (<c>list</c> only).</summary>
    public bool Verbose { get; set; }

    /// <summary>Emit single-line JSON (<c>call</c> only, useful for piping).</summary>
    public bool Compact { get; set; }

    /// <summary>
    /// Positional target — <c>service</c> for <c>describe</c> or
    /// <c>service/method</c> for <c>describe</c>/<c>call</c>. Not in
    /// config because it's positional, not keyed.
    /// </summary>
    public string? Target { get; set; }

    /// <summary>
    /// Repeated <c>-d</c>/<c>--data</c> values — each entry is one
    /// client-stream message (unary calls use the first entry).
    /// Accepts <c>@filename</c> just like the original parser.
    /// </summary>
    public List<string> Data { get; set; } = [];

    /// <summary>Repeated <c>-H "key: value"</c> metadata headers.</summary>
    public List<string> Headers { get; set; } = [];
}
