// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Tests.Endpoints;

/// <summary>
/// Serialisation marker for endpoint tests that drive process-global static
/// state — the protocol registry (<c>BowireEndpointHelpers.SetRegistry</c> /
/// <c>ResetRegistry</c>) and the duplex <c>ChannelStore</c>. Two classes
/// mutating either in parallel would clobber each other, and a registry swap
/// could also race any other test that calls <c>GetRegistry()</c>.
/// <c>DisableParallelization = true</c> keeps the collection off the parallel
/// phase entirely, so it never overlaps another collection.
/// </summary>
[CollectionDefinition("StaticEndpointState", DisableParallelization = true)]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515:Consider making public types internal", Justification = "xUnit collection definition must be public.")]
public sealed class StaticEndpointStateCollectionDefinition;
