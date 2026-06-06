// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.IntegrationTests;

/// <summary>
/// Mirror of the main-suite <c>BowireUserContextCollection</c> in this
/// project's namespace — collection definitions are scoped per test
/// assembly, so the AI integration tests that swap
/// <c>BowireUserContext.Current</c> need their own copy.
/// </summary>
[CollectionDefinition("BowireUserContext", DisableParallelization = true)]
#pragma warning disable CA1711 // *Collection suffix is the xUnit convention for collection-definition classes.
public sealed class BowireUserContextCollection
#pragma warning restore CA1711
{
}
