// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Semantics.Extensions;

/// <summary>
/// Marks a class as a Bowire extension — a viewer / editor
/// (<see cref="IBowireUiExtension"/>) or a semantic-kind detector
/// (<see cref="Kuestenlogik.Bowire.Semantics.Detectors.IBowireFieldDetector"/>)
/// that the workbench should auto-discover at startup via assembly scan.
/// The attribute itself carries no metadata; the implementation class
/// supplies id / capabilities / resource names through the
/// implemented contract interface.
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
/// <para>
/// Detector auto-discovery is additive to the manual
/// <c>services.AddSingleton&lt;IBowireFieldDetector, ...&gt;()</c> path:
/// hosts that hand-register a detector continue to work, and both
/// paths land in the same
/// <see cref="Microsoft.Extensions.DependencyInjection.IServiceCollection"/>
/// enumeration. Duplicate instances of the same detector type are
/// suppressed by the registry sweep — a built-in that carries the
/// marker AND is registered explicitly in the DI wiring does not fire
/// twice.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class BowireExtensionAttribute : Attribute
{
}
