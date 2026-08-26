// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.IntegrationTests;

/// <summary>
/// For suites that swap the process-wide protocol registry
/// (<c>BowireEndpointHelpers.SetRegistry</c>).
/// </summary>
/// <remarks>
/// <para>
/// The registry behind <c>GetRegistry()</c> is one static for the whole
/// process, and several suites here install their own — directly, or as a side
/// effect of mapping the full API. A suite that registers a stub plugin and
/// then asserts on what that stub received is therefore only correct while
/// nothing else is running: another collection installing its registry
/// mid-test makes the request dispatch to a different plugin, and the
/// assertion fails somewhere far from the cause.
/// </para>
/// <para>
/// <c>DisableParallelization</c> is what actually prevents that — it keeps this
/// collection from running alongside any other. The <c>[Collection]</c>
/// attribute on its own only groups classes for a shared fixture.
/// </para>
/// </remarks>
[CollectionDefinition("BowireProtocolRegistry", DisableParallelization = true)]
#pragma warning disable CA1711 // *Collection suffix is the xUnit convention for collection-definition classes.
public sealed class BowireProtocolRegistryCollection
#pragma warning restore CA1711
{
}
