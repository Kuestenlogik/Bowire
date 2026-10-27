// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Interop;

/// <summary>A source that claims a format and does not parse as it.</summary>
public sealed class InteropFormatException : Exception
{
    public InteropFormatException() { }

    public InteropFormatException(string message) : base(message) { }

    public InteropFormatException(string message, Exception inner) : base(message, inner) { }
}
