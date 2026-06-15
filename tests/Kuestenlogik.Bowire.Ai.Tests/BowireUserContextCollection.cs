// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Ai.Tests;

/// <summary>
/// Serial-execution gate for tests that swap the process-wide
/// <c>BowireUserContext.Current</c> static. Identical pattern to the
/// Kuestenlogik.Bowire.Tests collection: xUnit defaults to running
/// classes in parallel, and the static seam means one fixture's
/// temp-store would race the next fixture's path lookups. Tagging
/// classes with <c>[Collection("BowireUserContext")]</c> moves them
/// here and disables parallelism across the set.
/// </summary>
[CollectionDefinition("BowireUserContext", DisableParallelization = true)]
#pragma warning disable CA1515, CA1711 // xUnit requires the collection-definition class to be public; the *Collection suffix is xUnit's convention.
public sealed class BowireUserContextCollection
#pragma warning restore CA1515, CA1711
{
}
