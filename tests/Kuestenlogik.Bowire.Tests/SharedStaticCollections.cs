// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// The one process-global these tests still share, and the collection that
/// owns it.
/// </summary>
/// <remarks>
/// <para>
/// This replaces a <c>BowireUserContext</c> collection that had thirty
/// classes in it. That one was not really about the user store -- it had
/// become the place anything touching process-global state was put, so
/// unpicking it meant asking, per class, which global it actually shares
/// with which other class.
/// </para>
/// <para>
/// Nearly all of them shared only the user store, which now has a scope of
/// its own and needs no serialisation at all. Two more shared a
/// <em>second</em> way of saying where a file goes -- <c>FlowStore</c> and
/// the workspace inventory each carried a settable path beside a getter that
/// already resolved through the store. Those predate the store being
/// swappable, and with it swappable they said nothing the scope did not.
/// Both are gone, and so are the collections that existed for them.
/// </para>
/// </remarks>
[CollectionDefinition("PluginUpdateCheckDir", DisableParallelization = true)]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515:Consider making public types internal", Justification = "xUnit collection definition must be public.")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "*Collection is the xUnit convention for a collection definition.")]
public sealed class PluginUpdateCheckDirCollection { }
