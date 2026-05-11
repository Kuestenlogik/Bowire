// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;

namespace Kuestenlogik.Bowire.Semantics.Detectors;

/// <summary>
/// Default <see cref="IFrameProber"/> implementation — composes every
/// registered <see cref="IBowireFieldDetector"/> with the
/// <see cref="LayeredAnnotationStore.AutoDetectorLayer"/> and tracks
/// the set of probed triples in a
/// <see cref="ConcurrentDictionary{TKey, TValue}"/>.
/// </summary>
/// <remarks>
/// <para>
/// Singleton-scoped in the DI container — only one prober per host,
/// so the triple-set is shared across every concurrent stream
/// subscription. The set carries triples as a
/// <see cref="ValueTuple{T1, T2, T3}"/> so the dictionary's
/// <see cref="EqualityComparer{T}.Default"/> already does the right
/// thing.
/// </para>
/// </remarks>
public sealed class FrameProber : IFrameProber
{
    private readonly IReadOnlyList<IBowireFieldDetector> _detectors;
    private readonly InMemoryAnnotationLayer _autoLayer;
    private readonly ConcurrentDictionary<(string, string, string), byte> _probed = new();

    /// <summary>
    /// Construct a frame prober from an explicit detector set and
    /// the auto-detector layer it should write to. Used by the DI
    /// wiring inside
    /// <see cref="BowireServiceCollectionExtensions.AddBowire(Microsoft.Extensions.DependencyInjection.IServiceCollection, System.Action{BowireOptions})"/>
    /// but kept public so embedded hosts can wire a custom store.
    /// </summary>
    /// <param name="detectors">
    /// Every registered <see cref="IBowireFieldDetector"/>. May be
    /// empty — the prober still tracks novel triples (so an
    /// already-probed triple stays a single dictionary lookup) but
    /// produces no annotations.
    /// </param>
    /// <param name="autoLayer">
    /// The store's <see cref="LayeredAnnotationStore.AutoDetectorLayer"/>.
    /// Detector proposals land here; resolver priority does the rest.
    /// </param>
    public FrameProber(
        IEnumerable<IBowireFieldDetector> detectors,
        InMemoryAnnotationLayer autoLayer)
    {
        ArgumentNullException.ThrowIfNull(detectors);
        ArgumentNullException.ThrowIfNull(autoLayer);
        _detectors = [.. detectors];
        _autoLayer = autoLayer;
    }

    /// <summary>
    /// Number of distinct <c>(serviceId, methodId, messageType)</c>
    /// triples the prober has already classified. Exposed for tests
    /// + monitoring — never feeds back into the hot path.
    /// </summary>
    public int ProbedTripleCount => _probed.Count;

    /// <inheritdoc/>
    public void ObserveFrame(in DetectionContext ctx)
    {
        var triple = (ctx.ServiceId, ctx.MethodId, ctx.MessageType);

        // TryAdd returns false when the triple was already present.
        // The atomic-set-once primitive is what gives us the
        // "single dictionary lookup on the repeat path" property the
        // hot stream loop needs.
        if (!_probed.TryAdd(triple, 0)) return;

        if (_detectors.Count == 0) return;

        foreach (var detector in _detectors)
        {
            // A misbehaving detector throws on a particular frame
            // shape — we don't want one rogue extension to break
            // streaming. Swallow + continue; the other detectors
            // still get their pass.
            IEnumerable<DetectionResult>? results;
            try
            {
                results = detector.Detect(ctx);
            }
            catch
            {
                continue;
            }

            if (results is null) continue;
            foreach (var result in results)
            {
                _autoLayer.Set(result.Key, result.Semantic);
            }
        }
    }
}
