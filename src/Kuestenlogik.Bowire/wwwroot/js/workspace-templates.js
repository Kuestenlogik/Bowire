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
            description: 'A sample URL (httpbin.org), `baseUrl` + `apiToken` env vars, and a starter collection with GET + POST stubs.',
            icon: 'globe',
            apply: function (wsId) {
                _templateWriteUrls(wsId, ['https://httpbin.org']);
                _templateWriteGlobals(wsId, {
                    baseUrl: 'https://httpbin.org',
                    apiToken: 'changeme'
                });
                _templateWriteCollections(wsId, [{
                    id: 'col_rest_starter',
                    name: 'REST starter',
                    createdAt: Date.now(),
                    items: [
                        {
                            id: 'item_rest_get',
                            protocol: 'rest',
                            service: 'httpbin',
                            method: 'GET /get',
                            methodType: 'Unary',
                            body: '',
                            messages: [],
                            metadata: null,
                            serverUrl: 'https://httpbin.org'
                        },
                        {
                            id: 'item_rest_post',
                            protocol: 'rest',
                            service: 'httpbin',
                            method: 'POST /post',
                            methodType: 'Unary',
                            body: '{"hello":"world"}',
                            messages: ['{"hello":"world"}'],
                            metadata: null,
                            serverUrl: 'https://httpbin.org'
                        }
                    ]
                }]);
            }
        },
        {
            id: 'grpc',
            label: 'gRPC services',
            description: 'A gRPC URL prefix ready for a `.proto` upload, plus a `service` + `method` placeholder globals.',
            icon: 'connect',
            apply: function (wsId) {
                _templateWriteUrls(wsId, ['grpc@grpc.postman-echo.com:443']);
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
            description: 'A starter URL pointed at postman-echo, plus an empty collection ready to capture recordings as mock fixtures.',
            icon: 'recording',
            apply: function (wsId) {
                _templateWriteUrls(wsId, ['https://www.postman-echo.com']);
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
            description: 'REST + WebSocket + gRPC URLs in one workspace, ready for cross-protocol coverage runs.',
            icon: 'layers',
            apply: function (wsId) {
                _templateWriteUrls(wsId, [
                    'https://httpbin.org',
                    'ws://echo.websocket.org',
                    'grpc@grpc.postman-echo.com:443'
                ]);
                _templateWriteGlobals(wsId, {
                    baseUrl: 'https://httpbin.org',
                    wsUrl: 'ws://echo.websocket.org'
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
        for (var i = 0; i < BOWIRE_WORKSPACE_TEMPLATES.length; i++) {
            if (BOWIRE_WORKSPACE_TEMPLATES[i].id === id) return BOWIRE_WORKSPACE_TEMPLATES[i];
        }
        return BOWIRE_WORKSPACE_TEMPLATES[0];
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
            className: 'bowire-prompt-input',
            placeholder: 'e.g. Payments — staging',
            'aria-label': 'Workspace name',
            'data-bowire-no-vars-chip': '1',
            'data-bowire-no-vars-ac': '1'
        });

        var selectedTemplateId = _readLastTemplate();

        var templateList = el('div', { className: 'bowire-ws-template-list', role: 'radiogroup', 'aria-label': 'Start from template' });
        BOWIRE_WORKSPACE_TEMPLATES.forEach(function (tpl) {
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
                    var rows = templateList.querySelectorAll('.bowire-ws-template-row');
                    for (var i = 0; i < rows.length; i++) {
                        rows[i].classList.toggle('selected', rows[i].dataset.tplId === tpl.id);
                    }
                }
            });
            row.appendChild(radio);
            row.appendChild(el('span', { className: 'bowire-ws-template-icon', innerHTML: svgIcon(tpl.icon) }));
            var info = el('div', { className: 'bowire-ws-template-info' });
            info.appendChild(el('div', { className: 'bowire-ws-template-label', textContent: tpl.label }));
            info.appendChild(el('div', { className: 'bowire-ws-template-desc', textContent: tpl.description }));
            row.appendChild(info);
            templateList.appendChild(row);
        });

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
            el('div', { className: 'bowire-confirm-message', textContent: 'Name your workspace and pick a starting template.' }),
            nameInput,
            el('div', { className: 'bowire-ws-template-heading', textContent: 'Start from template' }),
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
