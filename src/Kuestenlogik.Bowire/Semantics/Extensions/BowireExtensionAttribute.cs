// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Semantics.Extensions;

/// <summary>
/// Marks a class as a Bowire extension — a viewer / editor / detector that
/// the workbench should auto-discover at startup via assembly scan. The
/// attribute itself carries no metadata; the implementation class supplies
/// id / capabilities / resource names through the
/// <see cref="IBowireUiExtension"/> contract (or a future
/// <c>IBowireFieldDetector</c> sibling).
/// </summary>
/// <remarks>
/// <para>
/// Mirrors the existing <c>IBowireProtocol</c> discovery pattern: a host
/// scans every loaded <c>Kuestenlogik.Bowire*</c> assembly, instantiates
/// every type tagged with <see cref="BowireExtensionAttribute"/> that
/// implements one of the supported extension interfaces, and registers it
/// with the framework. Plugin authors don't need to wire anything into
/// DI — dropping the package next to the host is enough.
/// </para>
/// <para>
/// A single nupkg can ship multiple <c>[BowireExtension]</c> types when
/// they belong together (e.g. a MIL-symbol package shipping both a
/// detector and a viewer extension). Each type is independently
/// instantiated and registered.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class BowireExtensionAttribute : Attribute
{
}
