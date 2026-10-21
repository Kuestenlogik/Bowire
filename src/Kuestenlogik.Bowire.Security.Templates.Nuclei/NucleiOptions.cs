// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Security.Templates.Nuclei;

/// <summary>
/// Limits applied while a Nuclei template is resolved against a target.
/// Bound from the <see cref="SectionName"/> configuration section by
/// <c>bowire scan</c>; an embedded host sets it on the scan options or
/// passes it to <see cref="NucleiVariableContext.FromTarget"/>.
/// </summary>
public sealed class NucleiOptions
{
    /// <summary>Configuration section the options bind from.</summary>
    public const string SectionName = "Bowire:Security:Nuclei";

    /// <summary>
    /// Default for <see cref="MaxRandomLength"/>: 1 MiB of random
    /// characters. Real templates use lengths in the tens; this leaves room
    /// for a deliberate large-payload probe while capping what one template
    /// can allocate at about 2 MB.
    /// </summary>
    public const int DefaultMaxRandomLength = 1_048_576;

    private int _maxRandomLength = DefaultMaxRandomLength;

    /// <summary>
    /// The most random characters one template may generate through
    /// <c>{{RandStr_N}}</c> / <c>{{RandInt_N}}</c>, summed over its distinct
    /// placeholders (a memoised repeat costs nothing). A template is
    /// untrusted input and <c>{{RandStr_2000000000}}</c> is a valid int
    /// (#881); a placeholder that would exceed the budget is left
    /// unresolved and recorded in
    /// <see cref="NucleiVariableContext.UnresolvedRandomPlaceholders"/>.
    /// </summary>
    public int MaxRandomLength
    {
        get => _maxRandomLength;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _maxRandomLength = value;
        }
    }
}
