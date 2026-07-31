// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Models;

namespace Kuestenlogik.Bowire;

/// <summary>
/// Optional capability surface for protocol plugins whose probe can
/// half-succeed (#544): "here are the services I found, AND here is what
/// broke while I found them".
/// <para>
/// <see cref="IBowireProtocol.DiscoverAsync"/> has no channel for that — a
/// plugin either returns a list or throws, so a partially-faulted probe had
/// to hide either the fault or the results. The MCP plugin is the canonical
/// case: an MCP server with a single malformed tool used to suppress its
/// perfectly good resources and prompts as well, because the throw was the
/// only way to make the fault visible.
/// </para>
/// <para>
/// Discovered exactly the way <see cref="IInlineHttpInvoker"/> is — core
/// does <c>protocol is IBowireDiscoveryDiagnostics</c> on the instance it
/// was going to call anyway. A plugin that does not implement this behaves
/// exactly as before, and third-party plugins keep compiling: nothing on
/// <see cref="IBowireProtocol"/> changed.
/// </para>
/// </summary>
/// <remarks>
/// <para>
/// <strong>Build the diagnostic from locals of the call that produced
/// it.</strong> The channel is a return value on purpose: it is bound to
/// its invocation by the call stack, so two concurrent probes of two URLs
/// through the same plugin instance cannot read each other's diagnosis.
/// A plugin that instead stashes the diagnostic in an instance field, a
/// static ring buffer or an <c>AsyncLocal</c> and lets core come back for
/// it re-creates precisely the cross-call bleed this seam exists to remove.
/// </para>
/// <para>
/// <see cref="BowireDiscoveryProbe"/> owns the outcome vocabulary. The
/// plugin states a <see cref="BowireDiscoverySeverity"/>; the probe pairs
/// it with the number of services that came back and picks
/// <c>ok</c> / <c>empty</c> / <c>partial</c> / <c>error</c> from that.
/// </para>
/// <para>
/// Throwing still works and is still the stronger signal: an exception is
/// classified as <c>error</c> (or <c>timeout</c>) with its own message,
/// which is strictly more than a diagnostic reported just before it would
/// have been. A diagnostic is for the case where there is something to
/// return.
/// </para>
/// </remarks>
public interface IBowireDiscoveryDiagnostics
{
    /// <summary>
    /// Same probe as <see cref="IBowireProtocol.DiscoverAsync"/>, but able
    /// to report a fault without swallowing the services it did find.
    /// </summary>
    /// <param name="serverUrl">The bare target URL, as DiscoverAsync gets it.</param>
    /// <param name="showInternalServices">Forwarded verbatim from the probe.</param>
    /// <param name="ct">Caller cancellation, linked into the probe ceiling.</param>
    Task<BowireDiscoveryReport> DiscoverWithDiagnosticsAsync(
        string serverUrl, bool showInternalServices, CancellationToken ct = default);
}

/// <summary>
/// One <see cref="IBowireDiscoveryDiagnostics.DiscoverWithDiagnosticsAsync"/>
/// pass: what the plugin found, and — optionally — what went wrong while it
/// was finding it.
/// </summary>
/// <param name="Services">
/// Exactly what <see cref="IBowireProtocol.DiscoverAsync"/> would have
/// returned. Never <see langword="null"/>; an empty list is fine.
/// </param>
/// <param name="Diagnostic">
/// <see langword="null"/> when the probe has nothing to add — which is the
/// common case and keeps the attempt reading exactly as it does today.
/// </param>
public sealed record BowireDiscoveryReport(
    List<BowireServiceInfo> Services,
    BowireDiscoveryDiagnostic? Diagnostic);
