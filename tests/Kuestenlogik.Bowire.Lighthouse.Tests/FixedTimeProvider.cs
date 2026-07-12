// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Lighthouse.Tests;

/// <summary>
/// A <see cref="TimeProvider"/> whose clock is fixed at construction — enough
/// for the runner + scheduler tests, which read <see cref="GetUtcNow"/> for
/// outcome timestamps and schedule maths but never wait on a real timer (the
/// tested paths use a zero delay or cancel before any wait).
/// </summary>
internal sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}
