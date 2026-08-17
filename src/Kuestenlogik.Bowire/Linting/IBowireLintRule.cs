// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Models;

namespace Kuestenlogik.Bowire.Linting;

/// <summary>
/// A single design-time rule over a discovered service. Rules are the
/// extension seam of the linter: a new check is a new <see cref="IBowireLintRule"/>
/// registered with <see cref="BowireSchemaLinter"/>. A rule inspects one
/// service in isolation and yields zero or more <see cref="BowireLintFinding"/>.
/// </summary>
public interface IBowireLintRule
{
    /// <summary>Stable machine id, e.g. <c>BWR-LINT-MISSING-PAGINATION</c>.</summary>
    string Id { get; }

    /// <summary>One-line human title for reports.</summary>
    string Title { get; }

    /// <summary>The severity every finding from this rule carries.</summary>
    BowireLintSeverity Severity { get; }

    /// <summary>Inspect one service and yield any findings.</summary>
    IEnumerable<BowireLintFinding> Inspect(BowireServiceInfo service);
}
