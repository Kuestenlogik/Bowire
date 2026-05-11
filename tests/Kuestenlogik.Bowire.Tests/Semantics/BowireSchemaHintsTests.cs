// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0

using Kuestenlogik.Bowire.Models;
using Kuestenlogik.Bowire.Semantics;

namespace Kuestenlogik.Bowire.Tests.Semantics;

public sealed class BowireSchemaHintsTests
{
    [Fact]
    public void Plugins_Without_Hints_Implement_Only_IBowireProtocol()
    {
        // Default state: a regular protocol plugin is just an
        // IBowireProtocol — the IBowireSchemaHints surface is
        // strictly opt-in, no v1.0 plugin is broken by the addition.
        var bare = new BareProtocol();
        Assert.False(bare is IBowireSchemaHints,
            "default protocol must not accidentally satisfy IBowireSchemaHints");
    }

    [Fact]
    public void Plugin_Can_Implement_Both_Interfaces_On_Same_Class()
    {
        var hinted = new HintingProtocol();
        Assert.True(hinted is IBowireSchemaHints);

        // Cast through the interface and pull hints — the call site
        // the host uses inside LayeredAnnotationStore's lambda.
        var hints = ((IBowireSchemaHints)hinted)
            .GetSchemaHints("dis.LiveExercise", "Subscribe")
            .ToList();
        Assert.Single(hints);
        Assert.Equal(BuiltInSemanticTags.CoordinateEcefX, hints[0].Semantic);
        Assert.Equal(AnnotationSource.Plugin, hints[0].Source);
    }

    [Fact]
    public void GetSchemaHints_Returns_Empty_For_Unknown_Service()
    {
        var hinted = new HintingProtocol();
        var hints = ((IBowireSchemaHints)hinted)
            .GetSchemaHints("unknown.service", "AnyMethod");
        Assert.Empty(hints);
    }

    // -------- helpers --------

    private class BareProtocol : IBowireProtocol
    {
        public string Name => "bare";
        public string Id => "bare";
        public string IconSvg => "";
        public Task<List<BowireServiceInfo>> DiscoverAsync(string serverUrl, bool show, CancellationToken ct = default)
            => Task.FromResult(new List<BowireServiceInfo>());
        public Task<InvokeResult> InvokeAsync(string serverUrl, string service, string method,
            List<string> msgs, bool show, Dictionary<string, string>? meta = null, CancellationToken ct = default)
            => Task.FromResult(new InvokeResult(null, 0, "OK", []));
        public async IAsyncEnumerable<string> InvokeStreamAsync(string serverUrl, string service, string method,
            List<string> msgs, bool show, Dictionary<string, string>? meta = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }
        public Task<IBowireChannel?> OpenChannelAsync(string serverUrl, string service, string method,
            bool show, Dictionary<string, string>? meta = null, CancellationToken ct = default)
            => Task.FromResult<IBowireChannel?>(null);
    }

    private sealed class HintingProtocol : BareProtocol, IBowireSchemaHints
    {
        public IEnumerable<Annotation> GetSchemaHints(string serviceId, string methodId)
        {
            if (serviceId == "dis.LiveExercise" && methodId == "Subscribe")
            {
                yield return new Annotation(
                    new("dis.LiveExercise", "Subscribe", "EntityStatePdu", "$.location.x"),
                    BuiltInSemanticTags.CoordinateEcefX,
                    AnnotationSource.Plugin);
            }
        }
    }
}
