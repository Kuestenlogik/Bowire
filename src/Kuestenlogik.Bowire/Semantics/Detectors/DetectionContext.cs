// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Text.Json;

namespace Kuestenlogik.Bowire.Semantics.Detectors;

/// <summary>
/// Addressing context handed to every <see cref="IBowireFieldDetector"/>
/// invocation: identifies which <c>(service, method, message-type)</c>
/// triple this frame belongs to and carries the decoded JSON payload
/// rooted at the frame body.
/// </summary>
/// <remarks>
/// <para>
/// <see langword="readonly"/> <see langword="record"/>
/// <see langword="struct"/> by design — value-equality is unused but
/// the by-value semantics keep the type cheap to construct on the hot
/// path. <see cref="JsonElement"/> is itself a struct that carries a
/// reference to the shared <see cref="JsonDocument"/> arena, so passing
/// the whole context by <see langword="in"/> avoids copying anything
/// beyond a handful of references.
/// </para>
/// <para>
/// In Phase 2 the stream hook fills <see cref="MessageType"/> with
/// <see cref="AnnotationKey.Wildcard"/> because plugin-side
/// discriminator wiring isn't in place yet. Phase 3 + later phases
/// will plumb the real discriminator value through.
/// </para>
/// </remarks>
/// <param name="ServiceId">
/// Plugin-defined service identifier — same shape as
/// <see cref="AnnotationKey.ServiceId"/>.
/// </param>
/// <param name="MethodId">
/// Plugin-defined method identifier within
/// <paramref name="ServiceId"/>.
/// </param>
/// <param name="MessageType">
/// Discriminator value, or <see cref="AnnotationKey.Wildcard"/> for
/// single-type methods.
/// </param>
/// <param name="Frame">
/// Decoded JSON payload rooted at the frame body. Detectors receive
/// the whole tree so paired-field rules
/// (<c>lat</c>+<c>lon</c> at the same parent) can match naturally.
/// </param>
public readonly record struct DetectionContext(
    string ServiceId,
    string MethodId,
    string MessageType,
    JsonElement Frame);
