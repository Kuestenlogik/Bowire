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
            overflow: (benchmarksList && benchmarksList.length > 0) ? [
                {
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
                }
            ] : null
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
                    meta = spec.n + '×';
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
                                        if (typeof runBenchmark === 'function') runBenchmark(spec.n, spec.concurrency);
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

        // Shape banner — surfaces what's under test so the operator
        // doesn't have to read the title to figure out the kind. Plus a
        // unified label for the Source field below.
        var shapeLabel = spec.kind === 'collection' ? 'Collection'
                       : spec.kind === 'recording' ? 'Recording'
                       : 'Single method';
        main.appendChild(el('div', {
            className: 'bowire-ws-detail-stat-hint',
            textContent: 'Shape: ' + shapeLabel
                + (spec.kind === 'single'
                    ? ' — one unary call repeated N times at K concurrency.'
                    : ' — each session replays the whole sequence; N sessions run at K concurrency.')
        }));

        // Shape-specific source picker. single-method shape keeps the
        // service + method dropdowns; collection / recording shapes
        // show their source label + an Open-in-source-rail shortcut.
        if (spec.kind === 'collection' || spec.kind === 'recording') {
            var sourceList = spec.kind === 'collection'
                ? (typeof collectionsList !== 'undefined' ? collectionsList : [])
                : (typeof recordingsList !== 'undefined' ? recordingsList : []);
            var source = sourceList.find(function (s) { return s.id === spec.sourceId; });
            var sourceSection = el('div', { className: 'bowire-ws-detail-section' });
            sourceSection.appendChild(el('div', { className: 'bowire-ws-detail-section-label', textContent: 'Source' }));
            if (source) {
                sourceSection.appendChild(el('div', { style: 'display:flex;align-items:center;gap:8px' },
                    el('span', { innerHTML: svgIcon(spec.kind === 'collection' ? 'folder' : 'recording'), style: 'width:14px;height:14px;display:inline-flex' }),
                    el('span', { style: 'flex:1', textContent: source.name || '(unnamed)' }),
                    el('button', {
                        className: 'bowire-recording-action-btn',
                        textContent: 'Open',
                        onClick: function () {
                            railMode = spec.kind === 'collection' ? 'collections' : 'recordings';
                            try { localStorage.setItem('bowire_rail_mode', railMode); } catch { /* ignore */ }
                            if (spec.kind === 'collection' && typeof collectionManagerSelectedId !== 'undefined') {
                                collectionManagerSelectedId = source.id;
                            } else if (spec.kind === 'recording' && typeof recordingManagerSelectedId !== 'undefined') {
                                recordingManagerSelectedId = source.id;
                            }
                            render();
                        }
                    })
                ));
            } else {
                sourceSection.appendChild(el('p', {
                    className: 'bowire-ws-detail-stat-hint',
                    textContent: 'Source ' + spec.kind + ' is not in the current workspace. Open the source rail to pick another.'
                }));
            }
            main.appendChild(sourceSection);

            // collection / recording shapes don't need the per-call
            // body / metadata templates — each item or step carries its
            // own. Render the load knobs + Run button inline (smaller
            // surface than the single-method path which also has body
            // / auth pickers).
            main.appendChild(el('div', { className: 'bowire-ws-detail-section' },
                el('div', { className: 'bowire-ws-detail-section-label', textContent: 'Load' }),
                el('div', { className: 'bowire-ws-detail-storage-row', style: 'gap:16px' },
                    el('label', { className: 'bowire-ws-detail-storage-option', style: 'flex:1' },
                        el('div', { textContent: 'Iterations (N)', style: 'font-weight:500;margin-bottom:4px' }),
                        el('input', {
                            type: 'number', min: '1', max: '10000',
                            value: String(spec.n),
                            style: 'width:100%;padding:6px 8px',
                            onChange: function (e) {
                                spec.n = Math.max(1, parseInt(e.target.value, 10) || 1);
                                persistBenchmarks();
                                render();
                            }
                        })
                    ),
                    el('label', { className: 'bowire-ws-detail-storage-option', style: 'flex:1' },
                        el('div', { textContent: 'Concurrency', style: 'font-weight:500;margin-bottom:4px' }),
                        el('input', {
                            type: 'number', min: '1', max: '20',
                            value: String(spec.concurrency),
                            style: 'width:100%;padding:6px 8px',
                            onChange: function (e) {
                                spec.concurrency = Math.max(1, Math.min(20, parseInt(e.target.value, 10) || 1));
                                persistBenchmarks();
                                render();
                            }
                        })
                    )
                )
            ));

            var seqRunning = benchmark.running && benchmarkActiveSpecId === spec.id;
            var seqCanRun = !!source;
            main.appendChild(el('div', { className: 'bowire-ws-detail-section', style: 'display:flex;align-items:center;gap:12px' },
                el('button', {
                    className: 'bowire-ws-detail-switch-btn',
                    style: 'background:' + (seqRunning ? 'var(--bowire-danger)' : 'var(--bowire-accent)') + ';color:#fff',
                    disabled: (!seqCanRun && !seqRunning) ? 'disabled' : null,
                    textContent: seqRunning ? 'Stop' : 'Run benchmark',
                    onClick: function () {
                        if (seqRunning) { stopBenchmark(); return; }
                        runBenchmarkSpec(spec, function () { render(); });
                    }
                }),
                seqRunning
                    ? el('span', {
                        className: 'bowire-ws-detail-stat-hint',
                        textContent: benchmark.completed + ' / ' + benchmark.total + ' sessions'
                    })
                    : null
            ));

            if (spec.lastRun && spec.lastRun.stats) {
                var ls = spec.lastRun;
                var lsStats = ls.stats;
                function _ms(v) { return Math.round(v * 10) / 10 + ' ms'; }
                main.appendChild(el('div', { className: 'bowire-ws-detail-section' },
                    el('div', { className: 'bowire-ws-detail-section-label', textContent: 'Session latency (last run)' }),
                    el('div', { className: 'bowire-ws-detail-stats' },
                        el('div', { className: 'bowire-ws-detail-stat' },
                            el('div', { className: 'bowire-ws-detail-stat-value', textContent: _ms(lsStats.p50) }),
                            el('div', { className: 'bowire-ws-detail-stat-label', textContent: 'p50' })
                        ),
                        el('div', { className: 'bowire-ws-detail-stat' },
                            el('div', { className: 'bowire-ws-detail-stat-value', textContent: _ms(lsStats.p95) }),
                            el('div', { className: 'bowire-ws-detail-stat-label', textContent: 'p95' })
                        ),
                        el('div', { className: 'bowire-ws-detail-stat' },
                            el('div', { className: 'bowire-ws-detail-stat-value', textContent: ls.success + ' / ' + ls.total }),
                            el('div', { className: 'bowire-ws-detail-stat-label', textContent: 'sessions passed' })
                        )
                    )
                ));
            }
            return main;
        }

        // ---- Target: service / method (single-method shape) ----
        // Drop-downs sourced from the current workspace's discovered
        // services. If the spec captures a name that's no longer in
        // discovery the input shows the captured value with a warning
        // dot so operators see the mismatch instead of silently
        // having the wrong target run.
        var serviceNames = (services || []).map(function (s) { return s.name; });
        var hasService = serviceNames.indexOf(spec.service) >= 0;
        var targetService = (services || []).find(function (s) { return s.name === spec.service; });
        var methodNames = (targetService && Array.isArray(targetService.methods))
            ? targetService.methods.map(function (m) { return m.name; })
            : [];
        var hasMethod = methodNames.indexOf(spec.method) >= 0;

        function selectField(label, value, options, hasMatch, onChange) {
            var select = el('select', {
                className: 'bowire-ws-detail-name',
                style: 'min-width:200px',
                onChange: function (e) { onChange(e.target.value); }
            });
            if (!hasMatch && value) {
                select.appendChild(el('option', { value: value, textContent: value + ' (not in discovery)', selected: true }));
            }
            options.forEach(function (opt) {
                select.appendChild(el('option', { value: opt, textContent: opt, selected: opt === value }));
            });
            if (options.length === 0 && hasMatch) {
                // shouldn't happen, but guards a stray empty list
                select.appendChild(el('option', { value: '', textContent: '—' }));
            }
            return el('div', { className: 'bowire-ws-detail-section' },
                el('div', { className: 'bowire-ws-detail-section-label', textContent: label }),
                select
            );
        }

        main.appendChild(selectField('Service', spec.service, serviceNames, hasService, function (v) {
            spec.service = v;
            spec.method = '';        // method choices change with the service
            persistBenchmarks();
            render();
        }));
        main.appendChild(selectField('Method', spec.method, methodNames, hasMethod, function (v) {
            spec.method = v;
            persistBenchmarks();
            render();
        }));

        // ---- Body template ----
        main.appendChild(el('div', { className: 'bowire-ws-detail-section' },
            el('div', { className: 'bowire-ws-detail-section-label', textContent: 'Request body (vars supported)' }),
            el('textarea', {
                className: 'bowire-ws-detail-desc',
                value: spec.body || '{}',
                rows: '6',
                spellcheck: 'false',
                style: 'font-family:var(--bowire-mono);font-size:12px',
                onChange: function (e) {
                    spec.body = String(e.target.value || '');
                    persistBenchmarks();
                }
            })
        ));

        // ---- N + concurrency ----
        main.appendChild(el('div', { className: 'bowire-ws-detail-section' },
            el('div', { className: 'bowire-ws-detail-section-label', textContent: 'Load' }),
            el('div', { className: 'bowire-ws-detail-storage-row', style: 'gap:16px' },
                el('label', { className: 'bowire-ws-detail-storage-option', style: 'flex:1' },
                    el('div', { textContent: 'Iterations (N)', style: 'font-weight:500;margin-bottom:4px' }),
                    el('input', {
                        type: 'number',
                        min: '1',
                        max: '100000',
                        value: String(spec.n),
                        style: 'width:100%;padding:6px 8px',
                        onChange: function (e) {
                            spec.n = Math.max(1, parseInt(e.target.value, 10) || 1);
                            persistBenchmarks();
                            render();
                        }
                    })
                ),
                el('label', { className: 'bowire-ws-detail-storage-option', style: 'flex:1' },
                    el('div', { textContent: 'Concurrency', style: 'font-weight:500;margin-bottom:4px' }),
                    el('input', {
                        type: 'number',
                        min: '1',
                        max: '20',
                        value: String(spec.concurrency),
                        style: 'width:100%;padding:6px 8px',
                        onChange: function (e) {
                            spec.concurrency = Math.max(1, Math.min(20, parseInt(e.target.value, 10) || 1));
                            persistBenchmarks();
                            render();
                        }
                    })
                )
            )
        ));

        // ---- Run / cancel + progress ----
        var isThisRunning = benchmark.running && benchmarkActiveSpecId === spec.id;
        var canRun = !!spec.service && !!spec.method && hasService && hasMethod;
        var runRow = el('div', { className: 'bowire-ws-detail-section', style: 'display:flex;align-items:center;gap:12px' },
            el('button', {
                className: 'bowire-ws-detail-switch-btn',
                style: 'background:' + (isThisRunning ? 'var(--bowire-danger)' : 'var(--bowire-accent)') + ';color:#fff',
                disabled: !canRun && !isThisRunning ? 'disabled' : null,
                textContent: isThisRunning ? 'Stop' : 'Run benchmark',
                onClick: function () {
                    if (isThisRunning) {
                        stopBenchmark();
                        return;
                    }
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
                        textContent: 'Pick a service + method from this workspace to enable Run.'
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
