    // ---- #182 — Side-by-side service version diff ----
    //
    // Pick two services (same API at two URLs / deployments, or a v1 vs a
    // _v2 twin), and Bowire aligns their methods, diffs the schema
    // (added / removed / signature-changed), invokes matched methods on
    // BOTH sides and diffs the responses field-by-field, and exports the
    // whole thing as a markdown report for the v2.5 PR bot.
    //
    // Reuse boundary: the schema-level facet diff is the pure #185 layer
    // (schemaMethodRecord / schemaChangeDetail / schemaMessageShape in
    // api.js); the field-level response diff is diffJsonStructured
    // (perf-diff.js). This fragment owns the alignment, the two headless
    // IO seams, the surface, and the markdown emitter.
    //
    // Why a dedicated discovery per side rather than reading the global
    // `services` array: discovery de-dupes URL results by source+'::'+name
    // (api.js), so the SAME service at two URLs — the core #182 scenario —
    // collapses to one entry in `services`. Each compare side therefore
    // discovers its chosen URL independently so both deployments are
    // actually representable.

    // Version markers we strip so a v1 method aligns with its v2 twin.
    // Deliberately conservative — only delimiter-anchored markers, never a
    // bare trailing digit (which would maul IPv4, Base64, &c). Covers the
    // three shapes real schemas use: REST path segment /v2/, dotted gRPC
    // package .v2., and the _v2 suffix the issue calls out.
    function _stripVersionMarkers(s) {
        return String(s || '')
            .replace(/\/v\d+(?=\/|$)/gi, '')
            .replace(/\.v\d+(?=\.|\/|$)/gi, '')
            .replace(/_v\d+\b/gi, '');
    }

    // The key a method aligns on across two services. Unlike #185 (which
    // keys on fullName to track ONE service over time), a version compare
    // keys on the method NAME — the operationId (REST), method name (gRPC),
    // field (GraphQL) — with version markers stripped. fullName is
    // deliberately avoided: for REST it is "VERB /path", so a v1 GET that
    // becomes a v2 POST would fail to align and read as add+remove instead
    // of the route change it is. Falls back to fullName when a method
    // somehow has no name.
    function compareMethodAlignKey(m) {
        return _stripVersionMarkers(m.name || m.fullName || '').toLowerCase();
    }

    // Build a per-service alignment map keyed by version-stripped name.
    // Stripping is meant to align a method with its twin in the OTHER
    // service, never to fuse two distinct methods within the SAME one:
    // a service that keeps GetUser AND GetUser_v2 (a normal
    // deprecate-but-keep pattern) would otherwise collapse both onto one
    // key and silently drop the second. On a collision the later method
    // falls back to its raw (un-stripped) name so neither is lost.
    function _buildAlignMap(methods) {
        var map = Object.create(null);
        (methods || []).forEach(function (m) {
            var k = compareMethodAlignKey(m);
            if (k in map) {
                k = (m.fullName || m.name || '').toLowerCase();
                while (k in map) k = k + '~';
            }
            map[k] = m;
        });
        return map;
    }

    // Align two services' methods by their version-stripped key. Returns
    // { paired:[{a,b}], onlyA:[m], onlyB:[m] }.
    function alignCompareMethods(svcA, svcB) {
        var mapA = _buildAlignMap(svcA && svcA.methods);
        var mapB = _buildAlignMap(svcB && svcB.methods);
        var paired = [], onlyA = [], onlyB = [], k;
        for (k in mapA) {
            if (k in mapB) paired.push({ key: k, a: mapA[k], b: mapB[k] });
            else onlyA.push(mapA[k]);
        }
        for (k in mapB) {
            if (!(k in mapA)) onlyB.push(mapB[k]);
        }
        return { paired: paired, onlyA: onlyA, onlyB: onlyB };
    }

    // Classify one aligned method pair. Reuses the #185 pure helpers:
    // schemaChangeDetail names which callable facet moved (route / kind /
    // request shape / response shape); deprecation and prose are compared
    // here because that layer keeps them in separate buckets.
    function compareMethodPair(a, b) {
        var ra = schemaMethodRecord(a), rb = schemaMethodRecord(b);
        // Version-normalise the route before classifying. The align key
        // already stripped /v1/ vs /v2/, so a pure version bump must not
        // read as a "route changed" signature difference — otherwise
        // EVERY method of a path-versioned REST API is flagged as changed
        // and the real signature changes drown in the noise. Only the
        // route carries the version marker; input/output field shapes do
        // not, so those stay raw.
        ra = { route: _stripVersionMarkers(ra.route), kind: ra.kind, input: ra.input, output: ra.output, deprecated: ra.deprecated, note: ra.note };
        rb = { route: _stripVersionMarkers(rb.route), kind: rb.kind, input: rb.input, output: rb.output, deprecated: rb.deprecated, note: rb.note };
        var detail = schemaChangeDetail(ra, rb);
        var bits = [];
        if (detail) bits.push(detail);
        if (ra.deprecated !== rb.deprecated) {
            bits.push(rb.deprecated ? 'marked deprecated' : 'deprecation removed');
        }
        var changed = bits.length > 0;
        // Prose-only edits are noted but do not count as a breaking change
        // (same discipline as #185 — descriptions move constantly).
        var noteOnly = !changed && ra.note !== rb.note;
        return { changed: changed, noteOnly: noteOnly, detail: bits.join(', ') };
    }

    // Full schema-level comparison of two services: aligned pairs split
    // into changed / unchanged, plus methods present on only one side.
    function computeServiceSchemaDiff(svcA, svcB) {
        var aligned = alignCompareMethods(svcA, svcB);
        var changed = [], unchanged = [];
        aligned.paired.forEach(function (p) {
            var c = compareMethodPair(p.a, p.b);
            (c.changed ? changed : unchanged).push({ key: p.key, a: p.a, b: p.b, detail: c.detail, noteOnly: c.noteOnly });
        });
        return {
            added: aligned.onlyB,      // in B, not A — "new in the target version"
            removed: aligned.onlyA,    // in A, not B — "gone in the target version"
            changed: changed,
            unchanged: unchanged
        };
    }

    // One-line human summary of a schema diff (mirrors schemaDeltaSummary).
    function compareSchemaSummary(d) {
        if (!d) return '';
        var parts = [];
        if (d.added.length) parts.push('+' + d.added.length + ' method' + (d.added.length !== 1 ? 's' : ''));
        if (d.removed.length) parts.push('−' + d.removed.length + ' method' + (d.removed.length !== 1 ? 's' : ''));
        if (d.changed.length) parts.push('~' + d.changed.length + ' changed');
        return parts.length ? parts.join(', ') : 'schema identical';
    }

    // ---- headless IO (does not touch the request tab / global selection) ----

    // Discover one URL's services without disturbing the open request tab
    // or the global `services` array. undefined url = the embedded host.
    async function discoverForCompare(url) {
        var q = (typeof url === 'string' && url) ? serverUrlParam(false, url) : '';
        q += (q ? '&' : '?') + 'includeAttempts=1';
        var resp = await fetch(config.prefix + '/api/services' + q);
        if (!resp.ok) {
            var msg = 'HTTP ' + resp.status;
            try { var prob = await resp.json(); if (prob && prob.title) msg = prob.title; } catch (_) { /* keep */ }
            throw new Error(msg);
        }
        var body = await resp.json();
        var list = Array.isArray(body) ? body : (body && Array.isArray(body.services) ? body.services : []);
        for (var i = 0; i < list.length; i++) {
            if (url && !list[i].originUrl) list[i].originUrl = url;
        }
        return list;
    }

    // Invoke one method against one service's own URL, headless. Mirrors
    // the benchmark worker / collection runner seam — POSTs directly, reads
    // the { response, status, duration_ms, title } envelope, and touches no
    // UI globals (never call invokeUnary here — it is bound to
    // selectedService / responseData / render).
    async function invokeForCompare(svc, methodName, bodyJson, metadata) {
        try {
            var resp = await fetch(config.prefix + '/api/invoke' + serverUrlParamForService(svc, false), {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({
                    service: svc.name,
                    method: methodName,
                    messages: [bodyJson || '{}'],
                    metadata: metadata || null,
                    protocol: svc.source || undefined
                })
            });
            var r = await resp.json();
            if (!resp.ok || (r && r.title)) {
                return { ok: false, status: (r && r.status) || ('HTTP ' + resp.status), error: (r && r.title) || ('HTTP ' + resp.status), response: null, durationMs: (r && r.duration_ms) || 0 };
            }
            return { ok: true, status: r.status, durationMs: r.duration_ms, response: r.response, error: null };
        } catch (e) {
            return { ok: false, status: 'error', error: (e && e.message) || 'network error', response: null, durationMs: 0 };
        }
    }

    // ---- markdown report (for the v2.5 PR bot) ----

    // Build a markdown comparison report from a resolved compare state.
    // Structure mirrors SecurityReport.ToMarkdown: title, summary line,
    // a schema section (added/removed/changed), then per-method response
    // field diffs. Pure over its argument — reads state, never the DOM.
    function buildCompareMarkdown(state) {
        var lines = [];
        var aLabel = compareSideLabel(state, 'a');
        var bLabel = compareSideLabel(state, 'b');
        lines.push('# Service comparison — ' + aLabel + ' → ' + bLabel);
        lines.push('');
        var d = state.schemaDiff;
        lines.push('**Schema:** ' + (d ? compareSchemaSummary(d) : 'not computed') + '.');
        lines.push('');

        if (d) {
            if (d.removed.length) {
                lines.push('## Removed methods');
                d.removed.forEach(function (m) { lines.push('- `' + (m.fullName || m.name) + '`'); });
                lines.push('');
            }
            if (d.added.length) {
                lines.push('## Added methods');
                d.added.forEach(function (m) { lines.push('- `' + (m.fullName || m.name) + '`'); });
                lines.push('');
            }
            if (d.changed.length) {
                lines.push('## Signature changes');
                d.changed.forEach(function (c) {
                    lines.push('- `' + (c.a.fullName || c.a.name) + '` — ' + c.detail);
                });
                lines.push('');
            }
        }

        var responses = state.responses || {};
        var respKeys = Object.keys(responses);
        if (respKeys.length) {
            lines.push('## Response diffs');
            respKeys.forEach(function (k) {
                var r = responses[k];
                var name = r.label || k;
                if (r.error) {
                    lines.push('### `' + name + '` — invoke error');
                    lines.push('- ' + r.error);
                    lines.push('');
                    return;
                }
                lines.push('### `' + name + '`');
                if (r.fieldDiff && r.fieldDiff.kind === 'json') {
                    if (r.fieldDiff.entries.length === 0) {
                        lines.push('Responses are field-identical.');
                    } else {
                        jsonDiffToMarkdown(r.fieldDiff.entries).forEach(function (l) { lines.push(l); });
                    }
                } else {
                    lines.push('Non-JSON responses — ' + (r.textIdentical ? 'identical' : 'differ') + ' (see the workbench for the line diff).');
                }
                lines.push('');
            });
        }
        return lines.join('\n');
    }

    // ---- state + wiring ----

    // Session generation token. Every open / close / service re-selection
    // bumps it; async invocations capture it and bail after their await
    // when it no longer matches, so an in-flight batch can't keep hitting
    // both servers after Close, and a late response can't leak into a
    // re-opened or re-selected session's state.
    var _compareGen = 0;

    function _freshCompareState() {
        return {
            sides: {
                a: { url: null, serviceName: null, service: null, loading: false, error: null },
                b: { url: null, serviceName: null, service: null, loading: false, error: null }
            },
            urlCache: Object.create(null),   // urlKey → { services } | { error }
            loadingUrls: Object.create(null), // urlKey → true while a discovery is in flight
            schemaDiff: null,
            responses: Object.create(null),
            showUnchanged: false,
            busy: false
        };
    }

    // urlKey normalises the embedded host (null/'') to a stable cache key.
    function _compareUrlKey(url) { return (typeof url === 'string' && url) ? url : '(embedded)'; }

    function compareUrlLabel(url) {
        if (!url) return 'Embedded host';
        if (typeof serverUrlAliases !== 'undefined' && serverUrlAliases[url]) return serverUrlAliases[url];
        return (typeof truncateMiddle === 'function') ? truncateMiddle(url, 42) : url;
    }

    function compareSideLabel(state, side) {
        var s = state.sides[side];
        var svc = s.serviceName || (s.service && s.service.name) || '(none)';
        return svc + ' @ ' + compareUrlLabel(s.url);
    }

    // Open the surface. Optional seed pre-fills side A from a service the
    // caller already has (e.g. a "Compare this…" shortcut on a header).
    function openServiceCompare(seed) {
        _compareGen++;
        serviceCompareState = _freshCompareState();
        railMode = 'discover';
        try { localStorage.setItem('bowire_rail_mode', 'discover'); } catch (_) { /* ignore */ }
        sidebarView = 'services';
        if (seed && seed.name) {
            serviceCompareState.sides.a.url = seed.originUrl || null;
            serviceCompareState.sides.a.serviceName = seed.name;
            serviceCompareState.sides.a.service = seed;
            _compareLoadUrl('a', seed.originUrl || null);
        }
        serviceCompareOpen = true;
        render();
    }

    function closeServiceCompare() {
        _compareGen++;   // invalidate any in-flight discovery / invocation
        serviceCompareOpen = false;
        render();
    }

    // Discover a URL's services for one side's chooser (cached per url).
    // Dedupes concurrent in-flight loads of the same url so picking the
    // same source on both sides before the first resolve fires one fetch.
    async function _compareLoadUrl(side, url) {
        var key = _compareUrlKey(url);
        serviceCompareState.sides[side].url = url;
        serviceCompareState.sides[side].error = null;
        serviceCompareState.sides[side].loading = !serviceCompareState.urlCache[key];
        if (serviceCompareState.urlCache[key] || serviceCompareState.loadingUrls[key]) {
            // Already cached, or another side is already fetching this url —
            // just re-render; that side's finally block will render again.
            render();
            return;
        }
        serviceCompareState.loadingUrls[key] = true;
        var gen = _compareGen;
        render();
        var result;
        try {
            result = { services: await discoverForCompare(url) };
        } catch (e) {
            result = { error: (e && e.message) || 'discovery failed' };
        }
        if (gen !== _compareGen || !serviceCompareState) return;   // session changed mid-flight
        serviceCompareState.urlCache[key] = result;
        delete serviceCompareState.loadingUrls[key];
        serviceCompareState.sides.a.loading = false;
        serviceCompareState.sides.b.loading = false;
        render();
    }

    function _compareServicesForSide(side) {
        var s = serviceCompareState.sides[side];
        if (s.url === null && !s.service) return [];
        var entry = serviceCompareState.urlCache[_compareUrlKey(s.url)];
        return (entry && entry.services) ? entry.services : [];
    }

    // Pick a service for a side; recompute the schema diff once both sides
    // resolve. Clears any stale response diffs (they belonged to the old
    // pairing).
    function _compareSelectService(side, name) {
        _compareGen++;   // a new pairing invalidates in-flight invocations
        var list = _compareServicesForSide(side);
        var svc = null;
        for (var i = 0; i < list.length; i++) { if (list[i].name === name) { svc = list[i]; break; } }
        serviceCompareState.sides[side].serviceName = name || null;
        serviceCompareState.sides[side].service = svc;
        serviceCompareState.responses = Object.create(null);
        serviceCompareState.busy = false;
        _recomputeCompareSchema();
        render();
    }

    function _recomputeCompareSchema() {
        var a = serviceCompareState.sides.a.service;
        var b = serviceCompareState.sides.b.service;
        serviceCompareState.schemaDiff = (a && b) ? computeServiceSchemaDiff(a, b) : null;
    }

    // Only unary methods can be invoke-both'd for a response diff — a
    // streaming / duplex pair has no single response body to compare.
    function _compareIsUnaryPair(pair) {
        return pair && pair.a && pair.b
            && pair.a.methodType === 'Unary' && pair.b.methodType === 'Unary';
    }

    // Invoke one aligned pair on both sides and field-diff the responses.
    // Guarded by the session generation so a Close / re-selection mid-await
    // discards the result instead of writing it into a replaced session.
    async function runCompareMethod(pair) {
        if (!_compareIsUnaryPair(pair)) return;
        var a = serviceCompareState.sides.a.service;
        var b = serviceCompareState.sides.b.service;
        if (!a || !b) return;
        var gen = _compareGen;
        var label = (pair.a.fullName || pair.a.name);
        serviceCompareState.responses[pair.key] = { label: label, pending: true };
        render();
        var pairRes = await Promise.all([
            invokeForCompare(a, pair.a.name, '{}', null),
            invokeForCompare(b, pair.b.name, '{}', null)
        ]);
        if (gen !== _compareGen || !serviceCompareState) return;   // session changed mid-flight
        var ra = pairRes[0], rb = pairRes[1];
        var entry = { label: label, a: ra, b: rb };
        if (!ra.ok || !rb.ok) {
            entry.error = [!ra.ok ? ('A: ' + ra.error) : null, !rb.ok ? ('B: ' + rb.error) : null]
                .filter(Boolean).join('; ');
        } else {
            var fd = diffJsonStructured(ra.response, rb.response);
            entry.fieldDiff = fd;
            if (fd.kind === 'text') {
                entry.textIdentical = prettyJson(ra.response) === prettyJson(rb.response);
                entry.lineDiff = computeLineDiff(prettyJson(ra.response), prettyJson(rb.response));
            }
        }
        serviceCompareState.responses[pair.key] = entry;
        render();
    }

    async function runAllCompareMethods() {
        var d = serviceCompareState.schemaDiff;
        if (!d) return;
        var gen = _compareGen;
        serviceCompareState.busy = true;
        render();
        // Only the unary pairs — the per-row button refuses non-unary too.
        // Sequential so a large service doesn't fire dozens of concurrent
        // fan-outs at two servers at once. Stops early if the session
        // changed (Close / re-selection) so it can't keep hammering.
        var pairs = d.changed.concat(d.unchanged).filter(_compareIsUnaryPair);
        for (var i = 0; i < pairs.length; i++) {
            if (gen !== _compareGen) return;
            await runCompareMethod(pairs[i]);
        }
        if (gen !== _compareGen || !serviceCompareState) return;
        serviceCompareState.busy = false;
        render();
    }

    function _exportCompareReport() {
        if (!serviceCompareState.schemaDiff) return;
        var md = buildCompareMarkdown(serviceCompareState);
        var a = (serviceCompareState.sides.a.serviceName || 'a').replace(/[^A-Za-z0-9._-]+/g, '-');
        var b = (serviceCompareState.sides.b.serviceName || 'b').replace(/[^A-Za-z0-9._-]+/g, '-');
        downloadTextFile(md, 'compare-' + a + '-vs-' + b + '.md', 'text/markdown');
        if (typeof toast === 'function') toast('Comparison report exported', 'success');
    }

    // ---- surface ----

    function renderServiceCompareMain() {
        var main = el('div', { id: 'bowire-main-compare', className: 'bowire-main bowire-main-compare' });
        var st = serviceCompareState;

        // Header — title, schema summary, export + close.
        var header = el('div', { className: 'bowire-compare-header' });
        header.appendChild(el('div', { className: 'bowire-compare-title' },
            el('span', { textContent: 'Compare services' }),
            st.schemaDiff
                ? el('span', { className: 'bowire-compare-summary', textContent: compareSchemaSummary(st.schemaDiff) })
                : null
        ));
        var headerActions = el('div', { className: 'bowire-compare-header-actions' });
        if (st.schemaDiff) {
            headerActions.appendChild(el('button', {
                className: 'bowire-compare-btn',
                textContent: 'Export markdown',
                title: 'Download a markdown comparison report (for a PR comment)',
                onClick: _exportCompareReport
            }));
        }
        headerActions.appendChild(el('button', {
            className: 'bowire-compare-btn bowire-compare-btn-close',
            textContent: 'Close',
            'aria-label': 'Close comparison',
            onClick: closeServiceCompare
        }));
        header.appendChild(headerActions);
        main.appendChild(header);

        // Chooser — two columns.
        var chooser = el('div', { className: 'bowire-compare-chooser' });
        chooser.appendChild(_renderCompareSide('a', 'Baseline (A)'));
        chooser.appendChild(el('span', { className: 'bowire-compare-vs', textContent: '→' }));
        chooser.appendChild(_renderCompareSide('b', 'Target (B)'));
        main.appendChild(chooser);

        if (!st.schemaDiff) {
            main.appendChild(el('div', { className: 'bowire-compare-hint',
                textContent: 'Pick a service on each side. Same API at two URLs, or a v1 vs v2 twin — methods align by name (version suffixes like _v2 are matched).' }));
            return main;
        }

        main.appendChild(_renderCompareSchema(st.schemaDiff));
        return main;
    }

    function _renderCompareSide(side, label) {
        var s = serviceCompareState.sides[side];
        var col = el('div', { className: 'bowire-compare-side' });
        col.appendChild(el('div', { className: 'bowire-compare-side-label', textContent: label }));

        // URL select.
        var urls = (typeof serverUrls !== 'undefined' && Array.isArray(serverUrls)) ? serverUrls : [];
        var urlSel = el('select', { className: 'bowire-compare-select', 'aria-label': label + ' URL',
            onChange: function (e) {
                var v = e.target.value;
                // Clear the stale service pick FIRST — _compareLoadUrl
                // renders synchronously on a cache hit, so clearing after
                // it would leave last selection's service + schema diff on
                // screen against the new url.
                serviceCompareState.sides[side].serviceName = null;
                serviceCompareState.sides[side].service = null;
                serviceCompareState.schemaDiff = null;
                serviceCompareState.responses = Object.create(null);
                _compareLoadUrl(side, v === '(embedded)' ? '' : v);
            }
        });
        urlSel.appendChild(el('option', { value: '__none', textContent: 'Pick a source…', selected: s.url === null }));
        urlSel.appendChild(el('option', { value: '(embedded)', textContent: 'Embedded host', selected: s.url === '' }));
        urls.forEach(function (u) {
            urlSel.appendChild(el('option', { value: u, textContent: compareUrlLabel(u), selected: s.url === u }));
        });
        col.appendChild(urlSel);

        // Service select (or loading / error).
        if (s.loading) {
            col.appendChild(el('div', { className: 'bowire-compare-side-status', textContent: 'Discovering…' }));
        } else if (s.url !== null) {
            var entry = serviceCompareState.urlCache[_compareUrlKey(s.url)];
            if (entry && entry.error) {
                col.appendChild(el('div', { className: 'bowire-compare-side-status bowire-compare-side-error', textContent: entry.error }));
            } else {
                var list = _compareServicesForSide(side);
                var svcSel = el('select', { className: 'bowire-compare-select', 'aria-label': label + ' service',
                    onChange: function (e) { _compareSelectService(side, e.target.value === '__none' ? null : e.target.value); }
                });
                svcSel.appendChild(el('option', { value: '__none', textContent: list.length ? 'Pick a service…' : 'No services found', selected: !s.serviceName }));
                list.forEach(function (svc) {
                    var n = svc.methods ? svc.methods.length : 0;
                    svcSel.appendChild(el('option', { value: svc.name, selected: s.serviceName === svc.name,
                        textContent: svc.name + ' (' + n + ')' }));
                });
                col.appendChild(svcSel);
            }
        }
        return col;
    }

    function _renderCompareSchema(d) {
        var wrap = el('div', { className: 'bowire-compare-results' });

        var bar = el('div', { className: 'bowire-compare-actions' });
        bar.appendChild(el('button', {
            className: 'bowire-compare-btn',
            textContent: serviceCompareState.busy ? 'Invoking…' : 'Invoke all & diff responses',
            disabled: serviceCompareState.busy ? 'disabled' : null,
            title: 'Call every aligned method on both sides and diff the responses field-by-field',
            onClick: serviceCompareState.busy ? null : runAllCompareMethods
        }));
        var toggleUnchanged = el('label', { className: 'bowire-compare-toggle' },
            el('input', { type: 'checkbox', checked: serviceCompareState.showUnchanged ? 'checked' : null,
                onChange: function (e) { serviceCompareState.showUnchanged = !!e.target.checked; render(); } }),
            el('span', { textContent: 'show unchanged (' + d.unchanged.length + ')' })
        );
        bar.appendChild(toggleUnchanged);
        wrap.appendChild(bar);

        var list = el('div', { className: 'bowire-compare-methods' });

        d.removed.forEach(function (m) { list.appendChild(_renderCompareMethodRow('removed', m, null, '')); });
        d.added.forEach(function (m) { list.appendChild(_renderCompareMethodRow('added', null, m, '')); });
        d.changed.forEach(function (c) { list.appendChild(_renderCompareMethodRow('changed', c.a, c.b, c.detail, c)); });
        if (serviceCompareState.showUnchanged) {
            d.unchanged.forEach(function (c) { list.appendChild(_renderCompareMethodRow('unchanged', c.a, c.b, c.noteOnly ? 'description updated' : '', c)); });
        }
        if (!list.children.length) {
            list.appendChild(el('div', { className: 'bowire-compare-hint', textContent: 'Schemas are identical.' }));
        }
        wrap.appendChild(list);
        return wrap;
    }

    var _COMPARE_GLYPH = { added: '+', removed: '−', changed: '~', unchanged: '=' };

    // Sanitise an align key into an id-safe token so each method row gets a
    // stable, distinct id — morphdom then matches rows by id and swaps a
    // row's DOM (and its handler closures) whenever the selection changes,
    // instead of position-reusing an old node with a stale `pair` closure.
    function _compareRowId(kind, pair, a, b) {
        var base = (pair && pair.key) || (a && (a.fullName || a.name)) || (b && (b.fullName || b.name)) || '';
        return 'bowire-compare-m-' + kind + '-' + String(base).replace(/[^A-Za-z0-9]+/g, '_');
    }

    function _renderCompareMethodRow(kind, a, b, detail, pair) {
        var row = el('div', {
            id: _compareRowId(kind, pair, a, b),
            className: 'bowire-compare-method bowire-compare-method-' + kind
        });
        var head = el('div', { className: 'bowire-compare-method-head' });
        head.appendChild(el('span', {
            className: 'bowire-compare-glyph bowire-compare-glyph-' + kind,
            textContent: _COMPARE_GLYPH[kind] || '~'
        }));
        head.appendChild(el('span', {
            className: 'bowire-compare-method-name',
            textContent: (a && (a.fullName || a.name)) || (b && (b.fullName || b.name)) || ''
        }));
        if (detail) head.appendChild(el('span', { className: 'bowire-compare-method-detail', textContent: '— ' + detail }));

        // Invoke button only for methods present on both sides (a pair),
        // unary-only for v1.
        if (pair && a && b && a.methodType === 'Unary' && b.methodType === 'Unary') {
            var resp = serviceCompareState.responses[pair.key];
            head.appendChild(el('button', {
                className: 'bowire-compare-invoke',
                textContent: (resp && resp.pending) ? '…' : 'Diff response',
                disabled: (resp && resp.pending) ? 'disabled' : null,
                title: 'Invoke on both sides and diff the responses',
                onClick: function () { runCompareMethod(pair); }
            }));
        }
        row.appendChild(head);

        var resp2 = pair ? serviceCompareState.responses[pair.key] : null;
        if (resp2 && !resp2.pending) row.appendChild(_renderCompareResponseDiff(resp2));
        return row;
    }

    function _renderCompareResponseDiff(r) {
        var box = el('div', { className: 'bowire-compare-respdiff' });
        if (r.error) {
            box.appendChild(el('div', { className: 'bowire-compare-side-error', textContent: r.error }));
            return box;
        }
        if (r.fieldDiff && r.fieldDiff.kind === 'json') {
            if (r.fieldDiff.entries.length === 0) {
                box.appendChild(el('div', { className: 'bowire-compare-identical', textContent: 'Responses are field-identical.' }));
                return box;
            }
            r.fieldDiff.entries.forEach(function (e) {
                var cls = (e.change === 'added') ? 'add' : (e.change === 'removed') ? 'del' : 'changed';
                var line = el('div', { className: 'bowire-diff-line bowire-compare-fieldline bowire-compare-field-' + cls });
                line.appendChild(el('span', { className: 'bowire-compare-field-path', textContent: e.path }));
                var desc;
                if (e.change === 'kind-changed') desc = 'type ' + e.aKind + ' → ' + e.bKind;
                else if (e.change === 'added') desc = 'added (' + e.bKind + ' ' + e.bText + ')';
                else if (e.change === 'removed') desc = 'removed (' + e.aKind + ' ' + e.aText + ')';
                else if (e.change === 'array-length') desc = 'array length ' + e.aText + ' → ' + e.bText;
                else desc = e.aText + ' → ' + e.bText;
                line.appendChild(el('span', { className: 'bowire-compare-field-desc', textContent: desc }));
                box.appendChild(line);
            });
            return box;
        }
        // Non-JSON fallback — line diff.
        if (r.textIdentical) {
            box.appendChild(el('div', { className: 'bowire-compare-identical', textContent: 'Responses are identical.' }));
            return box;
        }
        (r.lineDiff || []).forEach(function (ln) {
            if (ln.type === 'eq') return;
            box.appendChild(el('div', { className: 'bowire-diff-line bowire-diff-' + ln.type,
                textContent: (ln.type === 'add' ? '+ ' : '- ') + ln.text }));
        });
        return box;
    }
