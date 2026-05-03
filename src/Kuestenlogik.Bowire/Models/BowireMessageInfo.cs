// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Models;

/// <summary>
/// Describes a protobuf message type with its fields.
/// </summary>
public sealed record BowireMessageInfo(
    string Name,
    string FullName,
    List<BowireFieldInfo> Fields);
