    // ---- Parallel Sessions (#132 Phase 1 — local) ----
    //
    // Live state for the most recently launched parallel-sessions run.
    // Per-session tiles render under the source toolbar while a run is
    // in flight; the toast carries start + finish summary. The state
    // object is shared between collection + recording sessions because
    // the only fields that differ are `kind` ('collection'/'recording')
    // and `sourceName`; the per-session shape is identical.
    //
    // Phase 1 scope (#132):
    //   - Sessions: 1 / 2 / 4 / 8 / custom (capped at 50 for browser sanity)
    //   - Each session walks the source independently (collection split
    //     round-robin, recording replayed in full per session)
    //   - Each session owns an isolated `captured` namespace so concurrent
    //     pre/post-script writes don't collide
    //   - Per-session progress tile in the result UI (current step,
    //     pass count, duration)
    //
    // Phase 2 (separate ticket): distributed across multiple Bowire
    // hosts, stagger ramp-up, per-session env policy, failure policy,
    // landing the saved run in the Benchmarks pane.
    var parallelSessionsState = null;

    // Sessions sketch presets surfaced by the Run-options modal.
    // 1 / 2 / 4 / 8 covers the day-to-day workflow; the custom slot
    // is a free-form input for stress runs (50 cap to keep the
    // browser responsive — beyond that the operator should move to a
    // host-side runner anyway).
    var PARALLEL_SESSION_PRESETS = [1, 2, 4, 8];
    var PARALLEL_SESSION_CAP = 50;

    // Modal-driven Run-options picker. Replaces the bare bowirePrompt
    // so the operator gets the preset shortcuts + an inline preview of
    // what the run will do (collection → round-robin / recording →
    // independent). Resolves to a run-options object
    // { sessions, hosts[], rampUpSeconds, continueOnError, envPool[] }
    // or null on cancel.
    //
    // #132 Phase 2 — the modal grew Hosts / Ramp-up / Failure policy
    // / Env pool fields. When the Hosts field carries a non-empty
    // comma-separated list the workbench dispatches via the
    // coordinator endpoint (/api/parallel/start); otherwise it stays
    // on the Phase 1 in-browser runner.
    function _openRunOptionsModal(kind, sourceId, sourceName) {
        return new Promise(function (resolve) {
            var existing = document.querySelector('.bowire-confirm-overlay');
            if (existing) existing.remove();

            var selected = 2;
            var customMode = false;

            var presetRow = el('div', { className: 'bowire-parallel-presets' });
            var customInput;
            var confirmBtn;
            var hostsInput;
            var rampUpInput;
            var failureSelect;
            var envPoolInput;

            function updateConfirmEnabled() {
                if (!confirmBtn) return;
                var n = customMode ? parseInt(String(customInput.value || '').trim(), 10) : selected;
                confirmBtn.disabled = !(n && n >= 1);
            }

            function selectPreset(n) {
                customMode = false;
                selected = n;
                Array.prototype.forEach.call(
                    presetRow.querySelectorAll('.bowire-parallel-preset'),
                    function (btn) {
                        btn.classList.toggle('selected',
                            !btn.dataset.custom && parseInt(btn.dataset.n, 10) === n);
                    });
                presetRow.querySelector('.bowire-parallel-preset[data-custom="1"]')
                    .classList.remove('selected');
                customInput.disabled = true;
                updateConfirmEnabled();
            }

            PARALLEL_SESSION_PRESETS.forEach(function (n) {
                presetRow.appendChild(el('button', {
                    type: 'button',
                    className: 'bowire-parallel-preset' + (n === selected ? ' selected' : ''),
                    'data-n': String(n),
                    textContent: String(n),
                    onClick: function () { selectPreset(n); }
                }));
            });
            var customBtn = el('button', {
                type: 'button',
                className: 'bowire-parallel-preset',
                'data-custom': '1',
                textContent: 'Custom',
                onClick: function () {
                    customMode = true;
                    Array.prototype.forEach.call(
                        presetRow.querySelectorAll('.bowire-parallel-preset'),
                        function (btn) { btn.classList.remove('selected'); });
                    customBtn.classList.add('selected');
                    customInput.disabled = false;
                    customInput.focus();
                    customInput.select();
                    updateConfirmEnabled();
                }
            });
            presetRow.appendChild(customBtn);

            customInput = el('input', {
                type: 'number',
                className: 'bowire-parallel-custom-input',
                value: '16',
                min: '1',
                max: String(PARALLEL_SESSION_CAP),
                disabled: true,
                'aria-label': 'Custom session count',
                onInput: updateConfirmEnabled
            });

            // Phase-2 fields — Hosts / Ramp-up / Failure / Env pool.
            // Persisted in localStorage so repeat runs against the same
            // executor fleet don't force the operator to retype the
            // host list every time. Stored as a JSON blob keyed by
            // workspace so two workspaces can target different
            // executor fleets without one stomping the other's
            // defaults.
            var savedOpts = _loadParallelDefaults();
            hostsInput = el('input', {
                type: 'text',
                className: 'bowire-parallel-field-input',
                placeholder: 'https://exec-eu.example, https://exec-us.example',
                value: savedOpts.hosts || '',
                'aria-label': 'Hosts (comma-separated)'
            });
            rampUpInput = el('input', {
                type: 'number',
                className: 'bowire-parallel-field-input',
                min: '0',
                step: '1',
                value: String(savedOpts.rampUpSeconds || 0),
                'aria-label': 'Ramp-up seconds'
            });
            failureSelect = el('select', {
                className: 'bowire-parallel-field-input',
                'aria-label': 'Failure policy'
            },
                el('option', { value: 'continue', textContent: 'Continue on error' }),
                el('option', { value: 'abort', textContent: 'Abort on first failure' })
            );
            failureSelect.value = savedOpts.continueOnError === false ? 'abort' : 'continue';
            envPoolInput = el('input', {
                type: 'text',
                className: 'bowire-parallel-field-input',
                placeholder: 'dev, staging, prod',
                value: savedOpts.envPool || '',
                'aria-label': 'Env pool (comma-separated)'
            });

            function makeField(labelText, hint, control) {
                return el('div', { className: 'bowire-parallel-field' },
                    el('label', { className: 'bowire-parallel-field-label', textContent: labelText }),
                    control,
                    hint ? el('div', { className: 'bowire-parallel-field-hint', textContent: hint }) : null
                );
            }

            var hintText = (kind === 'collection')
                ? 'Each session walks a round-robin slice of the collection items. Variable substitution and pre/post-scripts run per-session with an isolated captured namespace.'
                : 'Each session replays the full recording independently. Variable substitution and pre/post-scripts run per-session with an isolated captured namespace.';

            confirmBtn = el('button', {
                className: 'bowire-confirm-btn',
                textContent: 'Run',
                onClick: function () {
                    var n = customMode ? parseInt(String(customInput.value || '').trim(), 10) : selected;
                    if (!n || n < 1) return;
                    if (n > PARALLEL_SESSION_CAP) n = PARALLEL_SESSION_CAP;
                    var hosts = String(hostsInput.value || '')
                        .split(',').map(function (h) { return h.trim(); })
                        .filter(function (h) { return h.length > 0; });
                    var envPool = String(envPoolInput.value || '')
                        .split(',').map(function (h) { return h.trim(); })
                        .filter(function (h) { return h.length > 0; });
                    var rampUp = parseFloat(rampUpInput.value);
                    if (!isFinite(rampUp) || rampUp < 0) rampUp = 0;
                    var continueOnError = failureSelect.value !== 'abort';
                    var opts = {
                        sessions: n,
                        hosts: hosts,
                        rampUpSeconds: rampUp,
                        continueOnError: continueOnError,
                        envPool: envPool
                    };
                    _persistParallelDefaults({
                        hosts: hostsInput.value,
                        rampUpSeconds: rampUp,
                        continueOnError: continueOnError,
                        envPool: envPoolInput.value
                    });
                    overlay.remove();
                    resolve(opts);
                }
            });
            var cancelBtn = el('button', {
                className: 'bowire-confirm-btn cancel',
                textContent: 'Cancel',
                onClick: function () { overlay.remove(); resolve(null); }
            });

            // #140 — Presets bar at the top of the Run-options modal.
            // Saves the entire opts shape (sessions count + hosts +
            // ramp-up + failure policy + env pool) as a named preset.
            // Apply re-seeds every form field; the next run-options
            // modal open then pre-fills from the preset's snapshot via
            // the regular _loadParallelDefaults path because Apply also
            // persists the same defaults. Mirrors the renderPresetsBar
            // call shape benchmarks.js uses.
            var presetsBarSlot = null;
            if (typeof renderPresetsBar === 'function') {
                try {
                    presetsBarSlot = renderPresetsBar({
                        mode: 'parallel',
                        snapshot: function () {
                            var n = customMode ? parseInt(String(customInput.value || '').trim(), 10) : selected;
                            return {
                                sessions: n,
                                hosts: hostsInput ? hostsInput.value : '',
                                rampUpSeconds: rampUpInput ? parseFloat(rampUpInput.value) || 0 : 0,
                                continueOnError: failureSelect ? failureSelect.value !== 'abort' : true,
                                envPool: envPoolInput ? envPoolInput.value : ''
                            };
                        },
                        apply: function (cfg) {
                            if (!cfg) return;
                            if (typeof cfg.sessions === 'number' && cfg.sessions >= 1) {
                                if (PARALLEL_SESSION_PRESETS.indexOf(cfg.sessions) >= 0) {
                                    selectPreset(cfg.sessions);
                                } else {
                                    customMode = true;
                                    Array.prototype.forEach.call(
                                        presetRow.querySelectorAll('.bowire-parallel-preset'),
                                        function (btn) { btn.classList.remove('selected'); });
                                    var cb = presetRow.querySelector('.bowire-parallel-preset[data-custom="1"]');
                                    if (cb) cb.classList.add('selected');
                                    customInput.disabled = false;
                                    customInput.value = String(cfg.sessions);
                                    updateConfirmEnabled();
                                }
                            }
                            if (typeof cfg.hosts === 'string') hostsInput.value = cfg.hosts;
                            if (cfg.rampUpSeconds !== undefined) rampUpInput.value = String(cfg.rampUpSeconds);
                            if (cfg.continueOnError !== undefined) {
                                failureSelect.value = cfg.continueOnError === false ? 'abort' : 'continue';
                            }
                            if (typeof cfg.envPool === 'string') envPoolInput.value = cfg.envPool;
                            // Mirror the live form into the persisted
                            // defaults so the next plain modal-open path
                            // (no preset apply) sees the same shape.
                            _persistParallelDefaults({
                                hosts: hostsInput.value,
                                rampUpSeconds: parseFloat(rampUpInput.value) || 0,
                                continueOnError: failureSelect.value !== 'abort',
                                envPool: envPoolInput.value
                            });
                        }
                    });
                } catch (e) { /* presets.js not loaded — skip the bar */ }
            }

            var dialog = el('div', {
                className: 'bowire-confirm-dialog bowire-parallel-dialog',
                role: 'dialog',
                'aria-modal': 'true',
                'aria-labelledby': 'bowire-parallel-title'
            },
                el('div', { id: 'bowire-parallel-title', className: 'bowire-confirm-title',
                    textContent: 'Run options — ' + (sourceName || (kind === 'collection' ? 'collection' : 'recording')) }),
                presetsBarSlot,
                el('div', { className: 'bowire-confirm-message', textContent: 'How many parallel sessions?' }),
                presetRow,
                customInput,
                el('div', { className: 'bowire-parallel-hint', textContent: hintText }),
                el('div', { className: 'bowire-parallel-section-label', textContent: 'Distribution + policy (optional)' }),
                makeField('Hosts',
                    'Comma-separated Bowire host URLs. Empty = run in-browser; one or more = coordinator fans sessions out via /api/parallel/start.',
                    hostsInput),
                makeField('Ramp-up (seconds)',
                    '0 = launch all sessions at once. >0 = spread session starts evenly over N seconds.',
                    rampUpInput),
                makeField('Failure policy', null, failureSelect),
                makeField('Env pool',
                    'Comma-separated env ids. Session k uses pool[k % len]. Empty = active env for every session.',
                    envPoolInput),
                el('div', { className: 'bowire-confirm-actions' }, cancelBtn, confirmBtn)
            );

            var overlay = el('div', {
                className: 'bowire-confirm-overlay',
                onClick: function (e) { if (e.target === overlay) { overlay.remove(); resolve(null); } }
            }, dialog);
            document.body.appendChild(overlay);
            updateConfirmEnabled();
        });
    }

    // Run-options defaults — persisted per-workspace so the host list
    // and ramp-up survive a page reload. Wrapped in try/catch because
    // localStorage may be unavailable (private windows, quota errors)
    // — silent fallback keeps the modal usable.
    var PARALLEL_DEFAULTS_KEY = 'bowire_parallel_defaults_v1';
    function _loadParallelDefaults() {
        try {
            var raw = localStorage.getItem(wsKey(PARALLEL_DEFAULTS_KEY));
            return raw ? JSON.parse(raw) : {};
        } catch { return {}; }
    }
    function _persistParallelDefaults(opts) {
        try {
            localStorage.setItem(wsKey(PARALLEL_DEFAULTS_KEY), JSON.stringify(opts));
        } catch { /* quota / disabled — defaults just won't survive reload */ }
    }

    function _promptAndRunParallel(kind, sourceId, sourceName) {
        _openRunOptionsModal(kind, sourceId, sourceName).then(function (opts) {
            if (opts == null) return;
            _runParallelSessions(kind, sourceId, sourceName, opts);
        });
    }

    async function _runParallelSessions(kind, sourceId, sourceName, opts) {
        // Back-compat: callers that still pass a bare number get
        // promoted to the opts shape with empty hosts (local-only,
        // matches Phase 1 behaviour).
        if (typeof opts === 'number') {
            opts = { sessions: opts, hosts: [], rampUpSeconds: 0,
                continueOnError: true, envPool: [] };
        }
        var sessionCount = opts.sessions;
        var source = (kind === 'collection')
            ? collectionsList.find(function (c) { return c.id === sourceId; })
            : recordingsList.find(function (r) { return r.id === sourceId; });
        if (!source) {
            toast('Source ' + kind + ' not found', 'error');
            return;
        }

        var totalSteps = (kind === 'collection')
            ? (Array.isArray(source.items) ? source.items.length : 0)
            : (Array.isArray(source.steps) ? source.steps.length : 0);
        if (totalSteps === 0) {
            toast('Nothing to run — source is empty', 'error');
            return;
        }

        // Phase 2 — when the operator filled the Hosts field, the
        // distributed coordinator endpoint owns the run. We POST the
        // full target list + session count + ramp-up + failure policy
        // and let the coordinator shard sessions across hosts. The
        // per-session live tiles fall back to a single
        // "distributed run" summary tile in this path because the
        // browser doesn't observe per-step progress on remote hosts.
        if (Array.isArray(opts.hosts) && opts.hosts.length > 0) {
            return _runDistributedParallel(kind, sourceId, sourceName, source, opts);
        }

        // Per-session work distribution.
        //   collection: round-robin (session k owns items where idx % N === k)
        //               so a 10-item collection across 4 sessions gives
        //               sessions {3,3,2,2} items — even load with N coprime.
        //   recording:  every session replays every step (independent
        //               sessions — the parallel-load shape, not a
        //               sharded one).
        var sessions = [];
        for (var i = 0; i < sessionCount; i++) {
            var workIndices;
            if (kind === 'collection') {
                workIndices = [];
                for (var j = i; j < totalSteps; j += sessionCount) workIndices.push(j);
            } else {
                workIndices = null; // recording — all steps
            }
            sessions.push({
                index: i,
                status: 'running',
                pass: null,
                durationMs: 0,
                error: null,
                // Per-session captured-vars bag — keeps concurrent
                // ctx.vars.captured.* writes from colliding. Each
                // session reads + writes its own object; render +
                // post-run inspection can show the per-session
                // capture set without a global-bag race.
                captured: {},
                stepIndex: -1,
                stepTotal: kind === 'collection' ? workIndices.length : totalSteps,
                workIndices: workIndices,
                passCount: 0,
                failCount: 0,
                results: []
            });
        }
        parallelSessionsState = {
            kind: kind,
            sourceId: sourceId,
            sourceName: sourceName,
            sessionCount: sessionCount,
            totalSteps: totalSteps,
            sessions: sessions,
            status: 'running',
            startedAt: Date.now(),
            durationMs: 0
        };
        render();
        toast('Starting ' + sessionCount + ' parallel sessions on ' + sourceName, 'info');

        var runner = (kind === 'collection') ? _runCollectionSession : _runRecordingSession;
        var startMs = performance.now();
        var promises = sessions.map(function (s) {
            return runner(source, s).then(function (result) {
                s.status = 'done';
                s.pass = !!result.pass;
                s.durationMs = result.durationMs;
                s.results = result.results;
                render();
                return result;
            }, function (err) {
                s.status = 'done';
                s.pass = false;
                s.error = (err && err.message) || String(err);
                render();
                return { pass: false };
            });
        });
        await Promise.all(promises);
        parallelSessionsState.status = 'done';
        parallelSessionsState.durationMs = performance.now() - startMs;

        var passed = sessions.filter(function (s) { return s.pass; }).length;
        var failed = sessionCount - passed;
        toast(
            sessionCount + ' sessions finished — ' + passed + ' passed, ' + failed + ' failed'
            + ' in ' + Math.round(parallelSessionsState.durationMs) + ' ms',
            failed === 0 ? 'success' : 'error'
        );
        render();
    }

    // #132 Phase 2 — Build the flat target list the coordinator
    // endpoint replays. The browser pre-resolves env-var substitution
    // and turns each recording step / collection item into a
    // self-contained { url, method, body, headers, label, protocol }
    // record so the worker stays oblivious to the source shape and
    // doesn't need to ship the recording schema (or its protocol
    // plugins) on every executor host.
    //
    // Both kinds collapse to "POST against the source's serverUrl with
    // the substituted body". This is the same wire shape the
    // collection runner's runCollectionItem emits — the executor host
    // just hits the URL directly instead of routing through its own
    // /api/invoke (which would need the recording's protocol plugin to
    // be installed on every executor).
    function _buildParallelTargets(kind, source) {
        var out = [];
        var raw = (kind === 'collection') ? (source.items || []) : (source.steps || []);
        for (var i = 0; i < raw.length; i++) {
            var step = raw[i];
            if (step.methodType && step.methodType !== 'Unary') continue;
            var bodyText = step.body
                || (Array.isArray(step.messages) ? step.messages[0] : '')
                || '{}';
            var substitutedBody = (typeof substituteVars === 'function')
                ? substituteVars(bodyText) : bodyText;
            var headers = {};
            if (step.metadata) {
                Object.keys(step.metadata).forEach(function (k) {
                    var v = String(step.metadata[k]);
                    headers[k] = (typeof substituteVars === 'function')
                        ? substituteVars(v) : v;
                });
            }
            var serverUrl = step.serverUrl
                || ((typeof serverUrls !== 'undefined' && serverUrls.length > 0)
                    ? serverUrls[0] : '');
            // Default to the gRPC-style {server}/{service}/{method}
            // path. REST steps already carry the path on serverUrl
            // (Postman import seeds serverUrl with the full URL), so
            // the / service / method tail collapses to "//" which
            // most servers accept. The executor host can be a real
            // upstream or a Bowire host running in mock mode.
            var url = serverUrl;
            if (step.service && step.method
                && url && url.indexOf(step.service) === -1) {
                url = url.replace(/\/+$/, '') + '/'
                    + step.service + '/' + step.method;
            }
            out.push({
                url: url,
                method: step.httpVerb || (step.protocol === 'rest' ? 'POST' : 'POST'),
                body: substitutedBody,
                headers: Object.keys(headers).length > 0 ? headers : null,
                label: (step.service ? step.service + '/' : '')
                    + (step.method || 'step ' + (i + 1)),
                protocol: step.protocol || null
            });
        }
        return out;
    }

    async function _runDistributedParallel(kind, sourceId, sourceName, source, opts) {
        var targets = _buildParallelTargets(kind, source);
        if (targets.length === 0) {
            toast('Nothing to run — no unary targets in this ' + kind, 'error');
            return;
        }
        // Render one synthetic "host" tile per remote host so the
        // operator gets at-a-glance feedback while the coordinator
        // is in flight. Per-step tiles only land after the
        // coordinator returns; this matches the Phase-1 live-tile
        // pattern within the constraints of remote execution.
        parallelSessionsState = {
            kind: kind,
            sourceId: sourceId,
            sourceName: sourceName,
            sessionCount: opts.sessions,
            totalSteps: targets.length,
            sessions: [],
            distributed: true,
            hosts: opts.hosts.slice(),
            status: 'running',
            startedAt: Date.now(),
            durationMs: 0
        };
        render();
        toast('Dispatching ' + opts.sessions + ' sessions across '
            + opts.hosts.length + ' host' + (opts.hosts.length === 1 ? '' : 's'), 'info');

        var startMs = performance.now();
        var body = {
            targets: targets,
            sessionCount: opts.sessions,
            hosts: opts.hosts,
            rampUpSeconds: opts.rampUpSeconds || 0,
            continueOnError: opts.continueOnError !== false,
            envPool: Array.isArray(opts.envPool) ? opts.envPool : []
        };
        try {
            var resp = await fetch(config.prefix + '/api/parallel/start', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify(body)
            });
            var json = await resp.json();
            if (!resp.ok) {
                parallelSessionsState.status = 'done';
                parallelSessionsState.durationMs = performance.now() - startMs;
                toast('Distributed run failed: '
                    + (json && json.title ? json.title : ('HTTP ' + resp.status)),
                    'error');
                render();
                return;
            }
            // Project the wire shape onto the per-session tile model so
            // the existing renderParallelSessionsPanel can show the
            // result without a separate render path. Each session
            // tile carries its host (derived from the hosts[] roll-up
            // — sessions land in host order in the merged response).
            var perHostSessions = [];
            (json.hosts || []).forEach(function (h) {
                for (var k = 0; k < h.sessionCount; k++) perHostSessions.push(h.host);
            });
            // #132 Phase 1 polish — slot the flat per-target results
            // back onto their session so the aggregate panel (which
            // walks session.results[]) sees the same shape for local
            // and distributed runs.
            var resultsBySession = {};
            (json.results || []).forEach(function (r) {
                var key = r.sessionIndex != null ? r.sessionIndex : 0;
                if (!resultsBySession[key]) resultsBySession[key] = [];
                resultsBySession[key].push({
                    pass: !!r.pass,
                    status: r.status != null ? String(r.status) : (r.pass ? 'OK' : 'Error'),
                    durationMs: r.durationMs || 0,
                    stepIndex: r.targetIndex != null ? r.targetIndex : 0,
                    stepLabel: r.label || ((targets[r.targetIndex] && targets[r.targetIndex].label)
                        || ('target ' + ((r.targetIndex || 0) + 1))),
                    error: r.error || null
                });
            });
            parallelSessionsState.sessions = (json.sessions || []).map(function (s, idx) {
                var sIdx = s.sessionIndex != null ? s.sessionIndex : idx;
                return {
                    index: sIdx,
                    host: perHostSessions[idx] || null,
                    envId: s.envId || null,
                    status: 'done',
                    pass: s.failCount === 0 && !s.aborted,
                    durationMs: s.durationMs || 0,
                    passCount: s.passCount || 0,
                    failCount: s.failCount || 0,
                    stepIndex: -1,
                    stepTotal: targets.length,
                    aborted: s.aborted || null,
                    error: s.aborted || null,
                    results: resultsBySession[sIdx] || []
                };
            });
            parallelSessionsState.hostSummaries = json.hosts || [];
            parallelSessionsState.status = 'done';
            parallelSessionsState.durationMs = performance.now() - startMs;
            toast(
                'Distributed run finished — '
                + (json.passCount || 0) + ' passed, '
                + (json.failCount || 0) + ' failed'
                + ' across ' + (json.hosts || []).length + ' host'
                + ((json.hosts || []).length === 1 ? '' : 's')
                + ' in ' + Math.round(parallelSessionsState.durationMs) + ' ms',
                (json.failCount || 0) === 0 ? 'success' : 'error'
            );
            render();
        } catch (e) {
            parallelSessionsState.status = 'done';
            parallelSessionsState.durationMs = performance.now() - startMs;
            toast('Distributed run failed: ' + (e.message || e), 'error');
            render();
        }
    }

    // One isolated collection session — sequential through its assigned
    // item slice. `session.workIndices` holds the round-robin slice;
    // `session.captured` is the per-session vars bag forwarded into
    // pre/post-scripts so concurrent sessions don't write each other's
    // capture state.
    //
    // #132 Phase 1 polish — each result row carries stepIndex (the
    // source-item index, NOT the per-session slice position) + label
    // so the aggregate panel can roll up the per-step histogram across
    // every session without re-derefencing the source.
    async function _runCollectionSession(col, session) {
        var startMs = performance.now();
        var results = [];
        var allPass = true;
        var indices = session.workIndices || col.items.map(function (_, i) { return i; });
        for (var k = 0; k < indices.length; k++) {
            session.stepIndex = k;
            render();
            var srcIdx = indices[k];
            var item = col.items[srcIdx];
            var stepLabel = (item && (item.name || (item.service && item.method
                ? item.service + '/' + item.method : null)))
                || ('item ' + (srcIdx + 1));
            try {
                var r = await runCollectionItem(item, { capturedBag: session.captured });
                r.stepIndex = srcIdx;
                r.stepLabel = stepLabel;
                results.push(r);
                if (r.pass) session.passCount++; else { session.failCount++; allPass = false; }
            } catch (e) {
                allPass = false;
                session.failCount++;
                results.push({ pass: false, status: 'NetworkError',
                    error: e.message, stepIndex: srcIdx, stepLabel: stepLabel });
            }
        }
        return { pass: allPass, results: results, durationMs: performance.now() - startMs };
    }

    // One isolated recording session — sequential through every step
    // with the per-session captured bag forwarded into pre/post-scripts.
    //
    // #132 Phase 1 polish — same per-step annotation contract as the
    // collection runner so the cross-session aggregator has one shape
    // to consume regardless of source kind.
    async function _runRecordingSession(rec, session) {
        var startMs = performance.now();
        var results = [];
        var allPass = true;
        for (var i = 0; i < rec.steps.length; i++) {
            session.stepIndex = i;
            render();
            var step = rec.steps[i];
            var stepLabel = (step.service && step.method)
                ? (step.service + '/' + step.method)
                : ('step ' + (i + 1));
            try {
                var r = await replaySingleStep(step, { capturedBag: session.captured });
                r.stepIndex = i;
                r.stepLabel = stepLabel;
                results.push(r);
                if (r.pass) session.passCount++; else { session.failCount++; allPass = false; }
            } catch (e) {
                allPass = false;
                session.failCount++;
                results.push({ pass: false, status: 'NetworkError',
                    error: e.message, stepIndex: i, stepLabel: stepLabel });
            }
        }
        return { pass: allPass, results: results, durationMs: performance.now() - startMs };
    }

    // Per-session live tiles rendered under the source toolbar while a
    // parallel run is in flight (and the post-run summary). Caller
    // passes the (kind, sourceId) of the panel it's rendering — we
    // bail when the active parallel-sessions run belongs to a
    // different source so two recordings/collections can have their
    // own panels without cross-contamination.
    function renderParallelSessionsPanel(kind, sourceId) {
        if (!parallelSessionsState) return null;
        if (parallelSessionsState.kind !== kind) return null;
        if (parallelSessionsState.sourceId !== sourceId) return null;

        var st = parallelSessionsState;
        var panel = el('div', { className: 'bowire-parallel-panel' });

        var passed = st.sessions.filter(function (s) { return s.pass; }).length;
        var failed = st.sessions.filter(function (s) { return s.status === 'done' && !s.pass; }).length;
        var running = st.sessions.filter(function (s) { return s.status === 'running'; }).length;
        var headerText = st.status === 'running'
            ? running + ' running, ' + passed + ' passed, ' + failed + ' failed'
            : passed + ' / ' + st.sessionCount + ' sessions passed'
                + ' in ' + Math.round(st.durationMs) + ' ms';
        panel.appendChild(el('div', { className: 'bowire-parallel-panel-header' },
            el('strong', { textContent: 'Parallel sessions' }),
            el('span', { className: 'bowire-parallel-panel-summary', textContent: headerText }),
            st.status === 'done' ? el('button', {
                type: 'button',
                className: 'bowire-parallel-panel-dismiss',
                title: 'Dismiss',
                'aria-label': 'Dismiss parallel sessions panel',
                textContent: '×',
                onClick: function () { parallelSessionsState = null; render(); }
            }) : null
        ));

        // #132 Phase 2 — distributed runs show a per-host roll-up
        // strip above the per-session tiles so the operator sees
        // host-level pass/fail / latency at a glance. Local runs
        // skip this (no host fan-out, no roll-up).
        if (st.distributed && Array.isArray(st.hostSummaries) && st.hostSummaries.length > 0) {
            var hostStrip = el('div', { className: 'bowire-parallel-hosts-strip' });
            st.hostSummaries.forEach(function (h) {
                var statusClass = h.error
                    ? ' fail'
                    : (h.failCount > 0 ? ' fail' : ' pass');
                hostStrip.appendChild(el('div', { className: 'bowire-parallel-host-chip' + statusClass,
                        title: h.error || (h.passCount + ' passed, ' + h.failCount + ' failed in ' + h.durationMs + ' ms') },
                    el('span', { className: 'bowire-parallel-host-chip-name', textContent: h.host }),
                    el('span', { className: 'bowire-parallel-host-chip-count',
                        textContent: h.sessionCount + 's' })
                ));
            });
            panel.appendChild(hostStrip);
        } else if (st.distributed && Array.isArray(st.hosts) && st.status === 'running') {
            // Pre-result placeholder — show the host list so the
            // operator can confirm the dispatch went out.
            var pending = el('div', { className: 'bowire-parallel-hosts-strip' });
            st.hosts.forEach(function (host) {
                pending.appendChild(el('div', { className: 'bowire-parallel-host-chip running',
                        title: 'Dispatch in flight' },
                    el('span', { className: 'bowire-parallel-host-chip-name', textContent: host })
                ));
            });
            panel.appendChild(pending);
        }

        var tiles = el('div', { className: 'bowire-parallel-tiles' });
        st.sessions.forEach(function (s) {
            var statusClass = s.status === 'done'
                ? (s.pass ? ' pass' : ' fail')
                : ' running';
            var progressText;
            if (s.status === 'running') {
                var nth = s.stepIndex >= 0 ? (s.stepIndex + 1) : 0;
                progressText = 'step ' + nth + ' / ' + s.stepTotal;
            } else {
                progressText = s.passCount + ' pass'
                    + (s.failCount > 0 ? ' / ' + s.failCount + ' fail' : '')
                    + ' · ' + Math.round(s.durationMs) + ' ms';
            }
            // Distributed sessions surface their host + env slot
            // inline so the operator can attribute a failed slot back
            // to its executor without hunting through the host strip.
            var hostBadge = s.host
                ? el('span', { className: 'bowire-parallel-tile-host',
                    title: s.host, textContent: _shortHost(s.host) })
                : null;
            var envBadge = s.envId
                ? el('span', { className: 'bowire-parallel-tile-env', textContent: s.envId })
                : null;
            var tile = el('div', { className: 'bowire-parallel-tile' + statusClass },
                el('span', { className: 'bowire-parallel-tile-index',
                    textContent: '#' + (s.index + 1) }),
                el('span', { className: 'bowire-parallel-tile-status',
                    textContent: s.status === 'done' ? (s.pass ? 'PASS' : 'FAIL') : 'RUNNING' }),
                hostBadge,
                envBadge,
                el('span', { className: 'bowire-parallel-tile-progress', textContent: progressText }),
                s.error ? el('span', { className: 'bowire-parallel-tile-error',
                    title: s.error, textContent: s.error.slice(0, 60) }) : null
            );
            tiles.appendChild(tile);
        });
        panel.appendChild(tiles);

        // #132 Phase 1 polish — aggregate percentile tiles + per-step
        // status histogram + Save-to-Benchmarks / Export toolbar. The
        // operator gets the cross-session distribution at the same
        // altitude they already see per-session tiles; the histogram
        // surfaces "did sessions diverge on the same step?" — the
        // shape that turns a 200-iteration parallel run into actual
        // load-test insight rather than a wall of OK/FAIL.
        if (st.status === 'done') {
            var agg = _computeParallelAggregates(st);
            if (agg.count > 0) {
                panel.appendChild(_renderParallelAggregateTiles(agg));
                panel.appendChild(_renderParallelStepHistogram(agg));
            }
            panel.appendChild(_renderParallelResultToolbar(st));
        }

        return panel;
    }

    // ---- #132 Phase 1 polish — aggregate math + post-run UI ----
    //
    // _computeParallelAggregates(state) → {
    //     count, success, failure,
    //     stats: { min, max, avg, p50, p90, p95, p99 },
    //     statusCounts: { OK: n, NetworkError: n, ... },
    //     perStep: { stepIndex: { label, count, success, failure,
    //                             durations[], statusCounts{} } },
    //     iterations: [{ i, sessionIndex, stepIndex, label, status,
    //                    pass, durationMs }]
    // }
    //
    // Walks every session.results[] entry. Distributed runs project
    // the coordinator's flat result list back onto sessions (see
    // _runDistributedParallel above) so this aggregator sees the same
    // shape regardless of run mode.
    function _computeParallelAggregates(state) {
        var durations = [];
        var statusCounts = {};
        var perStep = {};
        var iterations = [];
        var success = 0;
        var failure = 0;
        var i = 0;
        (state.sessions || []).forEach(function (s) {
            (s.results || []).forEach(function (r) {
                i++;
                var d = r.durationMs || 0;
                var status = r.status != null ? String(r.status) : (r.pass ? 'OK' : 'Error');
                if (r.pass) success++; else failure++;
                durations.push(d);
                statusCounts[status] = (statusCounts[status] || 0) + 1;
                var key = r.stepIndex != null ? String(r.stepIndex) : '?';
                var slot = perStep[key];
                if (!slot) {
                    slot = perStep[key] = {
                        stepIndex: r.stepIndex != null ? r.stepIndex : -1,
                        label: r.stepLabel || ('step ' + key),
                        count: 0, success: 0, failure: 0,
                        durations: [], statusCounts: {}
                    };
                }
                slot.count++;
                if (r.pass) slot.success++; else slot.failure++;
                slot.durations.push(d);
                slot.statusCounts[status] = (slot.statusCounts[status] || 0) + 1;
                iterations.push({
                    i: i, sessionIndex: s.index, stepIndex: r.stepIndex,
                    label: r.stepLabel || '', status: status,
                    pass: !!r.pass, durationMs: d
                });
            });
        });
        var sorted = durations.slice().sort(function (a, b) { return a - b; });
        return {
            count: sorted.length,
            success: success,
            failure: failure,
            stats: _parallelStatsFromSorted(sorted),
            statusCounts: statusCounts,
            perStep: perStep,
            durations: sorted,
            iterations: iterations
        };
    }

    function _parallelPercentile(sorted, p) {
        if (!sorted || sorted.length === 0) return 0;
        var idx = Math.ceil((p / 100) * sorted.length) - 1;
        return sorted[Math.max(0, Math.min(idx, sorted.length - 1))];
    }

    function _parallelStatsFromSorted(sorted) {
        if (!sorted || sorted.length === 0) {
            return { min: 0, max: 0, avg: 0, p50: 0, p90: 0, p95: 0, p99: 0 };
        }
        var sum = 0;
        for (var i = 0; i < sorted.length; i++) sum += sorted[i];
        return {
            min: sorted[0],
            max: sorted[sorted.length - 1],
            avg: sum / sorted.length,
            p50: _parallelPercentile(sorted, 50),
            p90: _parallelPercentile(sorted, 90),
            p95: _parallelPercentile(sorted, 95),
            p99: _parallelPercentile(sorted, 99)
        };
    }

    function _formatParallelMs(v) {
        if (v == null) return '—';
        if (v >= 1000) return (v / 1000).toFixed(2) + ' s';
        if (v >= 100) return Math.round(v) + ' ms';
        return Math.round(v * 10) / 10 + ' ms';
    }

    function _renderParallelAggregateTiles(agg) {
        var s = agg.stats;
        var row = el('div', { className: 'bowire-parallel-agg-row' });
        function tile(label, value, klass) {
            return el('div', { className: 'bowire-parallel-agg-tile' + (klass ? ' ' + klass : '') },
                el('span', { className: 'bowire-parallel-agg-label', textContent: label }),
                el('span', { className: 'bowire-parallel-agg-value', textContent: value })
            );
        }
        row.appendChild(tile('p50', _formatParallelMs(s.p50)));
        row.appendChild(tile('p90', _formatParallelMs(s.p90)));
        row.appendChild(tile('p95', _formatParallelMs(s.p95)));
        row.appendChild(tile('p99', _formatParallelMs(s.p99), 'tight'));
        row.appendChild(tile('min', _formatParallelMs(s.min)));
        row.appendChild(tile('max', _formatParallelMs(s.max)));
        row.appendChild(tile('avg', _formatParallelMs(s.avg)));
        row.appendChild(tile('n', String(agg.count)));
        return row;
    }

    function _renderParallelStepHistogram(agg) {
        var section = el('div', { className: 'bowire-parallel-histogram' });
        section.appendChild(el('div', { className: 'bowire-parallel-histogram-title',
            textContent: 'Per-step status histogram' }));

        var stepKeys = Object.keys(agg.perStep)
            .sort(function (a, b) { return agg.perStep[a].stepIndex - agg.perStep[b].stepIndex; });
        if (stepKeys.length === 0) {
            section.appendChild(el('div', { className: 'bowire-parallel-histogram-empty',
                textContent: 'No completed steps recorded.' }));
            return section;
        }

        var table = el('table', { className: 'bowire-parallel-histogram-table' });
        var thead = el('thead', null,
            el('tr', null,
                el('th', { textContent: 'Step' }),
                el('th', { textContent: 'n' }),
                el('th', { textContent: 'success' }),
                el('th', { textContent: 'failure' }),
                el('th', { textContent: 'p50' }),
                el('th', { textContent: 'p95' }),
                el('th', { textContent: 'p99' }),
                el('th', { textContent: 'status mix' })
            )
        );
        table.appendChild(thead);
        var tbody = el('tbody');
        stepKeys.forEach(function (k) {
            var slot = agg.perStep[k];
            var sorted = slot.durations.slice().sort(function (a, b) { return a - b; });
            var stats = _parallelStatsFromSorted(sorted);
            // status-mix bar — proportional segments of success / failure
            // / abort buckets. Same 3-bucket logic the per-step row uses
            // so the bar matches the count columns visually.
            var bar = el('span', { className: 'bowire-parallel-histogram-bar' });
            var passPct = slot.count > 0 ? Math.round(100 * slot.success / slot.count) : 0;
            var failPct = slot.count > 0 ? Math.round(100 * slot.failure / slot.count) : 0;
            if (passPct > 0) {
                bar.appendChild(el('span', { className: 'bowire-parallel-histogram-bar-pass',
                    style: 'width:' + passPct + '%', title: slot.success + ' success' }));
            }
            if (failPct > 0) {
                bar.appendChild(el('span', { className: 'bowire-parallel-histogram-bar-fail',
                    style: 'width:' + failPct + '%', title: slot.failure + ' failure' }));
            }
            var statusMix = Object.keys(slot.statusCounts).sort().map(function (sk) {
                return sk + '=' + slot.statusCounts[sk];
            }).join(' · ');
            tbody.appendChild(el('tr', null,
                el('td', { className: 'bowire-parallel-histogram-step', title: slot.label,
                    textContent: slot.label }),
                el('td', { textContent: String(slot.count) }),
                el('td', { className: 'bowire-parallel-histogram-pass',
                    textContent: String(slot.success) }),
                el('td', { className: 'bowire-parallel-histogram-fail',
                    textContent: String(slot.failure) }),
                el('td', { textContent: _formatParallelMs(stats.p50) }),
                el('td', { textContent: _formatParallelMs(stats.p95) }),
                el('td', { textContent: _formatParallelMs(stats.p99) }),
                el('td', { className: 'bowire-parallel-histogram-mix' },
                    bar,
                    el('span', { className: 'bowire-parallel-histogram-mix-text',
                        textContent: statusMix })
                )
            ));
        });
        table.appendChild(tbody);
        section.appendChild(table);
        return section;
    }

    // Save-to-Benchmarks + Export toolbar. Routes through the
    // benchmarks rail's exporters (#234) so CSV / k6-summary / OTLP
    // for a parallel run is byte-identical to the same export off a
    // regular benchmark envelope — operators only learn one format
    // shape across both runners.
    function _renderParallelResultToolbar(state) {
        var bar = el('div', { className: 'bowire-parallel-toolbar' });

        // Save to Benchmarks — lands a fresh benchmark spec keyed on
        // (kind, sourceId, ranAt) so a parallel run is replayable +
        // diff-able alongside regular benchmark history. Subsequent
        // runs against the same source land additional `runs[]`
        // entries on the same spec (history cap honoured by the
        // existing #233 ring).
        bar.appendChild(el('button', {
            className: 'bowire-recording-action-btn',
            title: 'Save this run into Benchmarks for replay + diff (#131 / #233)',
            onClick: function () { _saveParallelRunToBenchmarks(state); }
        },
            el('span', { innerHTML: typeof svgIcon === 'function' ? svgIcon('lightning') : '' }),
            el('span', { textContent: 'Save to Benchmarks' })
        ));

        // Export — same three formats as #234, accessed off the
        // benchmarks rail. We build a transient spec.lastRun shape,
        // call the exporter, and pipe the bytes through a download
        // anchor; no benchmark spec is created when the operator
        // just wants the file.
        ['csv', 'k6-summary', 'otlp'].forEach(function (fmt) {
            var label = fmt === 'csv' ? 'CSV'
                : fmt === 'k6-summary' ? 'k6-summary' : 'OTLP';
            bar.appendChild(el('button', {
                className: 'bowire-recording-action-btn',
                title: 'Export results as ' + label,
                onClick: function () { _exportParallelRun(state, fmt); }
            },
                el('span', { innerHTML: typeof svgIcon === 'function' ? svgIcon('download') : '' }),
                el('span', { textContent: label })
            ));
        });

        return bar;
    }

    // Project the parallel-sessions state into the same shape
    // exportRunAsCsv / exportRunAsK6Summary / exportRunAsOtlpJson
    // consume from benchmarks.js (spec.lastRun.{stats, statusCounts,
    // targetStats, iterations, durations, total, success, failure,
    // startTimeMs, endTimeMs, ranAt, ranAtWallClock}). One projection
    // keeps the three exporters DRY — and any new exporter the
    // benchmarks rail adds (Grafana, Datadog) automatically picks up
    // parallel-sessions runs.
    function _parallelStateToBenchmarkSpec(state) {
        var agg = _computeParallelAggregates(state);
        var startWall = state.startedAt || Date.now();
        var endWall = startWall + Math.round(state.durationMs || 0);
        var totalSeconds = (state.durationMs || 0) / 1000;
        // Per-step rollup → benchmark targetStats shape.
        var targetStats = {};
        Object.keys(agg.perStep).forEach(function (k) {
            var slot = agg.perStep[k];
            var sorted = slot.durations.slice().sort(function (a, b) { return a - b; });
            targetStats['step_' + k] = {
                key: 'step_' + k,
                label: slot.label,
                count: slot.count,
                errors: slot.failure,
                durations: sorted,
                statusCounts: Object.assign({}, slot.statusCounts)
            };
        });
        var iterationsForExport = agg.iterations.map(function (it) {
            return {
                i: it.i,
                target: it.label,
                targetKey: 'step_' + (it.stepIndex != null ? it.stepIndex : '?'),
                durationMs: it.durationMs,
                status: it.status,
                pass: !!it.pass,
                ranAt: startWall
            };
        });
        var stats = Object.assign({}, agg.stats, {
            count: agg.count,
            totalSeconds: totalSeconds,
            throughput: totalSeconds > 0 ? agg.count / totalSeconds : 0
        });
        var lastRun = {
            ranAt: startWall,
            ranAtWallClock: startWall,
            durations: agg.durations,
            statusCounts: agg.statusCounts,
            iterationTargets: [],
            success: agg.success,
            failure: agg.failure,
            total: agg.count,
            startTimeMs: startWall,
            endTimeMs: endWall,
            cancelled: false,
            stats: stats,
            iterations: iterationsForExport,
            targetStats: targetStats,
            // Parallel-sessions marker — lets the Benchmarks pane label
            // the run distinctly from a "regular" envelope iteration.
            parallelSessions: {
                kind: state.kind,
                sourceId: state.sourceId,
                sourceName: state.sourceName,
                sessionCount: state.sessionCount,
                distributed: !!state.distributed,
                hosts: Array.isArray(state.hosts) ? state.hosts.slice() : null
            }
        };
        return {
            id: 'parallel_' + (state.kind || 'src') + '_' + (state.sourceId || 'unknown'),
            name: 'Parallel: ' + (state.sourceName || state.kind || 'sessions'),
            kind: 'parallel',
            mode: 'parallel',
            sourceKind: state.kind,
            sourceId: state.sourceId,
            // Synthetic single envelope target — the run already happened,
            // so the spec exists for replay + diff bookkeeping only.
            targets: [{
                type: state.kind === 'collection' ? 'collection-ref'
                    : state.kind === 'recording' ? 'recording-ref' : 'method',
                collectionId: state.kind === 'collection' ? state.sourceId : undefined,
                recordingId: state.kind === 'recording' ? state.sourceId : undefined,
                label: state.sourceName
            }],
            phases: [{
                vus: state.sessionCount || 1,
                totalIterations: agg.count,
                durationMs: null,
                rampToVus: null,
                arrivalRate: null
            }],
            lastRun: lastRun,
            runs: [lastRun],
            createdAt: startWall
        };
    }

    function _saveParallelRunToBenchmarks(state) {
        var transient = _parallelStateToBenchmarkSpec(state);
        if (typeof loadBenchmarks === 'function') {
            try { loadBenchmarks(); } catch { /* ignore — list is empty */ }
        }
        if (typeof benchmarksList === 'undefined' || !Array.isArray(benchmarksList)) {
            toast('Benchmarks rail not available in this build', 'error');
            return;
        }
        // Stable key — every parallel-sessions run on the SAME source
        // lands additional history entries on the SAME spec so the
        // diff banner (#233 prev-run delta) just works on the second
        // run onwards. Cross-source runs get their own spec.
        var existing = benchmarksList.find(function (s) { return s.id === transient.id; });
        if (existing) {
            existing.lastRun = transient.lastRun;
            if (!Array.isArray(existing.runs)) existing.runs = [];
            existing.runs.push(transient.lastRun);
            var cap = (typeof _benchmarkHistoryCap === 'function') ? _benchmarkHistoryCap() : 5;
            if (existing.runs.length > cap) {
                existing.runs.splice(0, existing.runs.length - cap);
            }
            existing.sourceKind = transient.sourceKind;
            existing.sourceId = transient.sourceId;
        } else {
            benchmarksList.push(transient);
        }
        if (typeof persistBenchmarks === 'function') persistBenchmarks();
        var savedId = (existing && existing.id) || transient.id;
        if (typeof benchmarksSelectedId !== 'undefined') {
            benchmarksSelectedId = savedId;
        }
        toast('Saved parallel run to Benchmarks — open the Benchmarks rail to replay or diff',
            'success', {
                action: {
                    label: 'Open',
                    onClick: function () {
                        railMode = 'benchmarks';
                        try { localStorage.setItem('bowire_rail_mode', 'benchmarks'); }
                        catch { /* ignore */ }
                        if (typeof benchmarksSelectedId !== 'undefined') {
                            benchmarksSelectedId = savedId;
                        }
                        render();
                    }
                }
            });
        render();
    }

    function _exportParallelRun(state, format) {
        var spec = _parallelStateToBenchmarkSpec(state);
        var stem = ((state.sourceName || 'parallel') + '_'
            + (state.sessionCount || 1) + 'x_' + Date.now())
            .replace(/[^\w\-]+/g, '_');
        var content;
        var mime;
        var ext;
        if (format === 'csv') {
            if (typeof exportRunAsCsv !== 'function') {
                toast('CSV exporter not available — Benchmarks rail missing', 'error');
                return;
            }
            content = exportRunAsCsv(spec, 'per-method');
            mime = 'text/csv;charset=utf-8'; ext = '.results.csv';
        } else if (format === 'k6-summary') {
            if (typeof exportRunAsK6Summary !== 'function') {
                toast('k6 exporter not available — Benchmarks rail missing', 'error');
                return;
            }
            content = JSON.stringify(exportRunAsK6Summary(spec), null, 2);
            mime = 'application/json'; ext = '.k6-summary.json';
        } else if (format === 'otlp') {
            if (typeof exportRunAsOtlpJson !== 'function') {
                toast('OTLP exporter not available — Benchmarks rail missing', 'error');
                return;
            }
            content = JSON.stringify(exportRunAsOtlpJson(spec), null, 2);
            mime = 'application/json'; ext = '.otlp.json';
        } else {
            return;
        }
        var blob = new Blob([content], { type: mime });
        var a = document.createElement('a');
        a.href = URL.createObjectURL(blob);
        a.download = stem + ext;
        a.click();
        URL.revokeObjectURL(a.href);
        toast('Exported parallel run as ' + format.toUpperCase(), 'success');
    }

    // Compact host label for the per-session tile — strip scheme and
    // trailing slashes so a 60-char URL doesn't blow out the tile
    // width. The full URL stays in the tile's title attribute so the
    // operator can still hover to see it.
    function _shortHost(url) {
        if (!url) return '';
        var s = String(url).replace(/^https?:\/\//, '').replace(/\/+$/, '');
        return s.length > 28 ? s.slice(0, 26) + '…' : s;
    }

    // ---- Collection Runner ----
    // Executes all items in a collection sequentially against the
    // current environment. Variable substitution runs per-item via
    // substituteVars, so ${baseUrl}, ${token}, etc. resolve fresh.
    // Response chaining carries ${response.X} values forward between
    // items (the last response's JSON feeds the next item's
    // substitution context).

    async function runCollection(id) {
        var col = collectionsList.find(function (c) { return c.id === id; });
        if (!col || col.items.length === 0) return;

        collectionRunState = {
            collectionId: id,
            stepIndex: -1,
            status: 'running',
            results: []
        };
        render();

        for (var i = 0; i < col.items.length; i++) {
            collectionRunState.stepIndex = i;
            render();

            var item = col.items[i];
            try {
                var result = await runCollectionItem(item);
                collectionRunState.results.push({
                    itemId: item.id,
                    pass: result.pass,
                    status: result.status,
                    durationMs: result.durationMs,
                    response: result.response,
                    error: result.error
                });
                // Capture response for chaining
                if (result.response) captureResponse(result.response);
            } catch (e) {
                collectionRunState.results.push({
                    itemId: item.id,
                    pass: false,
                    status: 'NetworkError',
                    error: e.message
                });
            }
            render();
        }

        collectionRunState.status = 'done';
        render();
    }

    function runCollectionItem(item, opts) {
        // Optional opts.capturedBag — per-session captured-vars bag
        // forwarded into pre/post-scripts (#132 parallel sessions).
        // Collection items don't bind pre/post-scripts today, but the
        // signature is forward-compatible so a future "scripts on
        // collection items" patch can thread the bag through without
        // touching every caller.
        // eslint-disable-next-line no-unused-vars
        var capturedBag = opts && opts.capturedBag ? opts.capturedBag : null;
        // Only unary items can be replayed — streaming would need
        // the SSE endpoint and is skipped (same as recording replay).
        if (item.methodType && item.methodType !== 'Unary') {
            return Promise.resolve({
                pass: false,
                status: 'Skipped (' + item.methodType + ')',
                durationMs: 0,
                error: 'Only Unary methods are executed; streaming methods are skipped.'
            });
        }

        var messages = (item.messages && item.messages.length > 0)
            ? item.messages
            : [item.body || '{}'];

        var substituted = messages.map(function (m) { return substituteVars(m); });
        var meta = item.metadata
            ? Object.fromEntries(Object.entries(item.metadata).map(function (kv) {
                return [kv[0], substituteVars(String(kv[1]))];
            }))
            : null;

        // #252 — Resolve URL at execute time. For urlMode='source'
        // items the snapshot's serverUrl is treated as a HINT — we
        // re-check against the current workspace's Source URL list
        // so a rename / retire propagates to the saved item. When
        // the hint is no longer in the list, fall back to the
        // current first Source URL and emit a console warning (the
        // operator's hint that something they pinned to a Source
        // has drifted; toast would be too noisy for batch runs).
        var serverUrl;
        if (item.urlMode === 'source') {
            if (Array.isArray(serverUrls) && serverUrls.length > 0) {
                if (item.serverUrl && serverUrls.indexOf(item.serverUrl) >= 0) {
                    serverUrl = item.serverUrl;
                } else {
                    serverUrl = serverUrls[0];
                    if (item.serverUrl && item.serverUrl !== serverUrl) {
                        console.warn('[#252] collection item bound to source URL "'
                            + item.serverUrl + '" no longer in workspace — replaying against "'
                            + serverUrl + '"');
                    }
                }
            } else {
                // No sources at all → best-effort fall back to the
                // hint so the call still has SOMETHING to hit.
                serverUrl = item.serverUrl || '';
            }
        } else {
            // urlMode='inline' (or unset, pre-#252) — self-contained
            // URL on the item. Use as-is.
            serverUrl = item.serverUrl || (serverUrls.length > 0 ? serverUrls[0] : '');
        }
        var body = {
            service: item.service,
            method: item.method,
            messages: substituted,
            metadata: meta,
            protocol: item.protocol || null
        };

        var url = config.prefix + '/api/invoke'
            + (serverUrl ? '?serverUrl=' + encodeURIComponent(serverUrl) : '');

        return fetch(url, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(body)
        }).then(function (resp) {
            return resp.json().then(function (json) {
                var ok = resp.ok && !json.title;
                return {
                    pass: ok,
                    status: json.status || (ok ? 'OK' : 'Error'),
                    durationMs: json.duration_ms || 0,
                    response: json.response,
                    error: json.title || null
                };
            });
        });
    }

    // ---- Collection Manager (modal) ----
    // renderCollectionManager modal retired in #133 Phase 3.
    // Every entry path now jumps to railMode = 'collections'.

    function renderCollectionDetail(col) {
        var pane = el('div', { className: 'bowire-recording-detail' });

        // Header
        var header = el('div', { className: 'bowire-recording-detail-header' });
        header.appendChild(el('input', {
            id: 'bowire-collection-name-input',
            type: 'text',
            className: 'bowire-recording-name-input',
            value: col.name,
            onChange: function (e) {
                col.name = e.target.value;
                persistCollections();
            }
        }));
        pane.appendChild(header);

        // Toolbar
        var toolbar = el('div', { className: 'bowire-recording-toolbar' });
        var isRunning = collectionRunState
            && collectionRunState.collectionId === col.id
            && collectionRunState.status === 'running';

        toolbar.appendChild(el('button', {
            className: 'bowire-recording-action-btn',
            disabled: isRunning || col.items.length === 0,
            title: 'Run all items sequentially with the current environment',
            onClick: function () { runCollection(col.id); }
        },
            el('span', { innerHTML: svgIcon('play') }),
            el('span', { textContent: isRunning ? 'Running\u2026' : 'Run All' })
        ));

        // #132 minimal \u2014 fire N parallel sessions of this collection.
        // Each session runs the items sequentially in its own context.
        // No per-session env policy yet (single shared env); phase 2
        // adds per-session env slot, stagger, failure policy.
        toolbar.appendChild(el('button', {
            className: 'bowire-recording-action-btn',
            disabled: col.items.length === 0,
            title: 'Run this collection in N parallel sessions at once',
            onClick: function () { _promptAndRunParallel('collection', col.id, col.name); }
        },
            el('span', { innerHTML: svgIcon('repeat') }),
            el('span', { textContent: 'Parallel sessions' })
        ));

        // #131 Phase 3 \u2014 "Benchmark" now opens an Add-to-envelope
        // picker. The operator can drop this collection into an
        // existing envelope or spawn a new one seeded with the
        // collection as its first target.
        if (typeof addTargetToEnvelopePicker === 'function') {
            toolbar.appendChild(el('button', {
                className: 'bowire-recording-action-btn',
                disabled: col.items.length === 0,
                title: 'Add this collection to a benchmark envelope',
                onClick: function (e) {
                    addTargetToEnvelopePicker(e.clientX, e.clientY,
                        { type: 'collection-ref', collectionId: col.id, itemIndex: null },
                        { name: 'Benchmark: ' + (col.name || 'collection') }
                    );
                }
            },
                el('span', { innerHTML: svgIcon('lightning') }),
                el('span', { textContent: 'Benchmark' })
            ));
        }

        toolbar.appendChild(el('button', {
            className: 'bowire-recording-action-btn',
            disabled: col.items.length === 0,
            title: 'Export as JSON',
            onClick: function () {
                var blob = new Blob([JSON.stringify(col, null, 2)], { type: 'application/json' });
                var a = document.createElement('a');
                a.href = URL.createObjectURL(blob);
                a.download = (col.name || 'collection').replace(/[^a-zA-Z0-9._-]/g, '_') + '.bwc';
                a.click();
                URL.revokeObjectURL(a.href);
            }
        },
            el('span', { innerHTML: svgIcon('download') }),
            el('span', { textContent: 'Export' })
        ));

        // Import Postman Collection
        toolbar.appendChild(el('button', {
            className: 'bowire-recording-action-btn',
            title: 'Import a Postman Collection v2.1 JSON file',
            onClick: function () {
                var input = document.createElement('input');
                input.type = 'file';
                input.accept = '.json,application/json';
                input.onchange = async function () {
                    if (!input.files || !input.files[0]) return;
                    try {
                        var text = await input.files[0].text();
                        var imported = importPostmanCollection(text);
                        if (imported) {
                            toast('Imported "' + imported.name + '" (' + imported.items.length + ' items)', 'success');
                            collectionManagerSelectedId = imported.id;
                            render();
                        }
                    } catch (e) {
                        toast('Import failed: ' + e.message, 'error');
                    }
                };
                input.click();
            }
        },
            el('span', { innerHTML: svgIcon('upload') }),
            el('span', { textContent: 'Import Postman' })
        ));

        toolbar.appendChild(el('button', {
            className: 'bowire-recording-action-btn bowire-recording-action-danger',
            title: 'Delete this collection',
            onClick: function () {
                bowireConfirm('Delete collection "' + col.name + '"?', function () {
                    var backup = JSON.parse(JSON.stringify(col));
                    deleteCollection(col.id);
                    render();
                    toast('Deleted collection "' + (backup.name || 'unnamed') + '"', 'info', {
                        undo: function () { collectionsList.push(backup); persistCollections(); render(); },
                        logAction: { kind: 'collection-delete',
                            title: 'Deleted collection "' + (backup.name || 'unnamed') + '"' }
                    });
                }, { title: 'Delete Collection', danger: true, confirmText: 'Delete' });
            }
        },
            el('span', { innerHTML: svgIcon('trash') }),
            el('span', { textContent: 'Delete' })
        ));

        pane.appendChild(toolbar);

        // #132 — per-session tiles for an in-flight (or just-finished)
        // parallel-sessions run on THIS collection. Returns null when
        // there's no run targeting this source; cheap when present.
        var parallelPanel = renderParallelSessionsPanel('collection', col.id);
        if (parallelPanel) pane.appendChild(parallelPanel);

        // Item list
        var stepsPane = el('div', { className: 'bowire-recording-steps' });
        if (col.items.length === 0) {
            stepsPane.appendChild(el('div', { className: 'bowire-recording-empty',
                textContent: 'No items yet. Use "Save to Collection" from the request pane.' }));
        } else {
            for (var i = 0; i < col.items.length; i++) {
                (function (item, idx) {
                    var runResult = null;
                    if (collectionRunState && collectionRunState.collectionId === col.id) {
                        runResult = collectionRunState.results.find(function (r) { return r.itemId === item.id; });
                    }

                    var statusBadge = el('span', {
                        className: 'bowire-recording-step-status'
                            + (runResult ? (runResult.pass ? ' pass' : ' fail') : ''),
                        textContent: runResult ? (runResult.pass ? 'PASS' : 'FAIL') : ''
                    });

                    var label = el('div', { className: 'bowire-recording-step-label' },
                        el('span', { className: 'bowire-recording-step-index', textContent: String(idx + 1) + '.' }),
                        el('span', { className: 'bowire-recording-step-protocol', textContent: item.protocol || 'grpc' }),
                        el('span', { className: 'bowire-recording-step-method',
                            textContent: (item.service || '') + ' / ' + (item.method || '') }),
                        statusBadge,
                        runResult && runResult.durationMs
                            ? el('span', { className: 'bowire-recording-step-duration',
                                textContent: runResult.durationMs + 'ms' })
                            : null
                    );

                    var deleteBtn = el('button', {
                        className: 'bowire-recording-step-delete',
                        innerHTML: svgIcon('trash'),
                        title: 'Remove this item',
                        'aria-label': 'Remove this item',
                        onClick: function (e) {
                            e.stopPropagation();
                            removeFromCollection(col.id, item.id);
                            render();
                        }
                    });

                    stepsPane.appendChild(el('div', { className: 'bowire-recording-step' }, label, deleteBtn));
                })(col.items[i], i);
            }
        }

        // Run results summary
        if (collectionRunState && collectionRunState.collectionId === col.id
            && collectionRunState.status === 'done' && collectionRunState.results.length > 0) {
            var passed = collectionRunState.results.filter(function (r) { return r.pass; }).length;
            var total = collectionRunState.results.length;
            stepsPane.appendChild(el('div', {
                className: 'bowire-recording-empty',
                style: 'border-top: 1px solid var(--bowire-border); margin-top: 8px; padding-top: 8px',
                textContent: passed + ' / ' + total + ' passed'
            }));
        }

        pane.appendChild(stepsPane);
        return pane;
    }

    // ---- "Save to Collection" dropdown ----
    function renderSaveToCollectionDropdown(parentBtn) {
        var existing = document.querySelector('.bowire-save-col-dropdown');
        if (existing) { existing.remove(); return; }

        var dropdown = el('div', { className: 'bowire-save-col-dropdown bowire-dropdown-menu visible' });

        if (collectionsList.length === 0) {
            dropdown.appendChild(el('div', {
                className: 'bowire-dropdown-item',
                textContent: '+ New Collection',
                onClick: function () {
                    var col = createCollection();
                    saveCurrentRequestToCollection(col.id);
                    dropdown.remove();
                }
            }));
        } else {
            for (var i = 0; i < collectionsList.length; i++) {
                (function (col) {
                    dropdown.appendChild(el('div', {
                        className: 'bowire-dropdown-item',
                        textContent: col.name + ' (' + col.items.length + ')',
                        onClick: function () {
                            saveCurrentRequestToCollection(col.id);
                            dropdown.remove();
                        }
                    }));
                })(collectionsList[i]);
            }
            dropdown.appendChild(el('div', { style: 'border-top:1px solid var(--bowire-border);margin:4px 0' }));
            dropdown.appendChild(el('div', {
                className: 'bowire-dropdown-item',
                textContent: '+ New Collection',
                onClick: function () {
                    var col = createCollection();
                    saveCurrentRequestToCollection(col.id);
                    dropdown.remove();
                }
            }));
        }

        parentBtn.parentElement.appendChild(dropdown);
        // Close on next outside click
        setTimeout(function () {
            document.addEventListener('click', function handler() {
                dropdown.remove();
                document.removeEventListener('click', handler);
            }, { once: true });
        }, 0);
    }

    // ---- Postman Collection Import ----
    // Parses a Postman Collection v2.1 JSON file and converts it to a
    // Bowire collection. Maps {{variable}} → ${variable} in URLs,
    // headers, and body. Supports nested folders (flattened).
    function importPostmanCollection(jsonText) {
        var pm = JSON.parse(jsonText);
        if (!pm || !pm.info || !pm.item) {
            throw new Error('Not a valid Postman Collection v2.1 file');
        }

        var col = createCollection(pm.info.name || 'Postman Import');
        var items = flattenPostmanItems(pm.item);

        for (var i = 0; i < items.length; i++) {
            var pmItem = items[i];
            var req = pmItem.request;
            if (!req) continue;

            var httpMethod = (typeof req.method === 'string' ? req.method : 'GET').toUpperCase();
            var url = postmanUrlToString(req.url);
            var body = '';
            if (req.body && req.body.raw) {
                body = convertPostmanVars(req.body.raw);
            }

            var metadata = {};
            if (req.header && Array.isArray(req.header)) {
                for (var hi = 0; hi < req.header.length; hi++) {
                    var hdr = req.header[hi];
                    if (hdr.key && !hdr.disabled) {
                        metadata[hdr.key] = convertPostmanVars(hdr.value || '');
                    }
                }
            }

            addToCollection(col.id, {
                protocol: 'rest',
                service: url,
                method: httpMethod,
                methodType: 'Unary',
                body: body || '{}',
                messages: [body || '{}'],
                metadata: Object.keys(metadata).length > 0 ? metadata : null,
                serverUrl: convertPostmanVars(url)
            });
        }

        return col;
    }

    function flattenPostmanItems(items) {
        var result = [];
        for (var i = 0; i < items.length; i++) {
            if (items[i].item && Array.isArray(items[i].item)) {
                // Folder — recurse
                result = result.concat(flattenPostmanItems(items[i].item));
            } else {
                result.push(items[i]);
            }
        }
        return result;
    }

    function postmanUrlToString(url) {
        if (typeof url === 'string') return convertPostmanVars(url);
        if (url && url.raw) return convertPostmanVars(url.raw);
        return '';
    }

    // Convert Postman {{variable}} syntax to Bowire ${variable}
    function convertPostmanVars(text) {
        if (typeof text !== 'string') return text;
        return text.replace(/\{\{([^}]+)\}\}/g, function (_, name) {
            return '${' + name.trim() + '}';
        });
    }
