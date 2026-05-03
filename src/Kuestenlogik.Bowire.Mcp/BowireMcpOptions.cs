// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Mcp;

/// <summary>
/// Tunables for <see cref="BowireMcpTools"/>. Defaults are
/// security-first: invocation is blocked unless either the URL is on the
/// <see cref="AllowedServerUrls"/> list or <see cref="AllowArbitraryUrls"/>
/// is explicitly opted into. The discover, env, and record-list tools stay
/// available regardless because they are read-only.
/// </summary>
public sealed class BowireMcpOptions
{
    /// <summary>
    /// Server-info name advertised in the MCP <c>initialize</c> response.
    /// </summary>
    public string ServerName { get; set; } = "bowire-mcp";

    /// <summary>
    /// Server-info version advertised in the MCP <c>initialize</c> response.
    /// </summary>
    public string ServerVersion { get; set; } = "0.9.4";

    /// <summary>
    /// URLs the agent may target via <c>bowire.invoke</c> /
    /// <c>bowire.subscribe</c>. Default-loaded from the active Bowire
    /// environments file (<c>~/.bowire/environments.json</c>) when
    /// <see cref="LoadAllowlistFromEnvironments"/> is left at its default
    /// of <c>true</c>. Add additional URLs explicitly when scripting the
    /// server outside of an interactive Bowire session.
    /// </summary>
    public IList<string> AllowedServerUrls { get; } = [];

    /// <summary>
    /// When <c>true</c> (default), seed <see cref="AllowedServerUrls"/>
    /// from every <c>serverUrl</c> field in <c>~/.bowire/environments.json</c>
    /// at server start. Disable when running fully detached (e.g. CI) and
    /// configure <see cref="AllowedServerUrls"/> manually.
    /// </summary>
    public bool LoadAllowlistFromEnvironments { get; set; } = true;

    /// <summary>
    /// Drop the allowlist entirely. Only safe in fully sandboxed contexts
    /// (CI containers without network access to internal infrastructure).
    /// The CLI prints a banner when this is set so the user can't claim
    /// ignorance.
    /// </summary>
    public bool AllowArbitraryUrls { get; set; }

    /// <summary>
    /// Cap on how long <c>bowire.subscribe</c> will sample a streaming
    /// call before returning the collected frames. The agent can pass a
    /// shorter <c>durationMs</c> argument; values above this cap are
    /// clamped so a buggy agent can't pin a stream open indefinitely.
    /// </summary>
    public int MaxSubscribeMs { get; set; } = 30_000;

    /// <summary>
    /// Default sample window for <c>bowire.subscribe</c> when the agent
    /// doesn't supply one. Five seconds is enough to see typical
    /// heartbeat / event cadences without making the agent wait.
    /// </summary>
    public int DefaultSubscribeMs { get; set; } = 5_000;

    /// <summary>
    /// Cap on how many frames <c>bowire.subscribe</c> returns in a single
    /// tool result, regardless of how long the agent samples. Stops a
    /// chatty stream from blowing up the JSON-RPC response.
    /// </summary>
    public int MaxSubscribeFrames { get; set; } = 200;
}
