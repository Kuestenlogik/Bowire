    // ---- v2.2 T3 — Regression Coverage Surface ----
    //
    // Per-method run-history aggregation. Every runner that resolves a
    // discovered method (Discover invoke, Compose runner, Benchmark
    // runner, Recording replay; Flow runner will hook in from
    // Kuestenlogik.Bowire.Flows once T1 lands) calls recordMethodRun,
    // which appends a small JSON event to a per-workspace ring buffer.
    //
    // The buffer answers two questions for the operator:
    //
    //   * "When was this method last exercised, and did it pass?"
    //     → coverageState(svc, m) returns 'recent' | 'stale' |
    //       'failing' | 'uncovered'; the Discover sidebar renders a
    //       chip per method based on this.
    //
    //   * "How much of the discovered surface have I covered lately?"
    //     → renderRunHistoryView() (mounted from Settings → Data)
    //       prints a summary card + a filterable table of the last
    //       N runs across every method.
    //
    // Storage is intentionally simple: one localStorage bucket per
    // workspace (via wsKey), capped at RUN_HISTORY_CAP (default 500,
    // operator-configurable), FIFO-evicted at write time. Aggregation
    // is computed lazily on read — no derived state on disk to keep in
    // sync, no migration story when the buckets churn.
    //
    // methodId canonical form follows the project convention
    // (`<serviceName>::<methodName>` — same shape activeJobs,
    // openChannels, transcodingKey, channelStoreKey all use) so the
    // ring buffer round-trips through .bww exports without ambiguity.

    var RUN_HISTORY_KEY        = 'bowire_run_history';
    var RUN_HISTORY_CAP_KEY    = 'bowire_run_history_cap';
    var RUN_HISTORY_DEFAULT_CAP = 500;

    function _runHistoryCap() {
        try {
            var raw = parseInt(localStorage.getItem(RUN_HISTORY_CAP_KEY), 10);
            if (!isNaN(raw) && raw >= 50 && raw <= 5000) return raw;
        } catch (_) { /* fall through */ }
        return RUN_HISTORY_DEFAULT_CAP;
    }

    function methodIdFor(service, method) {
        // Canonical id shared with activeJobs / openChannels /
        // transcodingKey / channelStoreKey. Defensive against
        // undefined inputs so a runner that forgot to thread the
        // service through doesn't crash render() — the bogus
        // '::method' entry simply never matches a sidebar row.
        return (service == null ? '' : String(service))
            + '::'
            + (method == null ? '' : String(method));
    }

    function getRunHistory() {
        try {
            var raw = localStorage.getItem(wsKey(RUN_HISTORY_KEY));
            if (!raw) return [];
            var parsed = JSON.parse(raw);
            return Array.isArray(parsed) ? parsed : [];
        } catch (_) { return []; }
    }

    function _persistRunHistory(list) {
        try {
            localStorage.setItem(wsKey(RUN_HISTORY_KEY), JSON.stringify(list));
        } catch (_) {
            // localStorage full / disabled — the run-log is best-
            // effort telemetry, not project content, so a write
            // failure here must NOT cascade into the calling runner.
        }
    }

    function clearRunHistory() {
        try { localStorage.removeItem(wsKey(RUN_HISTORY_KEY)); }
        catch (_) { /* ignore */ }
        if (typeof render === 'function') render();
    }

    // ---- The recording hook every runner calls ----
    //
    // opts: {
    //   service:       string          discovered-service name
    //   method:        string          discovered-method name
    //   methodId?:     string          override the derived methodId
    //   source:        string          'discover' | 'compose' | 'benchmark'
    //                                  | 'recording-replay' | 'flow'
    //   startedAt?:    number          ms epoch; defaults to Date.now()
    //   durationMs?:   number          per-run wall time; defaults 0
    //   outcome:       'ok'|'fail'|'error'
    //   errorMessage?: string          short cause (truncated to 240 chars)
    // }
    //
    // Returns the persisted entry (for tests / callers that want to
    // chain). Callers wrap in a try/catch defensively even though
    // recordMethodRun itself never throws — defensive belt for the
    // existing runner code paths.
    function recordMethodRun(opts) {
        if (!opts) return null;
        var src = opts.source || 'discover';
        var outcome = opts.outcome;
        if (outcome !== 'ok' && outcome !== 'fail' && outcome !== 'error') {
            // Unknown outcomes are bucketed as 'error' so a missed
            // categorisation surfaces as a red chip rather than
            // silently inflating the pass-rate.
            outcome = 'error';
        }
        var id = opts.methodId || methodIdFor(opts.service, opts.method);
        if (!id || id === '::') return null;
        var entry = {
            // Short random id is enough — runs aren't keyed by anyone.
            runId: 'r-' + Date.now().toString(36) + '-'
                + Math.random().toString(36).slice(2, 8),
            methodId: id,
            service: opts.service || '',
            method: opts.method || '',
            source: src,
            startedAt: typeof opts.startedAt === 'number' ? opts.startedAt : Date.now(),
            durationMs: typeof opts.durationMs === 'number' ? Math.round(opts.durationMs) : 0,
            outcome: outcome
        };
        if (opts.errorMessage) {
            var msg = String(opts.errorMessage);
            entry.errorMessage = msg.length > 240 ? msg.slice(0, 240) + '…' : msg;
        }
        var list = getRunHistory();
        list.push(entry);
        var cap = _runHistoryCap();
        if (list.length > cap) {
            // FIFO eviction — keep the most recent `cap` entries.
            list.splice(0, list.length - cap);
        }
        _persistRunHistory(list);
        return entry;
    }

    // Best-effort wrapper used at every runner call site so a runtime
    // exception in record-keeping can't kill the run itself.
    function safeRecordMethodRun(opts) {
        try { return recordMethodRun(opts); }
        catch (_) { return null; }
    }
    // Expose for rail fragments + extension assemblies (flows.js,
    // mocks.js) that ship in sibling NuGets and need the same hook.
    try { window.bowireRecordMethodRun = safeRecordMethodRun; }
    catch (_) { /* non-browser test env */ }

    // ---- Aggregation (lazy, on read) ----

    var COVERAGE_RECENT_MS = 7  * 24 * 60 * 60 * 1000;
    var COVERAGE_STALE_MS  = 30 * 24 * 60 * 60 * 1000;

    // Group raw entries by methodId. Used by every read-side helper so
    // the localStorage read only happens once per render pass even if
    // every visible method asks for its state.
    var _coverageIndexCache = null;
    var _coverageIndexSig = null;
    function _invalidateCoverageCache() { _coverageIndexCache = null; _coverageIndexSig = null; }
    function _coverageIndex() {
        var list = getRunHistory();
        // Cheap signature: length + last-entry timestamp. Cache
        // invalidates the moment a write lands without us having to
        // wire an explicit broadcast.
        var sig = list.length + ':' + (list.length > 0 ? list[list.length - 1].startedAt : 0);
        if (_coverageIndexCache && _coverageIndexSig === sig) {
            return _coverageIndexCache;
        }
        var idx = {};
        for (var i = 0; i < list.length; i++) {
            var e = list[i];
            if (!e || !e.methodId) continue;
            (idx[e.methodId] || (idx[e.methodId] = [])).push(e);
        }
        // Sort each bucket newest-first so consumers can read [0]
        // without a per-call sort.
        Object.keys(idx).forEach(function (k) {
            idx[k].sort(function (a, b) { return (b.startedAt || 0) - (a.startedAt || 0); });
        });
        _coverageIndexCache = idx;
        _coverageIndexSig = sig;
        return idx;
    }

    function getMethodCoverage(service, method) {
        var id = methodIdFor(service, method);
        var runs = _coverageIndex()[id] || [];
        if (runs.length === 0) {
            return {
                methodId: id,
                runs: 0,
                lastRunAt: null,
                lastOutcome: null,
                runsLast7Days: 0,
                runsLast30Days: 0,
                passRate30d: null,
                sources: []
            };
        }
        var now = Date.now();
        var l7 = 0, l30 = 0, ok30 = 0, fail30 = 0;
        var sources = {};
        for (var i = 0; i < runs.length; i++) {
            var r = runs[i];
            var age = now - (r.startedAt || 0);
            if (age <= COVERAGE_RECENT_MS) l7++;
            if (age <= COVERAGE_STALE_MS) {
                l30++;
                if (r.outcome === 'ok') ok30++;
                else fail30++;
            }
            if (r.source) sources[r.source] = true;
        }
        return {
            methodId: id,
            runs: runs.length,
            lastRunAt: runs[0].startedAt,
            lastOutcome: runs[0].outcome,
            runsLast7Days: l7,
            runsLast30Days: l30,
            passRate30d: (ok30 + fail30) > 0 ? (ok30 / (ok30 + fail30)) : null,
            sources: Object.keys(sources)
        };
    }

    // Returns one of 'recent' | 'stale' | 'failing' | 'uncovered'.
    //
    // Rules:
    //   uncovered — no runs ever
    //   failing   — most-recent outcome is 'fail' or 'error'
    //   recent    — last successful run is within the last 7 days
    //   stale     — last successful run is within the last 30 days
    //                 but not the last 7
    //   uncovered — there are runs but the newest is older than 30 days
    function coverageState(service, method) {
        var cov = getMethodCoverage(service, method);
        if (cov.runs === 0) return 'uncovered';
        if (cov.lastOutcome === 'fail' || cov.lastOutcome === 'error') return 'failing';
        var age = Date.now() - (cov.lastRunAt || 0);
        if (age <= COVERAGE_RECENT_MS) return 'recent';
        if (age <= COVERAGE_STALE_MS)  return 'stale';
        return 'uncovered';
    }

    // Aggregate snapshot used by the summary card. Walks every
    // discovered method (via the live `services` array) and tallies
    // the four states.
    function getCoverageSummary() {
        var summary = { total: 0, recent: 0, stale: 0, failing: 0, uncovered: 0 };
        try {
            if (!Array.isArray(services)) return summary;
            for (var s = 0; s < services.length; s++) {
                var svc = services[s];
                if (!svc || !Array.isArray(svc.methods)) continue;
                for (var m = 0; m < svc.methods.length; m++) {
                    var mn = svc.methods[m] && svc.methods[m].name;
                    if (!mn) continue;
                    summary.total++;
                    var st = coverageState(svc.name, mn);
                    summary[st]++;
                }
            }
        } catch (_) { /* ignore — services may not be hydrated yet */ }
        return summary;
    }

    // ---- Discover sidebar chip ----
    //
    // Color-blind-friendly glyphs: ●  ◐  ✕  ○  for recent / stale /
    // failing / uncovered. Tooltip carries the aggregate stats so
    // hover gives the operator the count + last run wall-clock.
    function renderCoverageChip(service, method) {
        if (typeof el !== 'function') return null;
        var st = coverageState(service, method);
        var cov = getMethodCoverage(service, method);
        var glyphs = { recent: '●', stale: '◐', failing: '✕', uncovered: '○' };
        var tooltipLines = [];
        if (cov.runs === 0) {
            tooltipLines.push('No runs recorded');
            tooltipLines.push('Invoke the method to start tracking');
        } else {
            tooltipLines.push('Last run: ' + new Date(cov.lastRunAt).toLocaleString()
                + ' (' + cov.lastOutcome + ')');
            tooltipLines.push('Runs: '
                + cov.runsLast7Days + ' in 7d / '
                + cov.runsLast30Days + ' in 30d / '
                + cov.runs + ' total');
            if (cov.passRate30d != null) {
                tooltipLines.push('30d pass-rate: ' + Math.round(cov.passRate30d * 100) + '%');
            }
            if (cov.sources.length > 0) {
                tooltipLines.push('Sources: ' + cov.sources.join(', '));
            }
        }
        return el('span', {
            className: 'bowire-method-coverage-chip bowire-method-coverage-' + st,
            'data-coverage': st,
            title: tooltipLines.join('\n'),
            textContent: glyphs[st] || glyphs.uncovered,
            'aria-label': 'Coverage: ' + st
        });
    }

    // ---- Run-history view (Settings → Data → Run history) ----

    function _coverageRailLabel(source) {
        switch (source) {
            case 'discover':          return 'Discover';
            case 'compose':           return 'Compose';
            case 'benchmark':         return 'Benchmark';
            case 'recording-replay':  return 'Recording';
            case 'flow':              return 'Flow';
            default:                  return source || 'unknown';
        }
    }

    // Mutable filter state for the table. Module-scope so morphdom
    // updates don't lose the operator's filter on the next render.
    var _runHistoryFilter = { source: 'all', outcome: 'all', search: '' };

    function _applyRunHistoryFilter(entries) {
        var src = _runHistoryFilter.source;
        var oc  = _runHistoryFilter.outcome;
        var q = (_runHistoryFilter.search || '').trim().toLowerCase();
        var out = [];
        for (var i = 0; i < entries.length; i++) {
            var e = entries[i];
            if (src !== 'all' && e.source !== src) continue;
            if (oc  !== 'all' && e.outcome !== oc)  continue;
            if (q) {
                var hay = ((e.methodId || '') + ' ' + (e.service || '') + ' '
                    + (e.method || '') + ' ' + (e.errorMessage || '')).toLowerCase();
                if (hay.indexOf(q) === -1) continue;
            }
            out.push(e);
        }
        return out;
    }

    function renderRunHistoryView() {
        if (typeof el !== 'function') return null;
        var wrap = el('div', { className: 'bowire-coverage-view' });

        // Summary card — uses getCoverageSummary so it reflects the
        // live `services` array. No precomputed totals on disk.
        var summary = getCoverageSummary();
        var summaryCard = el('div', { className: 'bowire-coverage-summary' });
        function metric(label, count, cls) {
            return el('div', { className: 'bowire-coverage-metric ' + (cls || '') },
                el('div', { className: 'bowire-coverage-metric-count', textContent: String(count) }),
                el('div', { className: 'bowire-coverage-metric-label', textContent: label })
            );
        }
        var coveredRecent = summary.recent;
        summaryCard.appendChild(metric(
            coveredRecent + ' of ' + summary.total + ' methods exercised in the last 7 days',
            coveredRecent,
            'recent'));
        summaryCard.appendChild(metric('stale (≤30d)',  summary.stale,     'stale'));
        summaryCard.appendChild(metric('failing',        summary.failing,   'failing'));
        summaryCard.appendChild(metric('uncovered',      summary.uncovered, 'uncovered'));
        wrap.appendChild(summaryCard);

        // Filter bar.
        var entries = getRunHistory().slice().reverse(); // newest-first
        var filters = el('div', { className: 'bowire-coverage-filters' });

        function makeSelect(opts, current, onChange) {
            var sel = el('select', {
                className: 'bowire-settings-select bowire-coverage-filter-select',
                onChange: function (e) { onChange(e.target.value); }
            });
            for (var i = 0; i < opts.length; i++) {
                var o = el('option', { value: opts[i].value, textContent: opts[i].label });
                if (opts[i].value === current) o.selected = true;
                sel.appendChild(o);
            }
            return sel;
        }
        filters.appendChild(el('label', { className: 'bowire-coverage-filter-label' },
            el('span', { textContent: 'Source' }),
            makeSelect(
                [
                    { value: 'all',              label: 'All sources' },
                    { value: 'discover',         label: 'Discover' },
                    { value: 'compose',          label: 'Compose' },
                    { value: 'benchmark',        label: 'Benchmark' },
                    { value: 'recording-replay', label: 'Recording replay' },
                    { value: 'flow',             label: 'Flow' }
                ],
                _runHistoryFilter.source,
                function (v) { _runHistoryFilter.source = v; if (typeof renderSettingsDialog === 'function') renderSettingsDialog(); }
            )
        ));
        filters.appendChild(el('label', { className: 'bowire-coverage-filter-label' },
            el('span', { textContent: 'Outcome' }),
            makeSelect(
                [
                    { value: 'all',   label: 'All outcomes' },
                    { value: 'ok',    label: 'Pass' },
                    { value: 'fail',  label: 'Fail' },
                    { value: 'error', label: 'Error' }
                ],
                _runHistoryFilter.outcome,
                function (v) { _runHistoryFilter.outcome = v; if (typeof renderSettingsDialog === 'function') renderSettingsDialog(); }
            )
        ));
        filters.appendChild(el('label', { className: 'bowire-coverage-filter-label' },
            el('span', { textContent: 'Search' }),
            el('input', {
                type: 'text',
                className: 'bowire-settings-input bowire-coverage-filter-search',
                value: _runHistoryFilter.search || '',
                placeholder: 'service/method or error text',
                onInput: function (e) { _runHistoryFilter.search = e.target.value; },
                onChange: function (e) {
                    _runHistoryFilter.search = e.target.value;
                    if (typeof renderSettingsDialog === 'function') renderSettingsDialog();
                }
            })
        ));
        var clearBtn = el('button', {
            className: 'bowire-settings-action-btn',
            type: 'button',
            textContent: 'Clear history',
            onClick: function () {
                if (typeof bowireConfirm === 'function') {
                    bowireConfirm('Clear the workspace run history?', function () {
                        clearRunHistory();
                        if (typeof renderSettingsDialog === 'function') renderSettingsDialog();
                    }, { title: 'Clear run history', danger: true, confirmText: 'Clear' });
                } else {
                    clearRunHistory();
                    if (typeof renderSettingsDialog === 'function') renderSettingsDialog();
                }
            }
        });
        filters.appendChild(clearBtn);
        wrap.appendChild(filters);

        // Table — capped at 50 visible rows per spec.
        var filtered = _applyRunHistoryFilter(entries);
        var visible = filtered.slice(0, 50);
        var totalCount = entries.length;
        var filteredCount = filtered.length;
        wrap.appendChild(el('div', { className: 'bowire-coverage-meta',
            textContent: visible.length + ' of ' + filteredCount + ' filtered runs shown'
                + (totalCount !== filteredCount ? ' (' + totalCount + ' total)' : '')
        }));

        if (visible.length === 0) {
            wrap.appendChild(el('div', { className: 'bowire-coverage-empty',
                textContent: totalCount === 0
                    ? 'No runs recorded yet. Invoke a method, run a benchmark, or replay a recording to start tracking.'
                    : 'No runs match the current filter.'
            }));
            return wrap;
        }

        var table = el('table', { className: 'bowire-coverage-table' });
        var thead = el('thead', {},
            el('tr', {},
                el('th', { textContent: 'When' }),
                el('th', { textContent: 'Method' }),
                el('th', { textContent: 'Source' }),
                el('th', { textContent: 'Outcome' }),
                el('th', { textContent: 'Duration' })
            )
        );
        table.appendChild(thead);
        var tbody = el('tbody');
        for (var i = 0; i < visible.length; i++) {
            (function (entry) {
                var when = new Date(entry.startedAt).toLocaleString();
                var label = (entry.service || '') + (entry.method ? ' / ' + entry.method : '');
                if (!label.trim() || label === ' / ') label = entry.methodId;
                var tr = el('tr', {
                    className: 'bowire-coverage-row bowire-coverage-row-' + entry.outcome,
                    title: entry.errorMessage || '',
                    onClick: function () {
                        // Click-through to the method tab — find the
                        // service + method in the live `services`
                        // array and call openTab if available.
                        if (typeof openTab !== 'function' || !Array.isArray(services)) return;
                        var sv = services.find(function (s) { return s.name === entry.service; });
                        if (!sv) return;
                        var mt = (sv.methods || []).find(function (m) { return m.name === entry.method; });
                        if (!mt) return;
                        try { openTab(sv, mt, { inNewTab: false }); }
                        catch (_) { /* best-effort */ }
                    }
                });
                tr.appendChild(el('td', { textContent: when }));
                tr.appendChild(el('td', { className: 'bowire-coverage-method', textContent: label }));
                tr.appendChild(el('td', { textContent: _coverageRailLabel(entry.source) }));
                tr.appendChild(el('td', {
                    className: 'bowire-coverage-outcome bowire-coverage-outcome-' + entry.outcome,
                    textContent: entry.outcome
                }));
                tr.appendChild(el('td', { textContent: (entry.durationMs || 0) + ' ms' }));
                tbody.appendChild(tr);
            })(visible[i]);
        }
        table.appendChild(tbody);
        wrap.appendChild(table);
        return wrap;
    }
