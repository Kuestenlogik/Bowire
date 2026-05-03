// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Models;

/// <summary>
/// Describes a single value within a protobuf enum type.
/// </summary>
public sealed record BowireEnumValue(
    string Name,
    int Number);
