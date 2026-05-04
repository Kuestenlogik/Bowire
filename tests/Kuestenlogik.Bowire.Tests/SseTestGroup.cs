// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// xunit.v3 collection marker that serialises every test class
/// touching <c>BowireSseProtocol.RegisteredEndpoints</c>. The four
/// SSE test fixtures (extensions / endpoint discovery / protocol /
/// live subscriber) all clear that static registry in their setup,
/// so without serialisation one class's clear races another class's
/// registration assertion.
/// </summary>
// xunit1027: collection definition classes must be public so xunit's
// reflection-based discovery can see them. CA1515 prefers internal for
// types not consumed across assemblies — disable it here, the xunit
// analyser wins.
#pragma warning disable CA1515
[CollectionDefinition(nameof(SseTestGroup))]
public sealed class SseTestGroup;
#pragma warning restore CA1515
