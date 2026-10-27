// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Interop.V1;

namespace Kuestenlogik.Bowire.Interop.Formats;

/// <summary>One source format <see cref="InteropRpc.ImportCollection"/> reads.</summary>
internal interface IInteropImporter
{
    /// <summary>The id a caller passes as <c>format</c>.</summary>
    string Id { get; }

    /// <summary>Whether <paramref name="files"/> look like this format. Cheap; no full parse.</summary>
    bool CanRead(IReadOnlyList<SourceFile> files);

    /// <summary>
    /// Read <paramref name="files"/>. Everything that does not come across as
    /// it was goes into <paramref name="report"/>; nothing is dropped silently.
    /// </summary>
    /// <exception cref="InteropFormatException">The files claim the format but do not parse.</exception>
    ImportCollectionResponse Read(IReadOnlyList<SourceFile> files, string? name, ReportBuilder report);
}

/// <summary>The two .http dialects that differ where it matters.</summary>
internal enum HttpDialect
{
    /// <summary>JetBrains HTTP Client: Rider, IntelliJ, and Visual Studio 2022.</summary>
    JetBrains,

    /// <summary>VS Code REST Client.</summary>
    VsCode,
}
