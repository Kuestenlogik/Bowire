// @generated
// request-builder.js — Hoppscotch-style single-line request bar (#289).
//
// Drops a NEW workbench mode that re-skins the freeform request
// builder into the canonical Hoppscotch layout:
//
//   [METHOD ▾] [URL ____________________________________] [Senden ▾]
//   ──────────────────────────────────────────────────────────────
//   Parameter (n) | Body | Header (m) | Auth | Pre-script | Post-script | Variables
//   ──────────────────────────────────────────────────────────────
//   <tab body — key/value table or editor>
//   ──────────────────────────────────────────────────────────────
//   <response pane>
//
// State shape extension. A freeformRequest gains a request-builder-mode marker
// (`_requestBuilder: true`) and a few extra fields stored under `_requestBuilder` so the
// classic freeform path keeps working untouched:
//
//   freeformRequest._requestBuilder = {
//       activeTab: 'parameter' | 'body' | 'header' | 'auth' | 'pre' | 'post' | 'vars',
//       params:   [{ key, value, description, enabled }, ...],
//       headers:  [{ key, value, description, enabled }, ...],
//       bodyMode: 'json' | 'form' | 'raw' | 'binary',
//       preScript:  '',
//       postScript: '',
//       authKind:   'none' | 'bearer' | 'basic' | 'apikey',
//       authData:   {}     // shape depends on authKind
//   }
//
// fr.method holds the HTTP verb (GET/POST/PUT/PATCH/DELETE/HEAD/OPTIONS).
// fr.serverUrl holds the FULL URL the user typed.
// fr.body holds the raw body when bodyMode === 'json' | 'raw'.
//
// This fragment is concatenated INSIDE the prologue's IIFE — every
// declaration below lives in module-shared scope. Init order: must be
// AFTER scripts.js (so the script runtime is available) and BEFORE
// render-main.js (so render() sees these helpers).

    // ---- Constants ----
    var HOPP_METHODS = ['GET', 'POST', 'PUT', 'PATCH', 'DELETE', 'HEAD', 'OPTIONS'];
    var HOPP_TABS = [
        { id: 'parameter', label: 'Parameter' },
        { id: 'body',      label: 'Body'      },
        { id: 'header',    label: 'Header'    },
        { id: 'auth',      label: 'Auth'      },
        { id: 'pre',       label: 'Pre-script' },
        { id: 'post',      label: 'Post-script' },
        { id: 'vars',      label: 'Variables' }
    ];
    var HOPP_BODY_MODES = [
        { id: 'json',   label: 'JSON' },
        { id: 'form',   label: 'Form'   },
        { id: 'raw',    label: 'Raw' },
        { id: 'binary', label: 'Binary' }
    ];
    var HOPP_AUTH_KINDS = [
        { id: 'none',   label: 'None'   },
        { id: 'bearer', label: 'Bearer Token' },
        { id: 'basic',  label: 'Basic Auth' },
        { id: 'apikey', label: 'API Key' }
    ];

    // Open/close state for the method dropdown + the Senden split caret.
    var rbMethodMenuOpen = false;
    var rbSendMenuOpen = false;
    // #290 — history dropdown (clock icon, between URL input and the
    // send button cluster). Closed by default; toggled by the clock
    // button click. Closed by the outside-click handler at the bottom
    // of this file.
    var rbHistoryMenuOpen = false;

    // ---- #290 — Bar-history persistence ----
    //
    // History entries persist across reloads so an operator who
    // accidentally closed the tab (or refreshed) doesn't lose the
    // request shape they just iterated on. The buffer is capped so
    // the localStorage bucket can't grow unboundedly across long
    // sessions; oldest entries get evicted when the cap is hit.
    //
    // Round-trips through workspace export under `requestBuilderHistory` —
    // disk-mode workspaces serialise `[]`; browser-mode workspaces
    // serialise the live buffer.
    var RB_HISTORY_KEY = 'bowire_request_builder_history';
    var RB_HISTORY_CAP = 50;

    // Module-scope buffer mirrors the per-workspace localStorage entry.
    // Lazily hydrated via loadHoppHistory() on first read; mutations
    // round-trip through persistHoppHistory().
    var rbHistoryList = null;

    function loadHoppHistory() {
        if (Array.isArray(rbHistoryList)) return rbHistoryList;
        try {
            var raw = localStorage.getItem(wsKey(RB_HISTORY_KEY));
            rbHistoryList = raw ? JSON.parse(raw) : [];
            if (!Array.isArray(rbHistoryList)) rbHistoryList = [];
        } catch (_) {
            rbHistoryList = [];
        }
        return rbHistoryList;
    }

    function persistHoppHistory() {
        try {
            localStorage.setItem(wsKey(RB_HISTORY_KEY),
                JSON.stringify(rbHistoryList || []));
        } catch (e) {
            // Quota-exhaustion is the realistic failure here — non-fatal,
            // history is a convenience, not project content.
            console.warn('[bowire] failed to persist request-builder history', e);
        }
    }

    function _makeHoppHistoryId() {
        return 'hh_' + Math.random().toString(36).slice(2, 10);
    }

    // Snapshot the bar's current state into a history entry. Called
    // from executeHoppRequest() after the wire result lands. The
    // response body is intentionally NOT captured — it can be huge
    // and balloon localStorage; only the request shape + status code
    // + duration are kept so the operator can see "did it work last
    // time?" without paying for re-storing the JSON payload.
    function pushHoppHistoryEntry(fr, outcome) {
        if (!fr) return;
        ensureHoppState(fr);
        loadHoppHistory();
        var entry = {
            id: _makeHoppHistoryId(),
            ts: Date.now(),
            method: fr.method || 'GET',
            url: fr.serverUrl || '',
            params: (fr._requestBuilder.params || []).map(_cloneRow),
            headers: (fr._requestBuilder.headers || []).map(_cloneRow),
            body: fr.body || '',
            bodyMode: fr._requestBuilder.bodyMode || 'json',
            // formBody is its own KV table when bodyMode === 'form'.
            // Capture it too so a history-restore re-hydrates the
            // form rows without the operator having to retype them.
            formBody: Array.isArray(fr._requestBuilder.formBody)
                ? fr._requestBuilder.formBody.map(_cloneRow) : [],
            // Binary content is a transient File reference and CAN'T
            // be persisted — record the filename so the entry shows
            // it, but the operator has to re-pick on restore.
            binaryName: fr._requestBuilder.binaryName || '',
            authKind: fr._requestBuilder.authKind || 'none',
            authData: Object.assign({}, fr._requestBuilder.authData || {}),
            preScript: fr._requestBuilder.preScript || '',
            postScript: fr._requestBuilder.postScript || '',
            status: outcome && outcome.status != null ? outcome.status : null,
            durationMs: outcome && outcome.durationMs != null ? outcome.durationMs : null,
            ok: outcome ? !!outcome.ok : null
        };
        // Newest at index 0 — same as history-env.js's unshift pattern.
        rbHistoryList.unshift(entry);
        if (rbHistoryList.length > RB_HISTORY_CAP) {
            rbHistoryList.length = RB_HISTORY_CAP;
        }
        persistHoppHistory();
    }

    function _cloneRow(r) {
        return {
            key: r && r.key ? String(r.key) : '',
            value: r && r.value != null ? String(r.value) : '',
            description: r && r.description != null ? String(r.description) : '',
            enabled: !r || r.enabled !== false
        };
    }

    // Restore a history entry into the active request-builder request — fills the
    // bar but DOES NOT auto-execute. The operator clicks Execute when
    // they're ready; this matches Hoppscotch's own "load from history"
    // behaviour and avoids unintended re-fire of (potentially expensive
    // or destructive) requests.
    function restoreHoppHistoryEntry(entryId) {
        loadHoppHistory();
        var entry = rbHistoryList.find(function (e) { return e.id === entryId; });
        if (!entry) return;
        if (!freeformRequest || !isHoppRequest(freeformRequest)) {
            // No live request-builder request — start a fresh one then fill it.
            startHoppRequest({});
        }
        var fr = freeformRequest;
        ensureHoppState(fr);
        fr.method = entry.method || 'GET';
        fr.serverUrl = entry.url || '';
        fr.body = entry.body || '';
        fr._requestBuilder.params  = (entry.params  || []).map(_cloneRow);
        fr._requestBuilder.headers = (entry.headers || []).map(_cloneRow);
        fr._requestBuilder.formBody = (entry.formBody || []).map(_cloneRow);
        fr._requestBuilder.bodyMode = entry.bodyMode || 'json';
        fr._requestBuilder.binaryName = entry.binaryName || '';
        // Binary File reference doesn't survive — clear so the renderer
        // shows the empty file picker, but keep the filename hint above
        // so the operator knows what to re-pick.
        fr._requestBuilder._binaryRef = null;
        fr._requestBuilder.authKind = entry.authKind || 'none';
        fr._requestBuilder.authData = Object.assign({}, entry.authData || {});
        fr._requestBuilder.preScript  = entry.preScript  || '';
        fr._requestBuilder.postScript = entry.postScript || '';
        rbHistoryMenuOpen = false;
        render();
    }

    function clearHoppHistory() {
        rbHistoryList = [];
        persistHoppHistory();
        rbHistoryMenuOpen = false;
        render();
    }

    // ---- Hopp-mode bootstrapping ----
    //
    // Used both by the empty-state "Just fire a request →" CTA and by
    // the Ctrl+L global keyboard shortcut. Replaces the classic
    // freeformRequest with one carrying the _requestBuilder marker so the
    // renderer picks up the new layout.
    function startHoppRequest(opts) {
        opts = opts || {};
        startFreeformRequest({
            protocol: 'rest',
            urlMode: 'inline',
            serverUrl: opts.url || ''
        });
        if (!freeformRequest) return;
        // Default to GET — operators land here for the "fire one"
        // case which is almost always a GET probe.
        freeformRequest.method = opts.method || 'GET';
        freeformRequest._requestBuilder = _newHoppState();
        // If the workspace has no envs, seed a single 'scratch' set so
        // {{var}} substitution lands in a defined place rather than
        // silently no-oping. Quiet — only fire when there are zero
        // envs; the user's existing envs always win.
        try {
            if (typeof getEnvironments === 'function') {
                var envs = getEnvironments();
                if (!Array.isArray(envs) || envs.length === 0) {
                    if (typeof createEnvironment === 'function') {
                        var seeded = createEnvironment('scratch');
                        if (seeded && typeof setActiveEnvId === 'function') {
                            setActiveEnvId(seeded.id);
                        }
                    }
                }
            }
        } catch (_) { /* env helpers may not be loaded yet — silent fallback */ }
        render();
    }

    function _newHoppState() {
        return {
            activeTab: 'parameter',
            params: [],
            headers: [],
            bodyMode: 'json',
            preScript: '',
            postScript: '',
            authKind: 'none',
            authData: {}
        };
    }

    // Ensure the existing freeform request — whether opened classically
    // or freshly converted — has a populated _requestBuilder blob. Idempotent.
    function ensureHoppState(fr) {
        if (!fr) return;
        if (!fr._requestBuilder || typeof fr._requestBuilder !== 'object') {
            fr._requestBuilder = _newHoppState();
        }
        if (!Array.isArray(fr._requestBuilder.params))  fr._requestBuilder.params  = [];
        if (!Array.isArray(fr._requestBuilder.headers)) fr._requestBuilder.headers = [];
        if (typeof fr._requestBuilder.bodyMode !== 'string') fr._requestBuilder.bodyMode = 'json';
        if (typeof fr._requestBuilder.preScript !== 'string')  fr._requestBuilder.preScript  = '';
        if (typeof fr._requestBuilder.postScript !== 'string') fr._requestBuilder.postScript = '';
        if (typeof fr._requestBuilder.authKind !== 'string')   fr._requestBuilder.authKind   = 'none';
        if (!fr._requestBuilder.authData) fr._requestBuilder.authData = {};
    }

    function isHoppRequest(fr) {
        return !!(fr && fr._requestBuilder);
    }

    // ---- KV-table helpers ----
    //
    // The Parameter + Header + (form) Body tabs all use the same shape:
    //   { key, value, description, enabled }
    // with an empty "add" row at the bottom. _activeKv() counts only
    // enabled rows with non-empty keys — drives the tab badge count.
    function _activeKvCount(rows) {
        if (!Array.isArray(rows)) return 0;
        var n = 0;
        for (var i = 0; i < rows.length; i++) {
            var r = rows[i];
            if (r && r.enabled !== false && r.key && r.key.trim()) n++;
        }
        return n;
    }
    function _kvToObject(rows) {
        var out = {};
        if (!Array.isArray(rows)) return out;
        for (var i = 0; i < rows.length; i++) {
            var r = rows[i];
            if (r && r.enabled !== false && r.key && r.key.trim()) {
                out[r.key.trim()] = String(r.value == null ? '' : r.value);
            }
        }
        return out;
    }

    // ---- URL composition ----
    //
    // For the Parameter tab to feed into the actual wire request we
    // merge it onto the base URL. Operators may also type ?foo=bar
    // directly in the URL field; both must coexist. Strategy: split
    // the base URL on '?', parse the existing query string into a Map,
    // overlay the Parameter rows (last-write-wins), then re-stringify.
    function _composeUrlWithParams(baseUrl, paramRows) {
        var url = String(baseUrl || '');
        var qIdx = url.indexOf('?');
        var hashIdx = url.indexOf('#');
        var head = qIdx >= 0 ? url.substring(0, qIdx) : (hashIdx >= 0 ? url.substring(0, hashIdx) : url);
        var existingQ = '';
        var hash = '';
        if (hashIdx >= 0) hash = url.substring(hashIdx);
        if (qIdx >= 0) {
            var qEnd = hashIdx >= 0 ? hashIdx : url.length;
            existingQ = url.substring(qIdx + 1, qEnd);
        }
        var pairs = [];
        if (existingQ) {
            existingQ.split('&').forEach(function (p) {
                if (!p) return;
                var eq = p.indexOf('=');
                if (eq < 0) pairs.push({ key: p, value: '' });
                else pairs.push({ key: p.substring(0, eq), value: p.substring(eq + 1) });
            });
        }
        if (Array.isArray(paramRows)) {
            paramRows.forEach(function (r) {
                if (!r || r.enabled === false) return;
                if (!r.key || !r.key.trim()) return;
                pairs.push({
                    key: encodeURIComponent(r.key.trim()),
                    value: encodeURIComponent(String(r.value == null ? '' : r.value))
                });
            });
        }
        if (pairs.length === 0) return head + hash;
        var q = pairs.map(function (p) {
            return p.value === '' ? p.key : p.key + '=' + p.value;
        }).join('&');
        return head + '?' + q + hash;
    }

    // ---- Variable highlighting in the URL ----
    //
    // Best-effort renderer that splits the URL string into a sequence
    // of plain segments and {{var}} chips. Each chip carries the
    // resolved value as the title so the operator sees the source on
    // hover (per the Acceptance bullet "Variables resolve inline …
    // with hover-tooltip showing source").
    function _renderHoppUrlOverlay(url) {
        var wrap = el('div', { className: 'bowire-request-builder-url-overlay' });
        if (!url) return wrap;
        var re = /\{\{([^}]+)\}\}/g;
        var lastIdx = 0;
        var m;
        while ((m = re.exec(url)) !== null) {
            if (m.index > lastIdx) {
                wrap.appendChild(el('span', {
                    className: 'bowire-request-builder-url-plain',
                    textContent: url.substring(lastIdx, m.index)
                }));
            }
            var raw = m[0];
            var name = m[1].trim();
            var resolved = '';
            try {
                if (typeof substituteVars === 'function') {
                    resolved = substituteVars(raw);
                }
            } catch (_) { /* keep raw — operator sees the typo */ }
            var unresolved = (resolved === raw);
            wrap.appendChild(el('span', {
                className: 'bowire-request-builder-url-var' + (unresolved ? ' is-unresolved' : ''),
                title: unresolved
                    ? '{{' + name + '}} — unresolved (no matching variable)'
                    : '{{' + name + '}} → ' + resolved,
                textContent: raw
            }));
            lastIdx = m.index + raw.length;
        }
        if (lastIdx < url.length) {
            wrap.appendChild(el('span', {
                className: 'bowire-request-builder-url-plain',
                textContent: url.substring(lastIdx)
            }));
        }
        return wrap;
    }

    // ---- Render: the request bar (method + URL + send) ----
    function _renderRequestBuilder(fr) {
        var bar = el('div', { className: 'bowire-request-builder-bar' });

        // Method dropdown
        var methodWrap = el('div', { className: 'bowire-request-builder-method-wrap' });
        var method = (fr.method || 'GET').toUpperCase();
        if (HOPP_METHODS.indexOf(method) < 0) method = 'GET';
        fr.method = method;
        var methodBtn = el('button', {
            type: 'button',
            id: 'bowire-request-builder-method-btn',
            className: 'bowire-request-builder-method-btn',
            'data-verb': method,
            'aria-haspopup': 'listbox',
            'aria-expanded': rbMethodMenuOpen ? 'true' : 'false',
            onClick: function (e) {
                e.stopPropagation();
                rbMethodMenuOpen = !rbMethodMenuOpen;
                render();
            }
        },
            el('span', { className: 'bowire-request-builder-method-label', textContent: method }),
            el('span', { className: 'bowire-request-builder-method-caret', innerHTML: svgIcon('chevronDown') })
        );
        methodWrap.appendChild(methodBtn);
        if (rbMethodMenuOpen) {
            var menu = el('div', {
                className: 'bowire-request-builder-method-menu',
                role: 'listbox',
                onClick: function (e) { e.stopPropagation(); }
            });
            HOPP_METHODS.forEach(function (m) {
                menu.appendChild(el('button', {
                    type: 'button',
                    className: 'bowire-request-builder-method-menu-item' + (m === method ? ' is-selected' : ''),
                    'data-verb': m,
                    role: 'option',
                    'aria-selected': m === method ? 'true' : 'false',
                    onClick: function () {
                        fr.method = m;
                        rbMethodMenuOpen = false;
                        render();
                    }
                },
                    el('span', { className: 'bowire-request-builder-method-chip', 'data-verb': m, textContent: m })
                ));
            });
            methodWrap.appendChild(menu);
        }
        bar.appendChild(methodWrap);

        // URL input + variable overlay
        var urlWrap = el('div', { className: 'bowire-request-builder-url-wrap' });
        var urlInput = el('input', {
            id: 'bowire-request-builder-url-input',
            type: 'text',
            className: 'bowire-request-builder-url-input',
            value: fr.serverUrl || '',
            placeholder: 'https://api.example.com/users  •  use {{baseUrl}} for env vars',
            spellcheck: 'false',
            autocomplete: 'off',
            onInput: function (e) {
                fr.serverUrl = e.target.value;
                // Live overlay refresh without full re-render — find
                // the overlay sibling and replace its children.
                var overlay = urlWrap.querySelector('.bowire-request-builder-url-overlay');
                if (overlay) {
                    overlay.replaceWith(_renderHoppUrlOverlay(e.target.value));
                }
            },
            onKeyDown: function (e) {
                // Ctrl+Enter executes — the de-facto Hoppscotch shortcut.
                if (e.key === 'Enter' && (e.ctrlKey || e.metaKey)) {
                    e.preventDefault();
                    executeHoppRequest();
                }
            }
        });
        urlWrap.appendChild(urlInput);
        urlWrap.appendChild(_renderHoppUrlOverlay(fr.serverUrl || ''));
        bar.appendChild(urlWrap);

        // #290 — History affordance. Clock button between the URL
        // input and the Execute cluster opens a dropdown of the last
        // N executed requests; clicking an entry fills the bar (does
        // NOT auto-execute — matches Hoppscotch's own behaviour). The
        // button only renders when the history bucket is non-empty so
        // first-time operators don't see a dead control.
        loadHoppHistory();
        if (rbHistoryList.length > 0) {
            bar.appendChild(_renderHoppHistoryButton(fr));
        }

        // Send button (split caret variant menu)
        var sendWrap = el('div', { className: 'bowire-request-builder-send-wrap' });
        var sendBtn = el('button', {
            type: 'button',
            id: 'bowire-request-builder-send-btn',
            className: 'bowire-request-builder-send-btn',
            title: 'Execute request (Ctrl+Enter)',
            onClick: function () { executeHoppRequest(); }
        },
            el('span', { innerHTML: svgIcon('play'), style: 'width:14px;height:14px;display:flex' }),
            el('span', { textContent: 'Execute' })
        );
        sendWrap.appendChild(sendBtn);
        var sendCaret = el('button', {
            type: 'button',
            id: 'bowire-request-builder-send-caret',
            className: 'bowire-request-builder-send-caret' + (rbSendMenuOpen ? ' is-open' : ''),
            title: 'More execute options',
            'aria-haspopup': 'menu',
            'aria-expanded': rbSendMenuOpen ? 'true' : 'false',
            onClick: function (e) {
                e.stopPropagation();
                rbSendMenuOpen = !rbSendMenuOpen;
                render();
            },
            innerHTML: svgIcon('chevronDown')
        });
        sendWrap.appendChild(sendCaret);
        if (rbSendMenuOpen) {
            var sendMenu = el('div', {
                className: 'bowire-request-builder-send-menu',
                role: 'menu',
                onClick: function (e) { e.stopPropagation(); }
            });
            sendMenu.appendChild(el('button', {
                className: 'bowire-request-builder-send-menu-item',
                role: 'menuitem',
                onClick: function () {
                    rbSendMenuOpen = false;
                    executeHoppRequest();
                }
            },
                el('span', { innerHTML: svgIcon('play'), style: 'width:14px;height:14px;display:flex' }),
                el('span', { textContent: 'Execute once' })
            ));
            sendMenu.appendChild(el('button', {
                className: 'bowire-request-builder-send-menu-item',
                role: 'menuitem',
                onClick: function () {
                    rbSendMenuOpen = false;
                    // Save to collection — reuse the freeform save path.
                    try {
                        if (typeof collectionsList !== 'undefined'
                            && typeof addToCollection === 'function'
                            && typeof createCollection === 'function') {
                            var col = (collectionsList && collectionsList.length > 0)
                                ? collectionsList[0]
                                : createCollection();
                            addToCollection(col.id, _snapshotHoppForCollection(fr));
                            if (typeof toast === 'function') {
                                toast('Saved to "' + col.name + '"', 'success');
                            }
                        }
                    } catch (e) {
                        if (typeof toast === 'function') toast('Save failed: ' + e.message, 'error');
                    }
                    render();
                }
            },
                el('span', { innerHTML: svgIcon('folder'), style: 'width:14px;height:14px;display:flex' }),
                el('span', { textContent: 'Save to collection' })
            ));
            // #290 — Benchmark variant. Wired to the existing
            // benchmarks-rail runner via createBenchmarkSpec + the
            // request-builder request snapshot as a single 'method'-style target.
            // Only renders when benchmarks.js is present (it ships
            // as a fragment that may be trimmed in embedded hosts).
            if (typeof createBenchmarkSpec === 'function'
                && typeof runBenchmarkSpec === 'function') {
                sendMenu.appendChild(el('button', {
                    className: 'bowire-request-builder-send-menu-item',
                    role: 'menuitem',
                    onClick: function () {
                        rbSendMenuOpen = false;
                        runHoppAsBenchmark(fr);
                    }
                },
                    el('span', { innerHTML: svgIcon('lightning'), style: 'width:14px;height:14px;display:flex' }),
                    el('span', { textContent: 'Execute as benchmark' })
                ));
            }
            sendWrap.appendChild(sendMenu);
        }
        bar.appendChild(sendWrap);

        return bar;
    }

    function _snapshotHoppForCollection(fr) {
        ensureHoppState(fr);
        return {
            service: '',
            method: fr.method || 'GET',
            methodType: 'Unary',
            protocol: 'rest',
            body: fr.body || '',
            messages: [fr.body || ''],
            metadata: _kvToObject(fr._requestBuilder.headers),
            params: _kvToObject(fr._requestBuilder.params),
            serverUrl: fr.serverUrl || '',
            urlMode: 'inline',
            kind: 'request-builder',
            preScript:  fr._requestBuilder.preScript || '',
            postScript: fr._requestBuilder.postScript || '',
            authKind:   fr._requestBuilder.authKind || 'none',
            authData:   fr._requestBuilder.authData || {},
            lineage: { kind: 'request-builder' }
        };
    }

    // ---- #290 — History dropdown ----
    //
    // Clock-icon button + dropdown showing the last N executed
    // requests for THIS workspace. Hydrated lazily from
    // localStorage on first paint and on each bar mount; the
    // dropdown re-renders on every state change because the menu's
    // open/close lives on a module-scope flag.
    function _renderHoppHistoryButton() {
        var wrap = el('div', { className: 'bowire-request-builder-history-wrap' });
        var btn = el('button', {
            type: 'button',
            id: 'bowire-request-builder-history-btn',
            className: 'bowire-request-builder-history-btn' + (rbHistoryMenuOpen ? ' is-open' : ''),
            title: 'Recent requests (' + rbHistoryList.length + ')',
            'aria-haspopup': 'menu',
            'aria-expanded': rbHistoryMenuOpen ? 'true' : 'false',
            onClick: function (e) {
                e.stopPropagation();
                rbHistoryMenuOpen = !rbHistoryMenuOpen;
                render();
            },
            innerHTML: svgIcon('history')
        });
        wrap.appendChild(btn);
        if (rbHistoryMenuOpen) {
            var menu = el('div', {
                className: 'bowire-request-builder-history-menu',
                role: 'menu',
                onClick: function (e) { e.stopPropagation(); }
            });
            // Header strip with a Clear-all action.
            menu.appendChild(el('div', { className: 'bowire-request-builder-history-head' },
                el('span', { className: 'bowire-request-builder-history-head-label',
                    textContent: 'Recent (' + rbHistoryList.length + ')' }),
                el('button', {
                    type: 'button',
                    className: 'bowire-request-builder-history-clear',
                    title: 'Clear history',
                    onClick: function () {
                        // Confirmation is the operator's only seatbelt
                        // since cleared history can't be restored.
                        if (typeof bowireConfirm === 'function') {
                            bowireConfirm({
                                title: 'Clear bar history?',
                                body: 'Removes all ' + rbHistoryList.length
                                    + ' entries for this workspace. Cannot be undone.',
                                confirmLabel: 'Clear',
                                onConfirm: clearHoppHistory
                            });
                        } else if (window.confirm('Clear ' + rbHistoryList.length + ' history entries?')) {
                            clearHoppHistory();
                        }
                    },
                    textContent: 'Clear'
                })
            ));
            // Up to RB_HISTORY_CAP entries — render newest first
            // (the buffer is already newest-at-index-0).
            rbHistoryList.forEach(function (entry) {
                var item = el('button', {
                    type: 'button',
                    className: 'bowire-request-builder-history-item',
                    role: 'menuitem',
                    title: entry.url + (entry.status != null
                        ? ' · ' + entry.status
                        + (entry.durationMs != null
                            ? ' · ' + Math.round(entry.durationMs) + 'ms'
                            : '')
                        : ''),
                    onClick: function () { restoreHoppHistoryEntry(entry.id); }
                });
                item.appendChild(el('span', {
                    className: 'bowire-request-builder-history-method',
                    'data-verb': entry.method || 'GET',
                    textContent: entry.method || 'GET'
                }));
                item.appendChild(el('span', {
                    className: 'bowire-request-builder-history-url',
                    textContent: entry.url || '(no url)'
                }));
                if (entry.status != null) {
                    item.appendChild(el('span', {
                        className: 'bowire-request-builder-history-status'
                            + (entry.ok ? ' is-ok' : ' is-err'),
                        textContent: String(entry.status)
                    }));
                }
                item.appendChild(el('span', {
                    className: 'bowire-request-builder-history-ts',
                    textContent: _formatRelativeTs(entry.ts)
                }));
                menu.appendChild(item);
            });
            wrap.appendChild(menu);
        }
        return wrap;
    }

    function _formatRelativeTs(ts) {
        if (!ts) return '';
        var diffSec = Math.max(0, Math.round((Date.now() - ts) / 1000));
        if (diffSec < 60) return diffSec + 's ago';
        if (diffSec < 3600) return Math.round(diffSec / 60) + 'm ago';
        if (diffSec < 86400) return Math.round(diffSec / 3600) + 'h ago';
        return Math.round(diffSec / 86400) + 'd ago';
    }

    // ---- Render: the sub-tab strip ----
    function _renderHoppSubTabs(fr) {
        ensureHoppState(fr);
        var strip = el('div', { className: 'bowire-request-builder-subtabs', role: 'tablist' });
        HOPP_TABS.forEach(function (t) {
            var badgeCount = 0;
            if (t.id === 'parameter') badgeCount = _activeKvCount(fr._requestBuilder.params);
            else if (t.id === 'header') badgeCount = _activeKvCount(fr._requestBuilder.headers);
            var isActive = fr._requestBuilder.activeTab === t.id;
            var tab = el('button', {
                type: 'button',
                className: 'bowire-request-builder-subtab' + (isActive ? ' is-active' : ''),
                'data-tab': t.id,
                role: 'tab',
                'aria-selected': isActive ? 'true' : 'false',
                onClick: function () {
                    fr._requestBuilder.activeTab = t.id;
                    render();
                }
            },
                el('span', { className: 'bowire-request-builder-subtab-label', textContent: t.label }),
                badgeCount > 0
                    ? el('span', { className: 'bowire-request-builder-subtab-badge', textContent: String(badgeCount) })
                    : null
            );
            strip.appendChild(tab);
        });
        return strip;
    }

    // ---- Render: a single KV table ----
    //
    // Renders one row per entry in `rows` plus one trailing empty
    // "add" row. mutateOnInput is called each time the user types so
    // the underlying array stays in sync. The empty row promotes to a
    // real row as soon as the user types into either column.
    function _renderHoppKvTable(rows, opts) {
        opts = opts || {};
        var keyPlaceholder = opts.keyPlaceholder || 'Key';
        var valPlaceholder = opts.valuePlaceholder || 'Value';
        var descPlaceholder = opts.descPlaceholder || 'Description';
        var table = el('div', { className: 'bowire-request-builder-kv-table' });

        // Column header strip (mirrors the screenshot in the ticket).
        var head = el('div', { className: 'bowire-request-builder-kv-head' },
            el('span', { className: 'bowire-request-builder-kv-head-drag' }),
            el('span', { className: 'bowire-request-builder-kv-head-key', textContent: keyPlaceholder }),
            el('span', { className: 'bowire-request-builder-kv-head-val', textContent: valPlaceholder }),
            el('span', { className: 'bowire-request-builder-kv-head-desc', textContent: descPlaceholder }),
            el('span', { className: 'bowire-request-builder-kv-head-on', textContent: '' }),
            el('span', { className: 'bowire-request-builder-kv-head-del', textContent: '' })
        );
        table.appendChild(head);

        // Real rows + one trailing empty add-row.
        var allRows = rows.slice();
        allRows.push({ key: '', value: '', description: '', enabled: true, _isAddRow: true });
        allRows.forEach(function (r, idx) {
            var isAdd = r._isAddRow === true;
            var row = el('div', {
                className: 'bowire-request-builder-kv-row' + (isAdd ? ' is-add-row' : ''),
                draggable: !isAdd ? 'true' : undefined,
                'data-idx': String(idx)
            });
            // Drag handle (cosmetic — drag-drop reorder wired via
            // native HTML5 DnD on the row itself).
            row.appendChild(el('span', {
                className: 'bowire-request-builder-kv-drag',
                title: 'Drag to reorder',
                textContent: '⋮⋮'
            }));
            row.appendChild(el('input', {
                type: 'text',
                className: 'bowire-request-builder-kv-input bowire-request-builder-kv-key',
                value: r.key || '',
                placeholder: keyPlaceholder,
                spellcheck: 'false',
                onInput: function (e) {
                    if (isAdd) {
                        if (e.target.value) {
                            // Promote add-row to real row.
                            rows.push({ key: e.target.value, value: '', description: '', enabled: true });
                            render();
                        }
                    } else {
                        rows[idx].key = e.target.value;
                    }
                }
            }));
            row.appendChild(el('input', {
                type: 'text',
                className: 'bowire-request-builder-kv-input bowire-request-builder-kv-val',
                value: r.value || '',
                placeholder: valPlaceholder,
                spellcheck: 'false',
                onInput: function (e) {
                    if (isAdd) {
                        if (e.target.value) {
                            rows.push({ key: '', value: e.target.value, description: '', enabled: true });
                            render();
                        }
                    } else {
                        rows[idx].value = e.target.value;
                    }
                }
            }));
            row.appendChild(el('input', {
                type: 'text',
                className: 'bowire-request-builder-kv-input bowire-request-builder-kv-desc',
                value: r.description || '',
                placeholder: descPlaceholder,
                spellcheck: 'false',
                onInput: function (e) {
                    if (isAdd) {
                        if (e.target.value) {
                            rows.push({ key: '', value: '', description: e.target.value, enabled: true });
                            render();
                        }
                    } else {
                        rows[idx].description = e.target.value;
                    }
                }
            }));
            // Enable checkbox — hidden for the add-row (no row to
            // toggle yet).
            row.appendChild(el('input', {
                type: 'checkbox',
                className: 'bowire-request-builder-kv-enable',
                checked: r.enabled !== false ? 'checked' : undefined,
                title: r.enabled !== false ? 'Enabled — included in request' : 'Disabled — skipped',
                style: isAdd ? 'visibility:hidden' : undefined,
                onChange: function (e) {
                    if (!isAdd) {
                        rows[idx].enabled = e.target.checked;
                        render();
                    }
                }
            }));
            // Delete icon — hidden for the add-row.
            row.appendChild(el('button', {
                type: 'button',
                className: 'bowire-request-builder-kv-del',
                title: 'Remove row',
                style: isAdd ? 'visibility:hidden' : undefined,
                innerHTML: svgIcon('close'),
                onClick: function () {
                    if (!isAdd) {
                        rows.splice(idx, 1);
                        render();
                    }
                }
            }));
            // Drag-drop wiring for reorder (real rows only).
            if (!isAdd) {
                row.addEventListener('dragstart', function (e) {
                    e.dataTransfer.effectAllowed = 'move';
                    e.dataTransfer.setData('text/plain', String(idx));
                });
                row.addEventListener('dragover', function (e) {
                    e.preventDefault();
                    e.dataTransfer.dropEffect = 'move';
                });
                row.addEventListener('drop', function (e) {
                    e.preventDefault();
                    var from = parseInt(e.dataTransfer.getData('text/plain'), 10);
                    var to = idx;
                    if (isNaN(from) || from === to || from >= rows.length || to >= rows.length) return;
                    var moved = rows.splice(from, 1)[0];
                    rows.splice(to, 0, moved);
                    render();
                });
            }
            table.appendChild(row);
        });
        return table;
    }

    // ---- Render: tab body for each sub-tab ----
    function _renderHoppTabBody(fr) {
        ensureHoppState(fr);
        var body = el('div', { className: 'bowire-request-builder-tab-body', 'data-tab': fr._requestBuilder.activeTab });
        switch (fr._requestBuilder.activeTab) {
            case 'parameter':
                body.appendChild(_renderHoppKvTable(fr._requestBuilder.params, {
                    keyPlaceholder: 'Parameter',
                    valuePlaceholder: 'Value',
                    descPlaceholder: 'Description'
                }));
                break;
            case 'header':
                body.appendChild(_renderHoppKvTable(fr._requestBuilder.headers, {
                    keyPlaceholder: 'Header',
                    valuePlaceholder: 'Value',
                    descPlaceholder: 'Description'
                }));
                break;
            case 'body':
                body.appendChild(_renderHoppBodyTab(fr));
                break;
            case 'auth':
                body.appendChild(_renderHoppAuthTab(fr));
                break;
            case 'pre':
                body.appendChild(_renderHoppScriptTab(fr, 'pre'));
                break;
            case 'post':
                body.appendChild(_renderHoppScriptTab(fr, 'post'));
                break;
            case 'vars':
                body.appendChild(_renderHoppVarsTab(fr));
                break;
        }
        return body;
    }

    // ---- Body tab: JSON / form / raw / binary ----
    function _renderHoppBodyTab(fr) {
        var wrap = el('div', { className: 'bowire-request-builder-body-wrap' });
        // Mode strip
        var modeStrip = el('div', { className: 'bowire-request-builder-body-modes' });
        HOPP_BODY_MODES.forEach(function (m) {
            var btn = el('button', {
                type: 'button',
                className: 'bowire-request-builder-body-mode-btn' + (fr._requestBuilder.bodyMode === m.id ? ' is-active' : ''),
                onClick: function () {
                    fr._requestBuilder.bodyMode = m.id;
                    render();
                }
            }, el('span', { textContent: m.label }));
            modeStrip.appendChild(btn);
        });
        wrap.appendChild(modeStrip);

        if (fr._requestBuilder.bodyMode === 'form') {
            // Form body uses a key/value table; stored on
            // fr._requestBuilder.formBody (lazy-init).
            if (!Array.isArray(fr._requestBuilder.formBody)) fr._requestBuilder.formBody = [];
            wrap.appendChild(_renderHoppKvTable(fr._requestBuilder.formBody, {
                keyPlaceholder: 'Field',
                valuePlaceholder: 'Value',
                descPlaceholder: 'Description'
            }));
        } else if (fr._requestBuilder.bodyMode === 'binary') {
            // Binary mode: file picker (browser-only). Snapshot the
            // chosen filename onto fr._requestBuilder.binaryName for display
            // and stash the File reference in module-scope (not
            // persistable to localStorage; the operator re-picks
            // after reload).
            wrap.appendChild(el('input', {
                type: 'file',
                className: 'bowire-request-builder-body-binary',
                onChange: function (e) {
                    var f = e.target.files && e.target.files[0];
                    fr._requestBuilder.binaryName = f ? f.name : '';
                    fr._requestBuilder._binaryRef = f || null;
                    render();
                }
            }));
            if (fr._requestBuilder.binaryName) {
                wrap.appendChild(el('div', {
                    className: 'bowire-request-builder-body-binary-name',
                    textContent: 'Selected: ' + fr._requestBuilder.binaryName
                }));
            }
        } else {
            // JSON or raw: textarea editor — same as classic
            // freeform's body editor with the JSON validator
            // when in JSON mode.
            var ed = el('textarea', {
                className: 'bowire-editor bowire-request-builder-body-editor',
                placeholder: fr._requestBuilder.bodyMode === 'json'
                    ? 'JSON request body — use {{var}} for env-var substitution'
                    : 'Raw request body…',
                spellcheck: 'false'
            });
            ed.value = fr.body || '';
            ed.addEventListener('input', function () { fr.body = this.value; });
            wrap.appendChild(ed);
            if (fr._requestBuilder.bodyMode === 'json') {
                var st = el('div', { className: 'bowire-json-status empty' });
                wrap.appendChild(st);
                try { if (typeof attachJsonValidator === 'function') attachJsonValidator(ed, st); }
                catch (_) { /* validator may not be loaded; non-fatal */ }
            }
        }
        return wrap;
    }

    // ---- Auth tab: bind to the existing auth resolver shape ----
    function _renderHoppAuthTab(fr) {
        var wrap = el('div', { className: 'bowire-request-builder-auth-wrap' });
        var kindRow = el('div', { className: 'bowire-request-builder-auth-kind-row' });
        HOPP_AUTH_KINDS.forEach(function (k) {
            kindRow.appendChild(el('button', {
                type: 'button',
                className: 'bowire-request-builder-auth-kind-btn' + (fr._requestBuilder.authKind === k.id ? ' is-active' : ''),
                textContent: k.label,
                onClick: function () {
                    fr._requestBuilder.authKind = k.id;
                    render();
                }
            }));
        });
        wrap.appendChild(kindRow);

        var formWrap = el('div', { className: 'bowire-request-builder-auth-form' });
        switch (fr._requestBuilder.authKind) {
            case 'bearer':
                formWrap.appendChild(_authField('Token', fr._requestBuilder.authData.token || '', function (v) {
                    fr._requestBuilder.authData.token = v;
                }));
                break;
            case 'basic':
                formWrap.appendChild(_authField('Username', fr._requestBuilder.authData.username || '', function (v) {
                    fr._requestBuilder.authData.username = v;
                }));
                formWrap.appendChild(_authField('Password', fr._requestBuilder.authData.password || '', function (v) {
                    fr._requestBuilder.authData.password = v;
                }, 'password'));
                break;
            case 'apikey':
                formWrap.appendChild(_authField('Key', fr._requestBuilder.authData.key || '', function (v) {
                    fr._requestBuilder.authData.key = v;
                }));
                formWrap.appendChild(_authField('Value', fr._requestBuilder.authData.value || '', function (v) {
                    fr._requestBuilder.authData.value = v;
                }));
                break;
            case 'none':
            default:
                formWrap.appendChild(el('div', {
                    className: 'bowire-request-builder-auth-empty',
                    textContent: 'No auth — requests go out without an Authorization header. {{var}} substitution still applies to URL + headers.'
                }));
                break;
        }
        wrap.appendChild(formWrap);
        return wrap;
    }
    function _authField(label, value, onInput, type) {
        var row = el('div', { className: 'bowire-request-builder-auth-field' });
        row.appendChild(el('label', { className: 'bowire-request-builder-auth-label', textContent: label }));
        row.appendChild(el('input', {
            type: type || 'text',
            className: 'bowire-request-builder-auth-input',
            value: value,
            spellcheck: 'false',
            onInput: function (e) { onInput(e.target.value); }
        }));
        return row;
    }

    // ---- Pre/Post script tabs ----
    function _renderHoppScriptTab(fr, phase) {
        var wrap = el('div', { className: 'bowire-request-builder-script-wrap' });
        wrap.appendChild(el('div', {
            className: 'bowire-request-builder-script-hint',
            textContent: phase === 'pre'
                ? 'JavaScript that runs BEFORE the request leaves. Use ctx.request to mutate headers/body, ctx.env to read env vars, ctx.vars.captured.X = v to persist a value.'
                : 'JavaScript that runs AFTER the response lands. Inspect ctx.response.body / .status / .headers; assert with ctx.assert.ok(...); persist with ctx.vars.captured.X = v.'
        }));
        var ta = el('textarea', {
            className: 'bowire-editor bowire-request-builder-script-editor',
            placeholder: phase === 'pre'
                ? '// e.g.  ctx.request.headers.set("X-Request-Id", crypto.randomUUID());'
                : '// e.g.  ctx.assert.equal(ctx.response.status, 200);\n//       ctx.vars.captured.id = JSON.parse(ctx.response.body).id;',
            spellcheck: 'false'
        });
        ta.value = phase === 'pre' ? fr._requestBuilder.preScript : fr._requestBuilder.postScript;
        ta.addEventListener('input', function () {
            if (phase === 'pre') fr._requestBuilder.preScript = this.value;
            else fr._requestBuilder.postScript = this.value;
        });
        wrap.appendChild(ta);
        return wrap;
    }

    // ---- Variables tab: env vars + scratch overrides ----
    function _renderHoppVarsTab(fr) {
        var wrap = el('div', { className: 'bowire-request-builder-vars-wrap' });
        // Try to surface the active env's vars first; fall back to a
        // hint if no env is selected.
        var rows = [];
        try {
            if (typeof getMergedVars === 'function') {
                var merged = getMergedVars();
                Object.keys(merged).sort().forEach(function (k) {
                    rows.push({ key: k, value: String(merged[k] == null ? '' : merged[k]) });
                });
            }
        } catch (_) { /* env helpers may not be loaded; show empty */ }
        if (rows.length === 0) {
            wrap.appendChild(el('div', {
                className: 'bowire-request-builder-vars-empty',
                textContent: 'No environment variables yet. Use the Workspaces → Environments rail to create some, or type {{var}} in the URL and the prompt will offer to seed it.'
            }));
        } else {
            wrap.appendChild(el('div', {
                className: 'bowire-request-builder-vars-hint',
                textContent: 'Read-only snapshot from the active environment. Edit values in Workspaces → Environments.'
            }));
            var list = el('div', { className: 'bowire-request-builder-vars-list' });
            rows.forEach(function (r) {
                list.appendChild(el('div', { className: 'bowire-request-builder-vars-row' },
                    el('span', { className: 'bowire-request-builder-vars-key', textContent: r.key }),
                    el('span', { className: 'bowire-request-builder-vars-eq', textContent: '=' }),
                    el('span', { className: 'bowire-request-builder-vars-val', textContent: r.value })
                ));
            });
            wrap.appendChild(list);
        }
        // Scratch overrides for this request — let the operator pin a
        // value JUST for this tab. Stored on fr._requestBuilder.scratchVars.
        if (!Array.isArray(fr._requestBuilder.scratchVars)) fr._requestBuilder.scratchVars = [];
        wrap.appendChild(el('div', {
            className: 'bowire-request-builder-vars-section',
            textContent: 'Scratch overrides (this request only)'
        }));
        wrap.appendChild(_renderHoppKvTable(fr._requestBuilder.scratchVars, {
            keyPlaceholder: 'Variable',
            valuePlaceholder: 'Value',
            descPlaceholder: 'Notes'
        }));
        return wrap;
    }

    // ---- Response renderer ----
    function _renderHoppResponse() {
        var pane = el('div', { className: 'bowire-request-builder-response' });
        pane.appendChild(el('div', { className: 'bowire-pane-heading', textContent: 'Response' }));
        if (responseError) {
            var errOut = el('div', { className: 'bowire-response-output error' });
            var prob = (typeof responseError === 'object' && typeof normalizeProblem === 'function')
                ? normalizeProblem(responseError) : null;
            if (prob && typeof renderProblem === 'function') renderProblem(prob, errOut);
            else errOut.textContent = (typeof responseError === 'string')
                ? responseError
                : (typeof problemTitle === 'function' ? problemTitle(responseError, 'Request failed') : 'Request failed');
            pane.appendChild(errOut);
        } else if (responseData) {
            var output = el('div', { className: 'bowire-response-output is-interactive' });
            try { output.innerHTML = highlightJsonInteractive(responseData); }
            catch (_) { output.textContent = String(responseData); }
            pane.appendChild(output);
        } else {
            pane.appendChild(el('div', {
                className: 'bowire-response-empty',
                textContent: 'Execute the request to see the response here. Tip: Ctrl+Enter sends.'
            }));
        }
        return pane;
    }

    // ---- Top-level render entry — composes the bar + subtabs + body + response ----
    function _appendRequestBuilderInto(pane) {
        var fr = freeformRequest;
        ensureHoppState(fr);
        pane.appendChild(_renderRequestBuilder(fr));
        pane.appendChild(_renderHoppSubTabs(fr));
        pane.appendChild(_renderHoppTabBody(fr));
        pane.appendChild(_renderHoppResponse());
    }

    // ---- Execute (Phase A wire-up) ----
    //
    // Reuses the same /api/invoke POST as freeform execute, with three
    // adaptations:
    //   1. The URL field carries the FULL request URL — merge in the
    //      Parameter rows as query string, then submit as serverUrl.
    //   2. Headers come from fr._requestBuilder.headers, plus an auth-derived
    //      Authorization header if authKind !== 'none'.
    //   3. The body is fr.body for JSON/raw or x-www-form-urlencoded
    //      from fr._requestBuilder.formBody for form mode.
    async function executeHoppRequest() {
        if (!freeformRequest) return;
        var fr = freeformRequest;
        ensureHoppState(fr);
        if (!fr.serverUrl || !fr.serverUrl.trim()) {
            if (typeof toast === 'function') toast('Enter a URL', 'error');
            return;
        }
        if (!fr.method) fr.method = 'GET';

        // Resolve {{var}} in URL.
        var urlBase = fr.serverUrl;
        try {
            if (typeof substituteVars === 'function') urlBase = substituteVars(fr.serverUrl);
        } catch (_) { /* keep raw */ }
        var urlWithParams = _composeUrlWithParams(urlBase, fr._requestBuilder.params);

        // Compose headers — KV table + auth derivation.
        var headers = _kvToObject(fr._requestBuilder.headers);
        // Resolve {{var}} inside header values.
        Object.keys(headers).forEach(function (k) {
            try {
                if (typeof substituteVars === 'function') headers[k] = substituteVars(headers[k]);
            } catch (_) { /* leave raw */ }
        });
        if (fr._requestBuilder.authKind === 'bearer' && fr._requestBuilder.authData.token) {
            var tok = fr._requestBuilder.authData.token;
            try { if (typeof substituteVars === 'function') tok = substituteVars(tok); } catch (_) { /* keep raw */ }
            headers['Authorization'] = 'Bearer ' + tok;
        } else if (fr._requestBuilder.authKind === 'basic') {
            var u = fr._requestBuilder.authData.username || '';
            var p = fr._requestBuilder.authData.password || '';
            try {
                if (typeof substituteVars === 'function') {
                    u = substituteVars(u);
                    p = substituteVars(p);
                }
            } catch (_) { /* keep raw */ }
            try { headers['Authorization'] = 'Basic ' + btoa(u + ':' + p); }
            catch (_) { /* btoa fails on non-Latin chars; skip silently */ }
        } else if (fr._requestBuilder.authKind === 'apikey' && fr._requestBuilder.authData.key) {
            var ak = fr._requestBuilder.authData.key.trim();
            var av = fr._requestBuilder.authData.value || '';
            try { if (typeof substituteVars === 'function') av = substituteVars(av); } catch (_) { /* keep raw */ }
            headers[ak] = av;
        }

        // Compose body.
        var bodyStr = '';
        var verb = fr.method.toUpperCase();
        var verbHasBody = ['POST', 'PUT', 'PATCH', 'DELETE'].indexOf(verb) >= 0;
        if (verbHasBody) {
            if (fr._requestBuilder.bodyMode === 'form') {
                var formObj = _kvToObject(fr._requestBuilder.formBody || []);
                var formParts = [];
                Object.keys(formObj).forEach(function (k) {
                    formParts.push(encodeURIComponent(k) + '=' + encodeURIComponent(formObj[k]));
                });
                bodyStr = formParts.join('&');
                if (!headers['Content-Type']) headers['Content-Type'] = 'application/x-www-form-urlencoded';
            } else if (fr._requestBuilder.bodyMode === 'binary') {
                // Binary upload not yet wired to /api/invoke; surface
                // a hint so the operator knows the path needs work.
                if (typeof toast === 'function') {
                    toast('Binary uploads via the Request builder are not yet wired through /api/invoke — coming in a follow-up', 'info');
                }
                bodyStr = '';
            } else {
                var rawBody = fr.body || '';
                try { if (typeof substituteVars === 'function') rawBody = substituteVars(rawBody); } catch (_) { /* keep raw */ }
                bodyStr = rawBody;
                if (fr._requestBuilder.bodyMode === 'json' && !headers['Content-Type']) {
                    headers['Content-Type'] = 'application/json';
                }
            }
        }

        // Run pre-script (best-effort; non-fatal). We pass a minimal
        // ctx that mirrors #126's shape — sufficient for header /
        // body / vars mutation.
        var preCtx = {
            request: { url: urlWithParams, method: verb, headers: headers, body: bodyStr },
            env: {
                get: function (k) { try { return getMergedVars()[k]; } catch (_) { return undefined; } },
                set: function (k, v) {
                    try {
                        if (typeof setActiveEnvVar === 'function') setActiveEnvVar(k, v);
                    } catch (_) { /* best-effort */ }
                }
            },
            vars: {
                captured: (typeof window !== 'undefined' && window.__bowire_captured)
                    ? window.__bowire_captured : (window.__bowire_captured = {})
            },
            log: function () { try { console.log.apply(console, ['[request-builder-pre]'].concat([].slice.call(arguments))); } catch (_) {} },
            assert: { ok: function (v, m) { if (!v) throw new Error(m || 'assert.ok failed'); } }
        };
        if (fr._requestBuilder.preScript && fr._requestBuilder.preScript.trim()) {
            try {
                // eslint-disable-next-line no-new-func
                new Function('ctx', fr._requestBuilder.preScript)(preCtx);
                // Pick up any mutations.
                urlWithParams = preCtx.request.url || urlWithParams;
                headers = preCtx.request.headers || headers;
                bodyStr = preCtx.request.body || bodyStr;
            } catch (e) {
                if (typeof toast === 'function') toast('Pre-script failed: ' + e.message, 'error');
                return;
            }
        }

        isExecuting = true;
        responseData = null;
        responseError = null;
        if (typeof markJobActive === 'function') markJobActive('request-builder', verb);
        render();

        var fullName = verb + ' ' + urlWithParams;
        if (typeof addConsoleEntry === 'function') {
            addConsoleEntry({ type: 'request', method: fullName, body: verbHasBody ? bodyStr : '' });
        }

        // #290 — capture status + duration for the history entry that
        // lands after the response (or error) comes back. The history
        // bucket persists what we know about the OUTCOME, not the full
        // response body — that's diagnosis-grade overhead we skip.
        var historyOutcome = { status: null, durationMs: null, ok: false };
        var historyStartMs = performance.now();

        try {
            var url = config.prefix + '/api/invoke?serverUrl=' + encodeURIComponent(urlWithParams);
            var invokeBody = {
                service: '',
                method: verb,
                messages: [verbHasBody ? bodyStr : '{}'],
                metadata: Object.keys(headers).length > 0 ? headers : null,
                protocol: 'rest'
            };
            // #290 — Binary upload via /api/invoke. We carry the bytes
            // base64-in-JSON rather than introducing a multipart endpoint
            // (decision documented at the top of executeHoppRequest +
            // mirrored on the server side BowireInvokeEndpoints.InvokeRequest).
            // Trade-off: ~33% wire overhead vs. multipart, but no new
            // endpoint, no Content-Type-sniffing branch in the existing
            // POST handler, and the existing JSON body inspection in the
            // proxy / recording / telemetry pipeline keeps working
            // verbatim. Larger uploads (>10MB) should switch to a dedicated
            // streaming endpoint — out of scope here.
            if (verbHasBody && fr._requestBuilder.bodyMode === 'binary' && fr._requestBuilder._binaryRef) {
                try {
                    var fileRef = fr._requestBuilder._binaryRef;
                    var bytes = await fileRef.arrayBuffer();
                    invokeBody.bodyBinary = _arrayBufferToBase64(bytes);
                    invokeBody.bodyBinaryContentType = fileRef.type || 'application/octet-stream';
                    invokeBody.bodyBinaryName = fileRef.name || fr._requestBuilder.binaryName || '';
                    // Replace the placeholder JSON message with an empty
                    // object so the protocol plugin doesn't try to parse
                    // the binary as JSON; the binary bucket is consulted
                    // by the server before falling back to messages[0].
                    invokeBody.messages = ['{}'];
                    if (!headers['Content-Type']) {
                        headers['Content-Type'] = fileRef.type || 'application/octet-stream';
                        invokeBody.metadata = headers;
                    }
                } catch (e) {
                    if (typeof toast === 'function') toast('Binary read failed: ' + e.message, 'error');
                    isExecuting = false;
                    if (typeof markJobDone === 'function') markJobDone('request-builder', verb);
                    render();
                    return;
                }
            }
            var resp = await fetch(url, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify(invokeBody)
            });
            var result = await resp.json();
            historyOutcome.durationMs = result && result.duration_ms != null
                ? result.duration_ms
                : Math.round(performance.now() - historyStartMs);
            if (result.title) {
                responseError = result;
                historyOutcome.status = (result.status != null ? result.status : 'Error');
                historyOutcome.ok = false;
                if (typeof addConsoleEntry === 'function') {
                    addConsoleEntry({ type: 'error', method: fullName, status: 'Error',
                        body: typeof richErrorDetail === 'function'
                            ? richErrorDetail(result, 'Request failed') : (result.detail || result.title) });
                }
            } else {
                responseData = result.response;
                historyOutcome.status = result.status || 'OK';
                historyOutcome.ok = true;
                if (typeof captureResponse === 'function' && result.response) captureResponse(result.response);
                if (typeof addConsoleEntry === 'function') {
                    addConsoleEntry({ type: 'response', method: fullName,
                        status: result.status, body: result.response });
                }

                // Post-script — runs AFTER the wire response lands.
                if (fr._requestBuilder.postScript && fr._requestBuilder.postScript.trim()) {
                    var postCtx = {
                        response: {
                            status: result.status,
                            body: result.response,
                            durationMs: result.duration_ms || 0
                        },
                        env: preCtx.env,
                        vars: preCtx.vars,
                        log: function () { try { console.log.apply(console, ['[request-builder-post]'].concat([].slice.call(arguments))); } catch (_) {} },
                        assert: preCtx.assert
                    };
                    try {
                        // eslint-disable-next-line no-new-func
                        new Function('ctx', fr._requestBuilder.postScript)(postCtx);
                    } catch (e) {
                        if (typeof toast === 'function') toast('Post-script failed: ' + e.message, 'error');
                    }
                }
            }
        } catch (e) {
            responseError = e.message;
            historyOutcome.status = 'NetworkError';
            historyOutcome.ok = false;
            historyOutcome.durationMs = Math.round(performance.now() - historyStartMs);
            if (typeof addConsoleEntry === 'function') {
                addConsoleEntry({ type: 'error', method: fullName, status: 'NetworkError', body: e.message });
            }
        }

        // #290 — persist a history snapshot for this execution. We do
        // this regardless of success/error so the operator can see what
        // last failed too. Push is wrapped to avoid a localStorage
        // failure cascading into the response render path.
        try { pushHoppHistoryEntry(fr, historyOutcome); }
        catch (e) { console.warn('[request-builder-history] push failed', e); }

        isExecuting = false;
        if (typeof markJobDone === 'function') markJobDone('request-builder', verb);
        render();
    }

    // ---- #290 — Execute as benchmark ----
    //
    // Snapshot the bar's current request shape into a method-style
    // benchmark target, build a single-phase envelope (defaults: 4
    // concurrent virtual users × 30 iterations — a reasonable smoke
    // probe; the operator can tune via the Benchmarks rail's detail
    // pane after the run lands), drop into the Benchmarks rail so the
    // existing detail pane + result histogram render the result, then
    // kick off runBenchmarkSpec. Reuses the same /api/invoke wire path
    // executeHoppRequest does — same auth, same params merging, same
    // metadata shape — so the benchmark's numbers actually reflect
    // what a one-shot Execute would produce.
    function runHoppAsBenchmark(fr) {
        ensureHoppState(fr);
        if (!fr.serverUrl || !fr.serverUrl.trim()) {
            if (typeof toast === 'function') toast('Enter a URL before benchmarking', 'error');
            return;
        }
        if (typeof createBenchmarkSpec !== 'function'
            || typeof runBenchmarkSpec !== 'function') {
            if (typeof toast === 'function') {
                toast('Benchmarks module not loaded in this host', 'error');
            }
            return;
        }

        // Resolve vars + merge params into the URL so the benchmark
        // target carries the SAME effective URL the bar would send.
        // Otherwise a {{baseUrl}} would be re-substituted per iteration
        // (which is fine but unnecessary churn — and a Parameter row
        // typed into the bar would be missed because the benchmark
        // runner only looks at target.body / target.metadata).
        var resolvedUrl = fr.serverUrl;
        try {
            if (typeof substituteVars === 'function') {
                resolvedUrl = substituteVars(fr.serverUrl);
            }
        } catch (_) { /* keep raw */ }
        var urlWithParams = _composeUrlWithParams(resolvedUrl, fr._requestBuilder.params || []);

        // Auth-derived headers — same logic as executeHoppRequest, just
        // applied to the snapshot rather than the in-flight request.
        var headers = _kvToObject(fr._requestBuilder.headers || []);
        if (fr._requestBuilder.authKind === 'bearer' && fr._requestBuilder.authData.token) {
            headers['Authorization'] = 'Bearer ' + fr._requestBuilder.authData.token;
        } else if (fr._requestBuilder.authKind === 'basic') {
            var u = fr._requestBuilder.authData.username || '';
            var p = fr._requestBuilder.authData.password || '';
            try { headers['Authorization'] = 'Basic ' + btoa(u + ':' + p); }
            catch (_) { /* non-Latin chars; skip */ }
        } else if (fr._requestBuilder.authKind === 'apikey' && fr._requestBuilder.authData.key) {
            headers[fr._requestBuilder.authData.key.trim()] = fr._requestBuilder.authData.value || '';
        }

        var verb = (fr.method || 'GET').toUpperCase();
        var bodyStr = '{}';
        var verbHasBody = ['POST', 'PUT', 'PATCH', 'DELETE'].indexOf(verb) >= 0;
        if (verbHasBody) {
            if (fr._requestBuilder.bodyMode === 'form') {
                var formObj = _kvToObject(fr._requestBuilder.formBody || []);
                var formParts = [];
                Object.keys(formObj).forEach(function (k) {
                    formParts.push(encodeURIComponent(k) + '=' + encodeURIComponent(formObj[k]));
                });
                bodyStr = formParts.join('&');
                if (!headers['Content-Type']) headers['Content-Type'] = 'application/x-www-form-urlencoded';
            } else if (fr._requestBuilder.bodyMode === 'binary') {
                // Binary bodies don't survive into the benchmark spec —
                // the file ref is transient and the spec persists across
                // reloads. Operator gets a clear message rather than a
                // silently-empty body the benchmark would happily hammer
                // the upstream with.
                if (typeof toast === 'function') {
                    toast('Binary uploads can\'t be benchmarked from the Request builder — Execute once instead, or move the request to a collection', 'info');
                }
                return;
            } else {
                bodyStr = fr.body || '';
                if (fr._requestBuilder.bodyMode === 'json' && !headers['Content-Type']) {
                    headers['Content-Type'] = 'application/json';
                }
            }
        }

        // Method-style target carrying the resolved URL + verb + headers
        // + body. service='' tells the protocol plugin "freeform URL,
        // no schema lookup needed" — same shape as executeHoppRequest
        // sends to /api/invoke.
        var target = {
            type: 'method',
            service: '',
            method: verb,
            protocol: 'rest',
            body: bodyStr,
            metadata: headers,
            // serverUrl: pinned via the spec name so the benchmark
            // runner picks it up through legacy mirror fields (see
            // _invokeBenchmarkTarget). The runner reads target.serverUrl
            // → serverUrlParamForService(...) — null falls back to the
            // workspace's default. Pin the actual URL so per-iteration
            // routing is unambiguous.
            serverUrl: urlWithParams
        };

        // Defaults — 4 VUs × 30 iterations. Quick to run (~120 calls,
        // ~2-3s on a local upstream), small enough to not flood a
        // shared staging env. The operator can tune in the detail
        // pane after the spec lands.
        var spec = createBenchmarkSpec({
            name: 'Hopp: ' + verb + ' ' + (resolvedUrl || '(no url)'),
            targets: [target],
            phases: [{ vus: 4, totalIterations: 30 }],
            mode: 'sequential'
        });

        // Switch the workbench into Benchmarks-rail mode so the
        // detail pane + result charts are visible while the run lands.
        // railMode is the canonical sidebar state (see render-sidebar.js).
        try {
            if (typeof railMode !== 'undefined') {
                // eslint-disable-next-line no-undef
                railMode = 'benchmarks';
                try { localStorage.setItem('bowire_rail_mode', 'benchmarks'); }
                catch { /* ignore */ }
            }
        } catch (_) { /* non-fatal */ }
        if (typeof benchmarksSelectedId !== 'undefined') {
            // Direct write — same pattern the sidebar 'New benchmark'
            // button uses. setMode + selecting the spec puts the
            // operator in the right place to watch the bars fill.
            // eslint-disable-next-line no-undef
            benchmarksSelectedId = spec.id;
        }
        if (typeof toast === 'function') {
            toast('Benchmarking ' + verb + ' (4 × 30 calls)…', 'info');
        }
        render();

        // Fire-and-forget — runBenchmarkSpec is async; we don't await
        // it because the render() above already painted the empty
        // spec into the rail, and the runner's onProgress callback
        // re-renders as results land.
        try {
            runBenchmarkSpec(spec, function () { render(); });
        } catch (e) {
            if (typeof toast === 'function') toast('Benchmark failed: ' + e.message, 'error');
        }
    }

    // #290 — Base64 encode a binary buffer for transport via the
    // /api/invoke JSON envelope. Streaming/large-payload uploads
    // should go through a dedicated multipart endpoint instead; the
    // base64 path is appropriate for typical Request-builder uploads
    // (a config file, an image, a serialized proto message).
    function _arrayBufferToBase64(buf) {
        var bytes = new Uint8Array(buf);
        // Chunked btoa to dodge call-stack limits on multi-MB blobs.
        var chunkSize = 0x8000;
        var binStr = '';
        for (var i = 0; i < bytes.length; i += chunkSize) {
            binStr += String.fromCharCode.apply(null,
                bytes.subarray(i, Math.min(i + chunkSize, bytes.length)));
        }
        return btoa(binStr);
    }

    // ---- Outside-click close for menus ----
    //
    // Wire a document-level click that closes the method dropdown and
    // the send caret menu when the user clicks outside them. Mounted
    // once at init time; the prologue's IIFE only runs this fragment
    // once per page load so it's safe to attach unconditionally.
    document.addEventListener('click', function (e) {
        var changed = false;
        if (rbMethodMenuOpen) {
            var mwrap = e.target.closest && e.target.closest('.bowire-request-builder-method-wrap');
            if (!mwrap) { rbMethodMenuOpen = false; changed = true; }
        }
        if (rbSendMenuOpen) {
            var swrap = e.target.closest && e.target.closest('.bowire-request-builder-send-wrap');
            if (!swrap) { rbSendMenuOpen = false; changed = true; }
        }
        // #290 — history dropdown closes on outside click, same shape.
        if (rbHistoryMenuOpen) {
            var hwrap = e.target.closest && e.target.closest('.bowire-request-builder-history-wrap');
            if (!hwrap) { rbHistoryMenuOpen = false; changed = true; }
        }
        if (changed) render();
    });

    // ---- Ctrl+L global shortcut (Phase F) ----
    //
    // Wired in init.js's keydown dispatcher — see the case for 'l'
    // there. The handler calls startHoppRequest() which sets up the
    // freeformRequest with _requestBuilder markers and renders.
