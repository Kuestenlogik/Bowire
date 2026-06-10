    // ---- #131 Phase 1 — Benchmarks rail-mode ----
    //
    // First-class home for performance probing. Phase 1 ships the
    // 'single' shape (one service+method, N calls, K concurrency,
    // latency percentiles + status histogram). Later phases add the
    // collection, recording, random and scheduled shapes that the
    // issue body enumerates — same spec object, different runner.
    //
    // Specs live per-workspace in localStorage under wsKey
    // ('bowire_benchmarks'). Each spec carries its last completed run
    // inline (durations are numbers — even 100k samples is ~800 KB,
    // well within quota). A spec is reusable: the user picks one in
    // the sidebar, tweaks N/concurrency in the detail pane, hits
    // Run, and the result replaces lastRun. Saving a copy is a
    // single button — no version history yet.
    //
    // The runner (runBenchmarkSpec) takes EXPLICIT parameters so it
    // doesn't depend on selectedService/selectedMethod or the request
    // editor — those drive the LEGACY perf-diff path, not this one.
    // That keeps benchmarks runnable from saved specs after a
    // workspace switch, without first navigating back to Discover.

    const BENCHMARKS_KEY = 'bowire_benchmarks';

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
        return benchmarksList;
    }

    function persistBenchmarks() {
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
        var spec = {
            id: makeBenchmarkId(),
            name: (seed && seed.name) || 'New benchmark',
            kind: 'single',
            service: (seed && seed.service) || (selectedService ? selectedService.name : ''),
            method: (seed && seed.method) || (selectedMethod ? selectedMethod.name : ''),
            protocol: (seed && seed.protocol)
                || (selectedService ? (selectedService.source || selectedProtocol) : null),
            serverUrl: null, // resolved at run-time from current selectedService when empty
            body: (seed && seed.body) || '{}',
            metadata: (seed && seed.metadata) || {},
            n: (seed && seed.n) || 100,
            concurrency: (seed && seed.concurrency) || 4,
            createdAt: 0,
            lastRun: null
        };
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

    // Runner — promise-returning, explicit-spec.
    //
    // Returns when every iteration has resolved (or been cancelled).
    // Updates the LIVE `benchmark` scratchpad on every completion so
    // existing perf-diff visualisations keep working; also calls the
    // optional onProgress callback after each batch so the rail-mode
    // detail pane can re-render itself without piggy-backing on the
    // global render() throttle.
    async function runBenchmarkSpec(spec, onProgress) {
        if (!spec || benchmark.running) return null;

        // Resolve the target service so we know which URL to hit. If
        // the spec captured a service that's no longer in the current
        // workspace's discovery, fall back to selectedService — but
        // surface the mismatch in the console for the operator.
        var targetService = (services || []).find(function (s) {
            return s.name === spec.service && (!spec.protocol || s.source === spec.protocol);
        });
        if (!targetService && selectedService && selectedService.name === spec.service) {
            targetService = selectedService;
        }
        if (!targetService) {
            addConsoleEntry({
                type: 'response',
                method: spec.service + '/' + spec.method,
                status: 'Benchmark error',
                body: 'Service "' + spec.service + '" not in current discovery'
            });
            return null;
        }

        // Snapshot what each iteration needs — once. The body template
        // stays raw so each call re-substitutes (${now}/${uuid} are
        // expected to change per call); metadata likewise.
        var bodyTemplate = String(spec.body || '{}');
        var metadataTemplate = spec.metadata || {};
        var n = Math.max(1, parseInt(spec.n, 10) || 1);
        var concurrency = Math.max(1, Math.min(parseInt(spec.concurrency, 10) || 1, 20));
        var serviceUrlParam = serverUrlParamForService(targetService, false);
        var protocolId = spec.protocol || targetService.source || selectedProtocol || undefined;
        var fullName = spec.service + '/' + spec.method;

        resetBenchmark({ n: n, concurrency: concurrency });
        benchmark.running = true;
        benchmark.startTime = performance.now();
        benchmarkActiveSpecId = spec.id;
        addConsoleEntry({
            type: 'request',
            method: fullName,
            status: 'Benchmark',
            body: 'Starting ' + n + ' calls (concurrency ' + concurrency + ')'
        });
        if (typeof onProgress === 'function') onProgress();

        var nextIndex = 0;
        async function worker() {
            while (true) {
                if (benchmark.cancelled) return;
                var idx = nextIndex++;
                if (idx >= n) return;

                var messages = [substituteVars(bodyTemplate)];
                var meta = {};
                for (var k in metadataTemplate) {
                    if (Object.prototype.hasOwnProperty.call(metadataTemplate, k)) {
                        meta[k] = substituteVars(metadataTemplate[k]);
                    }
                }
                meta = await applyAuth(meta);

                var t0 = performance.now();
                try {
                    var resp = await fetch(config.prefix + '/api/invoke' + serviceUrlParam, {
                        method: 'POST',
                        headers: { 'Content-Type': 'application/json' },
                        body: JSON.stringify({
                            service: spec.service,
                            method: spec.method,
                            messages: messages,
                            metadata: Object.keys(meta).length > 0 ? meta : null,
                            protocol: protocolId
                        })
                    });
                    var elapsed = performance.now() - t0;
                    var result = await resp.json();
                    if (result.error || result.title) {
                        benchmark.failure++;
                        benchmark.statusCounts['Error'] = (benchmark.statusCounts['Error'] || 0) + 1;
                    } else {
                        benchmark.success++;
                        benchmark.durations.push(elapsed);
                        var status = result.status || 'OK';
                        benchmark.statusCounts[status] = (benchmark.statusCounts[status] || 0) + 1;
                    }
                } catch (e) {
                    benchmark.failure++;
                    benchmark.statusCounts['NetworkError'] = (benchmark.statusCounts['NetworkError'] || 0) + 1;
                }
                benchmark.completed++;

                // Throttle progress callbacks to ~10/s for large runs.
                if (typeof onProgress === 'function' &&
                    (benchmark.completed % Math.max(1, Math.floor(n / 50)) === 0
                        || benchmark.completed === n)) {
                    onProgress();
                }
            }
        }

        var workers = [];
        for (var w = 0; w < concurrency; w++) workers.push(worker());
        await Promise.all(workers);

        benchmark.endTime = performance.now();
        benchmark.running = false;
        benchmarkActiveSpecId = null;
        var totalMs = benchmark.endTime - benchmark.startTime;
        addConsoleEntry({
            type: 'response',
            method: fullName,
            status: benchmark.cancelled ? 'Cancelled' : 'Benchmark complete',
            durationMs: Math.round(totalMs),
            body: benchmark.success + ' OK / ' + benchmark.failure + ' failed'
        });

        // Persist the result onto the spec so a workspace switch +
        // return shows the last run instead of an empty screen.
        var stats = computeBenchmarkStats();
        spec.lastRun = {
            ranAt: benchmark.startTime,           // perf-time; cosmetic only
            durations: benchmark.durations.slice(),
            statusCounts: Object.assign({}, benchmark.statusCounts),
            success: benchmark.success,
            failure: benchmark.failure,
            total: n,
            startTimeMs: benchmark.startTime,
            endTimeMs: benchmark.endTime,
            cancelled: benchmark.cancelled,
            stats: stats
        };
        persistBenchmarks();
        if (typeof onProgress === 'function') onProgress();
        return spec.lastRun;
    }

    // ---- Sidebar: list of saved benchmark specs ----

    function renderBenchmarksSidebar() {
        var sidebar = el('div', { id: 'bowire-sidebar', className: 'bowire-sidebar bowire-sidebar-mode' });
        loadBenchmarks();

        var header = el('div', { className: 'bowire-env-list-header' },
            el('span', { textContent: 'Benchmarks' }),
            el('button', {
                className: 'bowire-env-add-btn',
                title: 'New benchmark',
                'aria-label': 'New benchmark',
                innerHTML: svgIcon('plus'),
                onClick: function () {
                    var spec = createBenchmarkSpec();
                    benchmarksSelectedId = spec.id;
                    render();
                }
            })
        );
        sidebar.appendChild(header);

        var list = el('div', { id: 'bowire-benchmarks-list', className: 'bowire-env-list' });
        if (benchmarksList.length === 0) {
            list.appendChild(el('div', {
                className: 'bowire-sidebar-empty',
                textContent: 'No benchmarks yet. Add one to probe latency under load.'
            }));
        } else {
            benchmarksList.forEach(function (spec) {
                var isSelected = spec.id === benchmarksSelectedId;
                var isRunning = spec.id === benchmarkActiveSpecId && benchmark.running;
                var meta;
                if (isRunning) {
                    meta = benchmark.completed + '/' + benchmark.total;
                } else if (spec.lastRun && spec.lastRun.stats) {
                    meta = 'p95 ' + Math.round(spec.lastRun.stats.p95) + ' ms';
                } else {
                    meta = spec.n + '×';
                }
                var row = el('div', {
                    id: 'bowire-bench-row-' + spec.id,
                    className: 'bowire-env-list-item' + (isSelected ? ' selected' : ''),
                    onClick: function () {
                        benchmarksSelectedId = spec.id;
                        render();
                    }
                },
                    el('span', {
                        className: 'bowire-env-color-dot',
                        style: 'background:' + (isRunning ? 'var(--bowire-warning)' : 'var(--bowire-accent)')
                    }),
                    el('span', { className: 'bowire-env-list-item-name', textContent: spec.name }),
                    el('span', { style: 'flex:1' }),
                    el('span', { className: 'bowire-env-list-item-meta', textContent: meta })
                );
                list.appendChild(row);
            });
        }
        sidebar.appendChild(list);
        return sidebar;
    }

    // ---- Main: configure + run + show last result ----

    function renderBenchmarksDetailMain() {
        var main = el('div', {
            id: 'bowire-main-benchmarks',
            className: 'bowire-main bowire-main-benchmarks',
            style: 'padding:24px;overflow:auto'
        });
        loadBenchmarks();

        var spec = getBenchmarkSpec(benchmarksSelectedId);
        if (!spec) {
            var hasAny = benchmarksList.length > 0;
            main.appendChild(renderEmptyCard({
                icon: 'chart',
                headline: hasAny ? 'Pick a benchmark' : 'No benchmarks yet',
                body: hasAny
                    ? 'Pick one in the sidebar to see its config and last run, or add a new one.'
                    : 'Benchmarks repeat a single call N times under K concurrency and report latency percentiles + status distribution. Phase 1 ships the single-method shape; collection / recording / random / scheduled probes are tracked on #131.',
                actions: [{
                    label: 'New benchmark',
                    primary: true,
                    onClick: function () {
                        var s = createBenchmarkSpec();
                        benchmarksSelectedId = s.id;
                        render();
                    }
                }]
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

        // ---- Target: service / method ----
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
