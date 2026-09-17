// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Microsoft.AspNetCore.Http;

namespace Kuestenlogik.Bowire.Mock.Chaos;

/// <summary>
/// How many frames a streaming replay may emit before a fault cuts it
/// short — #170, "drop after N frames".
/// </summary>
/// <remarks>
/// <para>
/// <see cref="FaultRule.PartialBytes"/> caps the response body, which is
/// the right unit for a unary body and the wrong one for a stream: a
/// client that reads events does not care that 1024 bytes arrived, it
/// cares whether it got three events or thirty, and where the cut landed.
/// Counting bytes also cuts an event in half, so what the client sees is
/// a parse error rather than a stream that stopped — a different failure
/// from the one being modelled.
/// </para>
/// <para>
/// The budget travels on <see cref="HttpContext.Items"/> because the
/// decision is made in the handler, before dispatch, and spent in the
/// replayer, which has no other channel to it. Each streaming path knows
/// what one of its own frames is, so each spends the budget itself rather
/// than a wrapper guessing at wire formats.
/// </para>
/// </remarks>
internal sealed class FrameBudget
{
    internal const string ItemKey = "bowire.mock.frameBudget";

    private FrameBudget(int frames, bool abortAtEnd)
    {
        Frames = frames;
        AbortAtEnd = abortAtEnd;
    }

    /// <summary>Frames the replay may still emit.</summary>
    public int Frames { get; }

    /// <summary>
    /// True for <see cref="FaultKind.ConnectionDrop"/>: the socket is
    /// aborted once the budget runs out, rather than the stream ending
    /// cleanly. A client distinguishes the two, and that difference is
    /// usually the thing under test.
    /// </summary>
    public bool AbortAtEnd { get; }

    /// <summary>Attach a budget to this request. <paramref name="frames"/> of 0 or less attaches nothing.</summary>
    public static void Attach(HttpContext ctx, int frames, bool abortAtEnd)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        if (frames <= 0) return;
        ctx.Items[ItemKey] = new FrameBudget(frames, abortAtEnd);
    }

    /// <summary>This request's budget, or null when no fault capped it.</summary>
    public static FrameBudget? From(HttpContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        return ctx.Items.TryGetValue(ItemKey, out var value) ? value as FrameBudget : null;
    }

    /// <summary>
    /// Whether a replay that has already emitted <paramref name="emitted"/>
    /// frames must stop now. Aborts the connection first when the fault
    /// asked for a drop.
    /// </summary>
    /// <remarks>
    /// Called after the frame is written, so the count is what the client
    /// actually received: "drop after 3 frames" delivers three and then
    /// stops, which is what the rule reads like.
    /// </remarks>
    public static bool StopAfter(HttpContext ctx, int emitted)
    {
        var budget = From(ctx);
        if (budget is null || emitted < budget.Frames) return false;

        if (budget.AbortAtEnd) ctx.Abort();
        return true;
    }
}
