    // #126 Phase D — capture the active method's pre/post-script
    // source so a captured recording step can re-run the exact same
    // dynamic shape on replay (signed bodies, captured tokens,
    // assertion expectations, …). Returns null when both scripts
    // are empty so the captured step stays lean for the common
    // case where the operator isn't using scripts.
    function _captureRecordingScripts(svc, method) {
        try {
            if (typeof getMethodScripts !== 'function') return null;
            var s = getMethodScripts(svc, method);
            var pre = (s && s.preScript) || '';
            var post = (s && s.postScript) || '';
            if (!pre.trim() && !post.trim()) return null;
            return { preScript: pre, postScript: post };
        } catch (_) { return null; }
    }

    // #534 — read an application/problem+json body off a non-ok
    // discovery response. MUST never throw: a reverse proxy or gateway
    // in front of an embedded host answers 502 with HTML, and this runs
    // inside the caller's try block where a parse error would turn a
    // soft "Disconnected" into an unhandled rejection.
    async function _readProblemBody(resp) {
        try {
            var j = await resp.json();
            return (j && typeof j === 'object') ? j : {};
        } catch (_) { return {}; }
    }

    // Normalise a problem body's `attempts` / `hint` into the module
    // state the diagnostics disclosure reads. Accepts BOTH shapes:
    // the object array this Bowire emits, and the legacy string array
    // ("gRPC: connection refused") an older embedded host still sends —
    // a newer workbench pointed at an older host must still render
    // something rather than nothing.
    function _recordDiscoveryProblem(key, prob) {
        if (!key || !prob) return;
        var raw = prob.attempts;
        var list = [];
        if (Array.isArray(raw)) {
            for (var i = 0; i < raw.length; i++) {
                var a = raw[i];
                if (a && typeof a === 'object') {
                    list.push({
                        pluginId: a.pluginId || a.plugin || '',
                        plugin: a.plugin || a.pluginId || 'plugin',
                        outcome: a.outcome || 'error',
                        servicesFound: typeof a.servicesFound === 'number' ? a.servicesFound : 0,
                        durationMs: typeof a.durationMs === 'number' ? a.durationMs : 0,
                        message: a.message || '',
                        // Optional per-step breakdown (#544). Absent on
                        // every attempt a plugin without the diagnostics
                        // seam produces, and on every older host.
                        details: Array.isArray(a.details) ? a.details : null
                    });
                } else if (typeof a === 'string' && a) {
                    var sep = a.indexOf(':');
                    list.push({
                        pluginId: '',
                        plugin: sep > 0 ? a.slice(0, sep).trim() : a,
                        outcome: 'error',
                        servicesFound: 0,
                        durationMs: 0,
                        message: sep > 0 ? a.slice(sep + 1).trim() : ''
                    });
                }
            }
        }
        if (list.length > 0) discoveryAttempts[key] = list;
        else delete discoveryAttempts[key];

        if (prob.hint) discoveryHints[key] = String(prob.hint);
        else delete discoveryHints[key];
    }

    // Build the /api/services query string. `includeAttempts=1` opts into
    // the { services, attempts } success envelope (#544) — a partial probe
    // by definition returned services, so it arrives on a 200 and the bare
    // array shape has nowhere to put the diagnostic. Old hosts ignore the
    // flag and keep answering with the array, which _unwrapServices below
    // still understands.
    function _servicesQuery(url) {
        var q = (url === undefined) ? '' : serverUrlParam(false, url);
        return q + (q ? '&' : '?') + 'includeAttempts=1';
    }

    // Read a 200 body in either shape and file its attempts under `key`.
    // Returns the service array. Tolerating both shapes is what lets a
    // newer workbench keep working against an older embedded host — the
    // same stance #534 took on the attempts array itself.
    function _unwrapServices(key, body) {
        if (Array.isArray(body)) {
            // Legacy shape: no diagnostics channel at all, so a stale
            // failure list from this key's last bad probe has to go.
            delete discoveryAttempts[key];
            delete discoveryHints[key];
            discoveryDiagnosticsOpen.delete(key);
            return body;
        }
        if (!body || typeof body !== 'object') return [];
        // Attempts on a SUCCESS: this is the only path a `partial` outcome
        // can take. Recording them (rather than the old unconditional
        // delete) is what makes a half-broken server visible at all.
        _recordDiscoveryProblem(key, body);
        if (!discoveryAttempts[key]) discoveryDiagnosticsOpen.delete(key);
        return Array.isArray(body.services) ? body.services : [];
    }

    // Does this key have a plugin that returned services AND faulted?
    // Derived rather than stored: connectionStatuses stays 'connected'
    // because the HTTP call genuinely succeeded, and widening that bag
    // would touch ~20 readers for a state none of them mean.
    function urlDiscoveryDegraded(key) {
        var attempts = (typeof discoveryAttempts !== 'undefined') ? discoveryAttempts[key] : null;
        if (!Array.isArray(attempts)) return false;
        for (var i = 0; i < attempts.length; i++) {
            if (attempts[i] && attempts[i].outcome === 'partial') return true;
        }
        return false;
    }

    // ---- API Calls ----
    async function fetchServices() {
        // Mark discovery in flight + clear stale errors so the empty-state
        // landing page (landing.js) can render the loading state and the
        // failure card based on fresh state. The render() call below makes
        // the spinner instantly visible — without it, the user would see
        // the previous landing state until discovery completed.
        isLoadingServices = true;
        discoveryErrors = {};
        discoveryAttempts = {};
        discoveryHints = {};
        discoveryDiagnosticsOpen.clear();
        render();

        // Fetch protocols list once — this is identity, doesn't depend on URL
        try {
            var protocolsResp = await fetch(`${config.prefix}/api/protocols`);
            if (protocolsResp.ok) protocols = await protocolsResp.json();
        } catch (_) { /* protocols endpoint optional */ }

        // Always run embedded discovery (no URL) first — this finds
        // gRPC (via reflection), SignalR (via hub metadata), REST (via
        // API explorer), WebSocket, SSE, etc. on the host itself.
        try {
            // Abort after 12 s so a hung server-side discovery (e.g. a
            // stuck plugin probing an unreachable target URL) can never
            // wedge the UI in the loading spinner. Failure surfaces via
            // the discovery-failed landing card instead.
            const ctrl = new AbortController();
            const timer = setTimeout(() => ctrl.abort(), 12000);
            try {
                const resp = await fetch(`${config.prefix}/api/services${_servicesQuery(undefined)}`, { signal: ctrl.signal });
                if (!resp.ok) {
                    // Read the problem body BEFORE throwing — it carries
                    // the per-plugin attempts that explain the empty
                    // sidebar. The throw still lands in the catch below
                    // so discoveryErrors['(embedded)'] stays populated.
                    var prob = await _readProblemBody(resp);
                    _recordDiscoveryProblem('(embedded)', prob);
                    throw new Error(prob.title || ('HTTP ' + resp.status));
                }
                services = _unwrapServices('(embedded)', await resp.json());
            } finally {
                clearTimeout(timer);
            }
        } catch (e) {
            services = [];
            discoveryErrors['(embedded)'] = e.name === 'AbortError'
                ? 'Discovery timed out after 12 s'
                : e.message;
        }

        // Then fan out per configured URL for protocols that need a
        // remote address: MQTT (broker URL), OData ($metadata URL),
        // standalone gRPC servers, etc. Results are merged and
        // de-duplicated by (source + name).
        if (serverUrls.length > 0) {
            var urlResults = await fetchServicesForAllUrls();
            // De-dupe: skip URL results whose (source, name) already
            // appeared in the embedded set to avoid duplicates.
            var seen = new Set();
            for (var ei = 0; ei < services.length; ei++) {
                seen.add(services[ei].source + '::' + services[ei].name);
            }
            for (var ui = 0; ui < urlResults.length; ui++) {
                var key = urlResults[ui].source + '::' + urlResults[ui].name;
                if (!seen.has(key)) {
                    services.push(urlResults[ui]);
                    seen.add(key);
                }
            }
        }

        // Discovery is done — flip the in-flight flag before the auto-select
        // and the trailing render() so landing.js knows to leave the loading
        // state on the next paint.
        isLoadingServices = false;

        // Protocol filter (multi-select via chips + popup) defaults to
        // empty = "show all". No auto-select here — if the user previously
        // saved a filter in localStorage (loaded on boot), protocolFilter
        // already reflects it. Drop entries whose protocol is no longer in
        // the service set so stale filters don't hide everything.
        if (protocolFilter.size > 0) {
            var validIds = new Set(services.map(function (s) { return s.source; }));
            var changed = false;
            protocolFilter.forEach(function (id) {
                if (!validIds.has(id)) { protocolFilter.delete(id); changed = true; }
            });
            if (changed) {
                persistProtocolFilter();
                refreshSelectedProtocolFromFilter();
            }
        }

        // Auto-expand all services if there are few — but only when
        // the user hasn't already set their own expanded-state (don't
        // fight the persisted localStorage preference on reload).
        var visibleServices = getFilteredServices();
        if (visibleServices.length <= 5 && expandedServices.size === 0) {
            for (const s of visibleServices) expandedServices.add(s.name);
            persistExpandedServices();
        }
        render();
    }

    // ---- Schema Watch Mode (#48) ----
    // Re-discovers the active server URL(s) every N seconds and reports
    // what changed. Useful during API development, where the schema
    // moves under you while the workbench is open.
    //
    // The comparison is a keyed set diff, not a count subtraction. A
    // count told you "0 methods changed" when GetUser was replaced by
    // FetchUser — the arithmetic cancelled out and the rename, the one
    // event most likely to break a saved request, went unreported.
    var schemaWatchTimer = null;
    var schemaWatchRunning = false;
    var schemaWatchDelta = null;

    var SCHEMA_WATCH_DEFAULT_SECONDS = 15;
    var SCHEMA_WATCH_MIN_SECONDS = 5;
    var SCHEMA_WATCH_MAX_SECONDS = 300;

    // Settings → General writes bowire_watch_interval. Nothing read it
    // before #48, so the control was documented as working and did
    // nothing; the button hardcoded 15s. Clamped to the same bounds the
    // input advertises, because localStorage is user-editable.
    //
    // #185 — a workspace can override the global interval (set in the
    // workspace-detail General tab). The override rides the wsKey()
    // namespace, so it follows the active workspace automatically.
    function schemaWatchSeconds() {
        var raw = NaN;
        if (typeof wsKey === 'function') {
            raw = parseInt(localStorage.getItem(wsKey('bowire_watch_interval')), 10);
        }
        if (!isFinite(raw)) {
            raw = parseInt(localStorage.getItem('bowire_watch_interval'), 10);
        }
        if (!isFinite(raw)) return SCHEMA_WATCH_DEFAULT_SECONDS;
        return Math.min(SCHEMA_WATCH_MAX_SECONDS, Math.max(SCHEMA_WATCH_MIN_SECONDS, raw));
    }

    // Identity of a method across two discoveries. fullName is what the
    // sidebar, saved tabs and coverage all key on, so a change of
    // fullName is an add + a remove, which is the truth: the old one is
    // no longer callable.
    function schemaMethodKey(svc, m) {
        return (svc.name || '') + ' ' + (m.fullName || m.name || '');
    }

    // Field shape, one line per field. Depth-bounded: a schema may
    // reference itself (a Node with children of its own type), and an
    // unbounded walk would never return.
    //
    // Fields are sorted by name before serialising: a swagger-gen /
    // protoc rebuild that merely reorders properties emits the same
    // set in a different order, and reporting that as "~ changed" is
    // exactly the false alarm #185's AST-level criterion forbids.
    // (What identifies a field is its name — for protobuf its number —
    // never its position.)
    function schemaMessageShape(msg, depth) {
        if (!msg || depth > 3) return '';
        var fields = (msg.fields || []).slice().sort(function (a, b) {
            var an = a.name || '', bn = b.name || '';
            return an < bn ? -1 : (an > bn ? 1 : 0);
        });
        var parts = new Array(fields.length);
        for (var i = 0; i < fields.length; i++) {
            var f = fields[i];
            parts[i] = (f.name || '') + ':' + (f.type || '')
                + (f.isRepeated ? '[]' : '')
                + (f.required ? '!' : '')
                + (f.source ? '@' + f.source : '')
                + (f.messageType ? '{' + schemaMessageShape(f.messageType, depth + 1) + '}' : '');
        }
        return (msg.name || '') + '(' + parts.join(',') + ')';
    }

    // Everything about a method that changes what a caller has to send
    // or can expect back, split into named facets (#185) so a changed
    // method can be CLASSIFIED — signature vs deprecation vs
    // annotation — and the change log can say which facet moved
    // instead of a bare "~ changed". Two methods with the same key but
    // different callable facets are "~ changed".
    function schemaMethodRecord(m) {
        return {
            route: ((m.httpMethod || '') + ' ' + (m.httpPath || '')).trim(),
            kind: (m.methodType || '')
                + (m.clientStreaming ? '|cs' : '')
                + (m.serverStreaming ? '|ss' : ''),
            input: schemaMessageShape(m.inputType, 0),
            output: schemaMessageShape(m.outputType, 0),
            deprecated: !!m.deprecated,
            // Prose. Deliberately NOT part of the callable surface —
            // an edit here reaches the change log but never the toast
            // or the sidebar markers (#48's discipline stands).
            note: (m.summary || '') + '\n' + (m.description || '')
        };
    }

    // Which facets of the callable surface moved, as a short human
    // line for the change log ('route GET /a → PUT /a, request shape
    // changed'). Empty string when the callable surface is identical.
    function schemaChangeDetail(b, a) {
        var bits = [];
        if (b.route !== a.route) bits.push('route ' + (b.route || '—') + ' → ' + (a.route || '—'));
        if (b.kind !== a.kind) bits.push('invocation type changed');
        if (b.input !== a.input) bits.push('request shape changed');
        if (b.output !== a.output) bits.push('response shape changed');
        return bits.join(', ');
    }

    function schemaSnapshot(list) {
        var snap = Object.create(null);
        for (var i = 0; i < (list || []).length; i++) {
            var svc = list[i];
            var methods = Object.create(null);
            var ms = svc.methods || [];
            for (var j = 0; j < ms.length; j++) {
                methods[schemaMethodKey(svc, ms[j])] = schemaMethodRecord(ms[j]);
            }
            snap[svc.name] = { source: svc.source || '', methods: methods };
        }
        return snap;
    }

    // Returns null when nothing moved, so callers can treat "no delta"
    // and "no change" as the same thing.
    //
    // changedMethods entries carry a type — 'signature' (callable
    // surface) or 'deprecation' (flag flipped, surface intact) — plus
    // a facet-level detail line. Prose-only edits land in the separate
    // annotatedMethods bucket: they set callableMoved = false when
    // they are the only movement, and the watch tick uses that flag to
    // keep them out of the toast / banner / sidebar markers while the
    // change log (#185) still records them.
    function schemaDiff(before, after) {
        var d = {
            addedServices: [], removedServices: [],
            addedMethods: [], removedMethods: [], changedMethods: [],
            annotatedMethods: [],
            at: null
        };
        var name;
        for (name in after) {
            if (!(name in before)) {
                d.addedServices.push(name);
                // Every method of a new service is new, but listing them
                // individually would bury the one fact that matters.
                continue;
            }
            var b = before[name].methods, a = after[name].methods, key;
            for (key in a) {
                if (!(key in b)) {
                    d.addedMethods.push({ service: name, key: key });
                } else {
                    var detail = schemaChangeDetail(b[key], a[key]);
                    if (detail) {
                        d.changedMethods.push({ service: name, key: key, type: 'signature', detail: detail });
                    } else if (a[key].deprecated !== b[key].deprecated) {
                        d.changedMethods.push({
                            service: name, key: key, type: 'deprecation',
                            detail: a[key].deprecated ? 'marked deprecated' : 'deprecation removed'
                        });
                    } else if (a[key].note !== b[key].note) {
                        d.annotatedMethods.push({ service: name, key: key, detail: 'description updated' });
                    }
                }
            }
            for (key in b) {
                if (!(key in a)) d.removedMethods.push({ service: name, key: key });
            }
        }
        for (name in before) {
            if (!(name in after)) d.removedServices.push(name);
        }
        var moved = d.addedServices.length + d.removedServices.length
            + d.addedMethods.length + d.removedMethods.length + d.changedMethods.length;
        d.callableMoved = moved > 0;
        return (moved + d.annotatedMethods.length) === 0 ? null : d;
    }

    function schemaDeltaSummary(d) {
        var parts = [];
        if (d.addedServices.length) parts.push('+' + d.addedServices.length + ' service' + (d.addedServices.length !== 1 ? 's' : ''));
        if (d.removedServices.length) parts.push('−' + d.removedServices.length + ' service' + (d.removedServices.length !== 1 ? 's' : ''));
        if (d.addedMethods.length) parts.push('+' + d.addedMethods.length + ' method' + (d.addedMethods.length !== 1 ? 's' : ''));
        if (d.removedMethods.length) parts.push('−' + d.removedMethods.length + ' method' + (d.removedMethods.length !== 1 ? 's' : ''));
        if (d.changedMethods.length) parts.push('~' + d.changedMethods.length + ' changed');
        // Annotation edits are logged, not alerted — mentioned here only
        // when they ride along with a callable-surface change.
        if (d.annotatedMethods && d.annotatedMethods.length) {
            parts.push('±' + d.annotatedMethods.length + ' note' + (d.annotatedMethods.length !== 1 ? 's' : ''));
        }
        return parts.join(', ');
    }

    /// Last delta the watch observed, or null. The sidebar reads this;
    /// it survives ticks that report nothing so the operator can step
    /// away and still see what moved, and is cleared on dismiss.
    function getSchemaWatchDelta() { return schemaWatchDelta; }

    function clearSchemaWatchDelta() {
        schemaWatchDelta = null;
        render();
    }

    // Per-service tally for the sidebar header chip, or null when this
    // service was untouched by the last poll.
    function schemaServiceDelta(svcName) {
        if (!schemaWatchDelta) return null;
        var d = schemaWatchDelta;
        if (d.addedServiceNames.has(svcName)) {
            return { label: 'new', title: 'Service appeared since the last poll' };
        }
        var e = d.perService[svcName];
        if (!e) return null;
        var added = e.added, removed = e.removed, changed = e.changed;
        var bits = [], title = [];
        if (added) { bits.push('+' + added); title.push(added + ' method(s) added'); }
        if (removed) { bits.push('−' + removed); title.push(removed + ' method(s) removed'); }
        if (changed) { bits.push('~' + changed); title.push(changed + ' method(s) changed shape'); }
        return { label: bits.join(' '), title: title.join('\n') };
    }

    // True when this method row should carry an added / changed marker.
    // Removals have no row to mark — they are only in the summary.
    //
    // Called once per method row on the RENDER path, so it must not scan.
    // The lookup sets are built once when the delta is published; scanning
    // the arrays here instead made every render O(rows x delta), and that
    // cost outlived the watch because the delta is sticky until dismissed.
    function schemaWatchMarkerFor(svcName, method) {
        if (!schemaWatchDelta) return null;
        var key = schemaMethodKey({ name: svcName }, method);
        if (schemaWatchDelta.addedKeys.has(key)) return 'added';
        if (schemaWatchDelta.changedKeys.has(key)) return 'changed';
        if (schemaWatchDelta.addedServiceNames.has(svcName)) return 'added';
        return null;
    }

    // Index a fresh delta for the render path. Sets, not arrays: the render
    // path asks "is this one of them" once per row and once per service.
    function schemaIndexDelta(d) {
        d.addedKeys = new Set(d.addedMethods.map(function (m) { return m.key; }));
        d.changedKeys = new Set(d.changedMethods.map(function (m) { return m.key; }));
        d.addedServiceNames = new Set(d.addedServices);
        d.perService = Object.create(null);
        var bump = function (name, field) {
            var e = d.perService[name] || (d.perService[name] = { added: 0, removed: 0, changed: 0 });
            e[field]++;
        };
        d.addedMethods.forEach(function (m) { bump(m.service, 'added'); });
        d.removedMethods.forEach(function (m) { bump(m.service, 'removed'); });
        d.changedMethods.forEach(function (m) { bump(m.service, 'changed'); });
        return d;
    }

    // A poll that failed says nothing about the schema. Without this
    // guard a single unreachable moment — a restarting server, a blipped
    // connection, a gateway 502 — empties `services` and the diff
    // faithfully reports "−N services", as though the API had been
    // deleted. A watch that cries wolf on every hiccup is a watch you
    // learn to ignore, which costs more than the feature adds.
    function schemaWatchPollUsable() {
        for (var key in discoveryErrors) {
            if (Object.prototype.hasOwnProperty.call(discoveryErrors, key)) return false;
        }
        for (var i = 0; i < serverUrls.length; i++) {
            if (connectionStatuses[serverUrls[i]] === 'error') return false;
        }
        return true;
    }

    // The watch re-arms AFTER a poll settles rather than on a fixed
    // cadence, so two discoveries can never be in flight at once.
    //
    // `setInterval` with an async callback does not wait for the previous
    // callback to resolve — it fires on the wall clock regardless. A
    // discovery fan-out over every plugin takes seconds (measured: 5-8 s
    // against one URL), so any interval shorter than that had a new poll
    // starting before the last one returned. Two overlapping
    // `fetchServices` calls both reset `isLoadingServices`,
    // `discoveryErrors`, `discoveryAttempts` and `services`, then both
    // render — so the older one can finish last and publish stale results
    // over the newer one, and the page rebuilds its whole DOM tree twice
    // per period for as long as the watch runs.
    //
    // A self-scheduling timeout makes the period a *gap between* polls,
    // which is the honest reading of "re-discover every N seconds" for
    // work that takes an unknown time.
    function startSchemaWatch(intervalMs) {
        stopSchemaWatch({ quiet: true });
        var period = intervalMs || schemaWatchSeconds() * 1000;
        schemaWatchDelta = null;
        schemaWatchRunning = true;

        var tick = async function () {
            schemaWatchTimer = null;
            if (!schemaWatchRunning) return;
            try {
                var before = schemaSnapshot(services);
                await fetchServices();
                // Re-check: the operator may have switched the watch off
                // while this poll was in flight.
                if (!schemaWatchRunning || !schemaWatchPollUsable()) return;
                var delta = schemaDiff(before, schemaSnapshot(services));
                if (delta) {
                    delta.at = new Date();
                    // Toast / banner / sidebar markers only when the
                    // CALLABLE surface moved. An annotation-only delta
                    // still reaches the change log below — that's the
                    // whole point of the ± bucket — but alerting on
                    // prose would train the operator to ignore the
                    // alert (#48's discipline).
                    if (delta.callableMoved) {
                        schemaWatchDelta = schemaIndexDelta(delta);
                        var summary = schemaDeltaSummary(delta);
                        toast('Schema changed: ' + summary, 'info');
                        addConsoleEntry({
                            type: 'response', method: 'Schema Watch', status: 'Changed',
                            body: summary + '\n' + schemaDeltaDetail(delta)
                        });
                    }
                    // #185 — durable per-workspace change log feeding
                    // the statusbar pill + Discover rail badge.
                    if (typeof schemaChangeLogRecord === 'function') {
                        schemaChangeLogRecord(delta);
                    }
                    render();
                }
            } finally {
                // Re-arm even when the poll threw or returned early —
                // otherwise one failed discovery silently ends the watch
                // while the button still shows it as running.
                if (schemaWatchRunning) schemaWatchTimer = setTimeout(tick, period);
            }
        };

        schemaWatchTimer = setTimeout(tick, period);
        toast('Schema watch started (every ' + (period / 1000) + 's)', 'info');
    }

    // Long-form for the console, where there is room to name names.
    function schemaDeltaDetail(d) {
        var lines = [];
        d.addedServices.forEach(function (s) { lines.push('+ service ' + s); });
        d.removedServices.forEach(function (s) { lines.push('− service ' + s); });
        d.addedMethods.forEach(function (m) { lines.push('+ ' + m.key.replace(' ', ' / ')); });
        d.removedMethods.forEach(function (m) { lines.push('− ' + m.key.replace(' ', ' / ')); });
        d.changedMethods.forEach(function (m) {
            lines.push('~ ' + m.key.replace(' ', ' / ') + (m.detail ? ' (' + m.detail + ')' : ''));
        });
        (d.annotatedMethods || []).forEach(function (m) {
            lines.push('± ' + m.key.replace(' ', ' / ') + ' (description updated)');
        });
        return lines.join('\n');
    }

    function stopSchemaWatch(opts) {
        // Drop the delta unconditionally, before the running check. It is
        // read once per method row on the render path, so leaving it behind
        // taxed every later render — including every keystroke in the
        // sidebar search — long after the watch was switched off.
        schemaWatchDelta = null;
        if (!schemaWatchRunning) return;
        // Clearing the flag is what stops an in-flight poll from re-arming
        // when it settles.
        schemaWatchRunning = false;
        if (schemaWatchTimer !== null) {
            clearTimeout(schemaWatchTimer);
            schemaWatchTimer = null;
        }
        if (!opts || !opts.quiet) toast('Schema watch stopped', 'info');
    }

    function isSchemaWatchActive() {
        // The flag, not the timer handle: between a poll starting and
        // re-arming there is no timer, and the watch is still on.
        return schemaWatchRunning;
    }

    async function fetchServicesForAllUrls() {
        var results = await Promise.all(serverUrls.map(fetchServicesForUrl));
        // Flatten while preserving the per-URL discovery order
        var merged = [];
        for (var i = 0; i < results.length; i++) {
            for (var j = 0; j < results[i].length; j++) merged.push(results[i][j]);
        }
        return merged;
    }

    async function fetchServicesForUrl(url) {
        connectionStatuses[url] = 'connecting';
        // MQTT discovery scans topics for ~3s — log so the user
        // knows why it takes longer than HTTP-based protocols.
        var isMqtt = url.indexOf('mqtt') !== -1;
        if (isMqtt) {
            addConsoleEntry({ type: 'request', method: 'MQTT Discovery', status: 'Scanning', body: 'Subscribing to # at ' + url + ' (up to 3s)...' });
        }
        // Per-URL discovery timeout — stops one unreachable server
        // (TCP-refused, DNS-fail, hung HTTPS handshake) from wedging
        // the whole landing page in the loading spinner.
        const ctrl = new AbortController();
        const timer = setTimeout(() => ctrl.abort(), 12000);
        try {
            const resp = await fetch(`${config.prefix}/api/services${_servicesQuery(url)}`, { signal: ctrl.signal });
            if (!resp.ok) {
                // The problem+json body carries `attempts` (one entry per
                // probed plugin) and `hint`. Bailing on resp.ok alone —
                // which is what this did before #534 — threw all of that
                // away and left the operator with a bare "HTTP 502".
                var prob = await _readProblemBody(resp);
                connectionStatuses[url] = 'error';
                discoveryErrors[url] = prob.title || ('HTTP ' + resp.status + ' ' + resp.statusText);
                _recordDiscoveryProblem(url, prob);
                return [];
            }
            var data = await resp.json();
            if (data && data.title) {
                connectionStatuses[url] = 'error';
                discoveryErrors[url] = data.title;
                _recordDiscoveryProblem(url, data);
                return [];
            }
            connectionStatuses[url] = 'connected';
            // Replaces the per-URL diagnostics wholesale: refreshSourceServices()
            // re-enters this function for a single URL without the global
            // reset fetchServices() does, so a fixed URL must not keep the
            // failure list from its last bad probe — but a URL that is
            // merely DEGRADED has to keep the attempts that say so, which
            // the old unconditional delete threw away (#544).
            var list = _unwrapServices(url, data);
            if (isMqtt) {
                var topicCount = list.reduce(function (acc, s) { return acc + (s.methods ? s.methods.length : 0); }, 0);
                addConsoleEntry({ type: 'response', method: 'MQTT Discovery', status: 'OK', body: topicCount + ' topics discovered at ' + url });
            }
            // Make sure every service is tagged with its origin so per-service
            // routing works even if the server forgot to set it.
            for (var i = 0; i < list.length; i++) {
                if (!list[i].originUrl) list[i].originUrl = url;
            }
            return list;
        } catch (e) {
            connectionStatuses[url] = 'error';
            discoveryErrors[url] = e.name === 'AbortError'
                ? 'Discovery timed out after 12 s'
                : (e.message || 'Connection failed');
            return [];
        } finally {
            clearTimeout(timer);
        }
    }

    function getFilteredServices() {
        var list = services;

        // Source-mode filter (only when the URL is not locked — locked mode
        // bypasses the source selector entirely and shows everything).
        // The "Schema Files" tab shows everything that came from a user-
        // uploaded file (.proto, .json, .yaml). The "Server URL" tab shows
        // everything else.
        if (!config.lockServerUrl) {
            if (sourceMode === 'proto') {
                list = list.filter(function (s) { return s.isUploaded === true; });
            } else {
                list = list.filter(function (s) { return s.isUploaded !== true; });
            }
        }

        // Protocol filter — multi-select (union / OR). Empty set means
        // "no filter, show everything". Filter only applies in URL mode
        // because uploaded schemas come from the proto/OpenAPI upload
        // pane and don't need protocol filtering.
        if (protocolFilter.size > 0 && sourceMode !== 'proto') {
            list = list.filter(function (s) { return protocolFilter.has(s.source); });
        }

        // URL filter — multi-select per discovery URL (originUrl). Same
        // contract as protocolFilter: empty = no filter. Skip in proto
        // mode (uploads don't have a meaningful originUrl).
        if (urlFilter.size > 0 && sourceMode !== 'proto') {
            list = list.filter(function (s) { return s.originUrl && urlFilter.has(s.originUrl); });
        }

        // Plugin enable toggle — drop services from disabled plugins.
        // Separate from protocolFilter (which is an explicit include-set)
        // so users can disable a plugin once and not have to maintain
        // exclusions in every filter slot they use.
        if (sourceMode !== 'proto') {
            list = list.filter(function (s) {
                return !s.source || isProtocolEnabled(s.source);
            });
        }

        // #156 — favorites-only filter. Composes with the other
        // filters (intersect): a service is kept only when it has at
        // least one favorited method that survived the upstream
        // filters. The renderer further narrows each service to its
        // favorited methods only — see service-list rendering.
        if (typeof favoritesOnly !== 'undefined' && favoritesOnly
            && typeof isFavorite === 'function') {
            list = list.filter(function (s) {
                if (!s.methods) return false;
                for (var i = 0; i < s.methods.length; i++) {
                    if (isFavorite(s.name, s.methods[i].name)) return true;
                }
                return false;
            });
        }

        return list;
    }

    // #152 v2 — merge any per-URL headers (configured in the Sources
    // rail mode) into the metadata. Caller-supplied keys win — the
    // explicit metadata wins over the catch-all per-URL set so the
    // request pane's headers tab can still override.
    function _mergeUrlHeaders(svc, metadata) {
        try {
            if (typeof getUrlHeaders !== 'function') return metadata;
            // #253 — per-URL headers belong to the host the call actually
            // hits (override-aware, {{vars}} substituted so the lookup key
            // matches the real target host, not a template).
            var u = (typeof invocationUrlFor === 'function'
                ? invocationUrlFor(svc, selectedMethod) : (svc && svc.originUrl))
                || (typeof serverUrls !== 'undefined' && serverUrls[0]) || null;
            if (!u) return metadata;
            var bag = getUrlHeaders(u);
            if (!bag || Object.keys(bag).length === 0) return metadata;
            var out = Object.assign({}, bag, metadata || {});
            return out;
        } catch { return metadata; }
    }

    async function invokeUnary(service, method, messages, metadata) {
        metadata = _mergeUrlHeaders(selectedService, metadata);
        // #253 — snapshot the resolved (substituted) invocation URL AT FIRE
        // time. The recorder step runs after the await, by when the operator
        // may have switched methods; reading the override then would label
        // the recording with a different host than the call actually hit.
        var _sentInvocationUrl = (typeof invocationUrlFor === 'function'
            ? invocationUrlFor(selectedService, selectedMethod)
            : (selectedService && selectedService.originUrl)) || (serverUrls[0] || null);
        isExecuting = true;
        markJobActive(service, method);
        responseData = null;
        responseError = null;
        streamMessages = [];
        statusInfo = null;
        render();

        var fullName = service + '/' + method;
        // v2.2 T3 — wall-clock start so recordMethodRun gets the same
        // durationMs the response strip displays even if the server
        // doesn't echo duration_ms (network-error path).
        var _invokeStartMs = (typeof performance !== 'undefined' && performance.now) ? performance.now() : Date.now();
        addConsoleEntry({ type: 'request', method: fullName, body: messages[0] || '{}' });

        try {
            // If the user picked "via HTTP" on a transcoded gRPC method, attach
            // the inline TranscodedMethod hint so BowireApiEndpoints dispatches
            // through HttpInvoker instead of the gRPC plugin.
            var transcodedMethod = undefined;
            if (methodSupportsTranscoding(selectedMethod)
                && getTranscodingMode(service, method) === 'http')
            {
                var fields = [];
                if (selectedMethod.inputType && Array.isArray(selectedMethod.inputType.fields)) {
                    fields = selectedMethod.inputType.fields.map(function (f) {
                        return { name: f.name, type: f.type, source: f.source || 'body' };
                    });
                }
                transcodedMethod = {
                    httpMethod: selectedMethod.httpMethod,
                    httpPath: selectedMethod.httpPath,
                    fields: fields
                };
            }

            // Route to the URL the service was discovered from (multi-URL safety)
            const resp = await fetch(`${config.prefix}/api/invoke${serverUrlParamForService(selectedService, false, selectedMethod)}`, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({
                    service, method, messages,
                    metadata: metadata || null,
                    protocol: (selectedService && selectedService.source) || selectedProtocol || undefined,
                    transcodedMethod: transcodedMethod
                })
            });

            const result = await resp.json();
            if (result.title) {
                responseError = result;
                statusInfo = { status: 'Error', durationMs: 0, responseSize: 0 };
                addConsoleEntry({ type: 'error', method: fullName, status: 'Error', body: richErrorDetail(result, 'Request failed') });
            } else {
                responseData = result.response;
                captureResponse(result.response); // for ${response.X} chaining
                captureResponseForDiff(service, method, result.response, result.status, result.duration_ms);

                // Dispatch the unary response as a single synthetic frame
                // to the extension framework so any registered widget
                // (the MapLibre map on coordinate.wgs84 today) gets a
                // chance to render against it — same path the streaming
                // branch uses for every emitted frame. The dispatch
                // replay-cache absorbs the ordering against the async
                // mountWidgetsForMethod that the response pane will kick
                // off on the next render() pass. The frame id follows the
                // `service/method#index` format `bowireReplayKeyFor`
                // expects (single-frame unary → index 0).
                if (window.__bowireExtFramework) {
                    var unaryFrame = {
                        id: service + '/' + method + '#0',
                        index: 0,
                        data: result.response,
                        discriminator: (result && typeof result === 'object' && result.discriminator) || null,
                        interpretations: (result && typeof result === 'object' && Array.isArray(result.interpretations))
                            ? result.interpretations
                            : null,
                        _clientReceivedAtMs: Date.now()
                    };
                    window.__bowireExtFramework.dispatchStreamMessage(unaryFrame);
                }
                var reqSize = new Blob([JSON.stringify({ service: service, method: method, messages: messages })]).size;
                statusInfo = {
                    status: result.status,
                    durationMs: result.duration_ms,
                    metadata: result.metadata,
                    requestSize: reqSize,
                    responseSize: result.response ? new Blob([result.response]).size : 0
                };
                addConsoleEntry({
                    type: 'response',
                    method: fullName,
                    status: result.status,
                    durationMs: result.duration_ms,
                    body: result.response
                });

                // Run any test assertions configured for this method
                runAssertions(service, method, result.status, lastResponseJson);

                // ---- Post-response script ----
                // #126 — extra response info (status, durationMs, response
                // metadata) feeds the typed ctx.response surface so the
                // post-script can read response.status, response.headers,
                // response.durationMs without re-deriving them.
                runPostResponseScript(service, method, lastResponseJson, {
                    status: result.status,
                    durationMs: result.duration_ms,
                    headers: result.metadata || {}
                });
            }

            addHistory({
                service,
                method,
                methodType: selectedMethod?.methodType || 'Unary',
                body: messages[0] || '{}',
                messages: messages.slice(),
                metadata: metadata || null,
                status: statusInfo?.status || 'Error',
                durationMs: statusInfo?.durationMs || 0
            });

            // Recorder hook — also push the captured invocation onto the active
            // recording (silent no-op when no recording is active). The unary
            // path captures the response too, so the Convert-to-Tests and HAR
            // export paths have something to assert / dump.
            // httpPath / httpVerb are populated for protocols that carry them
            // (REST, gRPC-HTTP-transcoding) so the Phase-1 mock server can
            // match incoming wire requests against recorded steps.
            bowireCaptureStep({
                protocol: (selectedService && selectedService.source) || selectedProtocol || 'grpc',
                service,
                method,
                methodType: selectedMethod?.methodType || 'Unary',
                serverUrl: _sentInvocationUrl,
                body: messages[0] || '{}',
                messages: messages.slice(),
                metadata: metadata || null,
                status: statusInfo?.status || 'Error',
                durationMs: statusInfo?.durationMs || 0,
                response: responseData,
                // Protocols with a binary wire form distinct from their JSON
                // response body (gRPC today) populate this base64 field so
                // the Phase-1b mock server can re-emit the wire bytes 1:1.
                responseBinary: (result && typeof result === 'object' && result.response_binary) || null,
                // gRPC schema (FileDescriptorSet) captured at discovery time.
                // Attached to every step from this service so the Phase-1c
                // mock server can expose gRPC Server Reflection.
                schemaDescriptor: selectedService?.schemaDescriptor || null,
                httpPath: selectedMethod?.httpPath || null,
                httpVerb: selectedMethod?.httpMethod || null,
                // Phase-5 frame-semantics fields: the server resolved the
                // effective annotations against this frame and shipped the
                // typed interpretations + the discriminator value alongside
                // the response so replay can re-emit widgets deterministically.
                // Pre-Phase-5 servers leave both fields undefined; recording
                // captures from older Bowire builds round-trip unchanged.
                discriminator: (result && typeof result === 'object' && result.discriminator) || null,
                interpretations: (result && typeof result === 'object' && Array.isArray(result.interpretations))
                    ? result.interpretations
                    : null,
                // #126 Phase D — capture pre/post-script source so replay
                // re-runs the same dynamic shape that produced this
                // step. Skipped when the method has no scripts (the
                // common case) to keep the recording payload lean.
                scripts: _captureRecordingScripts(service, method)
            });
        } catch (e) {
            responseError = e.message;
            statusInfo = { status: 'NetworkError', durationMs: 0 };
            addConsoleEntry({ type: 'error', method: fullName, status: 'NetworkError', body: e.message });
        }

        // v2.2 T3 — record this invocation in the per-workspace
        // run-log so coverage chips + the run-history view pick it up.
        // outcome: 'ok' if the status looks successful (isHistoryEntryOk
        // already encodes the rule), 'error' on a network / protocol
        // exception (responseError set in the catch block above),
        // 'fail' on a server-returned error envelope (responseError is
        // an object with .title). Skipped when the runner returned
        // early (channel methods) — _invokeStartMs is the only signal.
        if (typeof safeRecordMethodRun === 'function') {
            var _outcome;
            if (responseError && typeof responseError === 'string') _outcome = 'error';
            else if (responseError) _outcome = 'fail';
            else _outcome = (typeof isHistoryEntryOk === 'function'
                ? (isHistoryEntryOk({ status: statusInfo && statusInfo.status }) ? 'ok' : 'fail')
                : 'ok');
            var _durMs = statusInfo && statusInfo.durationMs
                ? statusInfo.durationMs
                : Math.round(((typeof performance !== 'undefined' && performance.now) ? performance.now() : Date.now()) - _invokeStartMs);
            var _errMsg = null;
            if (typeof responseError === 'string') _errMsg = responseError;
            else if (responseError && responseError.title) _errMsg = responseError.title;
            safeRecordMethodRun({
                service: service,
                method: method,
                source: 'discover',
                startedAt: Date.now() - _durMs,
                durationMs: _durMs,
                outcome: _outcome,
                errorMessage: _errMsg
            });
        }
        isExecuting = false;
        markJobDone(service, method);
        render();
    }

    function invokeStreaming(service, method, messages, metadata) {
        metadata = _mergeUrlHeaders(selectedService, metadata);
        // #253 — snapshot the invocation URL at fire time; a server-stream's
        // 'done' handler can run long after the operator switched methods.
        var _sentInvocationUrl = (typeof invocationUrlFor === 'function'
            ? invocationUrlFor(selectedService, selectedMethod)
            : (selectedService && selectedService.originUrl)) || (serverUrls[0] || null);
        isExecuting = true;
        markJobActive(service, method);
        responseData = null;
        responseError = null;
        streamMessages = [];
        // Reset stream UI state so each new stream starts at the defaults
        // (auto-scroll on, follow latest, detail pane not maximized).
        streamSelectedIndex = null;
        streamAutoScroll = true;
        streamDetailMaximized = false;
        statusInfo = { status: 'Streaming', durationMs: 0 };
        render();

        const startTime = performance.now();
        const messagesJson = JSON.stringify(messages);
        // Pick the protocol the same way the unary path does: the service's
        // own origin first, then the global filter, else skip the param and
        // let the backend's default plugin handle it. Without this, streams
        // against a SignalR Hub were routed to the gRPC plugin and failed.
        var protocolForStream = (selectedService && selectedService.source) || selectedProtocol;
        var protocolParam = protocolForStream ? `&protocol=${encodeURIComponent(protocolForStream)}` : '';
        var metadataParam = metadata && Object.keys(metadata).length > 0
            ? `&metadata=${encodeURIComponent(JSON.stringify(metadata))}`
            : '';
        const url = `${config.prefix}/api/invoke/stream?service=${encodeURIComponent(service)}&method=${encodeURIComponent(method)}&messages=${encodeURIComponent(messagesJson)}${protocolParam}${metadataParam}${serverUrlParamForService(selectedService, true, selectedMethod)}`;

        var fullName = service + '/' + method;
        addConsoleEntry({ type: 'request', method: fullName, status: 'Streaming', body: messages[0] || '{}' });

        sseSource = new EventSource(url);
        // Track the SSE subscription so the statusbar pill + per-pane
        // state badge can answer "is this method still live?" without
        // poking at the legacy globals. The registry replaces nothing —
        // it's a parallel view that survives tab switching, which the
        // global `sseSource` does not (every tab share the same slot).
        registerSubscription(service, method, 'server', sseSource);

        sseSource.onmessage = function (event) {
            // Record wall-clock arrival so recordings persist the real
            // client-side cadence of the stream — needed for Phase-2 mock
            // replay timing. Server-side offset lives inside `parsed` as
            // `timestampMs` (see BowireInvokeEndpoints.cs).
            var receivedAt = Date.now();
            try {
                const parsed = JSON.parse(event.data);
                parsed._clientReceivedAtMs = receivedAt;
                // Phase 3.1 — mint a stable per-frame id used by the
                // Streaming-Frames selection sync. The server already
                // ships a monotonic `index`; we wrap it in
                // `${service}/${method}#${index}` so collisions across
                // method tabs in the same session are impossible. The
                // `id` is dispatched alongside the frame in the
                // `bowire:stream-message` event so widgets can correlate
                // selection-snapshot ids with frame events without
                // having to reach into method-state.
                var frameIndex = (typeof parsed.index === 'number') ? parsed.index : streamMessages.length;
                if (parsed.id === undefined) parsed.id = service + '/' + method + '#' + frameIndex;
                streamMessages.push(parsed);
                markSubscriptionFrame(service, method);
                // Chaining: capture the inner data payload (last message wins)
                if (parsed && parsed.data !== undefined) captureResponse(parsed.data);
                addConsoleEntry({ type: 'stream', method: fullName, body: parsed.data || event.data });
                // Fast-path: surgically append a single list item to the
                // streaming output instead of nuking the whole app DOM. The
                // first message of a stream still falls through to render()
                // because the container doesn't exist yet.
                //
                // ORDER MATTERS: the render/append step has to run BEFORE
                // we dispatch the frame to the extension framework. The
                // first frame of any stream is where the workbench mounts
                // the widget pane (renderStreamingPaneWithWidgets →
                // mountWidgetsForMethod → bowireMakeViewerCtx), which is
                // also where the `bowire:stream-message` listener gets
                // attached to document. Dispatching FIRST (the old order)
                // meant the event fired into an empty room — the widget's
                // frames-pipe was buffered, but the document-level
                // listener that feeds the pipe wasn't installed yet, so
                // the frame went nowhere. Map pins stayed off-screen until
                // a SECOND frame arrived, which the streaming-only
                // TacticalAPI sample never produced.
                if (!window.bowireAppendStreamMessage || !window.bowireAppendStreamMessage()) {
                    render();
                }
                // Frame-semantics extension framework — forward the
                // parsed frame to any viewer mounted against the active
                // method's annotations (Phase 3). Safe to call now that
                // the widget-mount listener is installed (either freshly
                // by the render() above or already from a previous frame).
                if (window.__bowireExtFramework) {
                    window.__bowireExtFramework.dispatchStreamMessage(parsed);
                }
            } catch {
                var fbIdx = streamMessages.length;
                const fallback = {
                    index: fbIdx,
                    id: service + '/' + method + '#' + fbIdx,
                    data: event.data,
                    _clientReceivedAtMs: receivedAt
                };
                streamMessages.push(fallback);
                markSubscriptionFrame(service, method);
                addConsoleEntry({ type: 'stream', method: fullName, body: event.data });
                // Same render-before-dispatch ordering as the JSON path
                // above — see the long comment there.
                if (!window.bowireAppendStreamMessage || !window.bowireAppendStreamMessage()) {
                    render();
                }
                if (window.__bowireExtFramework) {
                    window.__bowireExtFramework.dispatchStreamMessage(fallback);
                }
            }
        };

        sseSource.addEventListener('done', function () {
            const elapsed = Math.round(performance.now() - startTime);
            statusInfo = { status: 'OK', durationMs: elapsed };
            isExecuting = false;
            markJobDone(service, method);
            sseSource.close();
            sseSource = null;
            unregisterSubscription(service, method);

            // v2.2 T3 — server-streaming run completed cleanly.
            if (typeof safeRecordMethodRun === 'function') {
                safeRecordMethodRun({
                    service: service, method: method, source: 'discover',
                    startedAt: Date.now() - elapsed, durationMs: elapsed, outcome: 'ok'
                });
            }
            addConsoleEntry({ type: 'response', method: fullName, status: 'Completed', durationMs: elapsed });

            // ---- Post-response script (streaming) ----
            var streamResponseObj = streamMessages.length > 0 ? streamMessages[streamMessages.length - 1] : null;
            if (streamResponseObj && streamResponseObj.data !== undefined) {
                try { streamResponseObj = JSON.parse(streamResponseObj.data); } catch {}
            }
            runPostResponseScript(service, method, streamResponseObj, {
                status: 'OK',
                durationMs: elapsed,
                headers: {}
            });

            addHistory({
                service,
                method,
                methodType: selectedMethod?.methodType || 'ServerStreaming',
                body: messages[0] || '{}',
                messages: messages.slice(),
                metadata: metadata || null,
                status: 'OK',
                durationMs: elapsed
            });

            // Recorder hook — captured for HAR export, the recording log,
            // AND for Phase-2c mock replay: receivedMessages preserves the
            // full frame sequence with per-frame timestampMs so the mock
            // server can reproduce the original stream cadence.
            bowireCaptureStep({
                protocol: (selectedService && selectedService.source) || selectedProtocol || 'grpc',
                service,
                method,
                methodType: selectedMethod?.methodType || 'ServerStreaming',
                serverUrl: _sentInvocationUrl,
                body: messages[0] || '{}',
                messages: messages.slice(),
                metadata: metadata || null,
                status: 'OK',
                durationMs: elapsed,
                response: streamMessages.length > 0 ? streamMessages[streamMessages.length - 1] : null,
                receivedMessages: streamMessages.map(function (m, i) {
                    return {
                        index: (m && typeof m.index === 'number') ? m.index : i,
                        timestampMs: (m && typeof m.timestampMs === 'number') ? m.timestampMs : null,
                        data: m ? m.data : null,
                        // Protocols with a distinct binary wire form (gRPC)
                        // supply per-frame wire bytes via the envelope's
                        // responseBinary field. The Phase-2d mock-server
                        // replay path emits them 1:1 without re-encoding.
                        responseBinary: (m && typeof m.responseBinary === 'string') ? m.responseBinary : null,
                        // Phase-5 per-frame discriminator + interpretations.
                        // Older recordings (no Phase-5 server, no fields)
                        // round-trip unchanged because both keys stay null.
                        discriminator: (m && typeof m.discriminator === 'string') ? m.discriminator : null,
                        interpretations: (m && Array.isArray(m.interpretations)) ? m.interpretations : null
                    };
                }),
                httpPath: selectedMethod?.httpPath || null,
                httpVerb: selectedMethod?.httpMethod || null,
                // #126 Phase D — script source riding alongside the
                // captured step so replay reproduces the dynamic
                // shape (signed headers, captured tokens, …).
                scripts: _captureRecordingScripts(service, method)
            });

            render();
        });

        sseSource.addEventListener('error', function (e) {
            if (sseSource.readyState === EventSource.CLOSED) return;
            const elapsed = Math.round(performance.now() - startTime);
            responseError = 'Stream error occurred.';
            statusInfo = { status: 'Error', durationMs: elapsed };
            isExecuting = false;
            markJobDone(service, method);
            markSubscriptionError(service, method, 'Stream error');
            sseSource.close();
            sseSource = null;
            unregisterSubscription(service, method);
            // v2.2 T3 — stream errored out. Bucket as 'error' (server
            // never sent its 'done' event).
            if (typeof safeRecordMethodRun === 'function') {
                safeRecordMethodRun({
                    service: service, method: method, source: 'discover',
                    startedAt: Date.now() - elapsed, durationMs: elapsed,
                    outcome: 'error', errorMessage: 'Stream error'
                });
            }
            addConsoleEntry({ type: 'error', method: fullName, status: 'Error', body: 'Stream error occurred', durationMs: elapsed });
            render();
        });
    }

    function stopStreaming() {
        if (sseSource) {
            sseSource.close();
            sseSource = null;
        }
        isExecuting = false;
        if (selectedService && selectedMethod) {
            markJobDone(selectedService.name, selectedMethod.name);
            unregisterSubscription(selectedService.name, selectedMethod.name);
        }
        if (statusInfo) statusInfo.status = 'Cancelled';
        render();
    }

    // ---- Cross-method subscription stop ----
    // Closes the SSE for an arbitrary (service, method) — does NOT have
    // to be the active method. The active-subscriptions dropdown calls
    // this when the operator clicks "Stop" on a non-active row; the
    // streaming pane's own Stop button + the action bar both route
    // through here so the close path is uniform.
    function stopSubscriptionFor(svcName, methodName) {
        if (!svcName || !methodName) return;
        var entry = findSubscription(svcName, methodName);
        if (!entry) return;
        // If this is the active method's stream, also clear the legacy
        // globals so re-render doesn't pick up a stale "isExecuting".
        if (selectedService && selectedMethod
            && selectedService.name === svcName
            && selectedMethod.name === methodName) {
            if (sseSource) { try { sseSource.close(); } catch {} sseSource = null; }
            if (entry.kind === 'duplex' || entry.kind === 'client') {
                if (duplexSseSource) { try { duplexSseSource.close(); } catch {} duplexSseSource = null; }
                duplexConnected = false;
            }
            isExecuting = false;
            if (statusInfo) statusInfo.status = 'Cancelled';
        } else if (entry.sseSource) {
            try { entry.sseSource.close(); } catch {}
        }
        markJobDone(svcName, methodName);
        unregisterSubscription(svcName, methodName);
        // Duplex/client streams keep their own state in openChannels —
        // wipe that too so a future tab switch doesn't try to restore
        // a SSE source we just closed.
        var key = channelStoreKey(svcName, methodName);
        if (openChannels[key]) delete openChannels[key];
        render();
    }

    // ---- Channel Operations (Duplex / Client Streaming) ----

    function isChannelMethod() {
        return selectedMethod && (selectedMethod.methodType === 'Duplex' || selectedMethod.methodType === 'ClientStreaming');
    }

