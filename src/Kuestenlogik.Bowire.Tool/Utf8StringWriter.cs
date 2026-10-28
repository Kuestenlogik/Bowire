// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;

namespace Kuestenlogik.Bowire.App;

/// <summary>
/// A <see cref="StringWriter"/> that reports UTF-8. An <c>XmlWriter</c> takes
/// the encoding for its declaration from the writer, not from its settings,
/// so over a plain StringWriter it declares <c>utf-16</c> — while the text is
/// written to disk as UTF-8, and a strict parser refuses the file.
/// </summary>
internal sealed class Utf8StringWriter(StringBuilder sb) : StringWriter(sb, CultureInfo.InvariantCulture)
{
    public override Encoding Encoding => new UTF8Encoding(false);
}
