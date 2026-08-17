// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Linting;

/// <summary>
/// How serious a schema-lint finding is. Ordered so a caller can gate on a
/// minimum (e.g. fail the check on <see cref="Medium"/> and above).
/// </summary>
public enum BowireLintSeverity
{
    /// <summary>Advisory — a style or consistency nit.</summary>
    Info,

    /// <summary>Low — worth fixing, not urgent (e.g. missing versioning).</summary>
    Low,

    /// <summary>Medium — a design smell with real consequences (e.g. unbounded list).</summary>
    Medium,

    /// <summary>High — a likely defect or exposure (e.g. a secret in a response).</summary>
    High,
}

/// <summary>
/// One design-time issue found in a discovered API surface. Produced by an
/// <see cref="IBowireLintRule"/> and carried by <see cref="BowireSchemaLinter"/>.
/// <paramref name="Method"/> / <paramref name="Field"/> are null when the
/// finding is about the service as a whole.
/// </summary>
public sealed record BowireLintFinding(
    string RuleId,
    BowireLintSeverity Severity,
    string Service,
    string? Method,
    string? Field,
    string Message);
