// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Tests;

/// <summary>
/// xunit.v3 collection marker that serialises every test class touching
/// the static <see cref="OpenApiUploadStore"/>. Both <c>BowireRestProtocolTests</c>
/// and <c>OpenApiUploadStoreTests</c> add to and clear the same global
/// registry, so without serialisation one class's <c>Clear</c> can race
/// another class's <c>GetAll</c>/<c>Single</c> assertion.
/// </summary>
// xunit1027: collection definition classes must be public so xunit's
// reflection-based discovery can see them. CA1515 prefers internal for
// types not consumed across assemblies — disable it here, the xunit
// analyser wins.
#pragma warning disable CA1515
[CollectionDefinition(nameof(OpenApiUploadStoreTestGroup))]
public sealed class OpenApiUploadStoreTestGroup;
#pragma warning restore CA1515
