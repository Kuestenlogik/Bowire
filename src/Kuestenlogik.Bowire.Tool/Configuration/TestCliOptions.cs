// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.App.Configuration;

/// <summary>
/// Typed configuration for <c>bowire test</c>. Bound from the
/// <c>Bowire:Test</c> section of the shared configuration stack.
/// The collection-file path is positional, not a key — extracted
/// separately by <c>BowireCli</c>.
/// </summary>
/// <remarks>
/// <para>
/// Config shape:
/// </para>
/// <code>
/// {
///   "Bowire": {
///     "Test": {
///       "ReportPath": "./test-report.html"
///     }
///   }
/// }
/// </code>
/// </remarks>
internal sealed class TestCliOptions
{
    /// <summary>Path to the test-collection JSON file (positional arg).</summary>
    public string? CollectionPath { get; set; }

    /// <summary>Optional HTML report output (<c>--report</c>).</summary>
    public string? ReportPath { get; set; }

    /// <summary>
    /// Optional JUnit XML report output (<c>--junit</c>). The format is the
    /// de-facto CI standard consumed natively by Jenkins, GitLab CI, Azure
    /// DevOps, and GitHub Actions test reporters; complementary to the
    /// human-readable <see cref="ReportPath"/>.
    /// </summary>
    public string? JUnitPath { get; set; }
}
