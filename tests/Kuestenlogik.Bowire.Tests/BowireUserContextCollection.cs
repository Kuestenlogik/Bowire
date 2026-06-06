// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// Forces serial execution for tests that mutate the process-wide
/// <c>BowireUserContext.Current</c> static. The seam stores its
/// resolver in a single field; tests that swap it for a temp-store
/// during their fixture race against each other when xUnit runs
/// them in parallel and one side reads a context the other side just
/// replaced. Tagging every BowireUserContext-touching class with
/// <c>[Collection("BowireUserContext")]</c> moves them into this
/// collection and disables parallelisation across the set.
/// </summary>
[CollectionDefinition("BowireUserContext", DisableParallelization = true)]
#pragma warning disable CA1515, CA1711 // xUnit requires the collection-definition class to be public; the *Collection suffix is the xUnit convention.
public sealed class BowireUserContextCollection
#pragma warning restore CA1515, CA1711
{
}
