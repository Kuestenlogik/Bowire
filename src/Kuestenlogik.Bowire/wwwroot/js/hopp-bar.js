// @generated
// hopp-bar.js — Hoppscotch-style single-line request bar (#289).
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
// State shape extension. A freeformRequest gains a hopp-mode marker
// (`_hopp: true`) and a few extra fields stored under `_hopp` so the
// classic freeform path keeps working untouched:
//
//   freeformRequest._hopp = {
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
    var hoppMethodMenuOpen = false;
    var hoppSendMenuOpen = false;

    // ---- Hopp-mode bootstrapping ----
    //
    // Used both by the empty-state "Just fire a request →" CTA and by
    // the Ctrl+L global keyboard shortcut. Replaces the classic
    // freeformRequest with one carrying the _hopp marker so the
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
        freeformRequest._hopp = _newHoppState();
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
    // or freshly converted — has a populated _hopp blob. Idempotent.
    function ensureHoppState(fr) {
        if (!fr) return;
        if (!fr._hopp || typeof fr._hopp !== 'object') {
            fr._hopp = _newHoppState();
        }
        if (!Array.isArray(fr._hopp.params))  fr._hopp.params  = [];
        if (!Array.isArray(fr._hopp.headers)) fr._hopp.headers = [];
        if (typeof fr._hopp.bodyMode !== 'string') fr._hopp.bodyMode = 'json';
        if (typeof fr._hopp.preScript !== 'string')  fr._hopp.preScript  = '';
        if (typeof fr._hopp.postScript !== 'string') fr._hopp.postScript = '';
        if (typeof fr._hopp.authKind !== 'string')   fr._hopp.authKind   = 'none';
        if (!fr._hopp.authData) fr._hopp.authData = {};
    }

    function isHoppRequest(fr) {
        return !!(fr && fr._hopp);
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
        var wrap = el('div', { className: 'bowire-hopp-url-overlay' });
        if (!url) return wrap;
        var re = /\{\{([^}]+)\}\}/g;
        var lastIdx = 0;
        var m;
        while ((m = re.exec(url)) !== null) {
            if (m.index > lastIdx) {
                wrap.appendChild(el('span', {
                    className: 'bowire-hopp-url-plain',
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
                className: 'bowire-hopp-url-var' + (unresolved ? ' is-unresolved' : ''),
                title: unresolved
                    ? '{{' + name + '}} — unresolved (no matching variable)'
                    : '{{' + name + '}} → ' + resolved,
                textContent: raw
            }));
            lastIdx = m.index + raw.length;
        }
        if (lastIdx < url.length) {
            wrap.appendChild(el('span', {
                className: 'bowire-hopp-url-plain',
                textContent: url.substring(lastIdx)
            }));
        }
        return wrap;
    }

    // ---- Render: the request bar (method + URL + send) ----
    function _renderHoppBar(fr) {
        var bar = el('div', { className: 'bowire-hopp-bar' });

        // Method dropdown
        var methodWrap = el('div', { className: 'bowire-hopp-method-wrap' });
        var method = (fr.method || 'GET').toUpperCase();
        if (HOPP_METHODS.indexOf(method) < 0) method = 'GET';
        fr.method = method;
        var methodBtn = el('button', {
            type: 'button',
            id: 'bowire-hopp-method-btn',
            className: 'bowire-hopp-method-btn',
            'data-verb': method,
            'aria-haspopup': 'listbox',
            'aria-expanded': hoppMethodMenuOpen ? 'true' : 'false',
            onClick: function (e) {
                e.stopPropagation();
                hoppMethodMenuOpen = !hoppMethodMenuOpen;
                render();
            }
        },
            el('span', { className: 'bowire-hopp-method-label', textContent: method }),
            el('span', { className: 'bowire-hopp-method-caret', innerHTML: svgIcon('chevronDown') })
        );
        methodWrap.appendChild(methodBtn);
        if (hoppMethodMenuOpen) {
            var menu = el('div', {
                className: 'bowire-hopp-method-menu',
                role: 'listbox',
                onClick: function (e) { e.stopPropagation(); }
            });
            HOPP_METHODS.forEach(function (m) {
                menu.appendChild(el('button', {
                    type: 'button',
                    className: 'bowire-hopp-method-menu-item' + (m === method ? ' is-selected' : ''),
                    'data-verb': m,
                    role: 'option',
                    'aria-selected': m === method ? 'true' : 'false',
                    onClick: function () {
                        fr.method = m;
                        hoppMethodMenuOpen = false;
                        render();
                    }
                },
                    el('span', { className: 'bowire-hopp-method-chip', 'data-verb': m, textContent: m })
                ));
            });
            methodWrap.appendChild(menu);
        }
        bar.appendChild(methodWrap);

        // URL input + variable overlay
        var urlWrap = el('div', { className: 'bowire-hopp-url-wrap' });
        var urlInput = el('input', {
            id: 'bowire-hopp-url-input',
            type: 'text',
            className: 'bowire-hopp-url-input',
            value: fr.serverUrl || '',
            placeholder: 'https://api.example.com/users  •  use {{baseUrl}} for env vars',
            spellcheck: 'false',
            autocomplete: 'off',
            onInput: function (e) {
                fr.serverUrl = e.target.value;
                // Live overlay refresh without full re-render — find
                // the overlay sibling and replace its children.
                var overlay = urlWrap.querySelector('.bowire-hopp-url-overlay');
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

        // Send button (split caret variant menu)
        var sendWrap = el('div', { className: 'bowire-hopp-send-wrap' });
        var sendBtn = el('button', {
            type: 'button',
            id: 'bowire-hopp-send-btn',
            className: 'bowire-hopp-send-btn',
            title: 'Execute request (Ctrl+Enter)',
            onClick: function () { executeHoppRequest(); }
        },
            el('span', { innerHTML: svgIcon('play'), style: 'width:14px;height:14px;display:flex' }),
            el('span', { textContent: 'Execute' })
        );
        sendWrap.appendChild(sendBtn);
        var sendCaret = el('button', {
            type: 'button',
            id: 'bowire-hopp-send-caret',
            className: 'bowire-hopp-send-caret' + (hoppSendMenuOpen ? ' is-open' : ''),
            title: 'More execute options',
            'aria-haspopup': 'menu',
            'aria-expanded': hoppSendMenuOpen ? 'true' : 'false',
            onClick: function (e) {
                e.stopPropagation();
                hoppSendMenuOpen = !hoppSendMenuOpen;
                render();
            },
            innerHTML: svgIcon('chevronDown')
        });
        sendWrap.appendChild(sendCaret);
        if (hoppSendMenuOpen) {
            var sendMenu = el('div', {
                className: 'bowire-hopp-send-menu',
                role: 'menu',
                onClick: function (e) { e.stopPropagation(); }
            });
            sendMenu.appendChild(el('button', {
                className: 'bowire-hopp-send-menu-item',
                role: 'menuitem',
                onClick: function () {
                    hoppSendMenuOpen = false;
                    executeHoppRequest();
                }
            },
                el('span', { innerHTML: svgIcon('play'), style: 'width:14px;height:14px;display:flex' }),
                el('span', { textContent: 'Execute once' })
            ));
            sendMenu.appendChild(el('button', {
                className: 'bowire-hopp-send-menu-item',
                role: 'menuitem',
                onClick: function () {
                    hoppSendMenuOpen = false;
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
            // Benchmark variant — only enabled when the benchmarks
            // module is present (it ships as a fragment that may be
            // trimmed in embedded hosts).
            if (typeof runBenchmark === 'function') {
                sendMenu.appendChild(el('button', {
                    className: 'bowire-hopp-send-menu-item',
                    role: 'menuitem',
                    onClick: function () {
                        hoppSendMenuOpen = false;
                        if (typeof toast === 'function') {
                            toast('Benchmark from Hopp bar coming soon', 'info');
                        }
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
            metadata: _kvToObject(fr._hopp.headers),
            params: _kvToObject(fr._hopp.params),
            serverUrl: fr.serverUrl || '',
            urlMode: 'inline',
            kind: 'hopp',
            preScript:  fr._hopp.preScript || '',
            postScript: fr._hopp.postScript || '',
            authKind:   fr._hopp.authKind || 'none',
            authData:   fr._hopp.authData || {},
            lineage: { kind: 'hopp-bar' }
        };
    }

    // ---- Render: the sub-tab strip ----
    function _renderHoppSubTabs(fr) {
        ensureHoppState(fr);
        var strip = el('div', { className: 'bowire-hopp-subtabs', role: 'tablist' });
        HOPP_TABS.forEach(function (t) {
            var badgeCount = 0;
            if (t.id === 'parameter') badgeCount = _activeKvCount(fr._hopp.params);
            else if (t.id === 'header') badgeCount = _activeKvCount(fr._hopp.headers);
            var isActive = fr._hopp.activeTab === t.id;
            var tab = el('button', {
                type: 'button',
                className: 'bowire-hopp-subtab' + (isActive ? ' is-active' : ''),
                'data-tab': t.id,
                role: 'tab',
                'aria-selected': isActive ? 'true' : 'false',
                onClick: function () {
                    fr._hopp.activeTab = t.id;
                    render();
                }
            },
                el('span', { className: 'bowire-hopp-subtab-label', textContent: t.label }),
                badgeCount > 0
                    ? el('span', { className: 'bowire-hopp-subtab-badge', textContent: String(badgeCount) })
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
        var table = el('div', { className: 'bowire-hopp-kv-table' });

        // Column header strip (mirrors the screenshot in the ticket).
        var head = el('div', { className: 'bowire-hopp-kv-head' },
            el('span', { className: 'bowire-hopp-kv-head-drag' }),
            el('span', { className: 'bowire-hopp-kv-head-key', textContent: keyPlaceholder }),
            el('span', { className: 'bowire-hopp-kv-head-val', textContent: valPlaceholder }),
            el('span', { className: 'bowire-hopp-kv-head-desc', textContent: descPlaceholder }),
            el('span', { className: 'bowire-hopp-kv-head-on', textContent: '' }),
            el('span', { className: 'bowire-hopp-kv-head-del', textContent: '' })
        );
        table.appendChild(head);

        // Real rows + one trailing empty add-row.
        var allRows = rows.slice();
        allRows.push({ key: '', value: '', description: '', enabled: true, _isAddRow: true });
        allRows.forEach(function (r, idx) {
            var isAdd = r._isAddRow === true;
            var row = el('div', {
                className: 'bowire-hopp-kv-row' + (isAdd ? ' is-add-row' : ''),
                draggable: !isAdd ? 'true' : undefined,
                'data-idx': String(idx)
            });
            // Drag handle (cosmetic — drag-drop reorder wired via
            // native HTML5 DnD on the row itself).
            row.appendChild(el('span', {
                className: 'bowire-hopp-kv-drag',
                title: 'Drag to reorder',
                textContent: '⋮⋮'
            }));
            row.appendChild(el('input', {
                type: 'text',
                className: 'bowire-hopp-kv-input bowire-hopp-kv-key',
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
                className: 'bowire-hopp-kv-input bowire-hopp-kv-val',
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
                className: 'bowire-hopp-kv-input bowire-hopp-kv-desc',
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
                className: 'bowire-hopp-kv-enable',
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
                className: 'bowire-hopp-kv-del',
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
        var body = el('div', { className: 'bowire-hopp-tab-body', 'data-tab': fr._hopp.activeTab });
        switch (fr._hopp.activeTab) {
            case 'parameter':
                body.appendChild(_renderHoppKvTable(fr._hopp.params, {
                    keyPlaceholder: 'Parameter',
                    valuePlaceholder: 'Value',
                    descPlaceholder: 'Description'
                }));
                break;
            case 'header':
                body.appendChild(_renderHoppKvTable(fr._hopp.headers, {
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
        var wrap = el('div', { className: 'bowire-hopp-body-wrap' });
        // Mode strip
        var modeStrip = el('div', { className: 'bowire-hopp-body-modes' });
        HOPP_BODY_MODES.forEach(function (m) {
            var btn = el('button', {
                type: 'button',
                className: 'bowire-hopp-body-mode-btn' + (fr._hopp.bodyMode === m.id ? ' is-active' : ''),
                onClick: function () {
                    fr._hopp.bodyMode = m.id;
                    render();
                }
            }, el('span', { textContent: m.label }));
            modeStrip.appendChild(btn);
        });
        wrap.appendChild(modeStrip);

        if (fr._hopp.bodyMode === 'form') {
            // Form body uses a key/value table; stored on
            // fr._hopp.formBody (lazy-init).
            if (!Array.isArray(fr._hopp.formBody)) fr._hopp.formBody = [];
            wrap.appendChild(_renderHoppKvTable(fr._hopp.formBody, {
                keyPlaceholder: 'Field',
                valuePlaceholder: 'Value',
                descPlaceholder: 'Description'
            }));
        } else if (fr._hopp.bodyMode === 'binary') {
            // Binary mode: file picker (browser-only). Snapshot the
            // chosen filename onto fr._hopp.binaryName for display
            // and stash the File reference in module-scope (not
            // persistable to localStorage; the operator re-picks
            // after reload).
            wrap.appendChild(el('input', {
                type: 'file',
                className: 'bowire-hopp-body-binary',
                onChange: function (e) {
                    var f = e.target.files && e.target.files[0];
                    fr._hopp.binaryName = f ? f.name : '';
                    fr._hopp._binaryRef = f || null;
                    render();
                }
            }));
            if (fr._hopp.binaryName) {
                wrap.appendChild(el('div', {
                    className: 'bowire-hopp-body-binary-name',
                    textContent: 'Selected: ' + fr._hopp.binaryName
                }));
            }
        } else {
            // JSON or raw: textarea editor — same as classic
            // freeform's body editor with the JSON validator
            // when in JSON mode.
            var ed = el('textarea', {
                className: 'bowire-editor bowire-hopp-body-editor',
                placeholder: fr._hopp.bodyMode === 'json'
                    ? 'JSON request body — use {{var}} for env-var substitution'
                    : 'Raw request body…',
                spellcheck: 'false'
            });
            ed.value = fr.body || '';
            ed.addEventListener('input', function () { fr.body = this.value; });
            wrap.appendChild(ed);
            if (fr._hopp.bodyMode === 'json') {
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
        var wrap = el('div', { className: 'bowire-hopp-auth-wrap' });
        var kindRow = el('div', { className: 'bowire-hopp-auth-kind-row' });
        HOPP_AUTH_KINDS.forEach(function (k) {
            kindRow.appendChild(el('button', {
                type: 'button',
                className: 'bowire-hopp-auth-kind-btn' + (fr._hopp.authKind === k.id ? ' is-active' : ''),
                textContent: k.label,
                onClick: function () {
                    fr._hopp.authKind = k.id;
                    render();
                }
            }));
        });
        wrap.appendChild(kindRow);

        var formWrap = el('div', { className: 'bowire-hopp-auth-form' });
        switch (fr._hopp.authKind) {
            case 'bearer':
                formWrap.appendChild(_authField('Token', fr._hopp.authData.token || '', function (v) {
                    fr._hopp.authData.token = v;
                }));
                break;
            case 'basic':
                formWrap.appendChild(_authField('Username', fr._hopp.authData.username || '', function (v) {
                    fr._hopp.authData.username = v;
                }));
                formWrap.appendChild(_authField('Password', fr._hopp.authData.password || '', function (v) {
                    fr._hopp.authData.password = v;
                }, 'password'));
                break;
            case 'apikey':
                formWrap.appendChild(_authField('Key', fr._hopp.authData.key || '', function (v) {
                    fr._hopp.authData.key = v;
                }));
                formWrap.appendChild(_authField('Value', fr._hopp.authData.value || '', function (v) {
                    fr._hopp.authData.value = v;
                }));
                break;
            case 'none':
            default:
                formWrap.appendChild(el('div', {
                    className: 'bowire-hopp-auth-empty',
                    textContent: 'No auth — requests go out without an Authorization header. {{var}} substitution still applies to URL + headers.'
                }));
                break;
        }
        wrap.appendChild(formWrap);
        return wrap;
    }
    function _authField(label, value, onInput, type) {
        var row = el('div', { className: 'bowire-hopp-auth-field' });
        row.appendChild(el('label', { className: 'bowire-hopp-auth-label', textContent: label }));
        row.appendChild(el('input', {
            type: type || 'text',
            className: 'bowire-hopp-auth-input',
            value: value,
            spellcheck: 'false',
            onInput: function (e) { onInput(e.target.value); }
        }));
        return row;
    }

    // ---- Pre/Post script tabs ----
    function _renderHoppScriptTab(fr, phase) {
        var wrap = el('div', { className: 'bowire-hopp-script-wrap' });
        wrap.appendChild(el('div', {
            className: 'bowire-hopp-script-hint',
            textContent: phase === 'pre'
                ? 'JavaScript that runs BEFORE the request leaves. Use ctx.request to mutate headers/body, ctx.env to read env vars, ctx.vars.captured.X = v to persist a value.'
                : 'JavaScript that runs AFTER the response lands. Inspect ctx.response.body / .status / .headers; assert with ctx.assert.ok(...); persist with ctx.vars.captured.X = v.'
        }));
        var ta = el('textarea', {
            className: 'bowire-editor bowire-hopp-script-editor',
            placeholder: phase === 'pre'
                ? '// e.g.  ctx.request.headers.set("X-Request-Id", crypto.randomUUID());'
                : '// e.g.  ctx.assert.equal(ctx.response.status, 200);\n//       ctx.vars.captured.id = JSON.parse(ctx.response.body).id;',
            spellcheck: 'false'
        });
        ta.value = phase === 'pre' ? fr._hopp.preScript : fr._hopp.postScript;
        ta.addEventListener('input', function () {
            if (phase === 'pre') fr._hopp.preScript = this.value;
            else fr._hopp.postScript = this.value;
        });
        wrap.appendChild(ta);
        return wrap;
    }

    // ---- Variables tab: env vars + scratch overrides ----
    function _renderHoppVarsTab(fr) {
        var wrap = el('div', { className: 'bowire-hopp-vars-wrap' });
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
                className: 'bowire-hopp-vars-empty',
                textContent: 'No environment variables yet. Use the Workspaces → Environments rail to create some, or type {{var}} in the URL and the prompt will offer to seed it.'
            }));
        } else {
            wrap.appendChild(el('div', {
                className: 'bowire-hopp-vars-hint',
                textContent: 'Read-only snapshot from the active environment. Edit values in Workspaces → Environments.'
            }));
            var list = el('div', { className: 'bowire-hopp-vars-list' });
            rows.forEach(function (r) {
                list.appendChild(el('div', { className: 'bowire-hopp-vars-row' },
                    el('span', { className: 'bowire-hopp-vars-key', textContent: r.key }),
                    el('span', { className: 'bowire-hopp-vars-eq', textContent: '=' }),
                    el('span', { className: 'bowire-hopp-vars-val', textContent: r.value })
                ));
            });
            wrap.appendChild(list);
        }
        // Scratch overrides for this request — let the operator pin a
        // value JUST for this tab. Stored on fr._hopp.scratchVars.
        if (!Array.isArray(fr._hopp.scratchVars)) fr._hopp.scratchVars = [];
        wrap.appendChild(el('div', {
            className: 'bowire-hopp-vars-section',
            textContent: 'Scratch overrides (this request only)'
        }));
        wrap.appendChild(_renderHoppKvTable(fr._hopp.scratchVars, {
            keyPlaceholder: 'Variable',
            valuePlaceholder: 'Value',
            descPlaceholder: 'Notes'
        }));
        return wrap;
    }

    // ---- Response renderer ----
    function _renderHoppResponse() {
        var pane = el('div', { className: 'bowire-hopp-response' });
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
    function _appendHoppInto(pane) {
        var fr = freeformRequest;
        ensureHoppState(fr);
        pane.appendChild(_renderHoppBar(fr));
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
    //   2. Headers come from fr._hopp.headers, plus an auth-derived
    //      Authorization header if authKind !== 'none'.
    //   3. The body is fr.body for JSON/raw or x-www-form-urlencoded
    //      from fr._hopp.formBody for form mode.
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
        var urlWithParams = _composeUrlWithParams(urlBase, fr._hopp.params);

        // Compose headers — KV table + auth derivation.
        var headers = _kvToObject(fr._hopp.headers);
        // Resolve {{var}} inside header values.
        Object.keys(headers).forEach(function (k) {
            try {
                if (typeof substituteVars === 'function') headers[k] = substituteVars(headers[k]);
            } catch (_) { /* leave raw */ }
        });
        if (fr._hopp.authKind === 'bearer' && fr._hopp.authData.token) {
            var tok = fr._hopp.authData.token;
            try { if (typeof substituteVars === 'function') tok = substituteVars(tok); } catch (_) { /* keep raw */ }
            headers['Authorization'] = 'Bearer ' + tok;
        } else if (fr._hopp.authKind === 'basic') {
            var u = fr._hopp.authData.username || '';
            var p = fr._hopp.authData.password || '';
            try {
                if (typeof substituteVars === 'function') {
                    u = substituteVars(u);
                    p = substituteVars(p);
                }
            } catch (_) { /* keep raw */ }
            try { headers['Authorization'] = 'Basic ' + btoa(u + ':' + p); }
            catch (_) { /* btoa fails on non-Latin chars; skip silently */ }
        } else if (fr._hopp.authKind === 'apikey' && fr._hopp.authData.key) {
            var ak = fr._hopp.authData.key.trim();
            var av = fr._hopp.authData.value || '';
            try { if (typeof substituteVars === 'function') av = substituteVars(av); } catch (_) { /* keep raw */ }
            headers[ak] = av;
        }

        // Compose body.
        var bodyStr = '';
        var verb = fr.method.toUpperCase();
        var verbHasBody = ['POST', 'PUT', 'PATCH', 'DELETE'].indexOf(verb) >= 0;
        if (verbHasBody) {
            if (fr._hopp.bodyMode === 'form') {
                var formObj = _kvToObject(fr._hopp.formBody || []);
                var formParts = [];
                Object.keys(formObj).forEach(function (k) {
                    formParts.push(encodeURIComponent(k) + '=' + encodeURIComponent(formObj[k]));
                });
                bodyStr = formParts.join('&');
                if (!headers['Content-Type']) headers['Content-Type'] = 'application/x-www-form-urlencoded';
            } else if (fr._hopp.bodyMode === 'binary') {
                // Binary upload not yet wired to /api/invoke; surface
                // a hint so the operator knows the path needs work.
                if (typeof toast === 'function') {
                    toast('Binary uploads via the Hopp bar are not yet wired through /api/invoke — coming in a follow-up', 'info');
                }
                bodyStr = '';
            } else {
                var rawBody = fr.body || '';
                try { if (typeof substituteVars === 'function') rawBody = substituteVars(rawBody); } catch (_) { /* keep raw */ }
                bodyStr = rawBody;
                if (fr._hopp.bodyMode === 'json' && !headers['Content-Type']) {
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
            log: function () { try { console.log.apply(console, ['[hopp-pre]'].concat([].slice.call(arguments))); } catch (_) {} },
            assert: { ok: function (v, m) { if (!v) throw new Error(m || 'assert.ok failed'); } }
        };
        if (fr._hopp.preScript && fr._hopp.preScript.trim()) {
            try {
                // eslint-disable-next-line no-new-func
                new Function('ctx', fr._hopp.preScript)(preCtx);
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
        if (typeof markJobActive === 'function') markJobActive('hopp', verb);
        render();

        var fullName = verb + ' ' + urlWithParams;
        if (typeof addConsoleEntry === 'function') {
            addConsoleEntry({ type: 'request', method: fullName, body: verbHasBody ? bodyStr : '' });
        }

        try {
            var url = config.prefix + '/api/invoke?serverUrl=' + encodeURIComponent(urlWithParams);
            var resp = await fetch(url, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({
                    service: '',
                    method: verb,
                    messages: [verbHasBody ? bodyStr : '{}'],
                    metadata: Object.keys(headers).length > 0 ? headers : null,
                    protocol: 'rest'
                })
            });
            var result = await resp.json();
            if (result.title) {
                responseError = result;
                if (typeof addConsoleEntry === 'function') {
                    addConsoleEntry({ type: 'error', method: fullName, status: 'Error',
                        body: typeof richErrorDetail === 'function'
                            ? richErrorDetail(result, 'Request failed') : (result.detail || result.title) });
                }
            } else {
                responseData = result.response;
                if (typeof captureResponse === 'function' && result.response) captureResponse(result.response);
                if (typeof addConsoleEntry === 'function') {
                    addConsoleEntry({ type: 'response', method: fullName,
                        status: result.status, body: result.response });
                }

                // Post-script — runs AFTER the wire response lands.
                if (fr._hopp.postScript && fr._hopp.postScript.trim()) {
                    var postCtx = {
                        response: {
                            status: result.status,
                            body: result.response,
                            durationMs: result.duration_ms || 0
                        },
                        env: preCtx.env,
                        vars: preCtx.vars,
                        log: function () { try { console.log.apply(console, ['[hopp-post]'].concat([].slice.call(arguments))); } catch (_) {} },
                        assert: preCtx.assert
                    };
                    try {
                        // eslint-disable-next-line no-new-func
                        new Function('ctx', fr._hopp.postScript)(postCtx);
                    } catch (e) {
                        if (typeof toast === 'function') toast('Post-script failed: ' + e.message, 'error');
                    }
                }
            }
        } catch (e) {
            responseError = e.message;
            if (typeof addConsoleEntry === 'function') {
                addConsoleEntry({ type: 'error', method: fullName, status: 'NetworkError', body: e.message });
            }
        }

        isExecuting = false;
        if (typeof markJobDone === 'function') markJobDone('hopp', verb);
        render();
    }

    // ---- Outside-click close for menus ----
    //
    // Wire a document-level click that closes the method dropdown and
    // the send caret menu when the user clicks outside them. Mounted
    // once at init time; the prologue's IIFE only runs this fragment
    // once per page load so it's safe to attach unconditionally.
    document.addEventListener('click', function (e) {
        var changed = false;
        if (hoppMethodMenuOpen) {
            var mwrap = e.target.closest && e.target.closest('.bowire-hopp-method-wrap');
            if (!mwrap) { hoppMethodMenuOpen = false; changed = true; }
        }
        if (hoppSendMenuOpen) {
            var swrap = e.target.closest && e.target.closest('.bowire-hopp-send-wrap');
            if (!swrap) { hoppSendMenuOpen = false; changed = true; }
        }
        if (changed) render();
    });

    // ---- Ctrl+L global shortcut (Phase F) ----
    //
    // Wired in init.js's keydown dispatcher — see the case for 'l'
    // there. The handler calls startHoppRequest() which sets up the
    // freeformRequest with _hopp markers and renders.
