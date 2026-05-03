// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Mock.Loading;

/// <summary>
/// Recording-format version constants and a compatibility check for the mock
/// server. Bumped when later phases add fields the mock actually depends on
/// at load time.
/// </summary>
public static class RecordingFormatVersion
{
    /// <summary>Phase-1a format — REST unary (httpPath / httpVerb).</summary>
    public const int V1 = 1;

    /// <summary>
    /// Phase-1b format — adds <c>responseBinary</c> for gRPC unary replay.
    /// Steps without <c>responseBinary</c> still replay as v1 semantics, so
    /// v2 recordings are a strict superset of v1.
    /// </summary>
    public const int V2 = 2;

    /// <summary>The highest version understood by this mock build.</summary>
    public const int Current = V2;

    /// <summary>
    /// Returns <c>true</c> when the supplied version can be replayed by this
    /// mock build, <c>false</c> when it's from the future (bump required) or
    /// malformed.
    /// </summary>
    public static bool IsSupported(int? version) => version is V1 or V2;
}
