    // ---- #131 Benchmarks rail-mode — Envelope model ----
    //
    // First-class home for performance probing. Specs are now
    // 'envelopes': a reusable {targets[], phases[], mode} bundle
    // that decouples WHAT to invoke (targets) from HOW OFTEN +
    // HOW PARALLEL (phases). One envelope can carry:
    //   - 1 method (legacy 'single' shape)
    //   - a saved collection (legacy 'collection' shape)
    //   - a saved recording (legacy 'recording' shape)
    //   - a free mix of any of the above, replayed sequentially or
    //     in parallel per VU iteration
    //
    // Phases follow the Artillery / k6 stages model so that import /
    // export to those formats is round-trippable later (Phase 4 of
    // the rollout). Phase shapes supported in v1:
    //   { vus, totalIterations, durationMs }      ← legacy n×K
    //   { vus, durationMs }                       ← hold for time
    //   { vus, rampToVus, durationMs }            ← linear ramp
    //   { arrivalRate, durationMs }               ← Artillery-style
    //
    // Specs live per-workspace in localStorage under wsKey
    // ('bowire_benchmarks'). Old format ({kind, service, method, n,
    // concurrency, sourceId, ...}) is migrated in place on first
    // load — see migrateEnvelope() below. Legacy mirror fields
    // (kind, service, method, body, metadata, protocol, sourceId,
    // n, concurrency) stay on the envelope so the existing detail
    // pane keeps rendering during the Phase 1 rollout; Phase 2
    // rewrites the detail pane and drops the mirror.

    const BENCHMARKS_KEY = 'bowire_benchmarks';

    // Default-phase factory — a single fixed n × K stage so the
    // simplest case ("100 calls at concurrency 4") just sets the
    // first phase's scalar fields.
    function defaultEnvelopePhase(seed) {
        seed = seed || {};
        return {
            vus: Math.max(1, parseInt(seed.vus, 10) || parseInt(seed.concurrency, 10) || 4),
            totalIterations: seed.totalIterations !== undefined
                ? Math.max(1, parseInt(seed.totalIterations, 10) || 1)
                : Math.max(1, parseInt(seed.n, 10) || 100),
            rampToVus: null,
            arrivalRate: null,
            durationMs: null
        };
    }

    // Build a target from a legacy seed. Seed shapes:
    //   { kind: 'single', service, method, body, metadata, protocol }
    //   { kind: 'collection', sourceId }
    //   { kind: 'recording', sourceId }
    function targetFromLegacySeed(seed) {
        seed = seed || {};
        if (seed.kind === 'collection') {
            return { type: 'collection-ref', collectionId: seed.sourceId || null, itemIndex: null };
        }
        if (seed.kind === 'recording') {
            return { type: 'recording-ref', recordingId: seed.sourceId || null, stepIndex: null };
        }
        return {
            type: 'method',
            service: seed.service || (typeof selectedService !== 'undefined' && selectedService ? selectedService.name : ''),
            method: seed.method || (typeof selectedMethod !== 'undefined' && selectedMethod ? selectedMethod.name : ''),
            protocol: seed.protocol
                || (typeof selectedService !== 'undefined' && selectedService
                    ? (selectedService.source || (typeof selectedProtocol !== 'undefined' ? selectedProtocol : null))
                    : null),
            body: seed.body || '{}',
            metadata: seed.metadata || {},
            serverUrl: null
        };
    }

    // Re-derive legacy mirror fields (kind/service/method/.../n/K)
    // from targets[0] and phases[0]. Called whenever a new envelope
    // is built or migrated so the existing detail pane (which reads
    // these fields directly) keeps working unchanged.
    function syncEnvelopeLegacyFields(spec) {
        if (!spec) return spec;
        var t0 = (spec.targets && spec.targets[0]) || null;
        var p0 = (spec.phases && spec.phases[0]) || null;
        if (t0) {
            if (t0.type === 'collection-ref') {
                spec.kind = 'collection';
                spec.sourceId = t0.collectionId;
                spec.service = ''; spec.method = '';
                spec.body = '{}'; spec.metadata = {};
                spec.protocol = null;
            } else if (t0.type === 'recording-ref') {
                spec.kind = 'recording';
                spec.sourceId = t0.recordingId;
                spec.service = ''; spec.method = '';
                spec.body = '{}'; spec.metadata = {};
                spec.protocol = null;
            } else {
                spec.kind = 'single';
                spec.service = t0.service || '';
                spec.method = t0.method || '';
                spec.body = t0.body || '{}';
                spec.metadata = t0.metadata || {};
                spec.protocol = t0.protocol || null;
                spec.sourceId = null;
            }
        }
        if (p0) {
            spec.n = p0.totalIterations || spec.n || 100;
            spec.concurrency = p0.vus || spec.concurrency || 4;
        }
        return spec;
    }

    // One-shot upgrade for specs persisted in the pre-envelope
    // shape. Idempotent — re-running on a v2 spec is a no-op.
    function migrateEnvelope(spec) {
        if (!spec) return spec;
        if (Array.isArray(spec.targets) && Array.isArray(spec.phases)) {
            // Already v2 envelope shape. The runs[] history (#233) is
            // a forward-compat addition — bring it up to date once so
            // a spec that has only `lastRun` from a previous build
            // contributes that run as the first history entry. This is
            // a one-shot bring-forward; if the operator never ran the
            // spec on the new build runs[] simply stays empty.
            if (!Array.isArray(spec.runs)) {
                spec.runs = spec.lastRun ? [spec.lastRun] : [];
            }
            return spec;
        }
        spec.targets = [targetFromLegacySeed(spec)];
        spec.phases = [defaultEnvelopePhase({
            vus: spec.concurrency,
            totalIterations: spec.n
        })];
        spec.mode = spec.mode || 'sequential';
        if (!Array.isArray(spec.runs)) {
            spec.runs = spec.lastRun ? [spec.lastRun] : [];
        }
        return syncEnvelopeLegacyFields(spec);
    }

    function loadBenchmarks() {
        // Lazy-hydrate from localStorage on first access. After that
        // benchmarksList stays in memory; persistBenchmarks() writes
        // back on every mutation. Per-workspace scope via wsKey().
        if (Array.isArray(benchmarksList)) return benchmarksList;
        try {
            var raw = localStorage.getItem(wsKey(BENCHMARKS_KEY));
            benchmarksList = raw ? JSON.parse(raw) : [];
            if (!Array.isArray(benchmarksList)) benchmarksList = [];
        } catch {
            benchmarksList = [];
        }
        // One-shot upgrade of pre-envelope specs. migrateEnvelope is
        // idempotent so re-running on already-v2 entries is a no-op.
        var dirty = false;
        for (var i = 0; i < benchmarksList.length; i++) {
            var before = !!(benchmarksList[i].targets && benchmarksList[i].phases);
            migrateEnvelope(benchmarksList[i]);
            if (!before) dirty = true;
        }
        if (dirty) {
            try {
                localStorage.setItem(wsKey(BENCHMARKS_KEY), JSON.stringify(benchmarksList));
            } catch (e) {
                console.warn('[bowire] failed to persist migrated benchmarks', e);
            }
        }
        return benchmarksList;
    }

    // Reflect detail-pane edits to legacy mirror fields (spec.n,
    // spec.concurrency, spec.service, …) back into the v2 envelope
    // shape (phases[0], targets[0]) before serialising. Keeps the
    // two surfaces in sync without invasive changes to every input
    // handler in the detail pane.
    function reflectLegacyEditsBack(spec) {
        if (!spec || !Array.isArray(spec.phases) || !Array.isArray(spec.targets)) return;
        if (spec.phases[0]) {
            if (typeof spec.n === 'number') spec.phases[0].totalIterations = spec.n;
            if (typeof spec.concurrency === 'number') spec.phases[0].vus = spec.concurrency;
        }
        if (spec.targets[0]) {
            var t0 = spec.targets[0];
            if (t0.type === 'method') {
                if (spec.service !== undefined) t0.service = spec.service;
                if (spec.method !== undefined) t0.method = spec.method;
                if (spec.body !== undefined) t0.body = spec.body;
                if (spec.metadata !== undefined) t0.metadata = spec.metadata;
                if (spec.protocol !== undefined) t0.protocol = spec.protocol;
            } else if (t0.type === 'collection-ref' && spec.sourceId !== undefined) {
                t0.collectionId = spec.sourceId;
            } else if (t0.type === 'recording-ref' && spec.sourceId !== undefined) {
                t0.recordingId = spec.sourceId;
            }
        }
    }

    function persistBenchmarks() {
        if (Array.isArray(benchmarksList)) {
            benchmarksList.forEach(reflectLegacyEditsBack);
        }
        try {
            localStorage.setItem(wsKey(BENCHMARKS_KEY), JSON.stringify(benchmarksList || []));
            markSaved('Benchmarks');
        } catch (e) {
            console.warn('[bowire] failed to persist benchmarks', e);
        }
    }

    function makeBenchmarkId() {
        // Same shape as elsewhere — short random suffix is enough since
        // collisions inside one workspace's benchmark list are
        // astronomically unlikely for human-curated entries.
        return 'bench_' + Math.random().toString(36).slice(2, 10);
    }

    function createBenchmarkSpec(seed) {
        loadBenchmarks();
        seed = seed || {};
        var kind = seed.kind || 'single';
        var defaultName = kind === 'collection' ? 'Benchmark collection'
                        : kind === 'recording' ? 'Benchmark recording'
                        : 'New benchmark';

        // Envelope-shape (v2). Legacy mirror fields (kind, service,
        // method, body, metadata, protocol, sourceId, n, concurrency,
        // serverUrl) are filled by syncEnvelopeLegacyFields below
        // so the existing detail-pane code keeps reading them
        // directly. Phase 2 of the rollout swaps the detail pane to
        // the new shape and drops the mirror.
        var spec = {
            id: makeBenchmarkId(),
            name: seed.name || defaultName,

            // WHAT to invoke per VU iteration. Multiple targets are
            // walked in order when mode === 'sequential' or fired
            // concurrently when mode === 'parallel'.
            targets: Array.isArray(seed.targets) && seed.targets.length > 0
                ? seed.targets.slice()
                : [targetFromLegacySeed(seed)],

            // HOW OFTEN × HOW PARALLEL. Each phase advances when
            // either totalIterations is reached OR durationMs elapses.
            phases: Array.isArray(seed.phases) && seed.phases.length > 0
                ? seed.phases.map(defaultEnvelopePhase)
                : [defaultEnvelopePhase({
                    vus: seed.concurrency,
                    totalIterations: seed.n
                })],

            mode: seed.mode || 'sequential',

            createdAt: 0,
            lastRun: null,
            // #233 — runs history. Bounded ring; cap honoured at write
            // time in runBenchmarkSpec(). The diff banner needs at
            // least two entries to render, so it stays hidden on a
            // fresh spec until the second run finishes.
            runs: []
        };
        syncEnvelopeLegacyFields(spec);

        benchmarksList.push(spec);
        persistBenchmarks();
        return spec;
    }

    function deleteBenchmarkSpec(id) {
        loadBenchmarks();
        var idx = benchmarksList.findIndex(function (s) { return s.id === id; });
        if (idx < 0) return;
        benchmarksList.splice(idx, 1);
        if (benchmarksSelectedId === id) benchmarksSelectedId = null;
        persistBenchmarks();
    }

    function getBenchmarkSpec(id) {
        loadBenchmarks();
        return benchmarksList.find(function (s) { return s.id === id; }) || null;
    }

    // ---- #231 'random' target shape ----
    //
    // A 'random' target carries a POOL spec + a per-iteration pick
    // count. On each VU iteration the runner expands the pool into a
    // concrete list of sub-targets (one of: discovered methods, items
    // of a saved collection, or steps of a saved recording), shuffles
    // the list, and picks the first N. The picked sub-targets are
    // dispatched sequentially within the iteration; iteration-level
    // pass = all sub-picks pass, durationMs = sum.
    //
    // Shape: { type: 'random', pool: 'workspace' | 'collection' | 'recording',
    //          collectionId?, recordingId?, count }
    //
    // - pool === 'workspace': picks from every method on every
    //   discovered service. Useful for fuzz / chaos load (#231 main
    //   acceptance — "anything reachable").
    // - pool === 'collection': picks from a saved collection's items.
    //   Each item is treated as a method target.
    // - pool === 'recording': picks from a saved recording's steps.
    //   Each step is treated as a method target.
    //
    // The pool is recomputed each iteration so newly-discovered
    // services / new collection items / new recording steps come in
    // without a benchmark-restart. Count defaults to 1.

    // Resolve a 'random' target into its concrete sub-target list.
    // Returns method-shaped sub-targets (so _invokeBenchmarkTarget can
    // re-enter without special-casing). Empty list ⇒ pool drained or
    // pool source missing; caller surfaces NoPool / MissingPool.
    function _expandRandomPool(target) {
        if (!target) return [];
        var pool = target.pool || 'workspace';
        var out = [];
        if (pool === 'workspace') {
            var svcs = (typeof services !== 'undefined' ? services : []);
            svcs.forEach(function (svc) {
                var methods = (svc && Array.isArray(svc.methods)) ? svc.methods : [];
                methods.forEach(function (m) {
                    var body = '{}';
                    if (typeof generateDefaultJson === 'function') {
                        try { body = generateDefaultJson(m.inputType, 0); }
                        catch { /* keep '{}' */ }
                    }
                    out.push({
                        type: 'method',
                        service: svc.name,
                        method: m.name,
                        protocol: svc.source || null,
                        body: body,
                        metadata: {},
                        serverUrl: null
                    });
                });
            });
            return out;
        }
        if (pool === 'collection') {
            var col = (typeof collectionsList !== 'undefined' ? collectionsList : [])
                .find(function (c) { return c.id === target.collectionId; });
            if (!col || !Array.isArray(col.items)) return [];
            col.items.forEach(function (it) {
                out.push({
                    type: 'method',
                    service: it.service || '',
                    method: it.method || '',
                    protocol: it.protocol || null,
                    body: it.body || '{}',
                    metadata: it.metadata || {},
                    serverUrl: null
                });
            });
            return out;
        }
        if (pool === 'recording') {
            var rec = (typeof recordingsList !== 'undefined' ? recordingsList : [])
                .find(function (r) { return r.id === target.recordingId; });
            if (!rec || !Array.isArray(rec.steps)) return [];
            rec.steps.forEach(function (st) {
                out.push({
                    type: 'method',
                    service: st.service || '',
                    method: st.method || '',
                    protocol: st.protocol || null,
                    body: st.request || '{}',
                    metadata: st.metadata || {},
                    serverUrl: null
                });
            });
            return out;
        }
        return [];
    }

    // Fisher-Yates partial shuffle. Mutates `arr` in place and returns
    // the first `k` elements as the picked slice. k > arr.length is
    // clamped to the full array (no replacement — each iteration picks
    // distinct endpoints per #231 acceptance).
    function _pickRandomSubset(arr, k) {
        if (!Array.isArray(arr) || arr.length === 0) return [];
        var pick = Math.max(1, Math.min(k | 0 || 1, arr.length));
        for (var i = 0; i < pick; i++) {
            var j = i + Math.floor(Math.random() * (arr.length - i));
            var tmp = arr[i]; arr[i] = arr[j]; arr[j] = tmp;
        }
        return arr.slice(0, pick);
    }

    // Per-target invocation dispatch. Returns {pass, durationMs,
    // statusLabel} so the phase-loop can aggregate uniformly regardless
    // of target type.
    async function _invokeBenchmarkTarget(target) {
        if (!target) return { pass: false, durationMs: 0, statusLabel: 'NoTarget' };

        if (target.type === 'method') {
            var targetService = (typeof services !== 'undefined' ? services : []).find(function (s) {
                return s.name === target.service && (!target.protocol || s.source === target.protocol);
            });
            if (!targetService && typeof selectedService !== 'undefined'
                && selectedService && selectedService.name === target.service) {
                targetService = selectedService;
            }
            if (!targetService) {
                return { pass: false, durationMs: 0, statusLabel: 'MissingService' };
            }
            var bodyTpl = String(target.body || '{}');
            var metaTpl = target.metadata || {};
            var messages = [substituteVars(bodyTpl)];
            var meta = {};
            for (var k in metaTpl) {
                if (Object.prototype.hasOwnProperty.call(metaTpl, k)) {
                    meta[k] = substituteVars(metaTpl[k]);
                }
            }
            if (typeof applyAuth === 'function') meta = await applyAuth(meta);
            var serviceUrlParam = serverUrlParamForService(targetService, false);
            var protocolId = target.protocol || targetService.source
                || (typeof selectedProtocol !== 'undefined' ? selectedProtocol : undefined);
            var t0 = performance.now();
            try {
                var resp = await fetch(config.prefix + '/api/invoke' + serviceUrlParam, {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({
                        service: target.service,
                        method: target.method,
                        messages: messages,
                        metadata: Object.keys(meta).length > 0 ? meta : null,
                        protocol: protocolId
                    })
                });
                var result = await resp.json();
                var elapsed = performance.now() - t0;
                if (result.title) {
                    return { pass: false, durationMs: elapsed, statusLabel: 'Error' };
                }
                return {
                    pass: true,
                    durationMs: elapsed,
                    statusLabel: result.status || 'OK'
                };
            } catch (e) {
                return {
                    pass: false,
                    durationMs: performance.now() - t0,
                    statusLabel: 'NetworkError'
                };
            }
        }

        if (target.type === 'collection-ref') {
            var col = (typeof collectionsList !== 'undefined' ? collectionsList : [])
                .find(function (c) { return c.id === target.collectionId; });
            if (!col) return { pass: false, durationMs: 0, statusLabel: 'MissingCollection' };
            if (typeof _runCollectionSession !== 'function') {
                return { pass: false, durationMs: 0, statusLabel: 'NoSessionRunner' };
            }
            var tc = performance.now();
            try {
                var rc = await _runCollectionSession(col);
                return {
                    pass: !!(rc && rc.pass),
                    durationMs: performance.now() - tc,
                    statusLabel: (rc && rc.pass) ? 'OK' : 'Error'
                };
            } catch {
                return { pass: false, durationMs: performance.now() - tc, statusLabel: 'NetworkError' };
            }
        }

        if (target.type === 'recording-ref') {
            var rec = (typeof recordingsList !== 'undefined' ? recordingsList : [])
                .find(function (r) { return r.id === target.recordingId; });
            if (!rec) return { pass: false, durationMs: 0, statusLabel: 'MissingRecording' };
            if (typeof _runRecordingSession !== 'function') {
                return { pass: false, durationMs: 0, statusLabel: 'NoSessionRunner' };
            }
            var tr = performance.now();
            try {
                var rr = await _runRecordingSession(rec);
                return {
                    pass: !!(rr && rr.pass),
                    durationMs: performance.now() - tr,
                    statusLabel: (rr && rr.pass) ? 'OK' : 'Error'
                };
            } catch {
                return { pass: false, durationMs: performance.now() - tr, statusLabel: 'NetworkError' };
            }
        }

        if (target.type === 'random') {
            // Expand the pool fresh each iteration so newly-discovered
            // services / new collection items / new recording steps
            // land in the rotation without a benchmark restart.
            var poolItems = _expandRandomPool(target);
            if (poolItems.length === 0) {
                return { pass: false, durationMs: 0, statusLabel: 'EmptyPool' };
            }
            var picked = _pickRandomSubset(poolItems, target.count || 1);
            // Aggregate per-sub-pick — pass = all pass, durationMs =
            // sum (sequential), statusLabel = first-fail or 'OK'.
            // Per-method status counts already accumulate naturally
            // because each pick goes through the regular method
            // dispatch which writes its own 'service/method' label
            // into the iteration-targets array via the caller.
            var totalMs = 0;
            var firstFailLabel = null;
            var allPass = true;
            var pickedLabels = [];
            for (var pi = 0; pi < picked.length; pi++) {
                pickedLabels.push((picked[pi].service || '?') + '/' + (picked[pi].method || '?'));
                var rs = await _invokeBenchmarkTarget(picked[pi]);
                totalMs += rs.durationMs || 0;
                if (!rs.pass) {
                    allPass = false;
                    if (!firstFailLabel) firstFailLabel = rs.statusLabel || 'Error';
                }
            }
            return {
                pass: allPass,
                durationMs: totalMs,
                statusLabel: allPass ? 'OK' : (firstFailLabel || 'Error'),
                pickedLabels: pickedLabels
            };
        }

        return { pass: false, durationMs: 0, statusLabel: 'UnknownType' };
    }

    // Build a stable, compact label for a target — used in per-iteration
    // tracking (#231 runResult.iterationTargets[]) so post-run UI can
    // slice percentiles per endpoint. 'random' resolves to its own
    // identity at this level; per-pick labels are captured separately
    // by the dispatch and merged into the iteration entry by the caller.
    function _targetTrackingLabel(target) {
        if (!target) return '?';
        if (target.type === 'method') {
            return (target.service || '?') + '/' + (target.method || '?');
        }
        if (target.type === 'collection-ref') return 'collection:' + (target.collectionId || '?');
        if (target.type === 'recording-ref') return 'recording:' + (target.recordingId || '?');
        if (target.type === 'random') return 'random:' + (target.pool || 'workspace');
        return target.type || '?';
    }

    // One VU iteration walks the spec's targets. mode === 'parallel'
    // fires them all concurrently; pass = ALL pass, durationMs =
    // max. mode === 'sequential' walks them one-by-one; pass = all
    // pass, durationMs = sum.
    //
    // Returns BOTH a `pickedLabels` array (#231 — concrete endpoint
    // labels invoked, used by the post-run per-endpoint bucketing UI)
    // AND a `targetHits[]` array (#234 — full per-target trace
    // {key,label,durationMs,statusLabel,pass} used by the CSV /
    // k6 / OTLP exporters). Both views derive from the same call set;
    // keeping them as separate fields avoids re-mapping at every
    // consumer.
    async function _runEnvelopeIteration(spec) {
        var targets = Array.isArray(spec.targets) ? spec.targets : [];
        if (targets.length === 0) {
            return { pass: false, durationMs: 0, statusLabel: 'NoTargets', pickedLabels: [], targetHits: [] };
        }
        function hitFromResult(target, r) {
            return {
                key: _benchmarkTargetKey(target),
                label: _benchmarkTargetLabel(target),
                durationMs: r && typeof r.durationMs === 'number' ? r.durationMs : 0,
                statusLabel: r && r.statusLabel ? r.statusLabel : (r && r.pass ? 'OK' : 'Error'),
                pass: !!(r && r.pass)
            };
        }
        if (spec.mode === 'parallel' && targets.length > 1) {
            var results = await Promise.all(targets.map(_invokeBenchmarkTarget));
            var pass = results.every(function (r) { return r && r.pass; });
            var maxMs = 0;
            var firstFailLabel = null;
            var allPicked = [];
            results.forEach(function (r, i) {
                if (r.durationMs > maxMs) maxMs = r.durationMs;
                if (!r.pass && !firstFailLabel) firstFailLabel = r.statusLabel;
                if (Array.isArray(r.pickedLabels) && r.pickedLabels.length > 0) {
                    allPicked.push.apply(allPicked, r.pickedLabels);
                } else {
                    allPicked.push(_targetTrackingLabel(targets[i]));
                }
            });
            return {
                pass: pass,
                durationMs: maxMs,
                statusLabel: pass ? 'OK' : (firstFailLabel || 'Error'),
                pickedLabels: allPicked,
                targetHits: results.map(function (r, i) { return hitFromResult(targets[i], r); })
            };
        }
        // sequential — walk targets in order, abort on first fail
        var totalMs = 0;
        var seqPicked = [];
        var hits = [];
        for (var i = 0; i < targets.length; i++) {
            var rs = await _invokeBenchmarkTarget(targets[i]);
            totalMs += rs.durationMs || 0;
            if (Array.isArray(rs.pickedLabels) && rs.pickedLabels.length > 0) {
                seqPicked.push.apply(seqPicked, rs.pickedLabels);
            } else {
                seqPicked.push(_targetTrackingLabel(targets[i]));
            }
            hits.push(hitFromResult(targets[i], rs));
            if (!rs.pass) {
                return { pass: false, durationMs: totalMs, statusLabel: rs.statusLabel, pickedLabels: seqPicked, targetHits: hits };
            }
        }
        return { pass: true, durationMs: totalMs, statusLabel: 'OK', pickedLabels: seqPicked, targetHits: hits };
    }

    // One phase: spawn `vus` workers; each pulls iterations until
    // `totalIterations` is reached OR `durationMs` elapses (whichever
    // is set). Tracks results into the live benchmark scratchpad.
    async function _runEnvelopePhase(spec, phase, onProgress) {
        var vus = Math.max(1, Math.min(parseInt(phase.vus, 10) || 1, 64));
        var iterCap = phase.totalIterations
            ? Math.max(1, parseInt(phase.totalIterations, 10) || 1)
            : null;
        var deadline = phase.durationMs
            ? performance.now() + Math.max(0, parseInt(phase.durationMs, 10) || 0)
            : null;
        if (!iterCap && !deadline) iterCap = 1;

        var nextIndex = 0;
        async function worker() {
            while (true) {
                if (benchmark.cancelled) return;
                if (deadline && performance.now() >= deadline) return;
                if (iterCap !== null) {
                    var idx = nextIndex++;
                    if (idx >= iterCap) return;
                }

                var ranAt = Date.now();
                var rs = await _runEnvelopeIteration(spec);

                if (rs.pass) {
                    benchmark.success++;
                    benchmark.durations.push(rs.durationMs);
                } else {
                    benchmark.failure++;
                }
                var lbl = rs.statusLabel || (rs.pass ? 'OK' : 'Error');
                benchmark.statusCounts[lbl] = (benchmark.statusCounts[lbl] || 0) + 1;
                // #231 — Track the concrete endpoint labels actually
                // invoked on this iteration so the post-run UI can slice
                // percentiles per endpoint. Stored alongside durations
                // by index — iterationTargets[i] is the picked labels
                // for the i-th completed iteration.
                if (Array.isArray(benchmark.iterationTargets)) {
                    benchmark.iterationTargets.push(rs.pickedLabels || []);
                }
                benchmark.completed++;

                // #234 — per-iteration trace + per-target rollup.
                // iteration row uses the first target's label as a
                // human-friendly hint; the per-target accounting
                // accrues every hit (sequential walks all on pass,
                // parallel always returns every hit).
                var hits = Array.isArray(rs.targetHits) ? rs.targetHits : [];
                benchmark.iterations.push({
                    i: benchmark.completed,
                    target: hits.length > 0 ? hits[0].label : '',
                    targetKey: hits.length > 0 ? hits[0].key : '',
                    durationMs: rs.durationMs || 0,
                    status: lbl,
                    pass: !!rs.pass,
                    ranAt: ranAt
                });
                hits.forEach(function (h) {
                    var slot = benchmark.targetStats[h.key];
                    if (!slot) {
                        slot = benchmark.targetStats[h.key] = {
                            key: h.key, label: h.label,
                            count: 0, errors: 0,
                            durations: [], statusCounts: {}
                        };
                    }
                    slot.count++;
                    if (h.pass) slot.durations.push(h.durationMs || 0);
                    else slot.errors++;
                    slot.statusCounts[h.statusLabel] = (slot.statusCounts[h.statusLabel] || 0) + 1;
                });

                if (typeof onProgress === 'function' && benchmark.total > 0
                    && (benchmark.completed % Math.max(1, Math.floor(benchmark.total / 50)) === 0
                        || benchmark.completed === benchmark.total)) {
                    onProgress();
                }
            }
        }
        var workers = [];
        for (var w = 0; w < vus; w++) workers.push(worker());
        await Promise.all(workers);
    }

    // Pre-flight: AI-prefetch is per-method, so walk the method-targets
    // once and prime the ai.* cache so each iteration uses the same
    // resolved value (consistent load profile, not per-call drift).
    async function _envelopePrefetchAi(spec) {
        if (typeof window.bowirePrefetchAiVars !== 'function') return;
        var tpls = [];
        (spec.targets || []).forEach(function (t) {
            if (t.type !== 'method') return;
            tpls.push(String(t.body || '{}'));
            var m = t.metadata || {};
            for (var k in m) {
                if (Object.prototype.hasOwnProperty.call(m, k)) tpls.push(String(m[k] || ''));
            }
        });
        if (tpls.length === 0) return;
        try { await window.bowirePrefetchAiVars(tpls); }
        catch (e) { console.warn('[ai-prefetch] envelope failed', e); }
    }

    // Runner — envelope-aware, multi-phase, multi-target.
    //
    // For each phase in spec.phases: spawn phase.vus workers that
    // pull iterations off a shared counter (or loop until durationMs
    // elapses); each iteration walks spec.targets[] in spec.mode
    // (sequential | parallel). Results stream into the live
    // benchmark scratchpad so existing perf-diff visualisations keep
    // working unchanged.
    async function runBenchmarkSpec(spec, onProgress) {
        if (!spec || benchmark.running) return null;
        migrateEnvelope(spec);
        reflectLegacyEditsBack(spec);

        var phases = Array.isArray(spec.phases) && spec.phases.length > 0
            ? spec.phases
            : [defaultEnvelopePhase({ vus: spec.concurrency, totalIterations: spec.n })];

        // Iteration-bounded total = sum of totalIterations across
        // phases. Time-bounded phases contribute 0 to the upfront
        // total — the progress bar will tick up but reach 100 % only
        // once the deadline hits and benchmark.total catches up.
        var iterTotal = 0;
        phases.forEach(function (p) {
            if (p.totalIterations) iterTotal += parseInt(p.totalIterations, 10) || 0;
        });
        if (iterTotal === 0) iterTotal = 1;
        var concurrencyMax = phases.reduce(function (acc, p) {
            return Math.max(acc, parseInt(p.vus, 10) || 0);
        }, 0) || 1;

        await _envelopePrefetchAi(spec);

        resetBenchmark({ n: iterTotal, concurrency: concurrencyMax });
        benchmark.running = true;
        benchmark.startTime = performance.now();
        benchmarkActiveSpecId = spec.id;

        var displayName = spec.name || (spec.targets && spec.targets[0]
            ? _benchmarkTargetLabel(spec.targets[0]) : 'Envelope');
        addConsoleEntry({
            type: 'request',
            method: displayName,
            status: 'Benchmark',
            body: 'Starting ' + phases.length + ' phase' + (phases.length === 1 ? '' : 's')
                + ' · ' + spec.targets.length + ' target' + (spec.targets.length === 1 ? '' : 's')
                + ' · mode ' + (spec.mode || 'sequential')
        });
        if (typeof onProgress === 'function') onProgress();

        for (var pi = 0; pi < phases.length; pi++) {
            if (benchmark.cancelled) break;
            await _runEnvelopePhase(spec, phases[pi], onProgress);
        }

        benchmark.endTime = performance.now();
        benchmark.running = false;
        benchmarkActiveSpecId = null;
        var totalMs = benchmark.endTime - benchmark.startTime;
        addConsoleEntry({
            type: 'response',
            method: displayName,
            status: benchmark.cancelled ? 'Cancelled' : 'Benchmark complete',
            durationMs: Math.round(totalMs),
            body: benchmark.success + ' OK / ' + benchmark.failure + ' failed'
        });

        var stats = computeBenchmarkStats();
        var thisRun = {
            ranAt: benchmark.startTime,
            ranAtWallClock: Date.now() - Math.round(benchmark.endTime - benchmark.startTime),
            durations: benchmark.durations.slice(),
            statusCounts: Object.assign({}, benchmark.statusCounts),
            // #231 — Per-iteration picked endpoint labels. Stays empty
            // for envelopes without a 'random' target since the other
            // shapes always invoke the same fixed list; carrying the
            // copy anyway keeps the lastRun shape stable for the
            // post-run UI (it just renders zero distinct buckets).
            iterationTargets: (benchmark.iterationTargets || []).slice(),
            success: benchmark.success,
            failure: benchmark.failure,
            total: benchmark.total,
            startTimeMs: benchmark.startTime,
            endTimeMs: benchmark.endTime,
            cancelled: benchmark.cancelled,
            stats: stats,
            // #234 — Trace + per-target rollup for result-exports.
            iterations: benchmark.iterations.slice(),
            targetStats: _snapshotTargetStats(benchmark.targetStats)
        };

        // #233 — Keep a bounded ring of recent runs alongside lastRun.
        // The diff banner reads runs[len-2] (previous) vs runs[len-1]
        // (current); the cap is operator-tunable (default 5) so the
        // last N comparisons stay available without ballooning the
        // serialised spec — durations[] is the heaviest field and
        // dominates the size budget. lastRun stays as a mirror of the
        // latest entry so every other call site (sidebar meta, presets
        // bar, stats grid below) keeps reading the same shape.
        if (!Array.isArray(spec.runs)) spec.runs = [];
        spec.runs.push(thisRun);
        var cap = _benchmarkHistoryCap();
        if (spec.runs.length > cap) {
            spec.runs.splice(0, spec.runs.length - cap);
        }
        spec.lastRun = thisRun;

        persistBenchmarks();
        // v2.2 T3 — coverage entry per method-target in the envelope.
        // Bucket per-target outcomes off targetStats when available
        // (each target tracks its own success/failure counters); fall
        // back to the aggregate run when targetStats is empty (single-
        // target envelopes that skipped the per-target rollup). Skips
        // request-type targets — those don't resolve to a discovered
        // method, so they don't show up in the sidebar tree.
        if (typeof safeRecordMethodRun === 'function') {
            try {
                var ts = thisRun.targetStats || [];
                if (ts.length === 0 && Array.isArray(spec.targets)) {
                    // Single-target envelope — synthesise one slot
                    // so the recorder still fires for it.
                    ts = spec.targets.filter(function (t) { return t && t.type === 'method'; })
                        .map(function (t) {
                            return {
                                target: t,
                                success: thisRun.success || 0,
                                failure: thisRun.failure || 0
                            };
                        });
                }
                for (var ti = 0; ti < ts.length; ti++) {
                    var tt = ts[ti];
                    if (!tt || !tt.target || tt.target.type !== 'method') continue;
                    var tgt = tt.target;
                    if (!tgt.service || !tgt.method) continue;
                    var ok = (tt.success || 0) > 0 && (tt.failure || 0) === 0;
                    var outc = benchmark.cancelled ? 'error'
                        : (ok ? 'ok' : ((tt.failure || 0) > 0 ? 'fail' : 'ok'));
                    safeRecordMethodRun({
                        service: tgt.service,
                        method:  tgt.method,
                        source:  'benchmark',
                        startedAt: thisRun.ranAtWallClock || (Date.now() - Math.round(totalMs)),
                        durationMs: Math.round(totalMs),
                        outcome: outc,
                        errorMessage: ((tt.failure || 0) > 0)
                            ? (tt.failure + ' failed call(s)') : null
                    });
                }
            } catch (_) { /* recording is best-effort */ }
        }
        // #303 — advance the run-a-benchmark tour the moment a run
        // settles (cancelled or not — the operator saw the result panel
        // either way). Detail carries the run snapshot so a future tour
        // step could read the percentile to underline a finding.
        if (typeof window !== 'undefined'
            && typeof window.bowireFireTourEvent === 'function') {
            window.bowireFireTourEvent('benchmark-run-complete', {
                specId: spec.id,
                cancelled: !!benchmark.cancelled,
                durationMs: Math.round(benchmark.endTime - benchmark.startTime),
                stats: stats
            });
        }
        if (typeof onProgress === 'function') onProgress();
        return spec.lastRun;
    }

    // ---- #233 Run-history settings + diff math ----
    //
    // The history cap and regression threshold are workspace-wide
    // operator preferences. They read from localStorage so a power user
    // who runs the same envelope dozens of times in a session can bump
    // the cap to 20; a noise-tolerant team can crank the threshold to
    // 10% and stop chasing every 6% blip. Both keys fall back to sane
    // defaults so a fresh install never trips on missing settings.

    function _benchmarkHistoryCap() {
        try {
            var raw = parseInt(localStorage.getItem('bowire_benchmarks_history_cap'), 10);
            if (!isNaN(raw) && raw >= 2 && raw <= 50) return raw;
        } catch { /* ignore */ }
        return 5;
    }

    function _benchmarkRegressionThresholdPct() {
        try {
            var raw = parseFloat(localStorage.getItem('bowire_benchmarks_regression_threshold_pct'));
            if (!isNaN(raw) && raw >= 0 && raw <= 100) return raw;
        } catch { /* ignore */ }
        return 5;
    }

    // Compute deltas between two runs for the diff banner. Returns
    // null when either side is missing stats (cancelled mid-run, no
    // successful calls). The threshold gates the visual flag —
    // smaller-than-threshold latency deltas read as "flat", anything
    // beyond is ▲ (regression) or ▼ (improvement). Status-histogram
    // deltas surface every changed key (added, removed, count-shifted)
    // because even a tiny error-count change is signal.
    function _diffBenchmarkRuns(curr, prev, thresholdPct) {
        if (!curr || !prev || !curr.stats || !prev.stats) return null;
        var th = (typeof thresholdPct === 'number') ? thresholdPct : 5;

        function pctChange(now, before) {
            if (before == null || before === 0) return null;
            return ((now - before) / before) * 100;
        }
        function classify(now, before) {
            var pct = pctChange(now, before);
            if (pct == null) return { delta: now - (before || 0), pct: null, dir: 'flat' };
            var abs = Math.abs(pct);
            var dir = abs < th ? 'flat' : (pct > 0 ? 'up' : 'down');
            return { delta: now - before, pct: pct, dir: dir };
        }

        // Status histogram delta — collect every key from both sides.
        // Anything that gained/lost calls makes the cut. We split into
        // 2xx-ish / 4xx-ish / 5xx-ish / other buckets for the headline
        // ("2xx: +12, 4xx: -3"); the expand-on-click table renders the
        // raw per-key table for full detail.
        var allKeys = {};
        Object.keys(curr.statusCounts || {}).forEach(function (k) { allKeys[k] = true; });
        Object.keys(prev.statusCounts || {}).forEach(function (k) { allKeys[k] = true; });
        var perKey = [];
        var buckets = { '2xx': 0, '4xx': 0, '5xx': 0, 'error': 0 };
        Object.keys(allKeys).forEach(function (k) {
            var nowC = (curr.statusCounts && curr.statusCounts[k]) || 0;
            var beforeC = (prev.statusCounts && prev.statusCounts[k]) || 0;
            var d = nowC - beforeC;
            perKey.push({ key: k, before: beforeC, now: nowC, delta: d });
            if (/^2/.test(k)) buckets['2xx'] += d;
            else if (/^4/.test(k)) buckets['4xx'] += d;
            else if (/^5/.test(k)) buckets['5xx'] += d;
            else if (k === 'Error' || k === 'NetworkError' || k === 'MissingService'
                  || k === 'MissingCollection' || k === 'MissingRecording') buckets['error'] += d;
        });
        // Sort so changed keys come first; alphabetic within.
        perKey.sort(function (a, b) {
            var dA = Math.abs(a.delta), dB = Math.abs(b.delta);
            if (dA !== dB) return dB - dA;
            return a.key.localeCompare(b.key);
        });

        return {
            p50: classify(curr.stats.p50, prev.stats.p50),
            p95: classify(curr.stats.p95, prev.stats.p95),
            p99: classify(curr.stats.p99, prev.stats.p99),
            avg: classify(curr.stats.avg, prev.stats.avg),
            throughput: classify(curr.stats.throughput, prev.stats.throughput),
            // Errors are inverted-direction — more errors is bad even
            // though the raw delta is positive. Same threshold applies.
            errors: (function () {
                var nowErr = (curr.failure || 0);
                var beforeErr = (prev.failure || 0);
                var delta = nowErr - beforeErr;
                return { delta: delta, before: beforeErr, now: nowErr };
            })(),
            statusBuckets: buckets,
            statusPerKey: perKey,
            ranAtPrev: prev.ranAt || prev.startTimeMs || 0,
            ranAtCurr: curr.ranAt || curr.startTimeMs || 0
        };
    }

    // Direction → glyph + tone. Latency / errors: 'up' is regression
    // (warn). Throughput: caller flips dir before passing in.
    function _diffGlyph(dir) {
        if (dir === 'up') return '▲';
        if (dir === 'down') return '▼';
        return '·';
    }
    function _diffSign(n) {
        if (n == null) return '';
        if (n > 0) return '+';
        return ''; // negative number already prints its own sign
    }
    function _formatMs(ms) {
        if (ms == null || isNaN(ms)) return '—';
        return (Math.round(ms * 10) / 10) + ' ms';
    }
    function _formatPct(pct) {
        if (pct == null || isNaN(pct)) return '';
        return (pct > 0 ? '+' : '') + (Math.round(pct * 10) / 10) + '%';
    }

    // ---- #233 Diff banner — collapsed strip + expand-on-click table ----
    //
    // Headline strip reads "p95 142 ms (▲ +8 ms · +5%) · rps 412/s
    // (▼ -8/s · -2%) · 2xx +12 · 4xx -3 · 5xx +0". The click handler
    // toggles `benchmarkDiffBannerExpanded[spec.id]`; expanded
    // appends a small side-by-side table covering every percentile,
    // throughput, totals and the full per-status histogram delta.
    //
    // Latency / errors: `up` paints as regression (warn), `down` as
    // improvement (good). Throughput / OK count: inverted — `up` is
    // good. _diffMetric() takes the inversion flag so the colour
    // mapping stays in one place.
    function _renderRunDiffBanner(spec, diff, runsList) {
        var expanded = !!benchmarkDiffBannerExpanded[spec.id];

        function relTime(ts) {
            if (!ts) return '';
            var dt = Date.now() - ts;
            if (dt < 60000) return Math.round(dt / 1000) + 's ago';
            if (dt < 3600000) return Math.round(dt / 60000) + 'm ago';
            if (dt < 86400000) return Math.round(dt / 3600000) + 'h ago';
            return Math.round(dt / 86400000) + 'd ago';
        }

        // Headline class — the worst regression among the tracked
        // metrics drives the banner accent. If anything is up on
        // latency or errors → warn. Else if throughput is down → warn.
        // Else if any improvement → good. Else flat.
        var overall = 'flat';
        if (diff.p95.dir === 'up' || diff.p99.dir === 'up' || diff.errors.delta > 0) {
            overall = 'warn';
        } else if (diff.throughput.dir === 'down') {
            overall = 'warn';
        } else if (diff.p95.dir === 'down' || diff.throughput.dir === 'up' || diff.errors.delta < 0) {
            overall = 'good';
        }

        var banner = el('div', {
            className: 'bowire-bench-diff-banner is-' + overall + (expanded ? ' is-expanded' : ''),
            role: 'button',
            tabindex: '0',
            'aria-expanded': expanded ? 'true' : 'false',
            title: 'Compare run #' + runsList.length + ' vs #' + (runsList.length - 1)
                + ' · click to ' + (expanded ? 'collapse' : 'expand'),
            onClick: function () {
                benchmarkDiffBannerExpanded[spec.id] = !benchmarkDiffBannerExpanded[spec.id];
                render();
            },
            onKeyDown: function (e) {
                if (e.key === 'Enter' || e.key === ' ') {
                    e.preventDefault();
                    benchmarkDiffBannerExpanded[spec.id] = !benchmarkDiffBannerExpanded[spec.id];
                    render();
                }
            }
        });

        // ---- Headline strip ----
        var head = el('div', { className: 'bowire-bench-diff-head' });

        head.appendChild(el('span', {
            className: 'bowire-bench-diff-title',
            textContent: 'vs previous run · ' + relTime(diff.ranAtPrev)
        }));

        function metricChip(label, classify, formatValue, currValue, invert) {
            // invert: true ⇒ 'up' is good (used for throughput).
            var dir = classify.dir;
            var tone = 'flat';
            if (dir === 'up') tone = invert ? 'good' : 'warn';
            else if (dir === 'down') tone = invert ? 'warn' : 'good';

            var chip = el('span', { className: 'bowire-bench-diff-chip is-' + tone });
            chip.appendChild(el('span', {
                className: 'bowire-bench-diff-chip-label',
                textContent: label
            }));
            chip.appendChild(el('span', {
                className: 'bowire-bench-diff-chip-value',
                textContent: formatValue(currValue)
            }));
            var glyph = _diffGlyph(dir);
            var pctText = _formatPct(classify.pct);
            chip.appendChild(el('span', {
                className: 'bowire-bench-diff-chip-delta',
                textContent: glyph + ' ' + pctText
            }));
            return chip;
        }

        var currStats = runsList[runsList.length - 1].stats;
        head.appendChild(metricChip('p95', diff.p95, _formatMs, currStats.p95, false));
        head.appendChild(metricChip('p99', diff.p99, _formatMs, currStats.p99, false));
        head.appendChild(metricChip(
            'rps', diff.throughput,
            function (v) { return (Math.round(v * 10) / 10) + '/s'; },
            currStats.throughput, true
        ));

        // Status-bucket summary — keep it terse on the headline. Each
        // bucket appears only if its delta is non-zero so banners
        // don't get cluttered for stable runs.
        var sb = diff.statusBuckets || {};
        var bucketEntries = [];
        if (sb['2xx']) bucketEntries.push({ k: '2xx', d: sb['2xx'], err: false });
        if (sb['4xx']) bucketEntries.push({ k: '4xx', d: sb['4xx'], err: true });
        if (sb['5xx']) bucketEntries.push({ k: '5xx', d: sb['5xx'], err: true });
        if (sb['error']) bucketEntries.push({ k: 'err', d: sb['error'], err: true });
        if (bucketEntries.length > 0) {
            var statusWrap = el('span', { className: 'bowire-bench-diff-status' });
            bucketEntries.forEach(function (b) {
                // For error buckets, a positive delta is bad; for 2xx a
                // positive delta is good. dir → tone uses the same
                // mapping as the metric chips above.
                var tone = 'flat';
                if (b.err) tone = b.d > 0 ? 'warn' : (b.d < 0 ? 'good' : 'flat');
                else       tone = b.d > 0 ? 'good' : (b.d < 0 ? 'warn' : 'flat');
                statusWrap.appendChild(el('span', {
                    className: 'bowire-bench-diff-status-chip is-' + tone,
                    textContent: b.k + ' ' + _diffSign(b.d) + b.d
                }));
            });
            head.appendChild(statusWrap);
        }

        head.appendChild(el('span', {
            className: 'bowire-bench-diff-expand',
            innerHTML: svgIcon(expanded ? 'chevronUp' : 'chevronDown')
        }));

        banner.appendChild(head);

        // ---- Expanded comparison table ----
        if (expanded) {
            var prev = runsList[runsList.length - 2];
            var curr = runsList[runsList.length - 1];

            var table = el('table', { className: 'bowire-bench-diff-table' });
            var thead = el('thead', {});
            thead.appendChild(el('tr', {},
                el('th', { textContent: 'Metric' }),
                el('th', { textContent: 'Previous' }),
                el('th', { textContent: 'Current' }),
                el('th', { textContent: 'Δ' })
            ));
            table.appendChild(thead);

            var tbody = el('tbody', {});
            function row(label, prevVal, currVal, classify, fmt, invert) {
                var tone = 'flat';
                if (classify && classify.dir === 'up') tone = invert ? 'good' : 'warn';
                else if (classify && classify.dir === 'down') tone = invert ? 'warn' : 'good';
                var deltaText = '—';
                if (classify) {
                    var glyph = _diffGlyph(classify.dir);
                    var pctText = _formatPct(classify.pct);
                    deltaText = glyph + ' ' + pctText;
                }
                tbody.appendChild(el('tr', { className: 'is-' + tone },
                    el('td', { className: 'bowire-bench-diff-table-label', textContent: label }),
                    el('td', { textContent: fmt(prevVal) }),
                    el('td', { textContent: fmt(currVal) }),
                    el('td', { className: 'bowire-bench-diff-table-delta', textContent: deltaText })
                ));
            }
            row('p50', prev.stats.p50, curr.stats.p50, diff.p50, _formatMs, false);
            row('p95', prev.stats.p95, curr.stats.p95, diff.p95, _formatMs, false);
            row('p99', prev.stats.p99, curr.stats.p99, diff.p99, _formatMs, false);
            row('avg', prev.stats.avg, curr.stats.avg, diff.avg, _formatMs, false);
            row('rps', prev.stats.throughput, curr.stats.throughput, diff.throughput,
                function (v) { return (Math.round(v * 10) / 10) + '/s'; }, true);
            row('errors',
                prev.failure || 0, curr.failure || 0,
                { dir: diff.errors.delta > 0 ? 'up' : (diff.errors.delta < 0 ? 'down' : 'flat'), pct: null },
                function (v) { return String(v); }, false);

            // Per-status histogram delta — every key with a non-zero
            // change. Stable keys are skipped so the table stays
            // readable on long status lists.
            (diff.statusPerKey || []).forEach(function (entry) {
                if (entry.delta === 0) return;
                var isErr = entry.key === 'Error' || entry.key === 'NetworkError'
                    || /^[45]/.test(entry.key);
                var dir = entry.delta > 0 ? 'up' : 'down';
                var classify = { dir: dir, pct: null };
                row('status · ' + entry.key,
                    entry.before, entry.now, classify,
                    function (v) { return String(v); },
                    !isErr); // for non-error keys, up is good
            });

            table.appendChild(tbody);
            banner.appendChild(el('div', { className: 'bowire-bench-diff-body' }, table));

            // Footer hint — threshold + history depth so the operator
            // knows why a 4% change reads as 'flat' (and can change
            // it in localStorage).
            banner.appendChild(el('div', {
                className: 'bowire-bench-diff-footer',
                textContent: 'Threshold ' + _benchmarkRegressionThresholdPct()
                    + '% · history ' + runsList.length + '/' + _benchmarkHistoryCap()
                    + ' runs · click banner to collapse'
            }));
        }
        return banner;
    }

    // Pretty-print a target for the console line above. Method targets
    // get "service/method"; collection / recording references resolve
    // to "name" or fall back to the id.
    function _benchmarkTargetLabel(target) {
        if (!target) return '?';
        if (target.type === 'method') return target.service + '/' + target.method;
        if (target.type === 'collection-ref') {
            var col = (typeof collectionsList !== 'undefined' ? collectionsList : [])
                .find(function (c) { return c.id === target.collectionId; });
            return col ? col.name : ('collection:' + target.collectionId);
        }
        if (target.type === 'recording-ref') {
            var rec = (typeof recordingsList !== 'undefined' ? recordingsList : [])
                .find(function (r) { return r.id === target.recordingId; });
            return rec ? rec.name : ('recording:' + target.recordingId);
        }
        if (target.type === 'random') {
            return 'Random[' + (target.pool || 'workspace') + '] × ' + (target.count || 1);
        }
        return target.type || '?';
    }

    // #234 — Freeze the live targetStats accounting onto the
    // persisted lastRun. Sorts each target's durations once so
    // every downstream export (CSV / k6 / OTLP) can derive
    // percentiles from a single canonical sorted array.
    function _snapshotTargetStats(live) {
        var out = {};
        var keys = Object.keys(live || {});
        for (var i = 0; i < keys.length; i++) {
            var s = live[keys[i]];
            var sorted = (s.durations || []).slice().sort(function (a, b) { return a - b; });
            out[s.key] = {
                key: s.key,
                label: s.label,
                count: s.count,
                errors: s.errors,
                durations: sorted,
                statusCounts: Object.assign({}, s.statusCounts || {})
            };
        }
        return out;
    }

    // Percentile over an already-sorted array. Nearest-rank — same
    // method computeBenchmarkStats() uses so per-target and overall
    // percentiles line up byte-for-byte.
    function _percentileSorted(sorted, p) {
        if (!sorted || sorted.length === 0) return 0;
        var idx = Math.ceil((p / 100) * sorted.length) - 1;
        return sorted[Math.max(0, Math.min(idx, sorted.length - 1))];
    }

    // Envelope-aware sidebar meta. Shows total iterations across all
    // phases when every phase is iteration-bounded ("400×"); falls
    // back to phase-count when any phase is time-bounded ("3 phases")
    // since an upfront iteration estimate isn't possible. Single-
    // phase iter-bounded envelopes get the compact "100×" form so
    // typical specs read identically to the pre-envelope shape.
    function _envelopeSidebarMeta(spec) {
        var phases = (spec.phases || []);
        if (phases.length === 0) return '—';
        var totalIter = 0;
        var anyTimeBounded = false;
        for (var i = 0; i < phases.length; i++) {
            if (phases[i].durationMs && !phases[i].totalIterations) {
                anyTimeBounded = true;
                continue;
            }
            totalIter += phases[i].totalIterations || 0;
        }
        if (anyTimeBounded) return phases.length + ' phase' + (phases.length === 1 ? '' : 's');
        if (totalIter > 0) return totalIter + '×';
        return phases.length + ' phase' + (phases.length === 1 ? '' : 's');
    }

    // ---- Sidebar: list of saved benchmark specs ----

    function renderBenchmarksSidebar() {
        var sidebar = el('div', { id: 'bowire-sidebar', className: 'bowire-sidebar bowire-sidebar-mode' });
        loadBenchmarks();

        sidebar.appendChild(renderSidebarToolbar({
            title: 'Benchmarks',
            primary: {
                icon: 'plus',
                title: 'New benchmark',
                onClick: function () {
                    var spec = createBenchmarkSpec();
                    benchmarksSelectedId = spec.id;
                    render();
                }
            },
            overflow: (function () {
                var items = [];
                items.push({ label: 'Import from Artillery JSON…', icon: 'replay',
                    onClick: function () { importEnvelopeFrom('artillery'); } });
                items.push({ label: 'Import from Postman Collection…', icon: 'replay',
                    onClick: function () { importEnvelopeFrom('postman'); } });
                items.push({ label: 'Import Bowire envelope…', icon: 'replay',
                    onClick: function () { importEnvelopeFrom('native'); } });
                if (benchmarksSelectedId && getBenchmarkSpec(benchmarksSelectedId)) {
                    items.push({ separator: true });
                    items.push({ label: 'Export as Artillery JSON', icon: 'send',
                        onClick: function () { exportSelectedEnvelopeAs('artillery'); } });
                    items.push({ label: 'Export as k6 script', icon: 'send',
                        onClick: function () { exportSelectedEnvelopeAs('k6'); } });
                    items.push({ label: 'Export as Bowire envelope', icon: 'send',
                        onClick: function () { exportSelectedEnvelopeAs('native'); } });
                }
                if (benchmarksList && benchmarksList.length > 0) {
                    items.push({ separator: true });
                    items.push({
                        label: 'Delete all benchmarks',
                        icon: 'trash',
                        danger: true,
                        meta: String(benchmarksList.length),
                        onClick: function () {
                            var n = benchmarksList.length;
                            bowireConfirm(
                                'Delete all ' + n + ' benchmarks?',
                                function () {
                                    benchmarksList.length = 0;
                                    benchmarksSelectedId = null;
                                    if (typeof persistBenchmarks === 'function') persistBenchmarks();
                                    toast(n + ' benchmark' + (n === 1 ? '' : 's') + ' deleted', 'success');
                                    render();
                                },
                                { title: 'Delete all benchmarks', confirmText: 'Delete ' + n, danger: true }
                            );
                        }
                    });
                }
                return items;
            })(),
            // #362 — search + sort once there's more than one to organise.
            search: (benchmarksList && benchmarksList.length > 1) ? {
                placeholder: 'Search benchmarks…',
                value: benchmarksSearchQuery,
                onInput: function (v) { benchmarksSearchQuery = v; render(); }
            } : null,
            sort: (benchmarksList && benchmarksList.length > 1) ? {
                title: 'Sort benchmarks',
                value: benchmarksSortBy || 'manual',
                options: BOWIRE_LIST_SORT_OPTIONS_WITH_MANUAL.concat(BOWIRE_LIST_SORT_OPTIONS),
                onChange: function (v) { benchmarksSortBy = v; render(); }
            } : null
        }));

        var list = el('div', { id: 'bowire-benchmarks-list', className: 'bowire-env-list' });
        var visibleBenchmarks = applyListFilterSort(benchmarksList, {
            filter: benchmarksSearchQuery,
            sort: benchmarksSortBy,
            nameOf: function (s) { return s.name; },
            createdOf: function (s) { return s.createdAt; }
        });
        if (benchmarksList.length === 0) {
            list.appendChild(el('div', {
                className: 'bowire-pane-empty',
                style: 'padding:12px 14px',
                textContent: 'No benchmarks yet.'
            }));
        } else if (visibleBenchmarks.length === 0) {
            list.appendChild(el('div', {
                className: 'bowire-pane-empty',
                style: 'padding:12px 14px',
                textContent: 'No benchmarks match "' + benchmarksSearchQuery + '".'
            }));
        } else {
            visibleBenchmarks.forEach(function (spec) {
                var isRunning = spec.id === benchmarkActiveSpecId && benchmark.running;
                var meta;
                if (isRunning) {
                    meta = benchmark.completed + '/' + benchmark.total;
                } else if (spec.lastRun && spec.lastRun.stats) {
                    meta = 'p95 ' + Math.round(spec.lastRun.stats.p95) + ' ms';
                } else {
                    meta = _envelopeSidebarMeta(spec);
                }
                var runOrStop = function () {
                    if (isRunning) {
                        if (typeof stopBenchmark === 'function') stopBenchmark();
                    } else {
                        benchmarksSelectedId = spec.id;
                        runBenchmarkSpec(spec, function () { render(); });
                    }
                };
                var deleteSpec = function () {
                    var idx = benchmarksList.indexOf(spec);
                    if (idx >= 0) benchmarksList.splice(idx, 1);
                    if (benchmarksSelectedId === spec.id) benchmarksSelectedId = null;
                    if (typeof persistBenchmarks === 'function') persistBenchmarks();
                    render();
                };
                var benchRow = renderSidebarListItem({
                    id: 'bowire-bench-row-' + spec.id,
                    accent: isRunning ? 'var(--bowire-warning)' : 'var(--bowire-accent)',
                    pulse: isRunning,
                    name: spec.name,
                    meta: meta,
                    selected: spec.id === benchmarksSelectedId,
                    // #362 — hover-tools + reserved active slot for house-
                    // pattern parity. Tools mirror the context-menu's
                    // primary actions; running benchmark gets the ✓ slot.
                    isActive: isRunning,
                    reserveActiveSlot: true,
                    activeIcon: 'play',
                    activeTitle: 'Running',
                    tools: [
                        { icon: isRunning ? 'stop' : 'play', title: isRunning ? 'Stop run' : 'Run benchmark', onClick: runOrStop },
                        { icon: 'trash', title: 'Delete benchmark', danger: true, onClick: deleteSpec }
                    ],
                    onClick: function () {
                        benchmarksSelectedId = spec.id;
                        render();
                    },
                    onContextMenu: function (e) {
                        e.preventDefault();
                        e.stopPropagation();
                        if (typeof showContextMenu !== 'function') return;
                        showContextMenu(e.clientX, e.clientY, [
                            {
                                label: isRunning ? 'Stop run' : 'Run benchmark',
                                onClick: function () {
                                    if (isRunning) {
                                        if (typeof stopBenchmark === 'function') stopBenchmark();
                                    } else {
                                        benchmarksSelectedId = spec.id;
                                        runBenchmarkSpec(spec, function () { render(); });
                                    }
                                }
                            },
                            {
                                label: 'Rename…',
                                onClick: function () {
                                    bowirePrompt('Benchmark name', {
                                        title: 'Rename benchmark',
                                        defaultValue: spec.name || '',
                                        confirmText: 'Save'
                                    }).then(function (name) {
                                        if (!name) return;
                                        spec.name = String(name).trim();
                                        if (typeof persistBenchmarks === 'function') persistBenchmarks();
                                        render();
                                    });
                                }
                            },
                            { separator: true },
                            {
                                label: 'Delete',
                                danger: true,
                                onClick: function () {
                                    var idx = benchmarksList.indexOf(spec);
                                    if (idx >= 0) benchmarksList.splice(idx, 1);
                                    if (benchmarksSelectedId === spec.id) benchmarksSelectedId = null;
                                    if (typeof persistBenchmarks === 'function') persistBenchmarks();
                                    render();
                                }
                            }
                        ]);
                    }
                });
                // #362 — manual drag-reorder when in manual sort with no
                // active filter.
                if (!benchmarksSearchQuery && (benchmarksSortBy === 'manual' || benchmarksSortBy === '')) {
                    attachListReorder(benchRow, spec.id, function (dragId, targetId, after) {
                        if (moveInArrayById(benchmarksList, dragId, targetId, after)) {
                            if (typeof persistBenchmarks === 'function') persistBenchmarks();
                            render();
                        }
                    });
                }
                list.appendChild(benchRow);
            });
        }
        sidebar.appendChild(list);
        return sidebar;
    }

    // ---- Add-target picker (Phase 3 add-surfaces) ----
    //
    // Shows a context menu at (clientX, clientY) that lets the user
    // either drop `target` into an existing envelope OR spawn a new
    // envelope seeded with it. Callers pass an `options.name` for
    // the new-envelope path so the resulting spec has a meaningful
    // default name (e.g. "Benchmark: <method>" or "Benchmark: <collection>").
    //
    // `options.jumpAfterCreate` defaults to true — creating a new
    // envelope hops the rail to Benchmarks and selects it so the
    // operator can immediately tune phases and Run; appending to an
    // existing envelope stays put with a toast (less interruptive).
    function addTargetToEnvelopePicker(clientX, clientY, target, options) {
        options = options || {};
        if (typeof showContextMenu !== 'function') return;
        loadBenchmarks();
        var items = [];
        items.push({
            label: 'New envelope from this',
            icon: 'plus',
            onClick: function () {
                var seed = { targets: [target] };
                if (options.name) seed.name = options.name;
                var spec = createBenchmarkSpec(seed);
                if (typeof benchmarksSelectedId !== 'undefined' && spec) {
                    benchmarksSelectedId = spec.id;
                }
                if (options.jumpAfterCreate !== false) {
                    railMode = 'benchmarks';
                    try { localStorage.setItem('bowire_rail_mode', 'benchmarks'); } catch { /* ignore */ }
                }
                toast('Envelope created', 'success');
                render();
            }
        });
        if (benchmarksList.length > 0) {
            items.push({ separator: true });
            benchmarksList.forEach(function (env) {
                var t = env.targets || [];
                var isRunning = (typeof benchmarkActiveSpecId !== 'undefined'
                    && benchmarkActiveSpecId === env.id
                    && benchmark && benchmark.running);
                items.push({
                    label: env.name,
                    title: env.name + ' · ' + t.length + ' target' + (t.length === 1 ? '' : 's')
                        + (isRunning ? ' · running' : ''),
                    icon: 'lightning',
                    meta: t.length + (t.length === 1 ? ' target' : ' targets'),
                    indicator: isRunning ? 'running' : null,
                    onClick: function () {
                        if (!Array.isArray(env.targets)) env.targets = [];
                        env.targets.push(target);
                        persistBenchmarks();
                        toast('Added to "' + env.name + '"', 'success');
                        render();
                    }
                });
            });
        }
        showContextMenu(clientX, clientY, items);
    }

    // ---- Detail-pane helpers ----

    function _renderModeSegmented(spec) {
        // Two-position segmented control — replaces the old pair of
        // bordered radio cards. Single bordered host with the active
        // option highlighted via accent tint.
        //
        // The onClick handler RE-READS spec.mode at click time —
        // morphdom preserves these button nodes across renders, so
        // capturing the render-time `active` flag in the closure would
        // leave the no-op guard stuck on its first-render value.
        // After the first mode switch every subsequent click on the
        // previously-active button would bounce on `if (active) return;`
        // because the stale closure still believes it's active —
        // exactly the #300 repro ("can only switch once, then inert").
        // See feedback-morphdom-stale-handler-pitfall.
        var current = spec.mode || 'sequential';
        function seg(value, label, title) {
            var active = current === value;
            return el('button', {
                type: 'button',
                className: 'bowire-envelope-mode-seg' + (active ? ' is-active' : ''),
                title: title,
                'aria-pressed': active ? 'true' : 'false',
                onClick: function () {
                    // Re-read at click time, NOT the closure-captured
                    // render-time value. `spec` is the live envelope
                    // object reference so spec.mode reflects the actual
                    // current selection even after morphdom preserves
                    // this button across renders.
                    if ((spec.mode || 'sequential') === value) return;
                    spec.mode = value;
                    persistBenchmarks();
                    render();
                }
            }, el('span', { textContent: label }));
        }
        return el('div', {
            className: 'bowire-envelope-mode-seg-host',
            role: 'radiogroup',
            'aria-label': 'Per-iteration target dispatch mode'
        },
            seg('sequential', 'Sequential',
                'Targets are invoked one after the other per VU iteration. Iteration fails on first error.'),
            seg('parallel', 'Parallel',
                'Targets are all invoked at once per VU iteration. Iteration succeeds when every target succeeds.')
        );
    }

    // Resolve every target in the spec. Returns true iff all of them
    // can run right now (the referenced service / collection /
    // recording still lives in the workspace). Used to gate the Run
    // button so we don't dispatch into ?MissingService errors.
    function _allTargetsRunnable(spec) {
        var targets = spec.targets || [];
        if (targets.length === 0) return false;
        for (var i = 0; i < targets.length; i++) {
            var t = targets[i];
            if (t.type === 'method') {
                var svc = (typeof services !== 'undefined' ? services : [])
                    .find(function (s) { return s.name === t.service && (!t.protocol || s.source === t.protocol); });
                if (!svc) return false;
                var methods = (svc && Array.isArray(svc.methods)) ? svc.methods : [];
                if (!methods.find(function (m) { return m.name === t.method; })) return false;
            } else if (t.type === 'collection-ref') {
                if (!(typeof collectionsList !== 'undefined'
                        ? collectionsList : []).find(function (c) { return c.id === t.collectionId; })) return false;
            } else if (t.type === 'recording-ref') {
                if (!(typeof recordingsList !== 'undefined'
                        ? recordingsList : []).find(function (r) { return r.id === t.recordingId; })) return false;
            } else if (t.type === 'random') {
                // The pool source must exist AND yield at least one
                // sub-target — otherwise the first iteration would
                // bail with EmptyPool / MissingPool.
                if (t.pool === 'collection') {
                    if (!(typeof collectionsList !== 'undefined'
                            ? collectionsList : []).find(function (c) { return c.id === t.collectionId; })) return false;
                } else if (t.pool === 'recording') {
                    if (!(typeof recordingsList !== 'undefined'
                            ? recordingsList : []).find(function (r) { return r.id === t.recordingId; })) return false;
                }
                if (_expandRandomPool(t).length === 0) return false;
            }
        }
        return true;
    }

    function _targetIcon(target) {
        if (!target) return 'plug';
        if (target.type === 'collection-ref') return 'folder';
        if (target.type === 'recording-ref') return 'recording';
        if (target.type === 'random') return 'lightning';
        return 'plug';
    }

    function _targetSummary(target) {
        if (!target) return '?';
        if (target.type === 'method') {
            var svcMissing = !((typeof services !== 'undefined' ? services : [])
                .find(function (s) { return s.name === target.service; }));
            return (target.service || '—') + '/' + (target.method || '—')
                + (svcMissing && target.service ? ' (not in discovery)' : '');
        }
        if (target.type === 'collection-ref') {
            var col = (typeof collectionsList !== 'undefined' ? collectionsList : [])
                .find(function (c) { return c.id === target.collectionId; });
            return col ? col.name : ('Missing collection · ' + target.collectionId);
        }
        if (target.type === 'recording-ref') {
            var rec = (typeof recordingsList !== 'undefined' ? recordingsList : [])
                .find(function (r) { return r.id === target.recordingId; });
            return rec ? rec.name : ('Missing recording · ' + target.recordingId);
        }
        if (target.type === 'random') {
            var poolSize = _expandRandomPool(target).length;
            var pickN = Math.max(1, target.count || 1);
            var src;
            if (target.pool === 'collection') {
                var rc = (typeof collectionsList !== 'undefined' ? collectionsList : [])
                    .find(function (c) { return c.id === target.collectionId; });
                src = 'collection · ' + (rc ? rc.name : ('missing · ' + target.collectionId));
            } else if (target.pool === 'recording') {
                var rr = (typeof recordingsList !== 'undefined' ? recordingsList : [])
                    .find(function (r) { return r.id === target.recordingId; });
                src = 'recording · ' + (rr ? rr.name : ('missing · ' + target.recordingId));
            } else {
                src = 'workspace';
            }
            return 'Random · pick ' + pickN + ' of ' + poolSize + ' · ' + src;
        }
        return target.type || '?';
    }

    function _renderTargetsSection(spec) {
        var section = el('div', { className: 'bowire-ws-detail-section' });
        section.appendChild(el('div', { className: 'bowire-ws-detail-section-label', textContent: 'Targets' }));

        if ((spec.targets || []).length === 0) {
            section.appendChild(el('div', {
                className: 'bowire-envelope-targets-empty',
                textContent: 'No targets yet — add a method, collection, or recording below.'
            }));
        } else {
            var list = el('div', { className: 'bowire-envelope-targets' });
            spec.targets.forEach(function (target, idx) {
                list.appendChild(_renderTargetRow(spec, target, idx));
            });
            section.appendChild(list);
        }

        // Add-target — single dropdown button replaces the three
        // bespoke buttons. Picks Method / Collection / Recording via
        // showContextMenu; the seeded target's actual id is filled
        // in via the row's inline editor.
        var addBtn = el('button', {
            type: 'button',
            className: 'bowire-envelope-target-add-btn',
            title: 'Add a target (method, collection or recording)',
            onClick: function (ev) {
                if (typeof showContextMenu !== 'function') return;
                showContextMenu(ev.clientX, ev.clientY, [
                    {
                        label: 'Method',
                        onClick: function () {
                            spec.targets.push({
                                type: 'method',
                                service: (typeof selectedService !== 'undefined' && selectedService) ? selectedService.name : '',
                                method: (typeof selectedMethod !== 'undefined' && selectedMethod) ? selectedMethod.name : '',
                                protocol: null, body: '{}', metadata: {}, serverUrl: null
                            });
                            persistBenchmarks(); render();
                        }
                    },
                    {
                        label: 'Collection replay',
                        onClick: function () {
                            spec.targets.push({ type: 'collection-ref', collectionId: null, itemIndex: null });
                            persistBenchmarks(); render();
                        }
                    },
                    {
                        label: 'Recording replay',
                        onClick: function () {
                            spec.targets.push({ type: 'recording-ref', recordingId: null, stepIndex: null });
                            persistBenchmarks(); render();
                        }
                    },
                    {
                        // #231 — Random pool. Pool defaults to 'workspace'
                        // (every discovered method) so the seed is
                        // immediately runnable when the workspace has
                        // any services; operator narrows to a saved
                        // collection or recording via the inline editor.
                        label: 'Random pool',
                        onClick: function () {
                            spec.targets.push({
                                type: 'random', pool: 'workspace',
                                collectionId: null, recordingId: null, count: 1
                            });
                            persistBenchmarks(); render();
                        }
                    }
                ]);
            }
        },
            el('span', { textContent: '+ Add target' }),
            el('span', { className: 'bowire-envelope-add-caret', innerHTML: svgIcon('chevronDown') })
        );
        section.appendChild(addBtn);
        return section;
    }

    function _renderTargetRow(spec, target, idx) {
        var row = el('div', { className: 'bowire-envelope-target-row' });

        // Header — icon, summary, move/remove tools
        var header = el('div', { className: 'bowire-envelope-target-header' });
        header.appendChild(el('span', {
            className: 'bowire-envelope-target-icon',
            innerHTML: svgIcon(_targetIcon(target))
        }));
        header.appendChild(el('span', {
            className: 'bowire-envelope-target-summary',
            textContent: _targetSummary(target)
        }));
        var tools = el('div', { className: 'bowire-envelope-target-tools' });
        if (idx > 0) {
            tools.appendChild(el('button', {
                type: 'button', className: 'bowire-envelope-target-tool-btn', title: 'Move up',
                innerHTML: svgIcon('chevronUp'),
                onClick: function () {
                    var t = spec.targets.splice(idx, 1)[0];
                    spec.targets.splice(idx - 1, 0, t);
                    persistBenchmarks(); render();
                }
            }));
        }
        if (idx < spec.targets.length - 1) {
            tools.appendChild(el('button', {
                type: 'button', className: 'bowire-envelope-target-tool-btn', title: 'Move down',
                innerHTML: svgIcon('chevronDown'),
                onClick: function () {
                    var t = spec.targets.splice(idx, 1)[0];
                    spec.targets.splice(idx + 1, 0, t);
                    persistBenchmarks(); render();
                }
            }));
        }
        tools.appendChild(el('button', {
            type: 'button', className: 'bowire-envelope-target-tool-btn is-danger', title: 'Remove target',
            innerHTML: svgIcon('trash'),
            onClick: function () {
                spec.targets.splice(idx, 1);
                persistBenchmarks(); render();
            }
        }));
        header.appendChild(tools);
        row.appendChild(header);

        // Body — inline editor per target type
        var body = el('div', { className: 'bowire-envelope-target-body' });
        if (target.type === 'method') {
            var svcNames = (typeof services !== 'undefined' ? services : []).map(function (s) { return s.name; });
            var svc = (typeof services !== 'undefined' ? services : []).find(function (s) { return s.name === target.service; });
            var methodNames = (svc && Array.isArray(svc.methods)) ? svc.methods.map(function (m) { return m.name; }) : [];

            body.appendChild(_inlineSelect('Service', target.service, svcNames, function (v) {
                target.service = v;
                target.method = '';
                // Latch the picked service's protocol onto the target
                // so the export layer doesn't have to re-resolve it
                // (and so collection-mixed envelopes keep the right
                // per-target protocol when services span more than
                // one protocol).
                var pickedSvc = (typeof services !== 'undefined' ? services : []).find(function (s) { return s.name === v; });
                target.protocol = pickedSvc ? (pickedSvc.source || null) : null;
                persistBenchmarks(); render();
            }));
            body.appendChild(_inlineSelect('Method', target.method, methodNames, function (v) {
                target.method = v;
                // Auto-seed the body template from the method's input
                // type when the row first picks up a real method — but
                // only when the existing body is the bare '{}' default.
                // Editing the body manually then switching methods
                // preserves the operator's customisation.
                if (v && (!target.body || target.body === '{}')
                        && typeof generateDefaultJson === 'function') {
                    var picked = svc && (svc.methods || []).find(function (mm) { return mm.name === v; });
                    if (picked) {
                        try { target.body = generateDefaultJson(picked.inputType, 0); }
                        catch { /* keep '{}' */ }
                    }
                }
                persistBenchmarks(); render();
            }));
            body.appendChild(el('div', { className: 'bowire-envelope-field' },
                el('div', { className: 'bowire-envelope-field-label', textContent: 'Body template' }),
                el('textarea', {
                    className: 'bowire-envelope-field-textarea',
                    rows: '4', spellcheck: 'false',
                    value: target.body || '{}',
                    onChange: function (e) {
                        target.body = String(e.target.value || ''); persistBenchmarks();
                    }
                })
            ));
        } else if (target.type === 'collection-ref') {
            var colList = (typeof collectionsList !== 'undefined' ? collectionsList : []);
            var colNames = colList.map(function (c) { return c.id + '|' + c.name; });
            body.appendChild(_inlineSelectKeyed('Collection', target.collectionId, colList, 'id', 'name', function (v) {
                target.collectionId = v; persistBenchmarks(); render();
            }));
        } else if (target.type === 'recording-ref') {
            var recList = (typeof recordingsList !== 'undefined' ? recordingsList : []);
            body.appendChild(_inlineSelectKeyed('Recording', target.recordingId, recList, 'id', 'name', function (v) {
                target.recordingId = v; persistBenchmarks(); render();
            }));
        } else if (target.type === 'random') {
            // Pool source — workspace / collection / recording. Picking
            // a different source clears the id of the previously-picked
            // source so we don't end up with a stale collectionId on a
            // 'workspace' pool.
            body.appendChild(_inlineSelect('Pool source',
                target.pool || 'workspace', ['workspace', 'collection', 'recording'],
                function (v) {
                    target.pool = v;
                    if (v !== 'collection') target.collectionId = null;
                    if (v !== 'recording') target.recordingId = null;
                    persistBenchmarks(); render();
                }));
            if (target.pool === 'collection') {
                var rCol = (typeof collectionsList !== 'undefined' ? collectionsList : []);
                body.appendChild(_inlineSelectKeyed('Collection', target.collectionId, rCol, 'id', 'name', function (v) {
                    target.collectionId = v; persistBenchmarks(); render();
                }));
            } else if (target.pool === 'recording') {
                var rRec = (typeof recordingsList !== 'undefined' ? recordingsList : []);
                body.appendChild(_inlineSelectKeyed('Recording', target.recordingId, rRec, 'id', 'name', function (v) {
                    target.recordingId = v; persistBenchmarks(); render();
                }));
            }
            // Pick-per-iteration. Bounded to the live pool size so the
            // operator can't ask for 10 picks from a 3-endpoint pool —
            // _pickRandomSubset would clamp anyway, but surfacing the
            // ceiling makes the constraint visible.
            var poolSize = _expandRandomPool(target).length;
            var maxPick = Math.max(1, poolSize);
            body.appendChild(_numberField('Pick per iteration',
                Math.max(1, target.count || 1), 1, maxPick, function (v) {
                    target.count = v; persistBenchmarks(); render();
                }));
            body.appendChild(el('div', {
                className: 'bowire-envelope-field',
                style: 'grid-column:1/-1'
            },
                el('div', {
                    className: 'bowire-ws-detail-stat-hint',
                    textContent: poolSize === 0
                        ? 'Pool is empty — pick a discovered service / saved collection / recording with at least one entry.'
                        : 'Pool size: ' + poolSize + ' endpoint' + (poolSize === 1 ? '' : 's')
                            + '. Each iteration shuffles the pool and picks the first ' + Math.min(maxPick, target.count || 1) + '.'
                })
            ));
        }
        row.appendChild(body);
        return row;
    }

    function _inlineSelect(label, value, options, onChange) {
        var select = el('select', {
            className: 'bowire-envelope-field-select',
            onChange: function (e) { onChange(e.target.value); }
        });
        var hasMatch = options.indexOf(value) >= 0;
        if (!value || !hasMatch) {
            select.appendChild(el('option', {
                value: value || '', textContent: value ? (value + ' (not in discovery)') : '— Pick —',
                selected: 'selected'
            }));
        }
        options.forEach(function (opt) {
            select.appendChild(el('option', { value: opt, textContent: opt, selected: opt === value ? 'selected' : null }));
        });
        return el('div', { className: 'bowire-envelope-field' },
            el('div', { className: 'bowire-envelope-field-label', textContent: label }),
            select
        );
    }

    function _inlineSelectKeyed(label, value, items, idKey, nameKey, onChange) {
        var select = el('select', {
            className: 'bowire-envelope-field-select',
            onChange: function (e) { onChange(e.target.value || null); }
        });
        select.appendChild(el('option', {
            value: '',
            textContent: value && !items.find(function (i) { return i[idKey] === value; })
                ? 'Missing · ' + value
                : '— Pick —',
            selected: !value ? 'selected' : null
        }));
        items.forEach(function (item) {
            select.appendChild(el('option', {
                value: item[idKey],
                textContent: item[nameKey] || item[idKey],
                selected: item[idKey] === value ? 'selected' : null
            }));
        });
        return el('div', { className: 'bowire-envelope-field' },
            el('div', { className: 'bowire-envelope-field-label', textContent: label }),
            select
        );
    }

    function _renderPhasesSection(spec) {
        var section = el('div', { className: 'bowire-ws-detail-section' });
        section.appendChild(el('div', { className: 'bowire-ws-detail-section-label', textContent: 'Phases' }));

        if ((spec.phases || []).length === 0) {
            section.appendChild(el('div', {
                className: 'bowire-envelope-targets-empty',
                textContent: 'No phases yet — add one to define the load profile.'
            }));
        } else {
            var list = el('div', { className: 'bowire-envelope-phases' });
            spec.phases.forEach(function (phase, idx) {
                list.appendChild(_renderPhaseRow(spec, phase, idx));
            });
            section.appendChild(list);
        }

        section.appendChild(el('button', {
            type: 'button',
            className: 'bowire-envelope-phase-add-btn',
            textContent: '+ Phase',
            title: 'Add a load phase',
            onClick: function () {
                spec.phases.push(defaultEnvelopePhase({ vus: 4, totalIterations: 100 }));
                persistBenchmarks();
                render();
            }
        }));
        return section;
    }

    // Parse '30s' / '5m' / '1h' / plain ms into a number of
    // milliseconds. Returns null on un-parseable input so the caller
    // can ignore the keystroke.
    function _parseDurationToMs(text) {
        if (text == null) return null;
        var s = String(text).trim().toLowerCase();
        if (!s) return null;
        var m = s.match(/^(\d+(?:\.\d+)?)\s*(ms|s|m|h)?$/);
        if (!m) return null;
        var n = parseFloat(m[1]);
        if (isNaN(n)) return null;
        var unit = m[2] || 'ms';
        switch (unit) {
            case 'ms': return Math.round(n);
            case 's':  return Math.round(n * 1000);
            case 'm':  return Math.round(n * 60 * 1000);
            case 'h':  return Math.round(n * 60 * 60 * 1000);
        }
        return null;
    }

    // Format ms back to the shortest readable string. Hidden-helper
    // for the duration input's initial value; the user can type any
    // valid form back.
    function _formatDurationMs(ms) {
        if (ms == null || isNaN(ms)) return '';
        var n = Number(ms);
        if (n >= 3600000 && n % 3600000 === 0) return (n / 3600000) + 'h';
        if (n >= 60000 && n % 60000 === 0) return (n / 60000) + 'm';
        if (n >= 1000 && n % 1000 === 0) return (n / 1000) + 's';
        return n + 'ms';
    }

    function _phaseKind(phase) {
        if (phase.arrivalRate) return 'arrival';
        if (phase.rampToVus != null) return 'ramp';
        if (phase.durationMs) return 'time';
        return 'iter';
    }

    function _renderPhaseRow(spec, phase, idx) {
        var row = el('div', { className: 'bowire-envelope-phase-row' });

        var kind = _phaseKind(phase);
        var kindSel = el('select', {
            className: 'bowire-envelope-field-select',
            title: 'Phase profile',
            onChange: function (e) {
                var k = e.target.value;
                if (k === 'iter') {
                    phase.totalIterations = phase.totalIterations || 100;
                    phase.durationMs = null; phase.rampToVus = null; phase.arrivalRate = null;
                } else if (k === 'time') {
                    phase.durationMs = phase.durationMs || 30000;
                    phase.totalIterations = null; phase.rampToVus = null; phase.arrivalRate = null;
                } else if (k === 'ramp') {
                    phase.durationMs = phase.durationMs || 30000;
                    phase.rampToVus = phase.rampToVus || (phase.vus * 2 || 8);
                    phase.totalIterations = null; phase.arrivalRate = null;
                } else if (k === 'arrival') {
                    phase.durationMs = phase.durationMs || 30000;
                    phase.arrivalRate = phase.arrivalRate || 10;
                    phase.totalIterations = null; phase.rampToVus = null;
                }
                persistBenchmarks();
                render();
            }
        });
        [
            { v: 'iter',    t: 'Fixed iterations' },
            { v: 'time',    t: 'Hold for duration' },
            { v: 'ramp',    t: 'Ramp VUs' },
            { v: 'arrival', t: 'Arrival rate (rps)' }
        ].forEach(function (o) {
            kindSel.appendChild(el('option', { value: o.v, textContent: o.t, selected: o.v === kind ? 'selected' : null }));
        });

        var fields = el('div', { className: 'bowire-envelope-phase-fields' });
        fields.appendChild(_numberField('VUs', phase.vus, 1, 64, function (v) {
            phase.vus = v; persistBenchmarks(); render();
        }));
        if (kind === 'iter') {
            fields.appendChild(_numberField('Iterations', phase.totalIterations || 100, 1, 1000000, function (v) {
                phase.totalIterations = v; persistBenchmarks(); render();
            }));
        } else if (kind === 'time') {
            fields.appendChild(_durationField('Duration', phase.durationMs || 30000, function (v) {
                phase.durationMs = v; persistBenchmarks(); render();
            }));
        } else if (kind === 'ramp') {
            fields.appendChild(_numberField('Target VUs', phase.rampToVus || (phase.vus * 2), 1, 64, function (v) {
                phase.rampToVus = v; persistBenchmarks(); render();
            }));
            fields.appendChild(_durationField('Duration', phase.durationMs || 30000, function (v) {
                phase.durationMs = v; persistBenchmarks(); render();
            }));
        } else if (kind === 'arrival') {
            fields.appendChild(_numberField('Rate (rps)', phase.arrivalRate || 10, 1, 10000, function (v) {
                phase.arrivalRate = v; persistBenchmarks(); render();
            }));
            fields.appendChild(_durationField('Duration', phase.durationMs || 30000, function (v) {
                phase.durationMs = v; persistBenchmarks(); render();
            }));
        }

        var tools = el('div', { className: 'bowire-envelope-phase-tools' });
        if (idx > 0) {
            tools.appendChild(el('button', {
                type: 'button', className: 'bowire-envelope-target-tool-btn', title: 'Move up',
                innerHTML: svgIcon('chevronUp'),
                onClick: function () {
                    var p = spec.phases.splice(idx, 1)[0]; spec.phases.splice(idx - 1, 0, p);
                    persistBenchmarks(); render();
                }
            }));
        }
        if (idx < spec.phases.length - 1) {
            tools.appendChild(el('button', {
                type: 'button', className: 'bowire-envelope-target-tool-btn', title: 'Move down',
                innerHTML: svgIcon('chevronDown'),
                onClick: function () {
                    var p = spec.phases.splice(idx, 1)[0]; spec.phases.splice(idx + 1, 0, p);
                    persistBenchmarks(); render();
                }
            }));
        }
        if (spec.phases.length > 1) {
            tools.appendChild(el('button', {
                type: 'button', className: 'bowire-envelope-target-tool-btn is-danger', title: 'Remove phase',
                innerHTML: svgIcon('trash'),
                onClick: function () {
                    spec.phases.splice(idx, 1); persistBenchmarks(); render();
                }
            }));
        }

        // Two-row layout: kind-select + tools share the header (so
        // the dropdown and remove-button never wrap below the fields
        // on narrow widths); per-kind fields sit below as a wrapping
        // flex group.
        var header = el('div', { className: 'bowire-envelope-phase-header' });
        header.appendChild(kindSel);
        header.appendChild(tools);
        row.appendChild(header);
        row.appendChild(fields);
        return row;
    }

    function _numberField(label, value, min, max, onChange) {
        return el('div', { className: 'bowire-envelope-field bowire-envelope-field-number' },
            el('div', { className: 'bowire-envelope-field-label', textContent: label }),
            el('input', {
                type: 'number',
                min: String(min), max: String(max),
                value: String(value),
                className: 'bowire-envelope-field-input',
                onChange: function (e) {
                    var v = parseInt(e.target.value, 10);
                    if (isNaN(v)) return;
                    onChange(Math.max(min, Math.min(max, v)));
                }
            })
        );
    }

    // Duration input — text field that accepts '30s' / '5m' / '1h' /
    // bare milliseconds. Initial display picks the shortest form
    // (e.g. 60000 → '1m', 30000 → '30s', 500 → '500ms'). Invalid
    // keystrokes leave the model unchanged; the input reverts on
    // blur via render().
    function _durationField(label, ms, onChange) {
        return el('div', { className: 'bowire-envelope-field bowire-envelope-field-number' },
            el('div', { className: 'bowire-envelope-field-label', textContent: label }),
            el('input', {
                type: 'text',
                value: _formatDurationMs(ms),
                placeholder: '30s, 5m, 1h, 60000…',
                className: 'bowire-envelope-field-input',
                onChange: function (e) {
                    var v = _parseDurationToMs(e.target.value);
                    if (v == null || v < 100) return;
                    onChange(v);
                }
            })
        );
    }

    // ---- Main: configure + run + show last result ----

    function renderBenchmarksDetailMain() {
        loadBenchmarks();

        // Pattern B: workspace-prereq branch first. The operator
        // clicked Benchmarks on purpose; the pane explains the prereq
        // and offers the create-CTA instead of bouncing them away.
        if (typeof activeWorkspaceId !== 'undefined'
                && !activeWorkspaceId
                && typeof renderWorkspacePrereqEmpty === 'function') {
            var prereqMain = el('div', {
                id: 'bowire-main-benchmarks',
                className: 'bowire-main bowire-main-benchmarks'
            });
            prereqMain.appendChild(renderWorkspacePrereqEmpty({
                icon: 'chart',
                railLabel: 'Benchmarks',
                railBody: 'Benchmarks repeat N runs at K concurrency and report latency percentiles plus status distribution.'
            }));
            return prereqMain;
        }

        var spec = getBenchmarkSpec(benchmarksSelectedId);
        // #301 — Empty-state branch uses the canonical rail-empty
        // pattern (main shell + bowire-main-pad wrap → renderEmptyCard)
        // so Benchmarks reads identically to Collections / Recordings /
        // Mocks / Flows. The populated branch keeps its own
        // `bowire-main-pad` + `overflow:auto` because the inline editor
        // needs the scroll container; for the empty card the wrapper
        // pattern is what every other rail uses.
        if (!spec) {
            var emptyMain = el('div', {
                id: 'bowire-main-benchmarks',
                className: 'bowire-main bowire-main-benchmarks'
            });
            var emptyWrap = el('div', { className: 'bowire-main-pad' });
            var hasAny = benchmarksList.length > 0;
            emptyWrap.appendChild(renderEmptyCard({
                icon: 'chart',
                headline: hasAny ? 'Pick a benchmark' : 'No benchmarks yet',
                body: hasAny
                    ? 'Pick one in the sidebar to see its config and last run, or start a new benchmark from a method, collection or recording.'
                    : 'A benchmark repeats N runs at K concurrency and reports latency percentiles + status distribution. Three shapes: single method (one unary call), collection (replay every item), recording (replay every step). Each source has a Benchmark button that prefills the right shape — start there, or create an empty spec.',
                actions: hasAny ? [{
                    id: 'bowire-bench-new-btn',
                    label: 'New benchmark',
                    primary: true,
                    onClick: function () {
                        var s = createBenchmarkSpec();
                        benchmarksSelectedId = s.id;
                        render();
                    }
                }] : [
                    {
                        // #303 — stable id consumed by the run-a-benchmark
                        // tour to spotlight the New-benchmark CTA on the
                        // empty card.
                        id: 'bowire-bench-new-btn',
                        label: 'New benchmark',
                        primary: true,
                        onClick: function () {
                            var s = createBenchmarkSpec();
                            benchmarksSelectedId = s.id;
                            render();
                        }
                    },
                    {
                        label: 'Pick a method',
                        onClick: function () {
                            railMode = 'discover';
                            try { localStorage.setItem('bowire_rail_mode', 'discover'); } catch { /* ignore */ }
                            render();
                        }
                    },
                    {
                        label: 'Pick a collection',
                        onClick: function () {
                            railMode = 'collections';
                            try { localStorage.setItem('bowire_rail_mode', 'collections'); } catch { /* ignore */ }
                            render();
                        }
                    },
                    {
                        label: 'Pick a recording',
                        onClick: function () {
                            railMode = 'recordings';
                            try { localStorage.setItem('bowire_rail_mode', 'recordings'); } catch { /* ignore */ }
                            render();
                        }
                    },
                    // #303 — secondary tour for the Benchmarks rail.
                    // Force-mode so the empty-card CTA re-triggers it
                    // even after the saved-once flag is set.
                    {
                        id: 'bowire-bench-empty-tour-btn',
                        label: 'Take a tour',
                        onClick: function () {
                            if (typeof window !== 'undefined'
                                && typeof window.bowireStartRunBenchmarkTour === 'function') {
                                window.bowireStartRunBenchmarkTour({ force: true });
                            }
                        }
                    }
                ]
            }));
            emptyMain.appendChild(emptyWrap);
            return emptyMain;
        }

        var main = el('div', {
            id: 'bowire-main-benchmarks',
            className: 'bowire-main bowire-main-benchmarks bowire-main-pad',
            style: 'overflow:auto'
        });

        // ---- Header: name + delete ----
        main.appendChild(el('div', { className: 'bowire-ws-detail-header' },
            el('input', {
                type: 'text',
                className: 'bowire-ws-detail-name',
                value: spec.name,
                'aria-label': 'Benchmark name',
                onChange: function (e) {
                    var v = String(e.target.value || '').trim();
                    if (v) { spec.name = v; persistBenchmarks(); render(); }
                }
            }),
            el('button', {
                className: 'bowire-ws-detail-switch-btn',
                style: 'color:var(--bowire-danger)',
                title: 'Delete this benchmark',
                textContent: 'Delete',
                onClick: function () {
                    bowireConfirm('Delete benchmark "' + spec.name + '"?', { confirmText: 'Delete', danger: true })
                        .then(function (ok) {
                            if (ok) { deleteBenchmarkSpec(spec.id); render(); }
                        });
                }
            })
        ));

        // ---- #140 Presets bar ----
        // Saves and loads load-profile configs (n / concurrency / body
        // / metadata). Service + method stay tied to the spec — a
        // preset called "100-session staging p95" applies to whatever
        // method this spec is pointing at. Auto-applies the default
        // preset (if any) when this spec is loaded for the first time
        // in this render — guarded by the spec's _autoApplied flag so
        // a user-driven later edit isn't clobbered.
        if (typeof renderPresetsBar === 'function') {
            try {
                var defaultPreset = (typeof getDefaultPreset === 'function')
                    ? getDefaultPreset('benchmarks') : null;
                if (defaultPreset && !spec._autoApplied) {
                    spec._autoApplied = true;
                    var cfg = defaultPreset.config || {};
                    if (cfg.body !== undefined) spec.body = cfg.body;
                    if (cfg.metadata !== undefined) spec.metadata = cfg.metadata;
                    if (cfg.n !== undefined) spec.n = cfg.n;
                    if (cfg.concurrency !== undefined) spec.concurrency = cfg.concurrency;
                    persistBenchmarks();
                }
                main.appendChild(renderPresetsBar({
                    mode: 'benchmarks',
                    snapshot: function () {
                        return {
                            body: spec.body,
                            metadata: spec.metadata,
                            n: spec.n,
                            concurrency: spec.concurrency
                        };
                    },
                    apply: function (cfg) {
                        if (cfg.body !== undefined) spec.body = cfg.body;
                        if (cfg.metadata !== undefined) spec.metadata = cfg.metadata;
                        if (cfg.n !== undefined) spec.n = cfg.n;
                        if (cfg.concurrency !== undefined) spec.concurrency = cfg.concurrency;
                        persistBenchmarks();
                    }
                }));
            } catch (e) { console.warn('[benchmarks] presets bar failed', e); }
        }

        // ---- Envelope summary ----
        var targetsCount = (spec.targets || []).length;
        var phasesCount = (spec.phases || []).length;
        main.appendChild(el('div', {
            className: 'bowire-ws-detail-stat-hint',
            textContent: targetsCount + ' target' + (targetsCount === 1 ? '' : 's')
                + ' · ' + phasesCount + ' phase' + (phasesCount === 1 ? '' : 's')
                + ' · mode: ' + (spec.mode || 'sequential')
        }));

        // ---- Mode toggle (sequential | parallel) ----
        main.appendChild(el('div', { className: 'bowire-ws-detail-section' },
            el('div', { className: 'bowire-ws-detail-section-label', textContent: 'Mode' }),
            _renderModeSegmented(spec)
        ));

        // ---- Targets ----
        main.appendChild(_renderTargetsSection(spec));

        // ---- Phases ----
        main.appendChild(_renderPhasesSection(spec));

        // ---- Run / cancel + progress ----
        var isThisRunning = benchmark.running && benchmarkActiveSpecId === spec.id;
        var canRun = (spec.targets || []).length > 0 && _allTargetsRunnable(spec);
        var runRow = el('div', { className: 'bowire-ws-detail-section', style: 'display:flex;align-items:center;gap:12px' },
            el('button', {
                className: 'bowire-ws-detail-switch-btn',
                style: 'background:' + (isThisRunning ? 'var(--bowire-danger)' : 'var(--bowire-accent)') + ';color:#fff',
                disabled: !canRun && !isThisRunning ? 'disabled' : null,
                textContent: isThisRunning ? 'Stop' : 'Run benchmark',
                onClick: function () {
                    if (isThisRunning) { stopBenchmark(); return; }
                    runBenchmarkSpec(spec, function () { render(); });
                }
            }),
            isThisRunning
                ? el('span', {
                    className: 'bowire-ws-detail-stat-hint',
                    textContent: benchmark.completed + ' / ' + benchmark.total + '  ·  '
                        + benchmark.success + ' OK  ·  ' + benchmark.failure + ' err'
                })
                : (!canRun
                    ? el('span', {
                        className: 'bowire-ws-detail-stat-hint',
                        textContent: 'Add at least one resolvable target to enable Run.'
                    })
                    : null)
        );
        main.appendChild(runRow);

        // ---- #233 Previous-run diff banner ----
        // When the envelope has been run at least twice, surface the
        // p95 / throughput / status-histogram deltas between the two
        // most recent runs at the very top of the result block. The
        // banner is clickable — expanded form shows a per-metric
        // side-by-side table so the operator can drill into any line
        // without leaving the pane. < 2 runs ⇒ nothing rendered
        // (no empty chrome, per ticket acceptance).
        var runsList = Array.isArray(spec.runs) ? spec.runs : [];
        if (runsList.length >= 2) {
            var diff = _diffBenchmarkRuns(
                runsList[runsList.length - 1],
                runsList[runsList.length - 2],
                _benchmarkRegressionThresholdPct()
            );
            if (diff) {
                main.appendChild(_renderRunDiffBanner(spec, diff, runsList));
            }
        }

        // ---- Last run ----
        var last = spec.lastRun;
        if (last && last.stats) {
            var s = last.stats;
            function statTile(label, value, hint) {
                return el('div', { className: 'bowire-ws-detail-stat' },
                    el('div', { className: 'bowire-ws-detail-stat-value', textContent: String(value) }),
                    el('div', { className: 'bowire-ws-detail-stat-label', textContent: label }),
                    hint ? el('div', { className: 'bowire-ws-detail-stat-hint', textContent: hint }) : null
                );
            }
            // Renamed from 'ms' → '_fmtMs' to avoid a strict-mode block-
            // scoped-function vs var conflict: the per-endpoint loop
            // below declares `var ms = last.durations[di++]` at function
            // scope, which clashed with this hoisted function binding.
            // Browsers threw 'Identifier ms has already been declared'.
            function _fmtMs(v) { return Math.round(v * 10) / 10 + ' ms'; }

            // #234 — Export ▾ split-button at the top of the result
            // pane. Primary action (the label area) is "Export as CSV"
            // because the per-method CSV is the most common operator
            // ask; the caret opens a context menu with the other three
            // formats (CSV per-iteration, k6-summary JSON, OTLP JSON).
            // Pattern mirrors _renderTargetsSection's "+ Add target"
            // split-button so the visual vocabulary stays consistent.
            var exportBar = el('div', {
                className: 'bowire-ws-detail-section',
                style: 'display:flex;align-items:center;justify-content:space-between;gap:8px'
            },
                el('div', { className: 'bowire-ws-detail-section-label', textContent: 'Results' }),
                el('div', { style: 'display:inline-flex;align-items:stretch' },
                    el('button', {
                        type: 'button',
                        className: 'bowire-envelope-target-add-btn',
                        title: 'Export per-method CSV (semicolon-separated, UTF-8 BOM)',
                        style: 'border-top-right-radius:0;border-bottom-right-radius:0',
                        onClick: function () { exportSelectedRunAs('csv'); }
                    },
                        el('span', { textContent: 'Export as CSV' })
                    ),
                    el('button', {
                        type: 'button',
                        className: 'bowire-envelope-target-add-btn',
                        title: 'Pick another export format',
                        style: 'border-top-left-radius:0;border-bottom-left-radius:0;border-left:none;padding-left:6px;padding-right:8px',
                        onClick: function (ev) {
                            if (typeof showContextMenu !== 'function') {
                                exportSelectedRunAs('csv');
                                return;
                            }
                            showContextMenu(ev.clientX, ev.clientY, [
                                { label: 'Export as CSV (per method)', icon: 'send',
                                    onClick: function () { exportSelectedRunAs('csv'); } },
                                { label: 'Export as CSV (per iteration)', icon: 'send',
                                    onClick: function () { exportSelectedRunAs('csv-iterations'); } },
                                { separator: true },
                                { label: 'Export as k6-summary JSON', icon: 'send',
                                    onClick: function () { exportSelectedRunAs('k6-summary'); } },
                                { label: 'Export as OTLP metrics JSON', icon: 'send',
                                    onClick: function () { exportSelectedRunAs('otlp'); } }
                            ]);
                        }
                    },
                        el('span', { className: 'bowire-envelope-add-caret', innerHTML: svgIcon('chevronDown') })
                    )
                )
            );
            main.appendChild(exportBar);

            main.appendChild(el('div', { className: 'bowire-ws-detail-section' },
                el('div', { className: 'bowire-ws-detail-section-label', textContent: 'Latency (last run)' }),
                el('div', { className: 'bowire-ws-detail-stats' },
                    statTile('p50', _fmtMs(s.p50)),
                    statTile('p90', _fmtMs(s.p90)),
                    statTile('p95', _fmtMs(s.p95)),
                    statTile('p99', _fmtMs(s.p99)),
                    statTile('avg', _fmtMs(s.avg), 'min ' + _fmtMs(s.min) + ' · max ' + _fmtMs(s.max))
                )
            ));
            main.appendChild(el('div', { className: 'bowire-ws-detail-section' },
                el('div', { className: 'bowire-ws-detail-section-label', textContent: 'Throughput' }),
                el('div', { className: 'bowire-ws-detail-stats' },
                    statTile('rps', (Math.round(s.throughput * 10) / 10) + '/s'),
                    statTile('elapsed', (Math.round(s.totalSeconds * 10) / 10) + ' s'),
                    statTile('total', last.total + '', last.success + ' OK · ' + last.failure + ' err'
                        + (last.cancelled ? ' · cancelled' : ''))
                )
            ));
            // Status histogram — counts per status name.
            var statusKeys = Object.keys(last.statusCounts || {});
            if (statusKeys.length > 0) {
                var maxCount = 0;
                statusKeys.forEach(function (k) { if (last.statusCounts[k] > maxCount) maxCount = last.statusCounts[k]; });
                var histRows = statusKeys.map(function (k) {
                    var count = last.statusCounts[k];
                    var pct = maxCount > 0 ? Math.round((count / maxCount) * 100) : 0;
                    var isErr = k === 'Error' || k === 'NetworkError' || /^[45]/.test(k);
                    return el('div', { style: 'display:flex;align-items:center;gap:8px;margin:4px 0' },
                        el('span', { style: 'min-width:120px;font-size:12px', textContent: k }),
                        el('div', {
                            style: 'flex:1;height:8px;background:var(--bowire-border);border-radius:4px;overflow:hidden'
                        },
                            el('div', {
                                style: 'height:100%;width:' + pct + '%;background:'
                                    + (isErr ? 'var(--bowire-danger)' : 'var(--bowire-accent)')
                            })
                        ),
                        el('span', { style: 'min-width:50px;text-align:right;font-size:12px', textContent: String(count) })
                    );
                });
                main.appendChild(el('div', { className: 'bowire-ws-detail-section' },
                    el('div', { className: 'bowire-ws-detail-section-label', textContent: 'Status distribution' }),
                    el('div', {}, histRows)
                ));
            }

            // #231 — Per-endpoint breakdown. Only renders when the run
            // hit more than one distinct endpoint (random-pool envelopes
            // and multi-target sequential/parallel envelopes both
            // qualify). For each label we report count + avg/p95 over
            // its slice of durations (success rows only — failure rows
            // didn't append to benchmark.durations). Skipped silently
            // when iterationTargets is empty (legacy lastRun records).
            var iterTargets = last.iterationTargets || [];
            if (iterTargets.length > 0 && Array.isArray(last.durations)) {
                var perEndpoint = {};
                // iterationTargets[i] holds the labels invoked on
                // iteration i; durations[i] holds the iteration's total
                // ms when it passed. Bucket the duration into each
                // contributing endpoint so a 'random' pick × 2 records
                // a sample for both picked methods.
                var di = 0;
                for (var ii = 0; ii < iterTargets.length; ii++) {
                    var labels = iterTargets[ii] || [];
                    if (di < last.durations.length) {
                        // Best-effort attribution — iterations are
                        // pushed in completion order alongside durations
                        // (failure iterations don't push a duration so
                        // index walks at duration's pace). Empirically
                        // good enough for the per-endpoint visualisation;
                        // a stricter join would need ordered failure
                        // markers which inflates the scratchpad.
                        var ms = last.durations[di++];
                        labels.forEach(function (lbl) {
                            if (!perEndpoint[lbl]) perEndpoint[lbl] = { count: 0, sum: 0, samples: [] };
                            perEndpoint[lbl].count++;
                            perEndpoint[lbl].sum += ms;
                            perEndpoint[lbl].samples.push(ms);
                        });
                    } else {
                        labels.forEach(function (lbl) {
                            if (!perEndpoint[lbl]) perEndpoint[lbl] = { count: 0, sum: 0, samples: [] };
                            perEndpoint[lbl].count++;
                        });
                    }
                }
                var keys = Object.keys(perEndpoint);
                if (keys.length > 1) {
                    keys.sort(function (a, b) { return perEndpoint[b].count - perEndpoint[a].count; });
                    var rows = keys.map(function (lbl) {
                        var b = perEndpoint[lbl];
                        var avg = b.samples.length > 0 ? b.sum / b.samples.length : 0;
                        var sorted = b.samples.slice().sort(function (x, y) { return x - y; });
                        var p95 = sorted.length > 0
                            ? sorted[Math.min(sorted.length - 1, Math.floor(sorted.length * 0.95))]
                            : 0;
                        return el('div', { style: 'display:flex;align-items:center;gap:8px;margin:4px 0' },
                            el('span', {
                                style: 'flex:1;min-width:0;font-size:12px;white-space:nowrap;overflow:hidden;text-overflow:ellipsis',
                                title: lbl,
                                textContent: lbl
                            }),
                            el('span', { style: 'min-width:60px;text-align:right;font-size:12px',
                                textContent: b.count + '×' }),
                            el('span', { style: 'min-width:80px;text-align:right;font-size:12px;color:var(--bowire-muted)',
                                textContent: 'avg ' + (Math.round(avg * 10) / 10) + ' ms' }),
                            el('span', { style: 'min-width:80px;text-align:right;font-size:12px;color:var(--bowire-muted)',
                                textContent: 'p95 ' + (Math.round(p95 * 10) / 10) + ' ms' })
                        );
                    });
                    main.appendChild(el('div', { className: 'bowire-ws-detail-section' },
                        el('div', { className: 'bowire-ws-detail-section-label', textContent: 'Per-endpoint breakdown' }),
                        el('div', {}, rows)
                    ));
                }
            }
        } else if (!isThisRunning) {
            main.appendChild(el('p', {
                className: 'bowire-ws-detail-stat-hint',
                style: 'padding:16px 0',
                textContent: 'No run yet. Configure the load above and hit Run.'
            }));
        }

        return main;
    }

    // ---------- Phase 4 — Interop (Artillery / k6 / Postman) ----------
    //
    // Artillery JSON is the natural fit for envelope round-trip:
    // config.phases ≈ spec.phases, scenarios[].flow ≈ spec.targets.
    // We emit JSON instead of YAML because Artillery accepts JSON
    // and the browser doesn't ship a YAML parser.
    //
    // k6 is export-only — we generate a complete .js script with
    // `export const options = { stages: [...] }` and a default
    // function body that fetches each method target. Stage shape
    // maps from our (vus, durationMs) phases.
    //
    // Postman is import-only — we accept a Postman Collection JSON
    // and map each request to a method target, then read the
    // collection-level "info.runner.iterations" (or fall back to 1)
    // for the iteration count.

    function _envelopeArtilleryBaseUrl() {
        // Pick the first discovered URL as the Artillery `target` —
        // Artillery is URL-based, our targets are RPC-shape, so this
        // is the best heuristic. Operators can edit after export.
        if (typeof serverUrls !== 'undefined' && serverUrls.length > 0) {
            return serverUrls[0];
        }
        return 'http://localhost:5000';
    }

    // Postman/Artillery use {{var}} interpolation, Bowire uses ${var}.
    // Both syntaxes name the same thing — the variable name — so the
    // rewrite is mechanical. Whitespace inside the braces is folded.
    // No escape mechanism: literal '{{ }}' in source text gets eaten.
    // The reverse direction is symmetric.
    function _postmanVarsToBowire(text) {
        if (text == null) return text;
        return String(text).replace(/\{\{\s*([^{}]+?)\s*\}\}/g, '${$1}');
    }
    function _bowireVarsToHandlebars(text) {
        if (text == null) return text;
        return String(text).replace(/\$\{\s*([^${}]+?)\s*\}/g, '{{$1}}');
    }
    function _convertMetadataVars(metadata, fn) {
        if (!metadata || typeof metadata !== 'object') return metadata;
        var out = {};
        Object.keys(metadata).forEach(function (k) {
            out[k] = fn(metadata[k]);
        });
        return out;
    }

    function _envelopeMethodTargetToArtilleryStep(target) {
        // Bowire's /api/invoke endpoint takes the protocol + service +
        // method in the body — emit a Postman/Artillery-style POST
        // pointing at it so an actual Artillery run hits the local
        // workbench, not the upstream service directly. This keeps
        // semantics consistent (auth, vars, plugin pipeline all run).
        // Bowire's ${var} syntax is rewritten to Artillery's {{var}}
        // so per-iteration substitution stays live on the receiving
        // side.
        return {
            post: {
                url: '/api/invoke',
                json: {
                    service: target.service,
                    method: target.method,
                    protocol: target.protocol,
                    messages: [_bowireVarsToHandlebars(target.body || '{}')],
                    metadata: _convertMetadataVars(target.metadata, _bowireVarsToHandlebars) || null
                }
            }
        };
    }

    function exportEnvelopeAsArtillery(spec) {
        var phases = (spec.phases || []).map(function (p) {
            var ap = {};
            if (p.durationMs) ap.duration = Math.max(1, Math.round(p.durationMs / 1000));
            if (p.totalIterations) {
                // Artillery has no native "fixed iterations" — emulate
                // as a 1-second phase with the iteration count as
                // arrivalCount (Artillery's one-shot batch shape).
                ap.duration = ap.duration || 1;
                ap.arrivalCount = p.totalIterations;
            }
            if (p.arrivalRate) ap.arrivalRate = p.arrivalRate;
            if (p.vus && !ap.arrivalRate && !ap.arrivalCount) {
                // VUs-only phase — translate to arrivalCount × duration
                ap.arrivalRate = p.vus;
            }
            if (p.rampToVus) ap.rampTo = p.rampToVus;
            return ap;
        });

        var flow = [];
        (spec.targets || []).forEach(function (t) {
            if (t.type === 'method') {
                flow.push(_envelopeMethodTargetToArtilleryStep(t));
            } else if (t.type === 'collection-ref') {
                var col = (typeof collectionsList !== 'undefined' ? collectionsList : [])
                    .find(function (c) { return c.id === t.collectionId; });
                if (col && Array.isArray(col.items)) {
                    col.items.forEach(function (it) {
                        flow.push(_envelopeMethodTargetToArtilleryStep(it));
                    });
                }
            } else if (t.type === 'recording-ref') {
                var rec = (typeof recordingsList !== 'undefined' ? recordingsList : [])
                    .find(function (r) { return r.id === t.recordingId; });
                if (rec && Array.isArray(rec.steps)) {
                    rec.steps.forEach(function (st) {
                        flow.push(_envelopeMethodTargetToArtilleryStep({
                            service: st.service,
                            method: st.method,
                            protocol: st.protocol,
                            body: st.request,
                            metadata: st.metadata
                        }));
                    });
                }
            } else if (t.type === 'random') {
                // #231 — Artillery has no native random-pick; the
                // closest fit is `requestFromList` which picks one step
                // at random from an inline list per VU. We emit it
                // `count` times so each Artillery iteration also fires
                // `count` random picks. The pool resolves to concrete
                // method steps via _expandRandomPool at export time —
                // round-trip back to a 'random' target needs a
                // dedicated Bowire-envelope export (the Bowire-native
                // round-trip preserves the type natively).
                var poolItems = _expandRandomPool(t);
                if (poolItems.length > 0) {
                    var randomSteps = poolItems.map(_envelopeMethodTargetToArtilleryStep);
                    for (var rp = 0; rp < Math.max(1, t.count || 1); rp++) {
                        flow.push({ requestFromList: randomSteps });
                    }
                }
            }
        });

        return {
            config: {
                target: _envelopeArtilleryBaseUrl(),
                phases: phases
            },
            scenarios: [{
                name: spec.name || 'Bowire envelope',
                flow: flow
            }]
        };
    }

    function importEnvelopeFromArtillery(parsed) {
        // Best-effort import. Artillery's flow steps are URL-based,
        // so step targets land with empty service/method — the
        // operator wires the real targets up after import. Phases
        // map cleanly.
        var cfg = parsed.config || {};
        var phases = (cfg.phases || []).map(function (ap) {
            var p = {
                vus: ap.arrivalRate || 1,
                totalIterations: ap.arrivalCount || null,
                durationMs: ap.duration ? ap.duration * 1000 : null,
                rampToVus: ap.rampTo || null,
                arrivalRate: ap.arrivalRate || null
            };
            return defaultEnvelopePhase(p);
        });
        if (phases.length === 0) phases = [defaultEnvelopePhase({})];

        var firstScenario = (parsed.scenarios && parsed.scenarios[0]) || { flow: [] };
        var targets = (firstScenario.flow || []).map(function (step) {
            // Only POST/GET/PUT/DELETE/PATCH are interpretable.
            var verbs = ['post', 'get', 'put', 'delete', 'patch'];
            var verb = verbs.find(function (v) { return step[v]; });
            if (!verb) return null;
            var stepCfg = step[verb];
            // Try to recover a method-shape from the URL — if it's
            // an /api/invoke call (our own export), parse the JSON
            // body for service+method.
            if (stepCfg.url === '/api/invoke' && stepCfg.json) {
                return {
                    type: 'method',
                    service: stepCfg.json.service || '',
                    method: stepCfg.json.method || '',
                    protocol: stepCfg.json.protocol || null,
                    body: _postmanVarsToBowire((stepCfg.json.messages && stepCfg.json.messages[0]) || '{}'),
                    metadata: _convertMetadataVars(stepCfg.json.metadata, _postmanVarsToBowire) || {},
                    serverUrl: null
                };
            }
            // Generic URL step — leave service/method blank for the
            // operator to fill in. {{var}} placeholders inside the
            // body get rewritten to ${var} so Bowire's variable
            // resolver picks them up on Run.
            return {
                type: 'method',
                service: '', method: '',
                protocol: null,
                body: stepCfg.json
                    ? _postmanVarsToBowire(JSON.stringify(stepCfg.json))
                    : '{}',
                metadata: {}, serverUrl: stepCfg.url || null
            };
        }).filter(Boolean);
        if (targets.length === 0) {
            targets = [{ type: 'method', service: '', method: '',
                protocol: null, body: '{}', metadata: {}, serverUrl: null }];
        }

        return createBenchmarkSpec({
            name: firstScenario.name || 'Imported from Artillery',
            targets: targets,
            phases: phases,
            mode: 'sequential'
        });
    }

    function exportEnvelopeAsK6(spec) {
        // Map phases to k6 stages. k6 has only vus+duration stages —
        // iteration-bounded phases get translated to vus + estimated
        // duration (1 second per phase as a placeholder; operators
        // adjust). Arrival-rate phases are translated via the
        // ramping-arrival-rate executor in a comment so the user
        // can switch over manually.
        var stages = (spec.phases || []).map(function (p) {
            var target = p.rampToVus || p.vus || 1;
            var durationS = p.durationMs ? Math.max(1, Math.round(p.durationMs / 1000)) : 1;
            return { duration: durationS + 's', target: target };
        });

        // Build the default function — one fetch per method target.
        // 'random' targets emit a small in-script shuffle-and-pick loop
        // that picks `count` distinct entries from an inline pool array
        // each VU iteration. We resolve the pool to concrete sub-targets
        // at export time; round-tripping back to a 'random' target needs
        // the native Bowire-envelope export.
        var targetCalls = [];
        var randomPoolIdx = 0;
        var randomPoolDecls = [];
        (spec.targets || []).forEach(function (t) {
            if (t.type === 'method') {
                targetCalls.push(
                    "    http.post(\n" +
                    "        baseUrl + '/api/invoke',\n" +
                    "        JSON.stringify({\n" +
                    "            service: '" + (t.service || '') + "',\n" +
                    "            method: '" + (t.method || '') + "',\n" +
                    "            protocol: " + (t.protocol ? "'" + t.protocol + "'" : 'null') + ",\n" +
                    "            messages: [" + JSON.stringify(t.body || '{}') + "],\n" +
                    "            metadata: " + JSON.stringify(t.metadata || null) + "\n" +
                    "        }),\n" +
                    "        { headers: { 'Content-Type': 'application/json' } }\n" +
                    "    );"
                );
            } else if (t.type === 'random') {
                var poolItems = _expandRandomPool(t);
                if (poolItems.length === 0) return;
                var poolVar = '__bowirePool' + (randomPoolIdx++);
                var poolPayloads = poolItems.map(function (sub) {
                    return {
                        service: sub.service || '',
                        method: sub.method || '',
                        protocol: sub.protocol || null,
                        messages: [sub.body || '{}'],
                        metadata: sub.metadata || null
                    };
                });
                randomPoolDecls.push(
                    // No-op .replace stripped — '.replace(/\\n/g, "\\n")'
                    // mapped newlines to themselves (CodeQL alert #1779
                    // js/identity-replacement). JSON.stringify with the
                    // 2-space indent already produces the multi-line
                    // shape we want inlined into the generated script.
                    "const " + poolVar + " = " + JSON.stringify(poolPayloads, null, 2) + ";"
                );
                var pickN = Math.max(1, t.count || 1);
                targetCalls.push(
                    "    // #231 random pool — shuffle in place, pick first " + pickN + "\n" +
                    "    {\n" +
                    "        const pool = " + poolVar + ".slice();\n" +
                    "        const pick = Math.min(" + pickN + ", pool.length);\n" +
                    "        for (let i = 0; i < pick; i++) {\n" +
                    "            const j = i + Math.floor(Math.random() * (pool.length - i));\n" +
                    "            [pool[i], pool[j]] = [pool[j], pool[i]];\n" +
                    "        }\n" +
                    "        for (let i = 0; i < pick; i++) {\n" +
                    "            http.post(\n" +
                    "                baseUrl + '/api/invoke',\n" +
                    "                JSON.stringify(pool[i]),\n" +
                    "                { headers: { 'Content-Type': 'application/json' } }\n" +
                    "            );\n" +
                    "        }\n" +
                    "    }"
                );
            }
        });

        return "// Generated from Bowire envelope '" + (spec.name || '(unnamed)') + "'\n" +
            "// Run with: k6 run script.js\n" +
            "import http from 'k6/http';\n" +
            "import { sleep } from 'k6';\n" +
            "\n" +
            "export const options = {\n" +
            "    stages: " + JSON.stringify(stages, null, 2).replace(/\n/g, '\n    ') + "\n" +
            "};\n" +
            "\n" +
            "const baseUrl = '" + _envelopeArtilleryBaseUrl() + "';\n" +
            (randomPoolDecls.length > 0 ? "\n" + randomPoolDecls.join('\n\n') + "\n" : "") +
            "\n" +
            "export default function () {\n" +
            (targetCalls.length > 0 ? targetCalls.join('\n') : "    // No method targets in envelope.") + "\n" +
            "}\n";
    }

    function importEnvelopeFromPostman(parsed) {
        // Walk the Postman Collection's items (possibly nested in
        // folders) and turn each request into a method target. The
        // collection-level "info" carries an optional runner config
        // that hints at iteration count.
        var requests = [];
        function _walk(items) {
            (items || []).forEach(function (it) {
                if (Array.isArray(it.item)) {
                    _walk(it.item);
                } else if (it.request) {
                    requests.push(it);
                }
            });
        }
        _walk((parsed.item) || []);

        var targets = requests.map(function (it) {
            var req = it.request || {};
            var url = typeof req.url === 'string' ? req.url
                : (req.url && (req.url.raw || req.url.path)) || '';
            var bodyText = '{}';
            if (req.body && req.body.raw) bodyText = String(req.body.raw);
            var metadata = {};
            (req.header || []).forEach(function (h) {
                if (h && h.key) metadata[h.key] = h.value || '';
            });
            // Postman variables ({{var}}) get rewritten to Bowire's
            // ${var} on both body and header values so per-iteration
            // substitution stays live after import. Url string carries
            // the same syntax; we rewrite it too for the captured
            // method label.
            return {
                type: 'method',
                service: 'rest',
                method: (req.method || 'GET') + ' ' + _postmanVarsToBowire(url),
                protocol: 'rest',
                body: _postmanVarsToBowire(bodyText),
                metadata: _convertMetadataVars(metadata, _postmanVarsToBowire),
                serverUrl: null
            };
        });
        if (targets.length === 0) {
            targets = [{ type: 'method', service: '', method: '',
                protocol: null, body: '{}', metadata: {}, serverUrl: null }];
        }

        var iter = 1;
        var info = parsed.info || {};
        if (info.runner && info.runner.iterations) iter = parseInt(info.runner.iterations, 10) || 1;

        return createBenchmarkSpec({
            name: info.name || 'Imported from Postman',
            targets: targets,
            phases: [defaultEnvelopePhase({ vus: 1, totalIterations: iter })],
            mode: 'sequential'
        });
    }

    // Trigger a browser download with `text` as `filename` (mime).
    function _downloadEnvelopeArtifact(filename, mime, text) {
        try {
            var blob = new Blob([text], { type: mime });
            var url = URL.createObjectURL(blob);
            var a = document.createElement('a');
            a.href = url;
            a.download = filename;
            document.body.appendChild(a);
            a.click();
            setTimeout(function () {
                URL.revokeObjectURL(url);
                a.remove();
            }, 0);
        } catch (e) {
            console.warn('[bowire] envelope download failed', e);
            toast('Download failed', 'error');
        }
    }

    // Prompt for a file via a hidden <input type=file>, read the
    // text, and hand it to the parser. Used for both Artillery and
    // Postman imports.
    function _pickEnvelopeJsonFile(label, onParsed) {
        var input = document.createElement('input');
        input.type = 'file';
        input.accept = '.json,application/json';
        input.onchange = function () {
            var file = input.files && input.files[0];
            if (!file) return;
            var reader = new FileReader();
            reader.onload = function () {
                try {
                    var parsed = JSON.parse(reader.result || '{}');
                    onParsed(parsed);
                } catch (e) {
                    toast(label + ' import failed: ' + e.message, 'error');
                }
            };
            reader.readAsText(file);
        };
        input.click();
    }

    function exportSelectedEnvelopeAs(format) {
        var spec = getBenchmarkSpec(benchmarksSelectedId);
        if (!spec) { toast('Pick an envelope to export', 'error'); return; }
        var stem = (spec.name || 'envelope').replace(/[^\w\-]+/g, '_');
        if (format === 'artillery') {
            var artillery = exportEnvelopeAsArtillery(spec);
            _downloadEnvelopeArtifact(stem + '.artillery.json', 'application/json',
                JSON.stringify(artillery, null, 2));
            toast('Exported as Artillery JSON', 'success');
        } else if (format === 'k6') {
            var k6 = exportEnvelopeAsK6(spec);
            _downloadEnvelopeArtifact(stem + '.k6.js', 'application/javascript', k6);
            toast('Exported as k6 script', 'success');
        } else if (format === 'native') {
            _downloadEnvelopeArtifact(stem + '.bowire-envelope.json', 'application/json',
                JSON.stringify(spec, null, 2));
            toast('Exported as Bowire envelope', 'success');
        }
    }

    // ---------- #234 — Result exports (CSV / k6-summary / OTLP) ----------
    //
    // These operate on spec.lastRun, NOT on the envelope itself. They
    // are the spreadsheet / dashboard / OTel-collector counterparts to
    // the envelope-shape exports above. Each builder is a pure function
    // (spec → string / object) so a future CLI sidecar can call them
    // without dragging the workbench DOM along.
    //
    //  - exportRunAsCsv(spec, mode)  — 'per-method' (default) or
    //                                  'per-iteration'. Semicolon
    //                                  separator + LF, UTF-8 BOM
    //                                  prefix so Excel honours UTF-8
    //                                  out of the box.
    //  - exportRunAsK6Summary(spec)  — Grafana k6's handleSummary()
    //                                  shape. metrics.http_req_duration
    //                                  carries p50/p90/p95/p99 + min /
    //                                  max / avg; metrics.iterations
    //                                  carries the count + rate;
    //                                  metrics.checks carries the
    //                                  pass / fail rollup. Empty
    //                                  root_group so a k6 dashboard
    //                                  parser sees the well-known
    //                                  scaffold.
    //  - exportRunAsOtlpJson(spec)   — OTLP/JSON metrics payload
    //                                  (one ResourceMetrics with a
    //                                  histogram + counter per target).
    //                                  Downloaded as a file; an OTel
    //                                  collector can replay it with
    //                                  `curl --data-binary @… -H
    //                                  Content-Type: application/json
    //                                  http://collector:4318/v1/metrics`.
    //                                  Direct push to an endpoint
    //                                  is deferred — a file is the
    //                                  safer scope for v1 because
    //                                  it doesn't entangle workbench
    //                                  CORS / auth with collector
    //                                  configuration.

    function _csvCell(v) {
        if (v == null) return '';
        var s = String(v);
        if (s.indexOf(';') >= 0 || s.indexOf('"') >= 0 || s.indexOf('\n') >= 0 || s.indexOf('\r') >= 0) {
            return '"' + s.replace(/"/g, '""') + '"';
        }
        return s;
    }
    function _csvRow(cells) { return cells.map(_csvCell).join(';'); }

    // Stable status-key set across every target-row so columns line
    // up. Drawn from both the overall histogram and every per-target
    // histogram so an unusual status that only one target emitted
    // still gets a column on every row (value 0 where absent).
    function _collectStatusKeys(last) {
        var seen = {};
        Object.keys(last.statusCounts || {}).forEach(function (k) { seen[k] = true; });
        var targetStats = last.targetStats || {};
        Object.keys(targetStats).forEach(function (tk) {
            var sc = targetStats[tk].statusCounts || {};
            Object.keys(sc).forEach(function (k) { seen[k] = true; });
        });
        return Object.keys(seen).sort();
    }

    function exportRunAsCsv(spec, mode) {
        var last = spec && spec.lastRun;
        if (!last) return '';
        var BOM = '﻿';
        var statusKeys = _collectStatusKeys(last);

        if (mode === 'per-iteration') {
            var header = ['i', 'ranAt', 'target', 'targetKey', 'status', 'pass', 'durationMs'];
            var lines = [_csvRow(header)];
            (last.iterations || []).forEach(function (it) {
                lines.push(_csvRow([
                    it.i,
                    new Date(it.ranAt || 0).toISOString(),
                    it.target,
                    it.targetKey,
                    it.status,
                    it.pass ? 'true' : 'false',
                    Math.round((it.durationMs || 0) * 100) / 100
                ]));
            });
            return BOM + lines.join('\n') + '\n';
        }

        // per-method (default) — one row per target. If the envelope
        // had a single target the table degenerates to a single row,
        // which still carries every aggregate the spec produced.
        var header2 = ['method', 'count', 'errors',
            'p50_ms', 'p95_ms', 'p99_ms', 'min_ms', 'max_ms', 'avg_ms',
            'throughput_rps'];
        statusKeys.forEach(function (k) { header2.push('status_' + k); });
        var lines2 = [_csvRow(header2)];

        var targetStats = last.targetStats || {};
        var keys = Object.keys(targetStats);
        var elapsedSec = (last.stats && last.stats.totalSeconds) || 0;
        if (keys.length === 0) {
            // No per-target accounting (legacy lastRun from before
            // #234). Synthesise a single row from the overall stats
            // so a CSV export still works against older runs.
            var s = last.stats || {};
            var row = ['(envelope)', last.total || 0, last.failure || 0,
                Math.round((s.p50 || 0) * 100) / 100,
                Math.round((s.p95 || 0) * 100) / 100,
                Math.round((s.p99 || 0) * 100) / 100,
                Math.round((s.min || 0) * 100) / 100,
                Math.round((s.max || 0) * 100) / 100,
                Math.round((s.avg || 0) * 100) / 100,
                Math.round((s.throughput || 0) * 100) / 100];
            statusKeys.forEach(function (k) { row.push((last.statusCounts || {})[k] || 0); });
            lines2.push(_csvRow(row));
        } else {
            keys.forEach(function (k) {
                var t = targetStats[k];
                var ds = t.durations || [];
                var sum = 0;
                for (var i = 0; i < ds.length; i++) sum += ds[i];
                var avg = ds.length > 0 ? sum / ds.length : 0;
                var min = ds.length > 0 ? ds[0] : 0;
                var max = ds.length > 0 ? ds[ds.length - 1] : 0;
                var rps = elapsedSec > 0 ? (t.count / elapsedSec) : 0;
                var row = [t.label, t.count, t.errors,
                    Math.round(_percentileSorted(ds, 50) * 100) / 100,
                    Math.round(_percentileSorted(ds, 95) * 100) / 100,
                    Math.round(_percentileSorted(ds, 99) * 100) / 100,
                    Math.round(min * 100) / 100,
                    Math.round(max * 100) / 100,
                    Math.round(avg * 100) / 100,
                    Math.round(rps * 100) / 100];
                statusKeys.forEach(function (sk) { row.push((t.statusCounts || {})[sk] || 0); });
                lines2.push(_csvRow(row));
            });
        }
        return BOM + lines2.join('\n') + '\n';
    }

    // k6 handleSummary() shape. See
    // https://k6.io/docs/results-output/end-of-test/custom-summary/
    // The key fields a dashboard consumes are metrics.* (with values
    // / trend on the duration histograms) + root_group. Bowire doesn't
    // run checks/groups so root_group is the empty skeleton k6 emits
    // for tests that don't call group().
    function exportRunAsK6Summary(spec) {
        var last = spec && spec.lastRun;
        if (!last) return null;
        var s = last.stats || {};
        var durations = last.durations || [];
        var sum = 0;
        for (var i = 0; i < durations.length; i++) sum += durations[i];
        var elapsedSec = s.totalSeconds || 0;
        var totalCount = last.total || 0;
        var failCount = last.failure || 0;
        var passCount = last.success || 0;
        var passRate = totalCount > 0 ? passCount / totalCount : 0;
        var iterRate = elapsedSec > 0 ? totalCount / elapsedSec : 0;

        return {
            // k6's default summary stamps the start; we surface the
            // wall-clock approximation we recorded (ranAtWallClock is
            // a Date.now() back-derived from performance.now() — close
            // enough for a dashboard timestamp).
            metrics: {
                http_req_duration: {
                    type: 'trend',
                    contains: 'time',
                    values: {
                        'avg': s.avg || 0,
                        'min': s.min || 0,
                        'max': s.max || 0,
                        'med': s.p50 || 0,
                        'p(90)': s.p90 || 0,
                        'p(95)': s.p95 || 0,
                        'p(99)': s.p99 || 0,
                        'count': durations.length
                    }
                },
                http_reqs: {
                    type: 'counter',
                    contains: 'default',
                    values: {
                        'count': totalCount,
                        'rate': iterRate
                    }
                },
                iterations: {
                    type: 'counter',
                    contains: 'default',
                    values: {
                        'count': totalCount,
                        'rate': iterRate
                    }
                },
                iteration_duration: {
                    type: 'trend',
                    contains: 'time',
                    values: {
                        'avg': s.avg || 0,
                        'min': s.min || 0,
                        'max': s.max || 0,
                        'med': s.p50 || 0,
                        'p(90)': s.p90 || 0,
                        'p(95)': s.p95 || 0,
                        'p(99)': s.p99 || 0
                    }
                },
                http_req_failed: {
                    type: 'rate',
                    contains: 'default',
                    values: {
                        'rate': totalCount > 0 ? failCount / totalCount : 0,
                        'passes': failCount,
                        'fails': passCount
                    }
                },
                checks: {
                    type: 'rate',
                    contains: 'default',
                    values: {
                        'rate': passRate,
                        'passes': passCount,
                        'fails': failCount
                    }
                },
                vus: {
                    type: 'gauge',
                    contains: 'default',
                    values: {
                        'value': (spec.phases && spec.phases[0] && spec.phases[0].vus) || spec.concurrency || 1,
                        'min': 1,
                        'max': (spec.phases || []).reduce(function (acc, p) {
                            return Math.max(acc, parseInt(p.vus, 10) || 0);
                        }, 0) || (spec.concurrency || 1)
                    }
                },
                data_received: {
                    type: 'counter',
                    contains: 'data',
                    values: { 'count': 0, 'rate': 0 }
                },
                data_sent: {
                    type: 'counter',
                    contains: 'data',
                    values: { 'count': 0, 'rate': 0 }
                }
            },
            root_group: {
                name: '',
                path: '',
                id: 'd41d8cd98f00b204e9800998ecf8427e',
                groups: [],
                checks: []
            },
            // Non-k6 extension — Bowire-specific provenance + per-target
            // rollup. k6 consumers ignore unknown top-level keys, so
            // it's safe to carry. Lets a Bowire-aware downstream tool
            // reconstruct the run without re-parsing CSV.
            bowire: {
                envelope: spec.name || spec.id,
                ranAt: last.ranAtWallClock || (last.ranAt && Date.now()) || Date.now(),
                durationSec: elapsedSec,
                cancelled: !!last.cancelled,
                statusCounts: last.statusCounts || {},
                targets: Object.keys(last.targetStats || {}).map(function (k) {
                    var t = last.targetStats[k];
                    var ds = t.durations || [];
                    return {
                        key: t.key,
                        label: t.label,
                        count: t.count,
                        errors: t.errors,
                        p50: _percentileSorted(ds, 50),
                        p95: _percentileSorted(ds, 95),
                        p99: _percentileSorted(ds, 99),
                        statusCounts: t.statusCounts || {}
                    };
                })
            },
            // Required by k6: the original options blob. We emit a
            // stages-only descriptor mirroring the envelope phases so
            // a dashboard parser sees the same shape `k6 run` would
            // have produced.
            options: {
                stages: (spec.phases || []).map(function (p) {
                    return {
                        duration: (parseInt(p.durationMs, 10) || 0) + 'ms',
                        target: parseInt(p.rampToVus || p.vus, 10) || 1
                    };
                })
            }
        };
    }

    // OTLP/JSON metrics. Shape from
    // https://opentelemetry.io/docs/specs/otlp/ (ExportMetricsServiceRequest)
    // — one ResourceMetrics, one ScopeMetrics (scope =
    // Kuestenlogik.Bowire.Benchmarks), three Metrics:
    //  - bowire_bench.iteration.duration_ms  (Histogram, ms)
    //  - bowire_bench.iteration.count        (Sum / counter)
    //  - bowire_bench.run.throughput         (Gauge, rps)
    // Per-target metrics ride the same payload, attributes carry the
    // target key + label so a collector can split by service/method
    // without further hints.
    function _otlpUnixNano(ms) {
        // OTLP timestamps are nanoseconds since epoch. We multiply
        // by 1e6 from millis; downstream collectors accept the string
        // form just as readily as a number.
        return String(Math.round(ms * 1e6));
    }
    function _otlpHistogramBuckets() {
        // k6-aligned bucket boundaries (ms). Coarse enough for a
        // dashboard scrub yet fine in the sub-second band where most
        // RPC latency lives.
        return [1, 2, 5, 10, 20, 50, 100, 200, 500, 1000, 2000, 5000, 10000];
    }
    function _otlpBucketCounts(durations, bounds) {
        var counts = new Array(bounds.length + 1).fill(0);
        for (var i = 0; i < durations.length; i++) {
            var v = durations[i];
            var placed = false;
            for (var b = 0; b < bounds.length; b++) {
                if (v <= bounds[b]) { counts[b]++; placed = true; break; }
            }
            if (!placed) counts[counts.length - 1]++;
        }
        return counts;
    }
    function _otlpHistogramPoint(durations, startMs, endMs, attrs) {
        var bounds = _otlpHistogramBuckets();
        var counts = _otlpBucketCounts(durations || [], bounds);
        var sum = 0;
        var min = durations && durations.length > 0 ? durations[0] : 0;
        var max = 0;
        for (var i = 0; i < durations.length; i++) {
            sum += durations[i];
            if (durations[i] < min) min = durations[i];
            if (durations[i] > max) max = durations[i];
        }
        return {
            attributes: attrs || [],
            startTimeUnixNano: _otlpUnixNano(startMs),
            timeUnixNano: _otlpUnixNano(endMs),
            count: String(durations ? durations.length : 0),
            sum: sum,
            min: min,
            max: max,
            bucketCounts: counts.map(String),
            explicitBounds: bounds
        };
    }
    function _otlpAttr(key, value) {
        if (typeof value === 'number') return { key: key, value: { intValue: String(Math.round(value)) } };
        if (typeof value === 'boolean') return { key: key, value: { boolValue: value } };
        return { key: key, value: { stringValue: String(value == null ? '' : value) } };
    }
    function exportRunAsOtlpJson(spec) {
        var last = spec && spec.lastRun;
        if (!last) return null;
        // Bowire runs on performance.now() — convert to wall-clock by
        // anchoring on ranAtWallClock (the back-derived Date.now()
        // captured at run end). Older lastRun entries that pre-date
        // #234 fall back to "now - duration".
        var elapsedSec = (last.stats && last.stats.totalSeconds) || 0;
        var endMs = (last.ranAtWallClock || (Date.now() - Math.round(elapsedSec * 1000)))
            + Math.round(elapsedSec * 1000);
        var startMs = endMs - Math.round(elapsedSec * 1000);

        var resourceAttrs = [
            _otlpAttr('service.name', 'kuestenlogik.bowire'),
            _otlpAttr('service.namespace', 'bowire.benchmarks'),
            _otlpAttr('bowire.envelope.id', spec.id || ''),
            _otlpAttr('bowire.envelope.name', spec.name || ''),
            _otlpAttr('bowire.run.cancelled', !!last.cancelled)
        ];

        var dataPoints = [];
        var counterPoints = [];
        var throughputPoints = [];

        // Per-target histogram + counter + throughput.
        var keys = Object.keys(last.targetStats || {});
        if (keys.length > 0) {
            keys.forEach(function (k) {
                var t = last.targetStats[k];
                var attrs = [_otlpAttr('bowire.target.key', t.key), _otlpAttr('bowire.target.label', t.label)];
                dataPoints.push(_otlpHistogramPoint(t.durations || [], startMs, endMs, attrs));
                counterPoints.push({
                    attributes: attrs,
                    startTimeUnixNano: _otlpUnixNano(startMs),
                    timeUnixNano: _otlpUnixNano(endMs),
                    asInt: String(t.count || 0)
                });
                counterPoints.push({
                    attributes: attrs.concat([_otlpAttr('outcome', 'error')]),
                    startTimeUnixNano: _otlpUnixNano(startMs),
                    timeUnixNano: _otlpUnixNano(endMs),
                    asInt: String(t.errors || 0)
                });
                throughputPoints.push({
                    attributes: attrs,
                    startTimeUnixNano: _otlpUnixNano(startMs),
                    timeUnixNano: _otlpUnixNano(endMs),
                    asDouble: elapsedSec > 0 ? (t.count / elapsedSec) : 0
                });
            });
        }
        // Always emit an overall ("envelope") rollup so a dashboard
        // can chart total throughput / latency without re-aggregating.
        var overallAttrs = [_otlpAttr('bowire.target.key', 'envelope'), _otlpAttr('bowire.target.label', spec.name || 'envelope')];
        dataPoints.push(_otlpHistogramPoint(last.durations || [], startMs, endMs, overallAttrs));
        counterPoints.push({
            attributes: overallAttrs,
            startTimeUnixNano: _otlpUnixNano(startMs),
            timeUnixNano: _otlpUnixNano(endMs),
            asInt: String(last.total || 0)
        });
        counterPoints.push({
            attributes: overallAttrs.concat([_otlpAttr('outcome', 'error')]),
            startTimeUnixNano: _otlpUnixNano(startMs),
            timeUnixNano: _otlpUnixNano(endMs),
            asInt: String(last.failure || 0)
        });
        throughputPoints.push({
            attributes: overallAttrs,
            startTimeUnixNano: _otlpUnixNano(startMs),
            timeUnixNano: _otlpUnixNano(endMs),
            asDouble: (last.stats && last.stats.throughput) || 0
        });

        return {
            resourceMetrics: [{
                resource: { attributes: resourceAttrs },
                scopeMetrics: [{
                    scope: {
                        name: 'Kuestenlogik.Bowire.Benchmarks',
                        version: '1.0.0'
                    },
                    metrics: [
                        {
                            name: 'bowire_bench.iteration.duration_ms',
                            description: 'Per-iteration latency for a Bowire benchmark envelope',
                            unit: 'ms',
                            histogram: {
                                aggregationTemporality: 2, // CUMULATIVE
                                dataPoints: dataPoints
                            }
                        },
                        {
                            name: 'bowire_bench.iteration.count',
                            description: 'Iterations executed (split by outcome via the `outcome` attribute)',
                            unit: '1',
                            sum: {
                                aggregationTemporality: 2,
                                isMonotonic: true,
                                dataPoints: counterPoints
                            }
                        },
                        {
                            name: 'bowire_bench.run.throughput',
                            description: 'Iterations per second over the run duration',
                            unit: '1/s',
                            gauge: { dataPoints: throughputPoints }
                        }
                    ]
                }]
            }]
        };
    }

    // Result-export dispatcher — pendant to exportSelectedEnvelopeAs.
    // Picks the right builder, the right filename / MIME, and toasts
    // the outcome. Errors when there is no completed run so the
    // Export ▾ menu in the result pane stays visually consistent
    // (button always present; click guarded).
    function exportSelectedRunAs(format) {
        var spec = getBenchmarkSpec(benchmarksSelectedId);
        if (!spec) { toast('Pick an envelope to export', 'error'); return; }
        if (!spec.lastRun) { toast('No run results yet — hit Run first', 'error'); return; }
        var stem = (spec.name || 'run').replace(/[^\w\-]+/g, '_');
        if (format === 'csv') {
            var csv = exportRunAsCsv(spec, 'per-method');
            _downloadEnvelopeArtifact(stem + '.results.csv', 'text/csv;charset=utf-8', csv);
            toast('Exported results as CSV', 'success');
        } else if (format === 'csv-iterations') {
            var csvIter = exportRunAsCsv(spec, 'per-iteration');
            _downloadEnvelopeArtifact(stem + '.iterations.csv', 'text/csv;charset=utf-8', csvIter);
            toast('Exported per-iteration CSV', 'success');
        } else if (format === 'k6-summary') {
            var k6 = exportRunAsK6Summary(spec);
            _downloadEnvelopeArtifact(stem + '.k6-summary.json', 'application/json',
                JSON.stringify(k6, null, 2));
            toast('Exported as k6-summary JSON', 'success');
        } else if (format === 'otlp') {
            var otlp = exportRunAsOtlpJson(spec);
            _downloadEnvelopeArtifact(stem + '.otlp.json', 'application/json',
                JSON.stringify(otlp, null, 2));
            toast('Exported as OTLP metrics (file)', 'success');
        }
    }

    function importEnvelopeFrom(format) {
        if (format === 'artillery') {
            _pickEnvelopeJsonFile('Artillery', function (parsed) {
                var spec = importEnvelopeFromArtillery(parsed);
                benchmarksSelectedId = spec.id;
                railMode = 'benchmarks';
                try { localStorage.setItem('bowire_rail_mode', 'benchmarks'); } catch { /* ignore */ }
                toast('Imported Artillery config', 'success');
                render();
            });
        } else if (format === 'postman') {
            _pickEnvelopeJsonFile('Postman', function (parsed) {
                var spec = importEnvelopeFromPostman(parsed);
                benchmarksSelectedId = spec.id;
                railMode = 'benchmarks';
                try { localStorage.setItem('bowire_rail_mode', 'benchmarks'); } catch { /* ignore */ }
                toast('Imported Postman collection', 'success');
                render();
            });
        } else if (format === 'native') {
            _pickEnvelopeJsonFile('Bowire envelope', function (parsed) {
                // Native imports preserve everything — just clone the
                // raw object with a fresh id so it doesn't collide
                // with an existing entry.
                parsed.id = 'env_' + Math.random().toString(36).slice(2, 10);
                migrateEnvelope(parsed);
                benchmarksList.push(parsed);
                persistBenchmarks();
                benchmarksSelectedId = parsed.id;
                toast('Imported envelope', 'success');
                render();
            });
        }
    }

    // #306 / #314 — register the Benchmarks renderers on the rail-renderer
    // seam (descriptor keys benchmarksSidebar / benchmarksMain) so core
    // resolves them by key instead of a hardcoded railMode arm.
    if (typeof window !== 'undefined') {
        window.__bowireRailRenderers = window.__bowireRailRenderers || {};
        window.__bowireRailRenderers.benchmarksSidebar = renderBenchmarksSidebar;
        window.__bowireRailRenderers.benchmarksMain = renderBenchmarksDetailMain;
    }
