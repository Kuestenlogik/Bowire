// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.IntegrationTests;

/// <summary>
/// For suites that repoint where Bowire stores things — <c>BowirePaths.Current</c>
/// or <c>BowireUserContext.Current</c>.
/// </summary>
/// <remarks>
/// <para>
/// The two are not independent: the default path resolver reads its root from
/// whatever user store is current, so a suite swapping only the user store
/// moves the plugin directory of a suite that swapped only the resolver. Each
/// restores what it found, which is correct on its own and wrong when two run
/// at once — one restores a value the other had already replaced.
/// </para>
/// <para>
/// One serialised collection for all of them is the fix that does not depend
/// on which pair happens to race. <c>DisableParallelization</c> is the part
/// that does the work; the attribute alone only groups classes.
/// </para>
/// </remarks>
[CollectionDefinition("BowireStorageRoot", DisableParallelization = true)]
#pragma warning disable CA1711 // *Collection suffix is the xUnit convention for collection-definition classes.
public sealed class BowireStorageRootCollection
#pragma warning restore CA1711
{
}
