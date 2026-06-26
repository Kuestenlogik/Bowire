// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Plugins;

namespace Kuestenlogik.Bowire.Ai;

/// <summary>
/// Module contribution for the AI assistant / chat surface (#294 Phase E).
/// </summary>
/// <remarks>
/// First module extracted into a per-package descriptor. Embedded hosts that
/// reference <c>Kuestenlogik.Bowire.Ai</c> get the AI hooks wired into the
/// workbench shell; hosts that don't reference the package don't pay for
/// any of the AI-specific JS render branches because the descriptor never
/// shows up in <c>__BOWIRE_CONFIG__.modules</c>.
/// </remarks>
public sealed class BowireAiModuleContribution : IBowireModuleContribution
{
    public string Id => "ai";
    public string DisplayName => "AI Assistant";
}
