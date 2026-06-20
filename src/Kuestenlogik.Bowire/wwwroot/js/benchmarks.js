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
            return spec; // already v2
        }
        spec.targets = [targetFromLegacySeed(spec)];
        spec.phases = [defaultEnvelopePhase({
            vus: spec.concurrency,
            totalIterations: spec.n
        })];
        spec.mode = spec.mode || 'sequential';
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
            lastRun: null
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

        return { pass: false, durationMs: 0, statusLabel: 'UnknownType' };
    }

    // One VU iteration walks the spec's targets. mode === 'parallel'
    // fires them all concurrently; pass = ALL pass, durationMs =
    // max. mode === 'sequential' walks them one-by-one; pass = all
    // pass, durationMs = sum.
    async function _runEnvelopeIteration(spec) {
        var targets = Array.isArray(spec.targets) ? spec.targets : [];
        if (targets.length === 0) {
            return { pass: false, durationMs: 0, statusLabel: 'NoTargets' };
        }
        if (spec.mode === 'parallel' && targets.length > 1) {
            var results = await Promise.all(targets.map(_invokeBenchmarkTarget));
            var pass = results.every(function (r) { return r && r.pass; });
            var maxMs = 0;
            var firstFailLabel = null;
            results.forEach(function (r) {
                if (r.durationMs > maxMs) maxMs = r.durationMs;
                if (!r.pass && !firstFailLabel) firstFailLabel = r.statusLabel;
            });
            return {
                pass: pass,
                durationMs: maxMs,
                statusLabel: pass ? 'OK' : (firstFailLabel || 'Error')
            };
        }
        // sequential — walk targets in order, abort on first fail
        var totalMs = 0;
        for (var i = 0; i < targets.length; i++) {
            var rs = await _invokeBenchmarkTarget(targets[i]);
            totalMs += rs.durationMs || 0;
            if (!rs.pass) {
                return { pass: false, durationMs: totalMs, statusLabel: rs.statusLabel };
            }
        }
        return { pass: true, durationMs: totalMs, statusLabel: 'OK' };
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

                var rs = await _runEnvelopeIteration(spec);

                if (rs.pass) {
                    benchmark.success++;
                    benchmark.durations.push(rs.durationMs);
                } else {
                    benchmark.failure++;
                }
                var lbl = rs.statusLabel || (rs.pass ? 'OK' : 'Error');
                benchmark.statusCounts[lbl] = (benchmark.statusCounts[lbl] || 0) + 1;
                benchmark.completed++;

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
        spec.lastRun = {
            ranAt: benchmark.startTime,
            durations: benchmark.durations.slice(),
            statusCounts: Object.assign({}, benchmark.statusCounts),
            success: benchmark.success,
            failure: benchmark.failure,
            total: benchmark.total,
            startTimeMs: benchmark.startTime,
            endTimeMs: benchmark.endTime,
            cancelled: benchmark.cancelled,
            stats: stats
        };
        persistBenchmarks();
        if (typeof onProgress === 'function') onProgress();
        return spec.lastRun;
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
        return target.type || '?';
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
                items.push({ label: 'Import from Artillery JSON…',
                    onClick: function () { importEnvelopeFrom('artillery'); } });
                items.push({ label: 'Import from Postman Collection…',
                    onClick: function () { importEnvelopeFrom('postman'); } });
                items.push({ label: 'Import Bowire envelope…',
                    onClick: function () { importEnvelopeFrom('native'); } });
                if (benchmarksSelectedId && getBenchmarkSpec(benchmarksSelectedId)) {
                    items.push({ separator: true });
                    items.push({ label: 'Export as Artillery JSON',
                        onClick: function () { exportSelectedEnvelopeAs('artillery'); } });
                    items.push({ label: 'Export as k6 script',
                        onClick: function () { exportSelectedEnvelopeAs('k6'); } });
                    items.push({ label: 'Export as Bowire envelope',
                        onClick: function () { exportSelectedEnvelopeAs('native'); } });
                }
                if (benchmarksList && benchmarksList.length > 0) {
                    items.push({ separator: true });
                    items.push({
                        label: 'Delete all benchmarks',
                        danger: true,
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
            })()
        }));

        var list = el('div', { id: 'bowire-benchmarks-list', className: 'bowire-env-list' });
        if (benchmarksList.length === 0) {
            list.appendChild(el('div', {
                className: 'bowire-pane-empty',
                style: 'padding:12px 14px',
                textContent: 'No benchmarks yet.'
            }));
        } else {
            benchmarksList.forEach(function (spec) {
                var isRunning = spec.id === benchmarkActiveSpecId && benchmark.running;
                var meta;
                if (isRunning) {
                    meta = benchmark.completed + '/' + benchmark.total;
                } else if (spec.lastRun && spec.lastRun.stats) {
                    meta = 'p95 ' + Math.round(spec.lastRun.stats.p95) + ' ms';
                } else {
                    meta = _envelopeSidebarMeta(spec);
                }
                list.appendChild(renderSidebarListItem({
                    id: 'bowire-bench-row-' + spec.id,
                    accent: isRunning ? 'var(--bowire-warning)' : 'var(--bowire-accent)',
                    name: spec.name,
                    meta: meta,
                    selected: spec.id === benchmarksSelectedId,
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
                }));
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
        var current = spec.mode || 'sequential';
        function seg(value, label, title) {
            var active = current === value;
            return el('button', {
                type: 'button',
                className: 'bowire-envelope-mode-seg' + (active ? ' is-active' : ''),
                title: title,
                'aria-pressed': active ? 'true' : 'false',
                onClick: function () {
                    if (active) return;
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
            }
        }
        return true;
    }

    function _targetIcon(target) {
        if (!target) return 'plug';
        if (target.type === 'collection-ref') return 'folder';
        if (target.type === 'recording-ref') return 'recording';
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
                target.service = v; target.method = ''; persistBenchmarks(); render();
            }));
            body.appendChild(_inlineSelect('Method', target.method, methodNames, function (v) {
                target.method = v; persistBenchmarks(); render();
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

        row.appendChild(kindSel);
        row.appendChild(fields);
        row.appendChild(tools);
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
        var main = el('div', {
            id: 'bowire-main-benchmarks',
            className: 'bowire-main bowire-main-benchmarks bowire-main-pad',
            style: 'overflow:auto'
        });
        loadBenchmarks();

        var spec = getBenchmarkSpec(benchmarksSelectedId);
        if (!spec) {
            var hasAny = benchmarksList.length > 0;
            main.appendChild(renderEmptyCard({
                icon: 'chart',
                headline: hasAny ? 'Pick a benchmark' : 'No benchmarks yet',
                body: hasAny
                    ? 'Pick one in the sidebar to see its config and last run, or start a new benchmark from a method, collection or recording.'
                    : 'A benchmark repeats N runs at K concurrency and reports latency percentiles + status distribution. Three shapes: single method (one unary call), collection (replay every item), recording (replay every step). Each source has a Benchmark button that prefills the right shape — start there, or create an empty spec.',
                actions: hasAny ? [{
                    label: 'New benchmark',
                    primary: true,
                    onClick: function () {
                        var s = createBenchmarkSpec();
                        benchmarksSelectedId = s.id;
                        render();
                    }
                }] : [
                    {
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
                    }
                ]
            }));
            return main;
        }

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
            function ms(v) { return Math.round(v * 10) / 10 + ' ms'; }
            main.appendChild(el('div', { className: 'bowire-ws-detail-section' },
                el('div', { className: 'bowire-ws-detail-section-label', textContent: 'Latency (last run)' }),
                el('div', { className: 'bowire-ws-detail-stats' },
                    statTile('p50', ms(s.p50)),
                    statTile('p90', ms(s.p90)),
                    statTile('p95', ms(s.p95)),
                    statTile('p99', ms(s.p99)),
                    statTile('avg', ms(s.avg), 'min ' + ms(s.min) + ' · max ' + ms(s.max))
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
        var targetCalls = [];
        (spec.targets || []).forEach(function (t) {
            if (t.type !== 'method') return;
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
