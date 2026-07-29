// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.App.Configuration;

/// <summary>
/// Typed configuration for the URL-oriented CLI subcommands
/// (<c>bowire discover</c>, <c>list</c>, <c>describe</c>, <c>call</c>).
/// Bound from the <c>Bowire:Cli</c> section of the shared configuration
/// stack — <c>appsettings.json</c> + env + CLI flags — so a project-local
/// config can pin the server URL and plaintext toggle while one-off
/// invocations still retype flags to override.
/// <para>
/// <c>list</c> and <c>describe</c> are still gRPC-reflection-only by
/// design (scripts depend on their output shape). <c>discover</c> and
/// <c>call</c> fan out over every loaded protocol plugin — see
/// <see cref="Protocol"/> — so this type is no longer gRPC-centric even
/// though its <c>Bowire:Cli</c> config keys predate the widening (#538).
/// </para>
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
    /// <summary>
    /// Target endpoint, always the BARE url. A <c>hint@url</c> prefix
    /// (<c>grpc@https://localhost:5001</c>) is split off by
    /// <c>BowireCli.BuildCliOptions</c> and lands in <see cref="Protocol"/>,
    /// so nothing downstream has to re-parse it. Defaults to
    /// <c>https://localhost:5001</c>.
    /// </summary>
    public string Url { get; set; } = "https://localhost:5001";

    /// <summary>
    /// Protocol plugin id — the <c>IBowireProtocol.Id</c> value
    /// (<c>grpc</c> / <c>rest</c> / <c>graphql</c> / <c>mqtt</c> / …).
    /// Arrives either from an explicit <c>--protocol</c> or from the
    /// <c>hint@url</c> prefix on <see cref="Url"/>; the explicit flag
    /// wins. <c>null</c> means "no hint" — <c>discover</c> then probes
    /// every plugin and <c>call</c> takes the gRPC fast path unless it
    /// has to fan out.
    /// </summary>
    public string? Protocol { get; set; }

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

    /// <summary>
    /// Consume the method as a stream (<c>--stream</c>): one JSON document
    /// per received frame until the server closes or Ctrl+C. Routes
    /// <c>call</c> through <c>IBowireProtocol.InvokeStreamAsync</c>
    /// instead of <c>InvokeAsync</c>. gRPC auto-detects server-streaming
    /// without the flag; every other plugin needs it because only the
    /// caller knows whether an SSE / WebSocket / MQTT target is meant to
    /// be read once or followed.
    /// </summary>
    public bool Stream { get; set; }

    /// <summary>
    /// Repeated <c>--var KEY=VALUE</c> (alias <c>--env</c>) pairs for the
    /// <c>{{name}}</c> / <c>${name}</c> resolver that runs over the body,
    /// the URL and every metadata value. Same grammar as
    /// <c>bowire test --env</c>; later occurrences win.
    /// </summary>
    public List<string> Vars { get; set; } = [];

    /// <summary>
    /// Repeated <c>--env-file</c> paths (dotenv-style KEY=VALUE lines).
    /// Seeded before <see cref="Vars"/>, so an explicit <c>--var</c>
    /// overrides a file entry of the same name.
    /// </summary>
    public List<string> VarFiles { get; set; } = [];
}
