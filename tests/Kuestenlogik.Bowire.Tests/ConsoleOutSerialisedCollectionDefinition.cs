// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// Cross-class serialisation marker for tests that read or mutate the
/// process-wide <see cref="Console.Out"/> writer.
///
/// <para>
/// <c>BowireCliTests</c> and the <c>ShowHelp</c> probe in
/// <c>PluginManagerTests</c> swap in a <see cref="StringWriter"/> via
/// <see cref="Console.SetOut(TextWriter)"/> inside a <c>using</c> block,
/// while <c>CliHandlerHelpersTests</c> drives the formatter helpers
/// (<c>Write</c> → <c>Console.WriteLine</c>) directly without redirecting.
/// Without a shared collection, xUnit.v3 runs these classes in parallel
/// across threads. The race that fell out: thread A captured
/// <c>Console.Out</c>, started a <c>WriteLine</c> against it, while
/// thread B finished its <c>using</c> scope and disposed the
/// underlying <c>StringWriter</c> mid-call — <c>ObjectDisposedException:
/// Cannot write to a closed TextWriter</c>.
/// </para>
/// </summary>
[CollectionDefinition("ConsoleOutSerialised", DisableParallelization = true)]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515:Consider making public types internal", Justification = "xUnit collection definition must be public.")]
public sealed class ConsoleOutSerialisedCollectionDefinition { }
