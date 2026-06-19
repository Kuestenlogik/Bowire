    // ---- Parallel Sessions (#132 minimal) ----
    //
    // Live state for the most recently launched parallel-sessions run.
    // Surfaced as a toast at start + finish; per-session rows render
    // under the source toolbar while a run is in flight. The state
    // object is shared between collection + recording sessions because
    // the only fields that differ are `kind` ('collection'/'recording')
    // and `sourceName`; the per-session shape is identical.
    var parallelSessionsState = null;

    function _promptAndRunParallel(kind, sourceId, sourceName) {
        bowirePrompt('How many parallel sessions?', {
            title: 'Run in parallel sessions',
            placeholder: 'e.g. 5',
            confirmText: 'Run'
        }).then(function (raw) {
            if (raw == null) return;
            var n = parseInt(String(raw).trim(), 10);
            if (!n || n < 1) return;
            if (n > 50) n = 50; // safety cap for the browser-side runner
            _runParallelSessions(kind, sourceId, sourceName, n);
        });
    }

    async function _runParallelSessions(kind, sourceId, sourceName, sessionCount) {
        var sessions = [];
        for (var i = 0; i < sessionCount; i++) {
            sessions.push({ index: i, status: 'running', pass: null, durationMs: 0, error: null });
        }
        parallelSessionsState = {
            kind: kind,
            sourceId: sourceId,
            sourceName: sourceName,
            sessionCount: sessionCount,
            sessions: sessions,
            startedAt: 0,  // wallclock unavailable in the JS bundle's
                           // sandbox; the toast carries 'just now' instead
            status: 'running'
        };
        render();
        toast('Starting ' + sessionCount + ' parallel sessions on ' + sourceName, 'info');

        var runner = (kind === 'collection') ? _runCollectionSession : _runRecordingSession;
        var source = (kind === 'collection')
            ? collectionsList.find(function (c) { return c.id === sourceId; })
            : recordingsList.find(function (r) { return r.id === sourceId; });
        if (!source) {
            toast('Source ' + kind + ' not found', 'error');
            parallelSessionsState = null;
            render();
            return;
        }

        var startMs = performance.now();
        var promises = sessions.map(function (s) {
            return runner(source).then(function (result) {
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

    // One isolated collection session — sequential through its items.
    // Stateless: takes the collection object, returns aggregate result.
    async function _runCollectionSession(col) {
        var startMs = performance.now();
        var results = [];
        var allPass = true;
        for (var i = 0; i < col.items.length; i++) {
            try {
                var r = await runCollectionItem(col.items[i]);
                results.push(r);
                if (!r.pass) allPass = false;
            } catch (e) {
                allPass = false;
                results.push({ pass: false, status: 'NetworkError', error: e.message });
            }
        }
        return { pass: allPass, results: results, durationMs: performance.now() - startMs };
    }

    // One isolated recording session — sequential through its steps.
    async function _runRecordingSession(rec) {
        var startMs = performance.now();
        var results = [];
        var allPass = true;
        for (var i = 0; i < rec.steps.length; i++) {
            try {
                var r = await replaySingleStep(rec.steps[i]);
                results.push(r);
                if (!r.pass) allPass = false;
            } catch (e) {
                allPass = false;
                results.push({ pass: false, status: 'NetworkError', error: e.message });
            }
        }
        return { pass: allPass, results: results, durationMs: performance.now() - startMs };
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

    function runCollectionItem(item) {
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

        var serverUrl = item.serverUrl || (serverUrls.length > 0 ? serverUrls[0] : '');
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
                    toast('Collection deleted', 'success', {
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
