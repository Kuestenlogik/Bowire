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
    /// When <c>true</c>, also seed <see cref="AllowedServerUrls"/> from
    /// the user's typed-URL history (<c>~/.bowire/typed-urls.json</c>).
    /// Wider than <see cref="LoadAllowlistFromEnvironments"/> — typed
    /// URLs include every server the user has actually pointed Bowire at,
    /// not just the ones they've saved as named environments. Default
    /// <c>false</c>: opt-in via <c>--allow-invoke</c> on the CLI because
    /// it materially widens what an agent can hit.
    /// </summary>
    /// <remarks>
    /// Strictly additive — combines with <see cref="AllowedServerUrls"/>
    /// + the environments seed; never narrows the list. Tools also gain
    /// a <c>bowire.allowlist.permit(url)</c> entry point that appends to
    /// the typed-URL file so an agent can record "the user just typed
    /// this URL into the workbench" without re-reading the file.
    /// </remarks>
    public bool LoadAllowlistFromTypedUrls { get; set; }

    /// <summary>
    /// When <c>true</c> (default), tools that mutate live state
    /// (<c>bowire.mock.start</c>, <c>bowire.record.start</c>,
    /// <c>bowire.env.switch</c>) require an explicit second-step
    /// confirmation: the first call returns <c>{ pending: true, token,
    /// plan }</c>; the agent re-invokes with <c>confirm: true</c> or
    /// passes the <c>confirmationToken</c> to actually execute. Read-only
    /// tools (<c>bowire.discover</c>, <c>bowire.env.list</c>) bypass
    /// this gate. Set <c>false</c> for fully-autonomous agent runs that
    /// have a higher-level approval layer of their own.
    /// </summary>
    public bool RequireConfirmationForMutations { get; set; } = true;

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
