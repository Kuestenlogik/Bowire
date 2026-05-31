// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

namespace Kuestenlogik.Bowire.Protocol.WebSocket.Tests;

/// <summary>
/// Cross-class serialisation marker for tests that read or mutate
/// the static <c>BowireWebSocketProtocol.RegisteredEndpoints</c> list.
///
/// <para>
/// Both <c>WebSocketHelperTests</c> and <c>WebSocketProtocolTests</c>
/// touch the same process-wide static — they each call
/// <c>ClearRegisteredEndpoints()</c> on construction and Dispose, then
/// register a known list inside the test body. Without a shared
/// collection, xUnit.v3 runs the two classes in parallel: thread A
/// registers its two endpoints and starts asserting Count == 2, while
/// thread B's constructor fires the Clear that empties the list under
/// it, producing the CI failure <c>Expected: 2 / Actual: 0</c>.
/// </para>
/// </summary>
[CollectionDefinition("RegisteredEndpointsSerialised", DisableParallelization = true)]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1515:Consider making public types internal", Justification = "xUnit collection definition must be public.")]
public sealed class RegisteredEndpointsSerialisedCollectionDefinition { }
