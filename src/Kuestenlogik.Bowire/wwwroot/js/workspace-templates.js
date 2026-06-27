    // #165 — Workspace templates on create.
    //
    // The first workspace ships empty — no URLs, no env vars, no
    // collections — and new operators face a tabula rasa with no
    // scent of what to do next. Templates seed a fresh workspace with
    // a small, realistic starting set keyed to the workload the
    // operator is here for (REST API testing, gRPC services, mock
    // server build, multi-protocol smoke test).
    //
    // Each template's apply(wsId) writes per-workspace localStorage
    // entries directly under the new workspace's wsKey prefix. The
    // calling create flow follows up with window.location.reload()
    // so the in-memory state (serverUrls, collectionsList, globals)
    // hydrates from the freshly-templated buckets. The 'empty'
    // template is the no-op default and skips the reload.

    var BOWIRE_WORKSPACE_TEMPLATES = [
        {
            id: 'empty',
            label: 'Empty',
            description: 'No URLs, no collections, no env vars. Start from scratch.',
            icon: 'plus',
            apply: function () { /* no-op */ }
        },
        {
            id: 'rest',
            label: 'REST API testing',
            description: 'Petstore (well-known public OpenAPI 2.0 endpoint) — Bowire auto-discovers the Pet / Store / User services on first connect. Plus a starter collection with two ready-to-invoke calls.',
            icon: 'globe',
            apply: function (wsId) {
                // Swagger Petstore exposes /v2/swagger.json — Bowire's
                // REST plugin's discovery surface picks that up
                // automatically, so the workspace lands with a
                // populated services tree instead of the empty
                // "no schema found" state httpbin.org produced
                // (httpbin doesn't publish OpenAPI / Swagger, so
                // discovery returned 0 services from it).
                _templateWriteUrls(wsId, ['https://petstore.swagger.io/v2']);
                _templateWriteGlobals(wsId, {
                    baseUrl: 'https://petstore.swagger.io/v2',
                    apiToken: 'special-key'    // documented Petstore token
                });
                _templateWriteCollections(wsId, [{
                    id: 'col_rest_starter',
                    name: 'REST starter',
                    createdAt: Date.now(),
                    items: [
                        {
                            id: 'item_rest_get',
                            protocol: 'rest',
                            service: 'pet',
                            method: 'GET /pet/findByStatus',
                            methodType: 'Unary',
                            body: '',
                            messages: [],
                            metadata: null,
                            serverUrl: 'https://petstore.swagger.io/v2'
                        },
                        {
                            id: 'item_rest_post',
                            protocol: 'rest',
                            service: 'pet',
                            method: 'POST /pet',
                            methodType: 'Unary',
                            body: '{"name":"doggie","photoUrls":[],"status":"available"}',
                            messages: ['{"name":"doggie","photoUrls":[],"status":"available"}'],
                            metadata: null,
                            serverUrl: 'https://petstore.swagger.io/v2'
                        }
                    ]
                }]);
            }
        },
        {
            id: 'grpc',
            label: 'gRPC services',
            description: 'A gRPC URL prefix pointing at grpcb.in (server-reflection enabled). Without reflection or a .proto upload, the services tree stays empty — the URL is a placeholder for your own gRPC backend.',
            icon: 'connect',
            apply: function (wsId) {
                // grpcb.in (port 9001 grpc, 443 grpcs) is a public
                // reflection-enabled gRPC test backend maintained by
                // the gRPC community. Previous template pointed at
                // grpc.postman-echo.com which returns HTTP 502 / is
                // not actually a gRPC endpoint; swapped to grpcb.in
                // so the discover tree populates on first connect
                // (Bowire's gRPC plugin walks server reflection
                // automatically when present).
                _templateWriteUrls(wsId, ['grpcs@grpcb.in:443']);
                _templateWriteGlobals(wsId, {
                    service: 'YourService',
                    method: 'YourMethod'
                });
                _templateWriteCollections(wsId, [{
                    id: 'col_grpc_starter',
                    name: 'gRPC starter',
                    createdAt: Date.now(),
                    items: []
                }]);
            }
        },
        {
            id: 'mock',
            label: 'Mock server build',
            description: 'Petstore (discovery-enabled) plus an empty Mock targets collection ready to capture recordings as mock fixtures. Record-then-replay workflow ready to go.',
            icon: 'recording',
            apply: function (wsId) {
                // Postman-echo is a generic-HTTP-echo service that
                // exposes no service catalogue — the Discover rail
                // landed at 0 services / 0 methods. Petstore exposes
                // OpenAPI 2.0 at /v2/swagger.json so the workspace
                // arrives with a populated Pet / Store / User tree
                // ready to record against.
                _templateWriteUrls(wsId, ['https://petstore.swagger.io/v2']);
                _templateWriteCollections(wsId, [{
                    id: 'col_mock_targets',
                    name: 'Mock targets',
                    createdAt: Date.now(),
                    items: []
                }]);
            }
        },
        {
            id: 'multi',
            label: 'Multi-protocol smoke test',
            description: 'Petstore REST + a public WebSocket echo + grpcb.in for gRPC reflection. Three different wire formats in one workspace — ready for cross-protocol coverage runs.',
            icon: 'layers',
            apply: function (wsId) {
                _templateWriteUrls(wsId, [
                    'https://petstore.swagger.io/v2',
                    'wss://ws.postman-echo.com/raw',
                    'grpcs@grpcb.in:443'
                ]);
                _templateWriteGlobals(wsId, {
                    baseUrl: 'https://petstore.swagger.io/v2',
                    wsUrl: 'wss://ws.postman-echo.com/raw'
                });
            }
        }
    ];

    // Shared writers — every template apply() lands data under the
    // *new* workspace's wsKey prefix, not the currently-active one.
    // wsKey() resolves off activeWorkspaceId; since createWorkspace
    // sets activeWorkspaceId = newId immediately, wsKey() points at
    // the right bucket. Explicit prefix form below avoids the
    // dependency for robustness in case callers ever re-order things.
    function _templateKey(wsId, baseKey) {
        if (!wsId) return baseKey;
        return 'bowire_ws_' + wsId + '_' + String(baseKey).replace(/^bowire_/, '');
    }
    function _templateWriteUrls(wsId, urls) {
        try { localStorage.setItem(_templateKey(wsId, 'bowire_server_urls'), JSON.stringify(urls)); }
        catch { /* quota / disabled */ }
    }
    function _templateWriteGlobals(wsId, vars) {
        try { localStorage.setItem(_templateKey(wsId, 'bowire_global_vars'), JSON.stringify(vars)); }
        catch { /* quota / disabled */ }
    }
    function _templateWriteCollections(wsId, list) {
        try { localStorage.setItem(_templateKey(wsId, 'bowire_collections'), JSON.stringify(list)); }
        catch { /* quota / disabled */ }
    }

    function getWorkspaceTemplate(id) {
        // Built-ins win when both lists carry the same id (they
        // shouldn't, but defensive). User-defined templates fall
        // back to the empty default if their id is no longer in
        // localStorage (e.g. deleted while the create-dialog was
        // open).
        for (var i = 0; i < BOWIRE_WORKSPACE_TEMPLATES.length; i++) {
            if (BOWIRE_WORKSPACE_TEMPLATES[i].id === id) return BOWIRE_WORKSPACE_TEMPLATES[i];
        }
        var user = _readUserTemplates();
        for (var j = 0; j < user.length; j++) {
            if (user[j].id === id) return _wrapUserTemplate(user[j]);
        }
        return BOWIRE_WORKSPACE_TEMPLATES[0];
    }

    // ---- User-defined workspace templates (#242) ----
    //
    // Operators can snapshot a populated workspace as a reusable
    // template. The snapshot freezes the current localStorage state
    // under the workspace's wsKey-prefixed buckets and stores it
    // cross-workspace (NOT workspace-scoped — templates outlive
    // their source workspace, so deleting the source must NOT take
    // its templates down).
    //
    // Storage shape:
    //   bowire_user_workspace_templates → [{
    //     id: 'tpl_<random>',
    //     name: 'Harbor research',
    //     description: '…',
    //     icon: 'globe',
    //     createdAt: <epoch>,
    //     sourceWorkspaceId: 'ws_<id>',     // informational; not enforced
    //     payload: {
    //       urls:        [...],
    //       urlMeta:     { ... },
    //       envs:        [...],
    //       activeEnvId: 'env_…' | '',
    //       globals:     { ... },
    //       collections: [...]
    //     }
    //   }, ...]
    //
    // Phase 1 scope per #242: URLs (+ meta), envs, collections, global
    // vars. Plugin pins (workspace.pluginPins) live on the workspace
    // record itself rather than under wsKey-prefixed storage; folded
    // into the payload by copying the pluginPins field off the
    // workspace entry at snapshot time.
    var USER_TEMPLATES_KEY = 'bowire_user_workspace_templates';

    var TEMPLATE_BUCKETS = [
        { key: 'bowire_server_urls', field: 'urls', def: '[]' },
        { key: 'bowire_url_meta',    field: 'urlMeta', def: '{}' },
        { key: 'bowire_environments', field: 'envs', def: '[]' },
        { key: 'bowire_active_env',  field: 'activeEnvId', def: '' },
        { key: 'bowire_global_vars', field: 'globals', def: '{}' },
        { key: 'bowire_collections', field: 'collections', def: '[]' }
    ];

    function _readUserTemplates() {
        try {
            var raw = localStorage.getItem(USER_TEMPLATES_KEY);
            if (!raw) return [];
            var arr = JSON.parse(raw);
            return Array.isArray(arr) ? arr : [];
        } catch { return []; }
    }

    function _writeUserTemplates(list) {
        try { localStorage.setItem(USER_TEMPLATES_KEY, JSON.stringify(list)); }
        catch (e) { try { markSaveFailed('workspace templates', e); } catch { /* ignore */ } }
    }

    function _wrapUserTemplate(stored) {
        // Adapt a stored user-template record to the same shape the
        // built-in templates use — { id, label, description, icon,
        // apply(wsId) }. The apply function replays the snapshotted
        // payload into the new workspace's wsKey buckets, identical
        // to how the built-in templates write their values.
        return {
            id: stored.id,
            label: stored.name || '(unnamed template)',
            description: stored.description || '',
            icon: stored.icon || 'layers',
            isUser: true,
            createdAt: stored.createdAt || 0,
            apply: function (wsId) {
                var p = stored.payload || {};
                TEMPLATE_BUCKETS.forEach(function (b) {
                    if (!(b.field in p)) return;
                    try {
                        var v = p[b.field];
                        var serialised = (typeof v === 'string') ? v : JSON.stringify(v);
                        localStorage.setItem(_templateKey(wsId, b.key), serialised);
                    } catch { /* quota / disabled */ }
                });
                // Plugin pins live on the workspace record (not on
                // its own wsKey bucket). Patch the workspaces array
                // in place if the snapshot carried any.
                if (p.pluginPins && Object.keys(p.pluginPins).length > 0) {
                    try {
                        var wsRaw = localStorage.getItem('bowire_workspaces');
                        if (wsRaw) {
                            var list = JSON.parse(wsRaw);
                            if (Array.isArray(list)) {
                                for (var i = 0; i < list.length; i++) {
                                    if (list[i].id === wsId) {
                                        list[i].pluginPins = p.pluginPins;
                                        break;
                                    }
                                }
                                localStorage.setItem('bowire_workspaces', JSON.stringify(list));
                            }
                        }
                    } catch { /* ignore */ }
                }
            }
        };
    }

    function listUserTemplates() {
        return _readUserTemplates().map(_wrapUserTemplate);
    }

    // Snapshot ANY workspace as a reusable template. wsId arg lets
    // callers snapshot inactive workspaces from the sidebar / overview
    // row tools — the storage is per-workspace (bowire_ws_<id>_*) so
    // the read path is identical regardless of active state. The
    // earlier 'must be active' restriction was an artifact of the
    // first implementation reading from the in-memory state of the
    // active workspace, NOT a fundamental constraint.
    //
    // Returns the new template id on success so the caller can
    // highlight it in the template manager.
    function saveWorkspaceAsTemplate(wsId, name, description, icon) {
        var safeName = String(name || '').trim();
        if (!safeName) throw new Error('Template name is required.');
        if (!wsId) throw new Error('Workspace id is required.');

        var payload = {};
        TEMPLATE_BUCKETS.forEach(function (b) {
            try {
                var raw = localStorage.getItem('bowire_ws_' + wsId + '_' + b.key.replace(/^bowire_/, ''));
                if (raw === null) raw = b.def;
                if (b.def === '[]' || b.def === '{}') {
                    // JSON-shaped fields: parse so the template stays
                    // structured (callers may want to introspect).
                    try { payload[b.field] = JSON.parse(raw); }
                    catch { payload[b.field] = JSON.parse(b.def); }
                } else {
                    payload[b.field] = raw;
                }
            } catch { /* skip individual bucket on read error */ }
        });

        // Pin the source workspace's pluginPins (if any) into the
        // snapshot so the new workspace inherits the same plugin
        // expectations.
        try {
            var wsRaw = localStorage.getItem('bowire_workspaces');
            if (wsRaw) {
                var list = JSON.parse(wsRaw);
                if (Array.isArray(list)) {
                    for (var i = 0; i < list.length; i++) {
                        if (list[i].id === wsId && list[i].pluginPins
                            && typeof list[i].pluginPins === 'object') {
                            payload.pluginPins = JSON.parse(JSON.stringify(list[i].pluginPins));
                            break;
                        }
                    }
                }
            }
        } catch { /* ignore */ }

        var record = {
            id: 'tpl_' + Math.random().toString(36).slice(2, 10),
            name: safeName,
            description: String(description || '').trim(),
            icon: icon || 'layers',
            createdAt: Date.now(),
            sourceWorkspaceId: wsId,
            payload: payload
        };
        var all = _readUserTemplates();
        all.push(record);
        _writeUserTemplates(all);
        return record.id;
    }

    // Backwards-compat wrapper — existing call sites use
    // saveCurrentWorkspaceAsTemplate(name, ...). Delegate to the
    // parameterised function with the active workspace id.
    function saveCurrentWorkspaceAsTemplate(name, description, icon) {
        var wsId = (typeof activeWorkspaceId !== 'undefined') ? activeWorkspaceId : null;
        if (!wsId) throw new Error('No active workspace to snapshot.');
        return saveWorkspaceAsTemplate(wsId, name, description, icon);
    }

    function deleteUserTemplate(id) {
        var all = _readUserTemplates().filter(function (t) { return t.id !== id; });
        _writeUserTemplates(all);
    }

    function renameUserTemplate(id, newName, newDescription) {
        var all = _readUserTemplates();
        var changed = false;
        for (var i = 0; i < all.length; i++) {
            if (all[i].id === id) {
                if (typeof newName === 'string') all[i].name = newName.trim() || all[i].name;
                if (typeof newDescription === 'string') all[i].description = newDescription.trim();
                changed = true;
                break;
            }
        }
        if (changed) _writeUserTemplates(all);
    }

    // Persisted last-picked template so the dialog defaults to
    // whatever the operator chose last — matches "muscle memory"
    // expectation for repeat workspace creation (e.g. you set up
    // five staging workspaces and they're all REST).
    function _readLastTemplate() {
        try { return localStorage.getItem('bowire_workspace_last_template') || 'empty'; }
        catch { return 'empty'; }
    }
    function _persistLastTemplate(id) {
        try { localStorage.setItem('bowire_workspace_last_template', id); }
        catch { /* ignore */ }
    }

    // Rich create-workspace dialog — name input + template radio list.
    // Drop-in replacement for the bowirePrompt('New workspace name')
    // call. onCreated(ws, templateId) fires on confirm with the newly
    // created (and templated) workspace.
    function openCreateWorkspaceDialog(onCreated) {
        var existing = document.querySelector('.bowire-confirm-overlay');
        if (existing) existing.remove();

        var nameInput = el('input', {
            type: 'text',
            id: 'bowire-ws-create-name',
            className: 'bowire-prompt-input',
            placeholder: 'e.g. Payments — staging',
            'aria-label': 'Workspace name',
            'data-bowire-no-vars-chip': '1',
            'data-bowire-no-vars-ac': '1'
        });

        var selectedTemplateId = _readLastTemplate();
        var templateFilter = '';

        // 'Empty' (start from scratch) lives ABOVE the templates list,
        // visually separated by a divider. Conceptually it's still a
        // template (no-op apply) but the operator's mental model
        // splits 'no template' from 'pick a template' — the layout
        // matches the model.
        var startFromScratchWrap = el('div', { className: 'bowire-ws-template-scratch-wrap' });
        var emptyTpl = BOWIRE_WORKSPACE_TEMPLATES.find(function (t) { return t.id === 'empty'; });
        if (emptyTpl) {
            var scratchRow = el('label', {
                className: 'bowire-ws-template-scratch'
                    + (selectedTemplateId === emptyTpl.id ? ' selected' : ''),
                'data-tpl-id': emptyTpl.id
            });
            scratchRow.appendChild(el('input', {
                type: 'radio',
                name: 'bowire-ws-template',
                value: emptyTpl.id,
                checked: selectedTemplateId === emptyTpl.id ? 'checked' : null,
                className: 'bowire-ws-template-radio',
                onChange: function () {
                    selectedTemplateId = emptyTpl.id;
                    _syncSelectionClasses();
                }
            }));
            scratchRow.appendChild(el('span', {
                className: 'bowire-ws-template-icon',
                innerHTML: svgIcon(emptyTpl.icon || 'plus')
            }));
            var scratchInfo = el('div', { className: 'bowire-ws-template-info' });
            scratchInfo.appendChild(el('div', {
                className: 'bowire-ws-template-label',
                textContent: 'Start from scratch'
            }));
            scratchInfo.appendChild(el('div', {
                className: 'bowire-ws-template-desc',
                textContent: 'No URLs, no collections, no env vars — empty workspace.'
            }));
            scratchRow.appendChild(scratchInfo);
            startFromScratchWrap.appendChild(scratchRow);
        }

        // Filter / search input above the templates list. Updates the
        // visible rows on every keystroke (filter by label substring).
        var templatesHeader = el('div', { className: 'bowire-ws-templates-header' });
        templatesHeader.appendChild(el('div', {
            className: 'bowire-ws-templates-title',
            textContent: 'Or pick a template'
        }));
        var filterInput = el('input', {
            type: 'text',
            className: 'bowire-ws-templates-filter',
            placeholder: 'Search templates…',
            'aria-label': 'Filter templates',
            'data-bowire-no-vars-chip': '1',
            'data-bowire-no-vars-ac': '1',
            onInput: function (e) {
                templateFilter = String(e.target.value || '').toLowerCase();
                refreshList();
            }
        });
        templatesHeader.appendChild(filterInput);

        var templateList = el('div', { id: 'bowire-ws-create-templates', className: 'bowire-ws-template-list', role: 'radiogroup', 'aria-label': 'Start from template' });

        function _syncSelectionClasses() {
            var rows = document.querySelectorAll('.bowire-ws-template-row, .bowire-ws-template-scratch');
            for (var i = 0; i < rows.length; i++) {
                rows[i].classList.toggle('selected', rows[i].dataset.tplId === selectedTemplateId);
            }
        }

        // Helper that appends one row to the list. Used for built-ins
        // and user templates alike — user templates additionally get
        // a trailing delete button.
        function appendRow(tpl) {
            var row = el('label', {
                className: 'bowire-ws-template-row' + (tpl.id === selectedTemplateId ? ' selected' : ''),
                'data-tpl-id': tpl.id
            });
            var radio = el('input', {
                type: 'radio',
                name: 'bowire-ws-template',
                value: tpl.id,
                checked: tpl.id === selectedTemplateId ? 'checked' : null,
                className: 'bowire-ws-template-radio',
                onChange: function () {
                    selectedTemplateId = tpl.id;
                    _syncSelectionClasses();
                }
            });
            row.appendChild(radio);
            row.appendChild(el('span', { className: 'bowire-ws-template-icon', innerHTML: svgIcon(tpl.icon) }));
            var info = el('div', { className: 'bowire-ws-template-info' });
            info.appendChild(el('div', { className: 'bowire-ws-template-label', textContent: tpl.label }));
            info.appendChild(el('div', { className: 'bowire-ws-template-desc', textContent: tpl.description }));
            row.appendChild(info);
            if (tpl.isUser) {
                row.appendChild(el('button', {
                    type: 'button',
                    className: 'bowire-ws-template-delete-btn',
                    title: 'Delete this user template',
                    'aria-label': 'Delete template ' + tpl.label,
                    innerHTML: svgIcon('trash'),
                    onClick: function (ev) {
                        // Prevent the label-click from selecting the
                        // about-to-be-deleted row.
                        ev.preventDefault(); ev.stopPropagation();
                        // Use the existing toast/confirm primitive if
                        // present; otherwise direct delete is fine.
                        var ok = (typeof confirm === 'function')
                            ? confirm('Delete template "' + tpl.label + '"? This cannot be undone.')
                            : true;
                        if (!ok) return;
                        deleteUserTemplate(tpl.id);
                        // If the row being deleted was the selection,
                        // fall back to 'empty' so the create button
                        // doesn't reference a vanished template id.
                        if (selectedTemplateId === tpl.id) selectedTemplateId = 'empty';
                        // Repaint the list inline rather than tearing
                        // the dialog down + reopening.
                        refreshList();
                    }
                }));
            }
            templateList.appendChild(row);
        }

        function refreshList() {
            templateList.innerHTML = '';
            // 'empty' lives outside the list (rendered above as the
            // 'Start from scratch' option). Built-ins follow, then
            // user templates — single continuous list, no divider.
            // Filter input narrows by label substring + description.
            var allTpls = BOWIRE_WORKSPACE_TEMPLATES.filter(function (t) {
                return t.id !== 'empty';
            }).concat(listUserTemplates());
            if (templateFilter) {
                allTpls = allTpls.filter(function (t) {
                    var lbl = (t.label || '').toLowerCase();
                    var desc = (t.description || '').toLowerCase();
                    return lbl.indexOf(templateFilter) >= 0 || desc.indexOf(templateFilter) >= 0;
                });
            }
            if (allTpls.length === 0) {
                templateList.appendChild(el('div', {
                    className: 'bowire-ws-templates-empty',
                    textContent: templateFilter
                        ? 'No templates match "' + templateFilter + '".'
                        : 'No templates yet. Save the current workspace as a template to start building your library.'
                }));
                return;
            }
            allTpls.forEach(appendRow);
        }
        refreshList();

        function commit() {
            var name = String(nameInput.value || '').trim();
            if (!name) {
                nameInput.focus();
                nameInput.classList.add('bowire-prompt-input-error');
                setTimeout(function () { nameInput.classList.remove('bowire-prompt-input-error'); }, 600);
                return;
            }
            // createWorkspace returns null when the operator-typed
            // name collides with an existing workspace. Keep the
            // dialog open + flash the input so they can edit the name
            // — the createWorkspace path already toasted the reason.
            var ws = createWorkspace(name);
            if (!ws) {
                nameInput.focus();
                nameInput.classList.add('bowire-prompt-input-error');
                setTimeout(function () { nameInput.classList.remove('bowire-prompt-input-error'); }, 600);
                return;
            }
            overlay.remove();
            var tpl = getWorkspaceTemplate(selectedTemplateId);
            _persistLastTemplate(selectedTemplateId);
            try { tpl.apply(ws.id); } catch { /* template seed should never crash creation */ }
            // #194 — symmetric to workspace-delete: record the create
            // so Ctrl/Cmd+Z lifts the workspace into trash, and Redo
            // restores it. Done after the template seed so the trash
            // snapshot captured on undo includes the template-seeded
            // buckets too — undo→redo round-trips don't lose them.
            if (typeof recordAction === 'function') {
                recordAction({
                    kind: 'workspace-create',
                    rail: 'workspaces',
                    title: 'Created workspace "' + ws.name + '"',
                    undoSpec: { workspaceId: ws.id }
                });
            }
            if (typeof onCreated === 'function') onCreated(ws, selectedTemplateId);
            // Templates write directly to localStorage under the new
            // workspace's wsKey prefix; an in-place render() wouldn't
            // pick them up because serverUrls / collectionsList /
            // globals live in module-level variables loaded once at
            // boot. Reloading the page rehydrates everything from the
            // freshly-seeded buckets. Empty template skips the reload
            // since there's nothing to rehydrate.
            if (selectedTemplateId !== 'empty') {
                try { window.location.reload(); } catch { /* embedded host */ }
            } else if (typeof render === 'function') {
                render();
            }
        }

        var confirmBtn = el('button', {
            id: 'bowire-ws-create-submit',
            className: 'bowire-confirm-btn',
            textContent: 'Create',
            onClick: commit
        });
        var cancelBtn = el('button', {
            className: 'bowire-confirm-btn cancel',
            textContent: 'Cancel',
            onClick: function () { overlay.remove(); }
        });

        nameInput.addEventListener('keydown', function (e) {
            if (e.key === 'Enter') { e.preventDefault(); commit(); }
            else if (e.key === 'Escape') { e.preventDefault(); overlay.remove(); }
        });

        var dialog = el('div', {
            className: 'bowire-confirm-dialog bowire-ws-create-dialog',
            role: 'dialog',
            'aria-modal': 'true',
            'aria-labelledby': 'bowire-ws-create-title'
        },
            el('div', { id: 'bowire-ws-create-title', className: 'bowire-confirm-title', textContent: 'Create workspace' }),
            el('div', { className: 'bowire-confirm-message', textContent: 'Name your workspace, then start from scratch or pick a template.' }),
            nameInput,
            startFromScratchWrap,
            templatesHeader,
            templateList,
            el('div', { className: 'bowire-confirm-actions' }, cancelBtn, confirmBtn)
        );

        var overlay = el('div', {
            className: 'bowire-confirm-overlay',
            onClick: function (e) { if (e.target === overlay) overlay.remove(); }
        }, dialog);
        document.body.appendChild(overlay);
        setTimeout(function () { nameInput.focus(); }, 0);
    }
