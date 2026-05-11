// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// Cross-class serialisation marker for tests that mutate process-wide
/// state — current directory (every config-tests class flips it to a
/// per-test temp dir for appsettings.json isolation) and the
/// <c>BOWIRE_PLUGIN_DIR</c> environment variable.
///
/// <para>
/// Bare <c>[Collection("CwdSerialised")]</c> attributes without a
/// matching <see cref="CollectionDefinitionAttribute"/> let xUnit.v3
/// run two instances of the same collection in parallel — which is
/// exactly the race that broke <c>LegacyEnvVar_BindsToConfigKey</c> on
/// CI: one test's ctor cleared the env var while another test's body
/// was reading it. The <c>DisableParallelization = true</c> here makes
/// the serialisation explicit.
/// </para>
/// </summary>
[CollectionDefinition("CwdSerialised", DisableParallelization = true)]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515:Consider making public types internal", Justification = "xUnit collection definition must be public.")]
public sealed class CwdSerialisedCollectionDefinition { }
