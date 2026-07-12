// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Mock.Tests;

/// <summary>
/// Serialises every test class that boots a <c>BowireMockHostManager</c> mock
/// host. The manager's port allocator draws from a fixed 5180..5199 window
/// (probe-then-bind), so two classes booting hosts in parallel can race for
/// the same port and intermittently fail with "address already in use".
/// Placing all mock-host-booting classes in one collection with
/// <c>DisableParallelization = true</c> keeps only one manager active at a
/// time, so the window is free at every boot.
/// </summary>
[CollectionDefinition("MockHostSerialised", DisableParallelization = true)]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515:Consider making public types internal", Justification = "xUnit collection definition must be public.")]
public sealed class MockHostSerialisedCollectionDefinition { }
