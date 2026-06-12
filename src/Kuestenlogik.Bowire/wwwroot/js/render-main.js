    // ---- Freeform Request Builder ----
    function renderFreeformRequestBuilder() {
        var fr = freeformRequest;
        var pane = el('div', { className: 'bowire-env-editor-main' });

        // Header
        var header = el('div', { className: 'bowire-env-editor-header' },
            el('h2', { className: 'bowire-env-editor-title', textContent: 'New Request' }),
            el('span', { style: 'flex:1' }),
            el('button', {
                id: 'bowire-freeform-cancel-btn',
                className: 'bowire-env-editor-action-btn',
                textContent: 'Cancel',
                onClick: cancelFreeformRequest
            })
        );
        pane.appendChild(header);

        // Protocol picker
        var protoRow = el('div', { className: 'bowire-freeform-row' });
        protoRow.appendChild(el('label', { className: 'bowire-freeform-label', textContent: 'Protocol' }));
        var protoSelect = el('select', {
            id: 'bowire-freeform-protocol-select',
            className: 'bowire-freeform-select',
            onChange: function (e) { fr.protocol = e.target.value; }
        });
        var protoOptions = protocols.length > 0
            ? protocols
            : [{ id: 'grpc', name: 'gRPC' }, { id: 'rest', name: 'REST' }, { id: 'graphql', name: 'GraphQL' },
               { id: 'signalr', name: 'SignalR' }, { id: 'mqtt', name: 'MQTT' }, { id: 'websocket', name: 'WebSocket' }];
        for (var pi = 0; pi < protoOptions.length; pi++) {
            var opt = el('option', { value: protoOptions[pi].id, textContent: protoOptions[pi].name });
            if (protoOptions[pi].id === fr.protocol) opt.selected = true;
            protoSelect.appendChild(opt);
        }
        protoRow.appendChild(protoSelect);

        // Method type
        var typeSelect = el('select', {
            id: 'bowire-freeform-type-select',
            className: 'bowire-freeform-select',
            style: 'margin-left:8px;width:auto',
            onChange: function (e) { fr.methodType = e.target.value; }
        });
        var types = ['Unary', 'ServerStreaming', 'ClientStreaming', 'Duplex'];
        for (var ti = 0; ti < types.length; ti++) {
            var topt = el('option', { value: types[ti], textContent: types[ti] });
            if (types[ti] === fr.methodType) topt.selected = true;
            typeSelect.appendChild(topt);
        }
        protoRow.appendChild(typeSelect);
        pane.appendChild(protoRow);

        // Server URL
        var urlRow = el('div', { className: 'bowire-freeform-row' });
        urlRow.appendChild(el('label', { className: 'bowire-freeform-label', textContent: 'Server URL' }));
        urlRow.appendChild(el('input', {
            id: 'bowire-freeform-url-input',
            type: 'text',
            className: 'bowire-freeform-input',
            value: fr.serverUrl,
            placeholder: 'https://api.example.com or mqtt://broker:1883',
            spellcheck: 'false',
            onInput: function (e) { fr.serverUrl = e.target.value; }
        }));
        pane.appendChild(urlRow);

        // Service name
        var svcRow = el('div', { className: 'bowire-freeform-row' });
        svcRow.appendChild(el('label', { className: 'bowire-freeform-label', textContent: 'Service' }));
        svcRow.appendChild(el('input', {
            id: 'bowire-freeform-service-input',
            type: 'text',
            className: 'bowire-freeform-input',
            value: fr.service,
            placeholder: 'my.package.MyService',
            spellcheck: 'false',
            onInput: function (e) { fr.service = e.target.value; }
        }));
        pane.appendChild(svcRow);

        // Method name
        var methodRow = el('div', { className: 'bowire-freeform-row' });
        methodRow.appendChild(el('label', { className: 'bowire-freeform-label', textContent: 'Method' }));
        methodRow.appendChild(el('input', {
            id: 'bowire-freeform-method-input',
            type: 'text',
            className: 'bowire-freeform-input',
            value: fr.method,
            placeholder: 'GetUser or sensors/temperature',
            spellcheck: 'false',
            onInput: function (e) { fr.method = e.target.value; }
        }));
        pane.appendChild(methodRow);

        // Request body
        var bodyRow = el('div', { className: 'bowire-freeform-row bowire-freeform-body-row' });
        bodyRow.appendChild(el('label', { className: 'bowire-freeform-label', textContent: 'Body' }));
        var bodyEditor = el('textarea', {
            className: 'bowire-editor',
            placeholder: 'Enter JSON request body...',
            spellcheck: 'false',
            style: 'min-height:120px'
        });
        bodyEditor.value = fr.body;
        bodyEditor.addEventListener('input', function () { fr.body = this.value; });
        bodyRow.appendChild(bodyEditor);

        // JSON validation
        var jsonStatus = el('div', { className: 'bowire-json-status empty' });
        bodyRow.appendChild(jsonStatus);
        attachJsonValidator(bodyEditor, jsonStatus);
        pane.appendChild(bodyRow);

        // Mock Response section — collapsible. Lets the user author a
        // mock response without hitting a live backend, which is the
        // core GUI-Mock-Builder flow. An Execute pre-fills the textarea
        // from the real response so the user can tweak then save.
        var mockToggleRow = el('div', {
            className: 'bowire-freeform-mock-toggle',
            onClick: function () {
                freeformMockExpanded = !freeformMockExpanded;
                render();
            }
        },
            el('span', {
                className: 'bowire-freeform-mock-caret',
                textContent: freeformMockExpanded ? '▼' : '▶'
            }),
            el('span', { textContent: 'Mock Response' }),
            el('span', {
                className: 'bowire-freeform-mock-hint',
                textContent: freeformMockExpanded
                    ? '— response the mock server will return for Save as Mock Step'
                    : '— click to author a mock response without executing'
            })
        );
        pane.appendChild(mockToggleRow);

        if (freeformMockExpanded) {
            var mockBlock = el('div', { className: 'bowire-freeform-mock-block' });

            var statusRow = el('div', { className: 'bowire-freeform-row' });
            statusRow.appendChild(el('label', { className: 'bowire-freeform-label', textContent: 'Status' }));
            statusRow.appendChild(el('input', {
                id: 'bowire-freeform-mock-status-input',
                type: 'text',
                className: 'bowire-freeform-input',
                value: fr.mockStatus,
                placeholder: 'OK',
                spellcheck: 'false',
                onInput: function (e) { fr.mockStatus = e.target.value; }
            }));
            mockBlock.appendChild(statusRow);

            var mockBodyRow = el('div', { className: 'bowire-freeform-row bowire-freeform-body-row' });
            mockBodyRow.appendChild(el('label', { className: 'bowire-freeform-label', textContent: 'Response' }));
            var mockEditor = el('textarea', {
                id: 'bowire-freeform-mock-body',
                className: 'bowire-editor',
                placeholder: 'JSON response body the mock server will return...',
                spellcheck: 'false',
                style: 'min-height:100px'
            });
            mockEditor.value = fr.mockResponse;
            mockEditor.addEventListener('input', function () { fr.mockResponse = this.value; });
            mockBodyRow.appendChild(mockEditor);
            var mockStatusLine = el('div', { className: 'bowire-json-status empty' });
            mockBodyRow.appendChild(mockStatusLine);
            attachJsonValidator(mockEditor, mockStatusLine);
            mockBlock.appendChild(mockBodyRow);

            pane.appendChild(mockBlock);
        }

        // Action buttons
        var actions = el('div', { className: 'bowire-freeform-actions' });
        actions.appendChild(el('button', {
            id: 'bowire-freeform-execute-btn',
            className: 'bowire-execute-btn',
            onClick: function () { executeFreeformRequest(); }
        },
            el('span', { innerHTML: svgIcon('play'), style: 'width:14px;height:14px;display:flex' }),
            el('span', { textContent: 'Execute' })
        ));
        actions.appendChild(el('button', {
            id: 'bowire-freeform-save-mock-btn',
            className: 'bowire-repeat-btn',
            title: 'Save this request + response as a mock step in the active recording',
            onClick: function () { saveFreeformAsMockStep(); }
        },
            el('span', { innerHTML: svgIcon('record'), style: 'width:14px;height:14px;display:flex' }),
            el('span', { textContent: 'Save as Mock Step' })
        ));
        actions.appendChild(el('button', {
            id: 'bowire-freeform-save-btn',
            className: 'bowire-repeat-btn',
            title: 'Save this request to a collection',
            onClick: function () {
                if (!fr.service || !fr.method) { toast('Enter a service and method name', 'error'); return; }
                if (collectionsList.length === 0) {
                    var col = createCollection();
                    addToCollection(col.id, {
                        protocol: fr.protocol,
                        service: fr.service,
                        method: fr.method,
                        methodType: fr.methodType,
                        body: fr.body,
                        messages: [fr.body],
                        metadata: null,
                        serverUrl: fr.serverUrl
                    });
                } else {
                    addToCollection(collectionsList[0].id, {
                        protocol: fr.protocol,
                        service: fr.service,
                        method: fr.method,
                        methodType: fr.methodType,
                        body: fr.body,
                        messages: [fr.body],
                        metadata: null,
                        serverUrl: fr.serverUrl
                    });
                }
                toast('Saved to collection', 'success');
            }
        },
            el('span', { innerHTML: svgIcon('list'), style: 'width:14px;height:14px;display:flex' }),
            el('span', { textContent: 'Save to Collection' })
        ));
        pane.appendChild(actions);

        // Response area (reuse global state)
        if (freeformRequest && (responseData || responseError)) {
            var respSection = el('div', { className: 'bowire-freeform-response' });
            if (responseError) {
                // #91 — render structured problem+json when available.
                var errOut = el('div', { className: 'bowire-response-output error' });
                var prob = (typeof responseError === 'object')
                    ? normalizeProblem(responseError)
                    : null;
                if (prob) {
                    renderProblem(prob, errOut);
                } else {
                    errOut.textContent = (typeof responseError === 'string')
                        ? responseError
                        : problemTitle(responseError, 'Request failed');
                }
                respSection.appendChild(errOut);
            } else if (responseData) {
                var output = el('div', { className: 'bowire-response-output is-interactive' });
                output.innerHTML = highlightJsonInteractive(responseData);
                respSection.appendChild(output);
            }
            pane.appendChild(respSection);
        }

        return pane;
    }

    async function executeFreeformRequest() {
        if (!freeformRequest) return;
        var fr = freeformRequest;
        if (!fr.service || !fr.method) {
            toast('Enter a service and method name', 'error');
            return;
        }

        isExecuting = true;
        responseData = null;
        responseError = null;
        markJobActive(fr.service, fr.method);
        render();

        var fullName = fr.service + '/' + fr.method;
        addConsoleEntry({ type: 'request', method: fullName, body: fr.body || '{}' });

        var statusInfo = null;
        try {
            var url = config.prefix + '/api/invoke'
                + (fr.serverUrl ? '?serverUrl=' + encodeURIComponent(fr.serverUrl) : '');
            var resp = await fetch(url, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({
                    service: fr.service,
                    method: fr.method,
                    messages: [fr.body || '{}'],
                    metadata: null,
                    protocol: fr.protocol
                })
            });
            var result = await resp.json();
            // #91 — accept both legacy { error } and RFC 7807 shapes.
            if (result.error || result.title) {
                responseError = result;
                statusInfo = { status: 'Error', durationMs: result.duration_ms || 0 };
                addConsoleEntry({ type: 'error', method: fullName, status: 'Error', body: problemTitle(result) });
            } else {
                responseData = result.response;
                statusInfo = { status: result.status, durationMs: result.duration_ms || 0 };
                if (result.response) captureResponse(result.response);
                addConsoleEntry({ type: 'response', method: fullName, status: result.status, body: result.response });

                // Auto-populate the Mock Response textarea from the real
                // response so the user can save-as-mock with one extra
                // click. Non-destructive: we only fill if empty, so a
                // user who already authored a mock response isn't
                // overwritten by a subsequent Execute.
                if (!fr.mockResponse && typeof result.response === 'string') {
                    fr.mockResponse = result.response;
                    fr.mockStatus = result.status || 'OK';
                }

                // Recorder hook — parity with the discovery-first path
                // in api.js. Silent no-op when no recording is active.
                captureFreeformRecordingStep(fr, {
                    response: result.response,
                    responseBinary: result.response_binary || null,
                    status: statusInfo.status,
                    durationMs: statusInfo.durationMs
                });
            }
        } catch (e) {
            responseError = e.message;
            addConsoleEntry({ type: 'error', method: fullName, status: 'NetworkError', body: e.message });
        }

        isExecuting = false;
        markJobDone(fr.service, fr.method);
        render();
    }

    // Freeze the freeform request + its (real or authored) response
    // into a recording step. When no recording is active, spin up a
    // fresh "Manual mocks" recording and mark it active — the user's
    // next click is enough to build up a whole mock surface, no
    // record-start ceremony first.
    function saveFreeformAsMockStep() {
        if (!freeformRequest) return;
        var fr = freeformRequest;
        if (!fr.service || !fr.method) {
            toast('Enter a service and method name', 'error');
            return;
        }
        var responseText = fr.mockResponse || responseData || '';
        if (!responseText) {
            toast('Enter a Mock Response or click Execute first', 'error');
            freeformMockExpanded = true;
            render();
            return;
        }

        if (!isRecording()) {
            // startRecording already push+persist+set-active; it also
            // renders, but the trailing render() below still runs so
            // the freeform pane re-shows with the captured step list.
            startRecording('Manual mocks');
        }

        captureFreeformRecordingStep(fr, {
            response: responseText,
            responseBinary: null,
            status: fr.mockStatus || 'OK',
            durationMs: 0
        });
        toast('Saved as mock step', 'success');
        render();
    }

    // Shared capture helper for both paths — autopopulated httpPath /
    // httpVerb for REST so Phase-1 mock-server wire matching can pair
    // an incoming request against the recorded step. For non-REST
    // protocols the mock matcher keys on service+method, so leaving
    // these null is the right thing.
    function captureFreeformRecordingStep(fr, extras) {
        var httpPath = null;
        var httpVerb = null;
        if (fr.protocol === 'rest') {
            // Method field in REST freeform is either "GET /path"
            // (Postman-style) or just "/path" with the verb elsewhere.
            // Parse both shapes; fall back to the method text.
            var m = String(fr.method || '').trim().match(/^([A-Z]+)\s+(.+)$/);
            if (m) { httpVerb = m[1]; httpPath = m[2]; }
            else if (fr.method && fr.method.startsWith('/')) {
                httpVerb = 'GET';
                httpPath = fr.method;
            }
        }
        captureRecordingStep({
            protocol: fr.protocol,
            service: fr.service,
            method: fr.method,
            methodType: fr.methodType || 'Unary',
            serverUrl: fr.serverUrl || null,
            body: fr.body || '{}',
            messages: [fr.body || '{}'],
            metadata: null,
            status: extras.status,
            durationMs: extras.durationMs,
            response: extras.response,
            responseBinary: extras.responseBinary,
            httpPath: httpPath,
            httpVerb: httpVerb
        });
    }

    // ---- Environment Editor (full-width main pane) ----
    function renderEnvironmentEditor() {
        var envs = getEnvironments();
        var activeId = getActiveEnvId();
        var isGlobals = envSidebarSelectedId === '__globals__';
        // Resolve from the SHARED env store (not the workspace-
        // filtered subset) so excluded-but-selectable envs still
        // render in the right pane when the operator picks one
        // for inclusion via the checkbox. Falls back to the
        // filtered list when the shared lookup isn't available.
        var allEnvs = (typeof getAllSharedEnvironments === 'function') ? getAllSharedEnvironments() : envs;
        var selectedEnv = isGlobals
            ? null
            : (allEnvs.find(function (e) { return e.id === envSidebarSelectedId; })
               || envs.find(function (e) { return e.id === envSidebarSelectedId; }));
        // ID includes the selected env AND the active tab so morphdom
        // fully replaces the editor when switching between environments
        // or tabs instead of reusing the old DOM with stale closures.
        var envTabKey = isGlobals ? 'variables' : envEditorTab;
        var pane = el('div', {
            id: 'bowire-env-editor-' + (envSidebarSelectedId || 'none') + '-' + envTabKey,
            className: 'bowire-env-editor-main'
        });

        if (!isGlobals && !selectedEnv) {
            pane.appendChild(el('div', { className: 'bowire-env-editor-empty' },
                el('div', { className: 'bowire-env-editor-empty-icon', innerHTML: svgIcon('globe') }),
                el('div', { textContent: 'Select an environment on the left or create a new one' }),
                el('button', {
                    id: 'bowire-env-create-btn',
                    className: 'bowire-env-editor-create-btn',
                    textContent: '+ Create Environment',
                    onClick: function () {
                        var env = createEnvironment('New Environment');
                        envSidebarSelectedId = env.id;
                        render();
                    }
                })
            ));
            return pane;
        }

        // ---- Header: name + action buttons ----
        var headerRow = el('div', { className: 'bowire-env-editor-header' });

        if (isGlobals) {
            headerRow.appendChild(el('span', { className: 'bowire-env-editor-icon', innerHTML: svgIcon('globe') }));
            headerRow.appendChild(el('h2', { className: 'bowire-env-editor-title', textContent: 'Global Variables' }));
        } else {
            // Color picker
            // Quick color swatches + custom picker
            var envColor = selectedEnv.color || '#6366f1';
            var swatchColors = [
                { color: '#6366f1', label: 'Default' },
                { color: '#10b981', label: 'Green' },
                { color: '#f59e0b', label: 'Yellow' },
                { color: '#ef4444', label: 'Red' },
                { color: '#3b82f6', label: 'Blue' },
                { color: '#8b5cf6', label: 'Purple' },
                { color: '#78716c', label: 'Brown' },
                { color: '#000000', label: 'Black' }
            ];
            var colorRow = el('div', { className: 'bowire-env-color-row' });
            for (var sci = 0; sci < swatchColors.length; sci++) {
                (function (sw) {
                    colorRow.appendChild(el('button', {
                        id: 'bowire-env-swatch-' + sw.label.toLowerCase(),
                        className: 'bowire-env-color-swatch' + (envColor === sw.color ? ' active' : ''),
                        style: 'background:' + sw.color,
                        title: sw.label,
                        onClick: function () {
                            updateEnvironment(envSidebarSelectedId, { color: sw.color });
                            render();
                        }
                    }));
                })(swatchColors[sci]);
            }
            // Custom color picker at the end
            var colorInput = el('input', {
                id: 'bowire-env-color-picker',
                type: 'color',
                className: 'bowire-env-color-picker',
                value: envColor,
                title: 'Custom color',
                onInput: function (e) { updateEnvironment(envSidebarSelectedId, { color: e.target.value }); }
            });
            colorRow.appendChild(colorInput);
            headerRow.appendChild(colorRow);
            headerRow.appendChild(el('input', {
                id: 'bowire-env-name-input',
                type: 'text',
                className: 'bowire-env-editor-name-input',
                value: selectedEnv.name,
                onChange: function (e) { updateEnvironment(envSidebarSelectedId, { name: e.target.value }); }
            }));
        }
        headerRow.appendChild(el('span', { style: 'flex:1' }));

        // Import / Export
        headerRow.appendChild(el('button', {
            id: 'bowire-env-export-btn',
            className: 'bowire-env-editor-action-btn',
            title: 'Export all environments as JSON',
            textContent: 'Export',
            onClick: function () {
                var json = exportEnvironments();
                var blob = new Blob([json], { type: 'application/json' });
                var a = document.createElement('a');
                a.href = URL.createObjectURL(blob);
                a.download = 'bowire-environments.bwe';
                a.click();
                URL.revokeObjectURL(a.href);
            }
        }));
        headerRow.appendChild(el('button', {
            id: 'bowire-env-import-btn',
            className: 'bowire-env-editor-action-btn',
            title: 'Import environments from JSON file',
            textContent: 'Import',
            onClick: function () {
                var input = document.createElement('input');
                input.type = 'file';
                input.accept = '.json,application/json';
                input.onchange = async function () {
                    if (!input.files || !input.files[0]) return;
                    try {
                        var text = await input.files[0].text();
                        importEnvironments(text);
                        toast('Environments imported', 'success');
                        render();
                    } catch (e) {
                        toast('Import failed: ' + e.message, 'error');
                    }
                };
                input.click();
            }
        }));

        if (!isGlobals) {
            if (envSidebarSelectedId !== activeId) {
                headerRow.appendChild(el('button', {
                    id: 'bowire-env-set-active-btn',
                    className: 'bowire-env-editor-action-btn',
                    textContent: 'Set Active',
                    onClick: function () { setActiveEnvId(envSidebarSelectedId); render(); }
                }));
            } else {
                headerRow.appendChild(el('span', {
                    className: 'bowire-env-editor-active-badge',
                    textContent: 'Active'
                }));
            }
            headerRow.appendChild(el('button', {
                id: 'bowire-env-delete-btn',
                className: 'bowire-env-editor-action-btn bowire-env-editor-danger',
                title: 'Delete this environment',
                'aria-label': 'Delete this environment',
                innerHTML: svgIcon('trash'),
                onClick: function () {
                    bowireConfirm('Delete environment "' + selectedEnv.name + '"?', function () {
                        var backup = JSON.parse(JSON.stringify(selectedEnv));
                        deleteEnvironment(envSidebarSelectedId);
                        envSidebarSelectedId = activeId || '__globals__';
                        render();
                        toast('Environment deleted', 'success', { undo: function () { restoreEnvironment(backup); envSidebarSelectedId = backup.id; render(); } });
                    }, { title: 'Delete Environment', danger: true, confirmText: 'Delete' });
                }
            }));
        }
        pane.appendChild(headerRow);

        // ---- Tabs: Variables | Auth | Compare ----
        // Globals only have Variables; named envs get all three.
        if (!isGlobals) {
            var tabs = el('div', { className: 'bowire-env-editor-tabs' });
            var tabDefs = [
                { id: 'variables', label: 'Variables' },
                { id: 'auth', label: 'Auth' },
                { id: 'compare', label: 'Compare' }
            ];
            for (var ti = 0; ti < tabDefs.length; ti++) {
                (function (td) {
                    tabs.appendChild(el('button', {
                        id: 'bowire-env-tab-' + td.id,
                        className: 'bowire-env-editor-tab' + (envEditorTab === td.id ? ' active' : ''),
                        textContent: td.label,
                        onClick: function () { envEditorTab = td.id; render(); }
                    }));
                })(tabDefs[ti]);
            }
            pane.appendChild(tabs);
        }

        // ---- Tab content ----
        var activeTab = isGlobals ? 'variables' : envEditorTab;

        if (activeTab === 'variables') {
            pane.appendChild(renderEnvVariableTable(
                isGlobals ? getGlobalVars() : (selectedEnv.vars || {}),
                isGlobals
                    ? function (v) { saveGlobalVars(v); }
                    : function (v) { updateEnvironment(envSidebarSelectedId, { vars: v }); },
                isGlobals
                    ? 'Available in every environment. Override per-environment with the same key name.'
                    : 'Use ${name} in request body, metadata or server URL.'
            ));
        } else if (activeTab === 'auth' && selectedEnv) {
            var freshAuth = function () {
                var e = getEnvironments().find(function (e) { return e.id === envSidebarSelectedId; });
                return (e && e.auth) || { type: 'none' };
            };
            pane.appendChild(renderAuthSection(
                freshAuth,
                function (a) { updateEnvironment(envSidebarSelectedId, { auth: a }); }
            ));
        } else if (activeTab === 'compare' && selectedEnv) {
            pane.appendChild(renderEnvDiffPane(selectedEnv,
                envManagerDiffTargetId
                    ? envs.find(function (e) { return e.id === envManagerDiffTargetId; })
                    : null,
                envs));
        }

        return pane;
    }

    // Variable table builder — shared by env editor
    function renderEnvVariableTable(vars, setVars, subtitle) {
        var frag = el('div', { className: 'bowire-env-editor-vars' });
        if (subtitle) {
            frag.appendChild(el('div', { className: 'bowire-env-editor-subtitle', textContent: subtitle }));
        }

        var keys = Object.keys(vars);
        var table = el('div', { className: 'bowire-env-editor-table' });

        table.appendChild(el('div', { className: 'bowire-env-editor-row bowire-env-editor-col-header' },
            el('span', { textContent: 'Variable' }),
            el('span', { textContent: 'Value' }),
            el('span')
        ));

        function commitAll() {
            var newVars = {};
            var rows = table.querySelectorAll('.bowire-env-editor-row:not(.bowire-env-editor-col-header)');
            for (var ri = 0; ri < rows.length; ri++) {
                var k = rows[ri].querySelector('.bowire-env-editor-key');
                var v = rows[ri].querySelector('.bowire-env-editor-val');
                if (k && v && k.value.trim()) {
                    newVars[k.value.trim()] = v.value;
                }
            }
            setVars(newVars);
        }

        function addVarRow(key, value) {
            var row = el('div', { className: 'bowire-env-editor-row' });
            var keyInput = el('input', {
                type: 'text',
                className: 'bowire-env-editor-key',
                value: key,
                placeholder: 'variable name',
                spellcheck: 'false'
            });
            var valInput = el('textarea', {
                className: 'bowire-env-editor-val',
                placeholder: 'value (multi-line, JSON, etc.)',
                spellcheck: 'false',
                rows: '1'
            });
            valInput.value = value;
            function autoResize() {
                valInput.style.height = 'auto';
                valInput.style.height = Math.max(28, valInput.scrollHeight) + 'px';
            }
            keyInput.addEventListener('change', commitAll);
            valInput.addEventListener('change', commitAll);
            valInput.addEventListener('input', autoResize);
            row.appendChild(keyInput);
            row.appendChild(valInput);
            row.appendChild(el('button', {
                className: 'bowire-env-editor-remove',
                title: 'Remove variable',
                textContent: '\u00d7',
                onClick: function () { row.remove(); commitAll(); }
            }));
            table.appendChild(row);
            requestAnimationFrame(autoResize);
        }

        for (var ki = 0; ki < keys.length; ki++) {
            addVarRow(keys[ki], String(vars[keys[ki]]));
        }
        addVarRow('', '');
        frag.appendChild(table);

        frag.appendChild(el('button', {
            id: 'bowire-env-add-variable-btn',
            className: 'bowire-env-editor-add-btn',
            textContent: '+ Add variable',
            onClick: function () { addVarRow('', ''); }
        }));

        return frag;
    }

    // #116 — Workspaces rail-mode main pane. Right-hand detail view:
    // color picker, inline rename, description, stats (env / recording
    // / collection counts), switch + delete actions. Bundles every
    // workspace config touchpoint into one surface.
    // Three-way import dialog for workspace JSON. bowireConfirm only
    // supports two buttons (confirm / cancel); the import flow needs
    // three distinct strategies (new / merge / replace) so we build a
    // small purpose-built modal that reuses bowire-confirm CSS classes
    // for visual consistency.
    function _showWorkspaceImportDialog(payload, targetWs) {
        var existing = document.querySelector('.bowire-confirm-overlay');
        if (existing) existing.remove();

        var meta = (payload && payload.workspace) || {};
        var data = (payload && payload.data) || {};
        var counts = {
            urls: Array.isArray(data.bowire_server_urls) ? data.bowire_server_urls.length : 0,
            collections: Array.isArray(data.bowire_collections) ? data.bowire_collections.length : 0,
            recordings: Array.isArray(data.bowire_recordings) ? data.bowire_recordings.length : 0,
            favorites: Array.isArray(data.bowire_favorites) ? data.bowire_favorites.length : 0,
            benchmarks: Array.isArray(data.bowire_benchmarks) ? data.bowire_benchmarks.length : 0,
            flows: Array.isArray(data.bowire_flows) ? data.bowire_flows.length : 0
        };
        var summary = (meta.name || 'Imported') + ' — '
            + counts.urls + ' URLs, '
            + counts.collections + ' collections, '
            + counts.recordings + ' recordings, '
            + counts.favorites + ' favorites, '
            + counts.benchmarks + ' benchmarks, '
            + counts.flows + ' flows';

        function close() { overlay.remove(); }
        function runImport(mode) {
            try {
                if (mode === 'new') {
                    var newId = importWorkspaceJson(payload, { mode: 'new' });
                    workspacesSelectedId = newId;
                    close();
                    toast('Workspace imported', 'success');
                    if (typeof switchWorkspace === 'function' && newId) {
                        switchWorkspace(newId);
                    } else {
                        render();
                    }
                } else {
                    importWorkspaceJson(payload, { mode: mode, target: targetWs.id });
                    close();
                    toast(mode === 'replace'
                        ? 'Workspace "' + targetWs.name + '" replaced'
                        : 'Merged into "' + targetWs.name + '"', 'success');
                    try { window.location.reload(); } catch { /* ignore */ }
                }
            } catch (e) {
                toast(e.message || 'Import failed', 'error');
            }
        }

        var dialog = el('div', {
            className: 'bowire-confirm-dialog',
            role: 'alertdialog',
            'aria-modal': 'true',
            style: 'min-width:380px'
        },
            el('div', { className: 'bowire-confirm-title', textContent: 'Import workspace' }),
            el('div', { className: 'bowire-confirm-message', textContent: summary }),
            el('div', { className: 'bowire-confirm-actions', style: 'flex-wrap:wrap;gap:6px' },
                el('button', {
                    className: 'bowire-confirm-btn cancel',
                    textContent: 'Cancel',
                    onClick: close
                }),
                el('button', {
                    className: 'bowire-confirm-btn danger',
                    textContent: 'Replace "' + targetWs.name + '"',
                    title: 'Wipe the target workspace and load the file into it',
                    onClick: function () { runImport('replace'); }
                }),
                el('button', {
                    className: 'bowire-confirm-btn',
                    textContent: 'Merge into "' + targetWs.name + '"',
                    title: 'Concat lists and assign maps into the current workspace',
                    onClick: function () { runImport('merge-into'); }
                }),
                el('button', {
                    className: 'bowire-confirm-btn',
                    textContent: 'New workspace',
                    title: 'Create a new workspace and load the file into it (recommended)',
                    onClick: function () { runImport('new'); }
                })
            )
        );
        var overlay = el('div', {
            className: 'bowire-confirm-overlay',
            onClick: function (e) { if (e.target === overlay) close(); }
        }, dialog);
        document.body.appendChild(overlay);
        overlay.addEventListener('keydown', function (e) {
            if (e.key === 'Escape') close();
        });
    }

    function renderWorkspaceDetailMain() {
        var main = el('div', { id: 'bowire-main-workspaces', className: 'bowire-main bowire-main-workspaces' });

        var ws = workspaces.find(function (w) { return w.id === workspacesSelectedId; })
                 || activeWorkspace();
        if (!ws) {
            main.appendChild(el('p', {
                className: 'bowire-ai-empty',
                style: 'padding:24px',
                textContent: 'No workspace selected. Pick one in the sidebar or create a new one.'
            }));
            return main;
        }

        var isActive = ws.id === activeWorkspaceId;
        var allEnvs = (typeof getAllSharedEnvironments === 'function') ? getAllSharedEnvironments() : [];
        var envCount = ws.includeAllEnvironments
            ? allEnvs.length
            : (Array.isArray(ws.includedEnvironmentIds) ? ws.includedEnvironmentIds.length : 0);

        var header = el('div', { className: 'bowire-ws-detail-header' },
            el('span', {
                className: 'bowire-ws-detail-dot',
                style: 'background:' + (ws.color || 'var(--bowire-accent)')
            }),
            el('input', {
                type: 'text',
                className: 'bowire-ws-detail-name',
                value: ws.name,
                'aria-label': 'Workspace name',
                onChange: function (e) {
                    var v = String(e.target.value || '').trim();
                    if (v) { renameWorkspace(ws.id, v); render(); }
                }
            }),
            isActive
                ? el('span', { className: 'bowire-ws-detail-badge', textContent: 'Active' })
                : el('button', {
                    className: 'bowire-ws-detail-switch-btn',
                    textContent: 'Switch to this workspace',
                    onClick: function () { switchWorkspace(ws.id); }
                })
        );
        main.appendChild(header);

        // Color picker — predefined palette so the dots stay distinct
        // at a glance. Custom-color UI can land later.
        var palette = ['#6366f1', '#22c55e', '#f59e0b', '#ec4899', '#06b6d4', '#a855f7', '#ef4444', '#64748b'];
        var colorRow = el('div', { className: 'bowire-ws-detail-section' },
            el('div', { className: 'bowire-ws-detail-section-label', textContent: 'Color' }),
            el('div', { className: 'bowire-ws-detail-color-row' },
                palette.map(function (c) {
                    return el('button', {
                        className: 'bowire-ws-detail-color-swatch' + (ws.color === c ? ' selected' : ''),
                        style: 'background:' + c,
                        title: c,
                        onClick: function () {
                            ws.color = c;
                            persistWorkspaces();
                            render();
                        }
                    });
                })
            )
        );
        main.appendChild(colorRow);

        var descRow = el('div', { className: 'bowire-ws-detail-section' },
            el('div', { className: 'bowire-ws-detail-section-label', textContent: 'Description' }),
            el('textarea', {
                className: 'bowire-ws-detail-desc',
                value: ws.description || '',
                placeholder: 'Notes — what this workspace is for, who shares it, …',
                rows: '3',
                onChange: function (e) {
                    ws.description = String(e.target.value || '').trim();
                    persistWorkspaces();
                }
            })
        );
        main.appendChild(descRow);

        // Stats grid — counts of what's in this workspace. Each tile is
        // clickable for the ACTIVE workspace: jumps to the matching rail
        // mode so the operator can drill in. Non-active tiles render as
        // inert hints (the count is a stored snapshot, not live).
        function statTile(label, value, hint, navTo) {
            var Tag = navTo ? 'button' : 'div';
            var node = el(Tag, {
                className: 'bowire-ws-detail-stat' + (navTo ? ' bowire-ws-detail-stat-clickable' : ''),
                type: navTo ? 'button' : undefined,
                onClick: navTo ? function () {
                    railMode = navTo;
                    try { localStorage.setItem('bowire_rail_mode', navTo); } catch { /* ignore */ }
                    render();
                } : undefined
            },
                el('div', { className: 'bowire-ws-detail-stat-value', textContent: String(value) }),
                el('div', { className: 'bowire-ws-detail-stat-label', textContent: label }),
                hint ? el('div', { className: 'bowire-ws-detail-stat-hint', textContent: hint }) : null
            );
            return node;
        }
        // Only the ACTIVE workspace's state is currently in memory.
        // For non-active workspaces the counts are stored hints from
        // the persisted workspace meta (added in future as needed); for
        // now show '—' so it's clear those reflect 'currently loaded'.
        var urlCount = isActive
            ? ((typeof serverUrls !== 'undefined' && Array.isArray(serverUrls)) ? serverUrls.length : 0)
            : '—';
        var connectedCount = 0;
        if (isActive && typeof connectionStatuses === 'object' && connectionStatuses
            && typeof serverUrls !== 'undefined') {
            for (var ui = 0; ui < serverUrls.length; ui++) {
                if (connectionStatuses[serverUrls[ui]] === 'connected') connectedCount++;
            }
        }
        var recordingCount = isActive ? (Array.isArray(recordingsList) ? recordingsList.length : 0) : '—';
        var collectionCount = isActive ? (Array.isArray(collectionsList) ? collectionsList.length : 0) : '—';
        var favCount = '—';
        if (isActive && typeof getFavorites === 'function') {
            try { favCount = getFavorites().length; } catch { /* ignore */ }
        }
        var mockCount = isActive && Array.isArray(mocksList) ? mocksList.length : '—';
        main.appendChild(el('div', { className: 'bowire-ws-detail-section' },
            el('div', { className: 'bowire-ws-detail-section-label', textContent: 'Contents' }),
            el('div', { className: 'bowire-ws-detail-stats' },
                statTile('URLs', urlCount,
                    isActive
                        ? (urlCount === 0 ? 'add one below' : connectedCount + ' connected')
                        : 'switch to load',
                    isActive ? 'discover' : null),
                statTile('Environments', envCount,
                    ws.includeAllEnvironments ? 'all shared envs' : 'selected',
                    isActive ? 'environments' : null),
                statTile('Recordings', recordingCount,
                    isActive ? 'in this workspace' : 'switch to load',
                    isActive ? 'recordings' : null),
                statTile('Collections', collectionCount,
                    isActive ? 'in this workspace' : 'switch to load',
                    isActive ? 'collections' : null),
                statTile('Favorites', favCount,
                    isActive ? 'starred methods' : 'switch to load',
                    isActive ? 'home' : null),
                statTile('Mocks', mockCount,
                    isActive ? 'running now' : 'switch to load',
                    isActive ? 'mocks' : null)
            )
        ));

        // Recording storage mode — per-workspace choice between
        // 'both' (default; localStorage cache + user-folder sync) and
        // 'browser-only' (skip every backend write; localStorage is
        // the source of truth). #144 Phase 1.8 will add 'disk-only'
        // once manifest-only loading is wired.
        var storageMode = (typeof getRecordingStorageMode === 'function')
            ? getRecordingStorageMode(ws) : 'both';
        main.appendChild(el('div', { className: 'bowire-ws-detail-section' },
            el('div', { className: 'bowire-ws-detail-section-label', textContent: 'Recording storage' }),
            el('div', { className: 'bowire-ws-detail-storage-row' },
                el('label', { className: 'bowire-ws-detail-storage-option' + (storageMode === 'both' ? ' selected' : '') },
                    el('input', {
                        type: 'radio',
                        name: 'ws-rec-storage-' + ws.id,
                        checked: storageMode === 'both',
                        onChange: function () {
                            if (typeof setWorkspaceRecordingStorageMode === 'function') {
                                setWorkspaceRecordingStorageMode(ws.id, 'both');
                                render();
                            }
                        }
                    }),
                    el('div', { className: 'bowire-ws-detail-storage-text' },
                        el('div', { className: 'bowire-ws-detail-storage-title', textContent: 'Disk + browser cache (recommended)' }),
                        el('div', { className: 'bowire-ws-detail-storage-meta', textContent: 'Source of truth on disk under ~/.bowire/workspaces/' + ws.id + '/recordings/. Survives browser clears + lets the CLI read what the workbench captured.' })
                    )
                ),
                el('label', { className: 'bowire-ws-detail-storage-option' + (storageMode === 'browser-only' ? ' selected' : '') },
                    el('input', {
                        type: 'radio',
                        name: 'ws-rec-storage-' + ws.id,
                        checked: storageMode === 'browser-only',
                        onChange: function () {
                            if (typeof setWorkspaceRecordingStorageMode === 'function') {
                                setWorkspaceRecordingStorageMode(ws.id, 'browser-only');
                                render();
                            }
                        }
                    }),
                    el('div', { className: 'bowire-ws-detail-storage-text' },
                        el('div', { className: 'bowire-ws-detail-storage-title', textContent: 'Browser-only (no disk writes)' }),
                        el('div', { className: 'bowire-ws-detail-storage-meta', textContent: 'Recordings stay in localStorage. Browser clear = data loss. Useful for sandbox / privacy workflows. Subject to the ~5–10 MB localStorage quota.' })
                    )
                ),
                el('label', { className: 'bowire-ws-detail-storage-option' + (storageMode === 'disk-only' ? ' selected' : '') },
                    el('input', {
                        type: 'radio',
                        name: 'ws-rec-storage-' + ws.id,
                        checked: storageMode === 'disk-only',
                        onChange: function () {
                            if (typeof setWorkspaceRecordingStorageMode === 'function') {
                                setWorkspaceRecordingStorageMode(ws.id, 'disk-only');
                                render();
                            }
                        }
                    }),
                    el('div', { className: 'bowire-ws-detail-storage-text' },
                        el('div', { className: 'bowire-ws-detail-storage-title', textContent: 'Disk-only (no browser cache)' }),
                        el('div', { className: 'bowire-ws-detail-storage-meta', textContent: 'Recordings on disk only. List loads metadata-only on init; step bodies fetch on demand when you open a recording. Use for GB-scale captures that would blow the localStorage quota.' })
                    )
                )
            )
        ));

        // Metadata strip — IDs / timestamps so the operator can see
        // creation / last-opened for audit / debugging.
        var createdStr = ws.createdAt ? new Date(ws.createdAt).toLocaleString() : '—';
        var openedStr = ws.lastOpenedAt ? new Date(ws.lastOpenedAt).toLocaleString() : '—';
        main.appendChild(el('div', { className: 'bowire-ws-detail-section' },
            el('div', { className: 'bowire-ws-detail-section-label', textContent: 'Metadata' }),
            el('div', { className: 'bowire-ws-detail-meta-grid' },
                el('div', { textContent: 'ID' }),
                el('div', { className: 'bowire-ws-detail-meta-value', textContent: ws.id }),
                el('div', { textContent: 'Created' }),
                el('div', { className: 'bowire-ws-detail-meta-value', textContent: createdStr }),
                el('div', { textContent: 'Last opened' }),
                el('div', { className: 'bowire-ws-detail-meta-value', textContent: openedStr })
            )
        ));

        // Sources section (#155 Phase 1) — URLs + Schema-Files for the
        // active workspace. The Sources rail mode was retired in
        // favour of this in-workspace surface: a workspace is the
        // project folder, sources are an integral part of it.
        // Only render for the ACTIVE workspace, because URL state
        // (serverUrls / connectionStatuses / services) is the live
        // discovery state of the active workspace only. Non-active
        // workspaces show a hint instead.
        var sourcesSection = el('div', { className: 'bowire-ws-detail-section' },
            el('div', { className: 'bowire-ws-detail-section-label', textContent: 'Sources' })
        );
        if (!isActive) {
            sourcesSection.appendChild(el('p', {
                className: 'bowire-ws-detail-stat-hint',
                textContent: 'Switch to this workspace to manage its URLs and schema files.'
            }));
        } else {
            // URL list — one row per URL with status + name + service
            // count + a Remove + Open-in-Discover action.
            var urls = (typeof serverUrls !== 'undefined' && Array.isArray(serverUrls)) ? serverUrls : [];
            if (urls.length === 0) {
                sourcesSection.appendChild(el('p', {
                    className: 'bowire-ws-detail-stat-hint',
                    style: 'margin-bottom:8px',
                    textContent: 'No URLs yet. Add one below to start discovery.'
                }));
            } else {
                var srcList = el('div', { style: 'display:flex;flex-direction:column;gap:4px;margin-bottom:8px' });
                urls.forEach(function (u) {
                    var st = (typeof connectionStatuses === 'object' && connectionStatuses)
                        ? (connectionStatuses[u] || 'disconnected') : 'disconnected';
                    var meta = (typeof getUrlMeta === 'function') ? getUrlMeta(u) : {};
                    var name = meta.name || (typeof _stripUrlPrefix === 'function' ? _stripUrlPrefix(u) : u);
                    var svcN = (typeof services !== 'undefined')
                        ? services.filter(function (s) {
                            return typeof urlMatchesService === 'function'
                                ? urlMatchesService(u, s) : s.originUrl === u;
                        }).length : 0;
                    srcList.appendChild(el('div', {
                        style: 'display:flex;align-items:center;gap:8px;padding:6px 8px;background:var(--bowire-surface);border:1px solid var(--bowire-border-subtle);border-radius:var(--bowire-radius-sm)'
                    },
                        el('span', {
                            className: 'bowire-conn-pill-dot bowire-conn-pill-dot-' + st,
                            style: 'width:10px;height:10px;border-radius:50%;flex-shrink:0;background:'
                                + (st === 'connected' ? 'var(--bowire-success)'
                                    : st === 'error' ? 'var(--bowire-danger)'
                                    : st === 'discovering' ? 'var(--bowire-warning)'
                                    : 'var(--bowire-text-tertiary)')
                        }),
                        el('span', { style: 'flex:1;font-size:12px;font-family:var(--bowire-mono);overflow:hidden;text-overflow:ellipsis;white-space:nowrap', textContent: name, title: u }),
                        el('span', { style: 'font-size:11px;color:var(--bowire-text-tertiary);flex-shrink:0', textContent: svcN + ' svc' + (svcN === 1 ? '' : 's') }),
                        el('button', {
                            className: 'bowire-presets-btn',
                            style: 'height:22px;padding:0 8px;font-size:11px',
                            title: 'Open this URL in Discover',
                            textContent: 'Discover',
                            onClick: function () {
                                railMode = 'discover';
                                sidebarView = 'services';
                                try { localStorage.setItem('bowire_rail_mode', 'discover'); } catch { /* ignore */ }
                                render();
                            }
                        }),
                        el('button', {
                            className: 'bowire-presets-btn',
                            style: 'height:22px;padding:0 8px;font-size:11px;color:var(--bowire-danger)',
                            title: 'Remove this URL from the workspace',
                            textContent: 'Remove',
                            onClick: function () {
                                bowireConfirm('Remove URL "' + name + '"?', {
                                    confirmText: 'Remove',
                                    danger: true
                                }).then(function (ok) {
                                    if (!ok) return;
                                    var idx = serverUrls.indexOf(u);
                                    if (idx >= 0) serverUrls.splice(idx, 1);
                                    if (typeof persistServerUrls === 'function') persistServerUrls();
                                    render();
                                });
                            }
                        })
                    ));
                });
                sourcesSection.appendChild(srcList);
            }
            // Add URL input row.
            sourcesSection.appendChild(el('button', {
                className: 'bowire-presets-btn',
                textContent: '+ Add URL',
                onClick: function () {
                    bowirePrompt('Server URL', {
                        title: 'Add URL',
                        placeholder: 'e.g. https://petstore3.swagger.io/api/v3/openapi.json',
                        confirmText: 'Add'
                    }).then(function (raw) {
                        if (!raw) return;
                        var trimmed = String(raw).trim();
                        if (!trimmed) return;
                        if (serverUrls.indexOf(trimmed) < 0) {
                            serverUrls.push(trimmed);
                            if (typeof persistServerUrls === 'function') persistServerUrls();
                            // Trigger discovery for the new URL.
                            if (typeof fetchServices === 'function') fetchServices();
                        }
                        render();
                    });
                }
            }));
            // Schema files — drop-zone for .proto / .openapi.json /
            // .yaml. Lives here because uploaded schemas are part of
            // what the workspace knows about; the legacy sidebar
            // selector (Discover/Flows/Proxy/Security) was retired in
            // #294 so workspace-detail is the only mount site now.
            sourcesSection.appendChild(el('div', { className: 'bowire-ws-detail-section-label', style: 'margin-top:14px', textContent: 'Schema files' }));
            if (typeof renderProtoUploadPanel === 'function') {
                sourcesSection.appendChild(renderProtoUploadPanel());
            }
        }
        main.appendChild(sourcesSection);

        // #125 Phase 4 — Secrets section. Session-only in-memory
        // store (cleared on reload); Phase 5 wraps an OS keyring.
        // Values are masked in the table; the resolver still hands
        // the real value to the request pipeline so the upstream
        // gets the bearer token / API key. Recording / export
        // sanitisers replace the value with '***' before it leaves
        // the workbench.
        var secretsBag = (typeof getWorkspaceSecrets === 'function')
            ? getWorkspaceSecrets(ws.id) : {};
        var secretsSection = el('div', { className: 'bowire-ws-detail-section' },
            el('div', { className: 'bowire-ws-detail-section-label', textContent: 'Secrets' }),
            el('div', {
                className: 'bowire-ws-detail-stat-hint',
                style: 'margin-bottom:8px',
                textContent: 'Reference as {{secret.NAME}} in any input. Session-only — cleared on reload. Phase 5 wraps the OS keyring.'
            })
        );
        var secretNames = (typeof listWorkspaceSecrets === 'function')
            ? listWorkspaceSecrets(ws.id) : [];
        if (secretNames.length > 0) {
            var secretsList = el('div', { style: 'display:flex;flex-direction:column;gap:4px;margin-bottom:8px' });
            secretNames.forEach(function (name) {
                secretsList.appendChild(el('div', {
                    style: 'display:flex;align-items:center;gap:8px;font-size:12px;padding:4px 8px;background:var(--bowire-surface);border:1px solid var(--bowire-border-subtle);border-radius:var(--bowire-radius-sm)'
                },
                    el('span', { style: 'flex:1;font-family:var(--bowire-mono)', textContent: name }),
                    el('span', { style: 'font-family:var(--bowire-mono);color:var(--bowire-text-tertiary)', textContent: '••••••••' }),
                    el('button', {
                        className: 'bowire-presets-btn',
                        style: 'height:22px;padding:0 8px',
                        textContent: 'Remove',
                        onClick: function () {
                            setWorkspaceSecret(name, null, ws.id);
                            render();
                        }
                    })
                ));
            });
            secretsSection.appendChild(secretsList);
        }
        secretsSection.appendChild(el('button', {
            className: 'bowire-presets-btn',
            textContent: '+ Add secret',
            onClick: function () {
                bowirePrompt('Secret name', {
                    title: 'Add secret',
                    placeholder: 'e.g. GH_TOKEN',
                    confirmText: 'Next'
                }).then(function (name) {
                    if (!name) return;
                    bowirePrompt('Value for ' + name, {
                        title: 'Add secret value',
                        placeholder: '(value is not stored on disk)',
                        confirmText: 'Save'
                    }).then(function (value) {
                        if (!value) return;
                        setWorkspaceSecret(name, value, ws.id);
                        render();
                    });
                });
            }
        }));
        main.appendChild(secretsSection);

        // Share / portability — Export this workspace as a .bowire JSON
        // bundle, or import another. Secrets are intentionally excluded
        // from the export (session-only, would leak credentials).
        var shareSection = el('div', { className: 'bowire-ws-detail-section' },
            el('div', { className: 'bowire-ws-detail-section-label', textContent: 'Share' })
        );
        shareSection.appendChild(el('p', {
            className: 'bowire-ws-detail-stat-hint',
            style: 'margin-bottom:10px',
            textContent: 'Export packages URLs, collections, recordings, favorites, benchmarks, flows and presets into a single JSON file. Secrets are NOT included.'
        }));
        var shareRow = el('div', { style: 'display:flex;gap:8px;flex-wrap:wrap' });
        shareRow.appendChild(el('button', {
            className: 'bowire-presets-btn',
            textContent: 'Export workspace…',
            onClick: function () {
                if (!downloadWorkspaceExport(ws.id)) {
                    toast('Export failed — see console', 'error');
                    return;
                }
                toast('Workspace exported', 'success');
            }
        }));
        shareRow.appendChild(el('button', {
            className: 'bowire-presets-btn',
            textContent: 'Import workspace…',
            onClick: function () {
                var input = document.createElement('input');
                input.type = 'file';
                input.accept = '.json,.bowire,application/json';
                input.onchange = function () {
                    if (!input.files || input.files.length === 0) return;
                    var file = input.files[0];
                    var reader = new FileReader();
                    reader.onload = function () {
                        var payload;
                        try { payload = JSON.parse(reader.result); }
                        catch (e) {
                            toast('Could not parse the file as JSON', 'error');
                            return;
                        }
                        _showWorkspaceImportDialog(payload, ws);
                    };
                    reader.readAsText(file);
                };
                input.click();
            }
        }));
        shareSection.appendChild(shareRow);
        main.appendChild(shareSection);

        // Danger zone — delete button. Only render when more than one
        // workspace exists (can't delete the last one).
        if (workspaces.length > 1) {
            main.appendChild(el('div', { className: 'bowire-ws-detail-section bowire-ws-detail-danger' },
                el('div', { className: 'bowire-ws-detail-section-label', textContent: 'Danger zone' }),
                el('button', {
                    className: 'bowire-settings-action-btn bowire-ws-detail-delete-btn',
                    textContent: 'Delete this workspace',
                    onClick: function () {
                        var wsName = ws.name;
                        var wsId = ws.id;
                        bowireConfirm(
                            'Delete workspace "' + wsName + '"? URLs / envs / recordings scoped to this workspace are removed.',
                            function () {
                                deleteWorkspace(wsId);
                                workspacesSelectedId = activeWorkspaceId;
                                render();
                            },
                            { title: 'Delete workspace', confirmText: 'Delete', danger: true }
                        );
                    }
                })
            ));
        }

        return main;
    }

    // #152 — Sources rail-mode main pane. Right-hand detail view
    // for the selected URL: status header + discovery summary +
    // headers (placeholder) + schema imports (placeholder) +
    // delete action. Subsequent commits flesh out the headers /
    // schema sections; v1 focuses on the consolidated surface.
    function renderSourcesDetailMain() {
        var main = el('div', { id: 'bowire-main-sources', className: 'bowire-main bowire-main-workspaces' });

        if (!serverUrls || serverUrls.length === 0) {
            main.appendChild(el('p', {
                className: 'bowire-ai-empty',
                style: 'padding:24px',
                textContent: 'No URLs configured yet. Add one via the + button in the sidebar to start discovery.'
            }));
            return main;
        }
        if (!sourcesSelectedUrl || serverUrls.indexOf(sourcesSelectedUrl) < 0) {
            sourcesSelectedUrl = serverUrls[0];
        }
        var u = sourcesSelectedUrl;
        var status = (typeof connectionStatuses === 'object' && connectionStatuses)
            ? (connectionStatuses[u] || 'disconnected') : 'disconnected';
        var statusLabel = status === 'connected' ? 'Connected'
                        : status === 'disconnected' ? 'Disconnected'
                        : status === 'discovering' ? 'Discovering' : status;
        var svcList = (typeof services !== 'undefined')
            ? services.filter(function (s) { return s.originUrl === u; }) : [];
        var protoCounts = {};
        for (var i = 0; i < svcList.length; i++) {
            var proto = svcList[i].source || 'unknown';
            protoCounts[proto] = (protoCounts[proto] || 0) + 1;
        }
        var methodN = svcList.reduce(function (n, s) { return n + ((s.methods && s.methods.length) || 0); }, 0);

        var meta = (typeof getUrlMeta === 'function') ? getUrlMeta(u) : {};
        var dotColor = meta.color || null;
        var header = el('div', { className: 'bowire-ws-detail-header' },
            el('span', {
                className: 'bowire-conn-pill-dot bowire-conn-pill-dot-' + status,
                style: 'width:20px;height:20px;border-radius:50%;flex-shrink:0;'
                    + (dotColor ? ('background:' + dotColor + ';') : '')
            }),
            el('input', {
                type: 'text',
                className: 'bowire-ws-detail-name',
                value: meta.name || _stripUrlPrefix(u),
                placeholder: 'Optional display name (defaults to host)',
                'aria-label': 'Display name',
                onChange: function (e) {
                    var v = String(e.target.value || '').trim();
                    setUrlMeta(u, { name: v || null });
                    render();
                }
            }),
            el('span', { className: 'bowire-ws-detail-badge', textContent: statusLabel })
        );
        main.appendChild(header);

        // Editable URL field below the header — the URL is what
        // discovery actually hits; the input above is the operator-
        // visible name.
        main.appendChild(el('div', { className: 'bowire-ws-detail-section' },
            el('div', { className: 'bowire-ws-detail-section-label', textContent: 'URL' }),
            el('input', {
                type: 'text',
                className: 'bowire-url-header-value',
                value: u,
                readonly: !!config.lockServerUrl,
                onChange: function (e) {
                    var v = String(e.target.value || '').trim();
                    if (!v || v === u) return;
                    var idx = serverUrls.indexOf(u);
                    if (idx < 0) return;
                    // Re-key the meta + headers bags so the operator
                    // doesn't lose them on a URL edit.
                    var oldMeta = urlMeta[u];
                    var oldHeaders = urlHeaders[u];
                    serverUrls[idx] = v;
                    if (oldMeta) { delete urlMeta[u]; urlMeta[v] = oldMeta; persistUrlMeta(); }
                    if (oldHeaders) { delete urlHeaders[u]; urlHeaders[v] = oldHeaders; persistUrlHeaders(); }
                    sourcesSelectedUrl = v;
                    if (typeof persistServerUrls === 'function') persistServerUrls();
                    if (typeof onServerUrlChanged === 'function') onServerUrlChanged();
                    render();
                }
            })
        ));

        // Color picker — same palette as the Workspaces detail
        // pane so the operator picks from a curated set instead of
        // a color wheel.
        var palette = ['#6366f1', '#22c55e', '#f59e0b', '#ec4899', '#06b6d4', '#a855f7', '#ef4444', '#64748b'];
        main.appendChild(el('div', { className: 'bowire-ws-detail-section' },
            el('div', { className: 'bowire-ws-detail-section-label', textContent: 'Color' }),
            el('div', { className: 'bowire-ws-detail-color-row' },
                palette.map(function (c) {
                    return el('button', {
                        className: 'bowire-ws-detail-color-swatch' + ((meta.color === c) ? ' selected' : ''),
                        style: 'background:' + c,
                        title: c,
                        onClick: function () { setUrlMeta(u, { color: c }); render(); }
                    });
                }),
                meta.color ? el('button', {
                    className: 'bowire-ws-detail-color-swatch',
                    style: 'background:transparent; border:1px dashed var(--bowire-border); color:var(--bowire-text-tertiary); font-size:14px;',
                    title: 'Clear color',
                    textContent: '×',
                    onClick: function () { setUrlMeta(u, { color: null }); render(); }
                }) : null
            )
        ));

        main.appendChild(el('div', { className: 'bowire-ws-detail-section' },
            el('div', { className: 'bowire-ws-detail-section-label', textContent: 'Discovery' }),
            el('div', { className: 'bowire-ws-detail-stats' },
                el('div', { className: 'bowire-ws-detail-stat' },
                    el('div', { className: 'bowire-ws-detail-stat-value', textContent: String(svcList.length) }),
                    el('div', { className: 'bowire-ws-detail-stat-label', textContent: 'Services' })
                ),
                el('div', { className: 'bowire-ws-detail-stat' },
                    el('div', { className: 'bowire-ws-detail-stat-value', textContent: String(methodN) }),
                    el('div', { className: 'bowire-ws-detail-stat-label', textContent: 'Methods' })
                ),
                el('div', { className: 'bowire-ws-detail-stat' },
                    el('div', { className: 'bowire-ws-detail-stat-value', textContent: String(Object.keys(protoCounts).length || '—') }),
                    el('div', { className: 'bowire-ws-detail-stat-label', textContent: 'Protocols' }),
                    el('div', { className: 'bowire-ws-detail-stat-hint', textContent: Object.keys(protoCounts).join(', ') || 'no protocols detected' })
                )
            )
        ));

        var actions = el('div', { className: 'bowire-ws-detail-section' },
            el('div', { className: 'bowire-ws-detail-section-label', textContent: 'Actions' }),
            el('div', { style: 'display:flex; gap:8px; flex-wrap:wrap;' },
                el('button', {
                    className: 'bowire-settings-action-btn',
                    textContent: 'Refresh discovery',
                    onClick: function () {
                        if (typeof refreshServices === 'function') refreshServices(u);
                        else if (typeof onServerUrlChanged === 'function') onServerUrlChanged();
                    }
                }),
                el('button', {
                    className: 'bowire-settings-action-btn',
                    textContent: 'Open in Discover',
                    onClick: function () {
                        railMode = 'discover';
                        sidebarView = 'services';
                        try { localStorage.setItem('bowire_rail_mode', 'discover'); } catch { /* ignore */ }
                        render();
                    }
                })
            )
        );
        main.appendChild(actions);

        // Schema-file management + per-URL headers are placeholders for
        // the next pass — drop zone + key/value editor land in
        // follow-ups so this rail mode ships in a usable state today.
        main.appendChild(el('div', { className: 'bowire-ws-detail-section' },
            el('div', { className: 'bowire-ws-detail-section-label', textContent: 'Schema files' }),
            el('p', {
                className: 'bowire-sources-hint',
                style: 'color:var(--bowire-text-tertiary); font-size:12px;',
                textContent: 'Drop-zone + Replace / Remove actions land in #152 follow-up.'
            })
        ));
        // #152 v2 — per-URL headers editor. Applied to every
        // request invoked against this URL by the api.js request-
        // header builder.
        var headers = (typeof getUrlHeaders === 'function') ? getUrlHeaders(u) : {};
        var headerRows = el('div', { className: 'bowire-url-headers-list' });
        var headerKeys = Object.keys(headers).sort();
        headerKeys.forEach(function (k) {
            headerRows.appendChild(el('div', { className: 'bowire-url-header-row' },
                el('input', {
                    type: 'text',
                    className: 'bowire-url-header-key',
                    value: k,
                    placeholder: 'Header name',
                    onChange: function (e) {
                        var newKey = String(e.target.value || '').trim();
                        if (!newKey || newKey === k) return;
                        var v = headers[k];
                        deleteUrlHeader(u, k);
                        setUrlHeader(u, newKey, v);
                        render();
                    }
                }),
                el('input', {
                    type: 'text',
                    className: 'bowire-url-header-value',
                    value: headers[k],
                    placeholder: 'Value (supports {{vars}})',
                    onChange: function (e) {
                        setUrlHeader(u, k, String(e.target.value || ''));
                    }
                }),
                el('button', {
                    type: 'button',
                    className: 'bowire-list-row-delete',
                    title: 'Remove header',
                    innerHTML: svgIcon('trash'),
                    style: 'opacity:1;',
                    onClick: function () {
                        deleteUrlHeader(u, k);
                        render();
                    }
                })
            ));
        });
        if (headerKeys.length === 0) {
            headerRows.appendChild(el('p', {
                className: 'bowire-sources-hint',
                style: 'color:var(--bowire-text-tertiary); font-size:12px; margin:0;',
                textContent: 'No headers configured. Add one to send it with every request to this URL.'
            }));
        }
        main.appendChild(el('div', { className: 'bowire-ws-detail-section' },
            el('div', { className: 'bowire-ws-detail-section-label', textContent: 'Per-URL headers' }),
            headerRows,
            el('button', {
                className: 'bowire-settings-action-btn',
                style: 'align-self:flex-start; margin-top:4px;',
                textContent: '+ Add header',
                onClick: function () {
                    bowirePrompt('Header name', {
                        title: 'Add header',
                        placeholder: 'Authorization',
                        confirmText: 'Add',
                    }).then(function (name) {
                        if (!name) return;
                        if (headers[name] !== undefined) { toast('Header already exists', 'info'); return; }
                        setUrlHeader(u, name, '');
                        render();
                    });
                }
            })
        ));

        if (!config.lockServerUrl && serverUrls.length > 1) {
            main.appendChild(el('div', { className: 'bowire-ws-detail-section bowire-ws-detail-danger' },
                el('div', { className: 'bowire-ws-detail-section-label', textContent: 'Danger zone' }),
                el('button', {
                    className: 'bowire-settings-action-btn bowire-ws-detail-delete-btn',
                    textContent: 'Remove this URL',
                    onClick: function () {
                        var snapshot = u;
                        bowireConfirm(
                            'Remove "' + snapshot + '" from the workspace? Discovered services for this URL are dropped from the in-memory cache.',
                            function () {
                                if (typeof removeServerUrl === 'function') removeServerUrl(snapshot);
                                else {
                                    var ri = serverUrls.indexOf(snapshot);
                                    if (ri >= 0) serverUrls.splice(ri, 1);
                                    if (typeof persistServerUrls === 'function') persistServerUrls();
                                }
                                if (sourcesSelectedUrl === snapshot) sourcesSelectedUrl = serverUrls[0] || null;
                                if (typeof onServerUrlChanged === 'function') onServerUrlChanged();
                                render();
                            },
                            { title: 'Remove URL', confirmText: 'Remove', danger: true }
                        );
                    }
                })
            ));
        }

        return main;
    }

    // Portal home — band title strip. Icon + label + small sub-label.
    function _renderHomeBandTitle(iconName, title, subtitle) {
        return el('div', { className: 'bowire-home-band-title' },
            el('span', { className: 'bowire-home-band-title-icon', innerHTML: svgIcon(iconName) }),
            el('span', { className: 'bowire-home-band-title-label', textContent: title }),
            subtitle ? el('span', { className: 'bowire-home-band-title-sub', textContent: subtitle }) : null
        );
    }

    // One Continue-band tile — wide, accent-bordered, single-click
    // resume target. icon + section-label up top, primary text in the
    // middle, optional meta at the bottom.
    function _renderContinueTile(iconName, label, primary, meta, onClick) {
        return el('button', {
            className: 'bowire-home-continue-tile',
            type: 'button',
            onClick: onClick
        },
            el('div', { className: 'bowire-home-continue-tile-head' },
                el('span', { className: 'bowire-home-continue-tile-icon', innerHTML: svgIcon(iconName) }),
                el('span', { className: 'bowire-home-continue-tile-label', textContent: label })
            ),
            el('div', { className: 'bowire-home-continue-tile-primary', textContent: primary, title: primary }),
            meta ? el('div', { className: 'bowire-home-continue-tile-meta', textContent: meta }) : null
        );
    }

    // Six canonical Start actions. firstRun=true labels the same set
    // slightly differently (the operator hasn't seen the workspace
    // yet) but the grid layout and hit-areas are identical.
    function _renderHomeStartGrid(firstRun) {
        var grid = el('div', { className: 'bowire-home-start-grid' });
        function card(iconName, label, sub, onClick) {
            grid.appendChild(el('button', {
                className: 'bowire-home-start-card',
                type: 'button',
                onClick: onClick
            },
                el('span', { className: 'bowire-home-start-card-icon', innerHTML: svgIcon(iconName) }),
                el('span', { className: 'bowire-home-start-card-label', textContent: label }),
                el('span', { className: 'bowire-home-start-card-sub', textContent: sub })
            ));
        }
        card('plus', 'New request', 'Freeform any protocol', function () {
            if (typeof startFreeformRequest === 'function') startFreeformRequest();
            else {
                railMode = 'discover';
                try { localStorage.setItem('bowire_rail_mode', 'discover'); } catch { /* ignore */ }
                render();
            }
        });
        card('connect', firstRun ? 'Add a URL' : 'Add URL', 'Configure a source', function () {
            railMode = 'workspaces';
            try { localStorage.setItem('bowire_rail_mode', 'workspaces'); } catch { /* ignore */ }
            if (typeof workspacesSelectedId !== 'undefined') workspacesSelectedId = activeWorkspaceId;
            render();
        });
        card('list', 'Import collection', 'Postman / OpenAPI', function () {
            railMode = 'collections';
            try { localStorage.setItem('bowire_rail_mode', 'collections'); } catch { /* ignore */ }
            render();
        });
        card('record', 'Record session', 'Capture live calls', function () {
            if (typeof startRecording === 'function') {
                startRecording();
                railMode = 'discover';
                try { localStorage.setItem('bowire_rail_mode', 'discover'); } catch { /* ignore */ }
                render();
            }
        });
        card('server', 'Build a mock', 'Replay recordings', function () {
            railMode = 'mocks';
            try { localStorage.setItem('bowire_rail_mode', 'mocks'); } catch { /* ignore */ }
            render();
        });
        card('compass', 'Browse Discover', 'Explore services', function () {
            railMode = 'discover';
            try { localStorage.setItem('bowire_rail_mode', 'discover'); } catch { /* ignore */ }
            sidebarView = 'services';
            render();
        });
        return grid;
    }

    // #139 — Home tile (favorite or recent entry). Resolves the
    // service + method from the live discovery list so the tile
    // shows method type + protocol icon when available, and
    // gracefully degrades to plain text when the method isn't in
    // the current discovery (stale favorite).
    function renderHomeTile(serviceName, methodName, isFavorite) {
        var svc = services.find(function (s) { return s.name === serviceName; });
        var meth = svc && (svc.methods || []).find(function (m) { return m.name === methodName; });
        var available = !!(svc && meth);
        var proto = svc ? svc.source : null;
        var dir = meth ? (typeof methodDirection === 'function' ? methodDirection(meth) : 'neutral') : 'neutral';

        var tile = el('div', {
            className: 'bowire-home-tile' + (available ? '' : ' bowire-home-tile-stale'),
            'data-protocol': proto || 'default',
            'data-direction': dir,
            title: available
                ? 'Open in Discover'
                : 'Method no longer in discovery — connect to its server to use it',
            onClick: function () {
                if (!available) return;
                railMode = 'discover';
                try { localStorage.setItem('bowire_rail_mode', 'discover'); } catch { /* ignore */ }
                sidebarView = 'services';
                openTab(svc, meth);
            }
        });

        if (meth) {
            tile.appendChild(el('span', {
                className: 'bowire-home-tile-badge bowire-method-badge',
                dataset: { type: methodBadgeType(meth), direction: dir },
                textContent: methodBadgeText(meth),
            }));
        }
        tile.appendChild(el('div', { className: 'bowire-home-tile-method', textContent: methodName }));
        tile.appendChild(el('div', { className: 'bowire-home-tile-service', textContent: serviceName }));

        return tile;
    }

    function renderMain() {
        // #139 — Home rail mode. Default landing for first-time
        // users; cross-workflow launchpad. Phase 1 shows recent
        // activity + favorites as two grids; click opens the method
        // in Discover. Launch-picker ('what do you want to do with
        // this favorite?') lands in Phase 2 once more modes are
        // wired.
        if (railMode === 'home') {
            var homeMain = el('div', { id: 'bowire-main-home', className: 'bowire-main bowire-main-home' });
            var homeWrap = el('div', { className: 'bowire-home-wrap' });

            // Portal home (3 bands, left-aligned, consistent gutter):
            //   1. Continue — last method / collection / recording (3 tiles)
            //   2. Start    — action cards for the canonical entry points
            //   3. Favorites + Recent activity (the muscle-memory layer)
            //
            // Same horizontal padding + max-width across every band so
            // the page reads as one column from top to bottom instead of
            // a centered hero floating above a wider grid (which was
            // the prior layout's restless feel — user feedback).
            //
            // first-run state replaces the Continue band with a short
            // tagline + two onboarding CTAs so the layout stays
            // visually aligned but the content matches the new operator.
            var isFirstRun = typeof window.bowireDetectLandingState === 'function'
                && window.bowireDetectLandingState() === 'first-run';

            var recent = (typeof getRecentMethods === 'function') ? getRecentMethods() : [];
            var collections = (typeof collectionsList !== 'undefined' && Array.isArray(collectionsList)) ? collectionsList : [];
            var recordings = (typeof recordingsList !== 'undefined' && Array.isArray(recordingsList)) ? recordingsList : [];

            // ---- Band 1: Continue (or first-run onboarding) ----
            if (isFirstRun) {
                var firstRunBand = el('div', { className: 'bowire-home-band bowire-home-band-firstrun' });
                // Same Start title as the returning-operator branch
                // below so every Home band carries a consistent header
                // — without it the cards row floats labelless above
                // Favorites / Recent, which the operator reads as a
                // missing section title.
                firstRunBand.appendChild(_renderHomeBandTitle('rocket', 'Start','Pick what you want to do'));
                firstRunBand.appendChild(_renderHomeStartGrid(true));
                homeWrap.appendChild(firstRunBand);
            } else {
                var hasContinueItems = recent.length > 0 || collections.length > 0 || recordings.length > 0;
                if (hasContinueItems) {
                    var continueBand = el('div', { className: 'bowire-home-band' });
                    continueBand.appendChild(_renderHomeBandTitle('replay', 'Continue', 'Pick up where you left off'));
                    var continueGrid = el('div', { className: 'bowire-home-continue-grid' });
                    if (recent.length > 0) {
                        continueGrid.appendChild(_renderContinueTile(
                            'compass', 'Last method', recent[0].service + ' / ' + recent[0].method,
                            'Open in Discover',
                            function () {
                                var r = recent[0];
                                var svc = (services || []).find(function (s) { return s.name === r.service; });
                                var mth = svc && (svc.methods || []).find(function (m) { return m.name === r.method; });
                                if (svc && mth && typeof openTab === 'function') {
                                    railMode = 'discover';
                                    try { localStorage.setItem('bowire_rail_mode', 'discover'); } catch { /* ignore */ }
                                    openTab(svc, mth);
                                } else {
                                    railMode = 'discover';
                                    try { localStorage.setItem('bowire_rail_mode', 'discover'); } catch { /* ignore */ }
                                    render();
                                }
                            }));
                    }
                    if (collections.length > 0) {
                        var col = collections[0];
                        continueGrid.appendChild(_renderContinueTile(
                            'list', 'Last collection', col.name,
                            (col.items && col.items.length) + ' item' + (col.items && col.items.length === 1 ? '' : 's'),
                            function () {
                                if (typeof collectionManagerSelectedId !== 'undefined') collectionManagerSelectedId = col.id;
                                railMode = 'collections';
                                try { localStorage.setItem('bowire_rail_mode', 'collections'); } catch { /* ignore */ }
                                render();
                            }));
                    }
                    if (recordings.length > 0) {
                        var rec = recordings[0];
                        continueGrid.appendChild(_renderContinueTile(
                            'recording', 'Last recording', rec.name || ('Recording ' + rec.id),
                            (rec.steps && rec.steps.length) + ' step' + (rec.steps && rec.steps.length === 1 ? '' : 's'),
                            function () {
                                if (typeof recordingManagerSelectedId !== 'undefined') recordingManagerSelectedId = rec.id;
                                railMode = 'recordings';
                                try { localStorage.setItem('bowire_rail_mode', 'recordings'); } catch { /* ignore */ }
                                render();
                            }));
                    }
                    continueBand.appendChild(continueGrid);
                    homeWrap.appendChild(continueBand);
                }

                // ---- Band 2: Start ----
                var startBand = el('div', { className: 'bowire-home-band' });
                startBand.appendChild(_renderHomeBandTitle('rocket', 'Start','Kick off a new use case'));
                startBand.appendChild(_renderHomeStartGrid(false));
                homeWrap.appendChild(startBand);
            }

            // ---- Band 3: Favorites + Recent activity ----
            var sectionsBand = el('div', { className: 'bowire-home-band' });
            var sections = el('div', { className: 'bowire-home-sections' });

            var favs = (typeof getFavorites === 'function') ? getFavorites() : [];
            var favSection = el('div', { className: 'bowire-home-section' });
            favSection.appendChild(el('h3', { className: 'bowire-home-section-title' },
                el('span', { innerHTML: svgIcon('starFilled'), style: 'color:var(--bowire-accent)' }),
                el('span', { textContent: 'Favorites' }),
                el('span', { className: 'bowire-home-section-count', textContent: favs.length + (favs.length === 1 ? ' entry' : ' entries') })
            ));
            if (favs.length === 0) {
                favSection.appendChild(renderEmptyCard({
                    icon: 'star',
                    headline: 'No favorites yet',
                    body: 'Star a method from the sidebar or any recent entry below — it lands here for one-click access across every workflow.',
                    hintKey: 'bowire_home_favs_hint'
                }));
            } else {
                var favGrid = el('div', { className: 'bowire-home-grid' });
                favs.forEach(function (fav) {
                    favGrid.appendChild(renderHomeTile(fav.service, fav.method, true));
                });
                favSection.appendChild(favGrid);
            }
            sections.appendChild(favSection);

            var recentSection = el('div', { className: 'bowire-home-section' });
            recentSection.appendChild(el('h3', { className: 'bowire-home-section-title' },
                el('span', { innerHTML: svgIcon('history') }),
                el('span', { textContent: 'Recent activity' }),
                el('span', { className: 'bowire-home-section-count', textContent: recent.length + (recent.length === 1 ? ' entry' : ' entries') })
            ));
            if (recent.length === 0) {
                recentSection.appendChild(renderEmptyCard({
                    icon: 'console',
                    headline: 'No recent activity',
                    body: 'Open methods in Discover and they land here in MRU order.'
                }));
            } else {
                var recentGrid = el('div', { className: 'bowire-home-grid' });
                recent.slice(0, 10).forEach(function (r) {
                    recentGrid.appendChild(renderHomeTile(r.service, r.method, false));
                });
                recentSection.appendChild(recentGrid);
            }
            sections.appendChild(recentSection);
            sectionsBand.appendChild(sections);
            homeWrap.appendChild(sectionsBand);

            homeMain.appendChild(homeWrap);
            return homeMain;
        }

        // #131 Phase 1 — Benchmarks ships the single-method shape;
        // collection / recording / random / scheduled probes land in
        // later phases. Renderer lives in benchmarks.js.
        if (railMode === 'benchmarks') {
            return renderBenchmarksDetailMain();
        }

        // #132 — Parallel sessions still a preview shell until phase 1
        // lands. Empty card links to the tracking issue so operators
        // see what's coming without falling into a dead end.
        if (railMode === 'parallel') {
            var stubMain = el('div', { id: 'bowire-main-parallel', className: 'bowire-main bowire-main-stub' });
            var stubWrap = el('div', { style: 'padding:24px' });
            stubWrap.appendChild(renderEmptyCard({
                icon: 'lightning',
                headline: 'Parallel sessions (preview)',
                body: 'Multi-session execution for recordings + collections. Phase 1 runs N concurrent sessions locally with per-session env slots; phase 2 distributes across linked Bowire nodes for geographic load or coordinated stress. Full implementation tracked in #132.',
                actions: [{
                    label: 'Open Parallel spec (#132)',
                    primary: true,
                    onClick: function () {
                        window.open('https://github.com/Kuestenlogik/Bowire/issues/132', '_blank', 'noopener');
                    }
                }]
            }));
            stubMain.appendChild(stubWrap);
            return stubMain;
        }

        // #133 Phase 2 — Collections rail mode. Sidebar lists every
        // saved collection; main pane shows the selected collection's
        // detail (renderCollectionDetail handles items + actions).
        // Same shape as Recordings; reuses the legacy modal's right-
        // pane renderer so the visual feels familiar to operators
        // muscle-trained on the old modal flow.
        if (railMode === 'collections') {
            var colMain = el('div', { id: 'bowire-main-collections', className: 'bowire-main bowire-main-collections' });
            var selectedCol = (collectionsList || []).find(function (c) { return c.id === collectionManagerSelectedId; });
            if (selectedCol && typeof renderCollectionDetail === 'function') {
                colMain.appendChild(renderCollectionDetail(selectedCol));
            } else {
                var emptyWrap = el('div', { style: 'padding:32px' });
                var noCols = !(collectionsList && collectionsList.length > 0);
                emptyWrap.appendChild(renderEmptyCard({
                    icon: 'list',
                    headline: noCols ? 'No collections yet' : 'Pick a collection',
                    body: noCols
                        ? 'Collections group saved requests so you can replay them as a set. Start fresh, or import a Postman collection / OpenAPI spec.'
                        : 'Pick a collection from the sidebar to see its items and actions.',
                    actions: noCols ? [
                        {
                            label: 'New collection',
                            primary: true,
                            onClick: function () {
                                var col = createCollection();
                                collectionManagerSelectedId = col.id;
                                render();
                            }
                        },
                        {
                            label: 'Import Postman / OpenAPI',
                            onClick: function () {
                                var input = document.createElement('input');
                                input.type = 'file';
                                input.accept = '.json,application/json';
                                input.onchange = function () {
                                    if (!input.files || input.files.length === 0) return;
                                    var reader = new FileReader();
                                    reader.onload = function () {
                                        if (typeof importPostmanCollection === 'function') {
                                            importPostmanCollection(reader.result);
                                            render();
                                        }
                                    };
                                    reader.readAsText(input.files[0]);
                                };
                                input.click();
                            }
                        },
                        {
                            label: 'Browse Discover',
                            onClick: function () {
                                railMode = 'discover';
                                try { localStorage.setItem('bowire_rail_mode', 'discover'); } catch { /* ignore */ }
                                sidebarView = 'services';
                                render();
                            }
                        }
                    ] : []
                }));
                colMain.appendChild(emptyWrap);
            }
            return colMain;
        }

        // #133 Phase 2 — Mocks rail mode owns the main pane. Sidebar
        // lists running mock hosts; main pane shows the selected
        // mock's detail (URL + copy + stop + a live request-log
        // toggle). Mirrors the table the legacy manager modal
        // (#94 #56) renders, but inlined into the workbench instead
        // of a popup.
        if (railMode === 'mocks') {
            var mockMain = el('div', { id: 'bowire-main-mocks', className: 'bowire-main bowire-main-mocks' });
            var mockMainWrap = el('div', { className: 'bowire-mocks-wrap', style: 'padding:24px' });

            var selectedMock = (mocksList || []).find(function (m) { return m.mockId === mockSelectedId; });

            if (!selectedMock) {
                mockMainWrap.appendChild(renderEmptyCard({
                    icon: 'server',
                    headline: (mocksList && mocksList.length > 0) ? 'Pick a mock' : 'No mocks running',
                    body: (mocksList && mocksList.length > 0)
                        ? 'Pick a running mock from the sidebar to see its URL, live request log, and stop control.'
                        : 'Mocks are spun up from a recording — switch to the Recordings mode and use "Run as mock" on any session.',
                    actions: (mocksList && mocksList.length > 0) ? [] : [{
                        label: 'Go to Recordings',
                        primary: true,
                        onClick: function () {
                            railMode = 'recordings';
                            try { localStorage.setItem('bowire_rail_mode', 'recordings'); } catch { /* ignore */ }
                            render();
                        }
                    }]
                }));
            } else {
                var url = 'http://127.0.0.1:' + selectedMock.port;
                mockMainWrap.appendChild(el('h2', {
                    className: 'bowire-sources-title',
                    textContent: selectedMock.recordingName || ('mock-' + selectedMock.port)
                }));
                mockMainWrap.appendChild(el('p', {
                    className: 'bowire-sources-subtitle',
                    textContent: 'Mock host on port ' + selectedMock.port + ' · started ' + (selectedMock.startedAt || 'unknown')
                }));

                // URL card with copy + open buttons
                var urlCard = el('div', { className: 'bowire-mocks-url-card' });
                urlCard.appendChild(el('code', { className: 'bowire-mocks-url', textContent: url }));
                urlCard.appendChild(el('button', {
                    className: 'bowire-empty-card-action',
                    textContent: 'Copy URL',
                    onClick: function () {
                        if (navigator.clipboard) {
                            navigator.clipboard.writeText(url).then(function () {
                                toast('Mock URL copied: ' + url, 'success');
                            });
                        }
                    }
                }));
                urlCard.appendChild(el('a', {
                    className: 'bowire-empty-card-action',
                    href: url,
                    target: '_blank',
                    rel: 'noopener',
                    textContent: 'Open in tab'
                }));
                urlCard.appendChild(el('button', {
                    className: 'bowire-empty-card-action bowire-recording-action-danger',
                    textContent: 'Stop mock',
                    onClick: function () {
                        if (typeof stopMock === 'function') stopMock(selectedMock.mockId);
                    }
                }));
                mockMainWrap.appendChild(urlCard);

                // Live log toggle + display
                var logCard = el('div', { className: 'bowire-mocks-log-card', style: 'margin-top:16px' });
                var logOpen = mockLogOpenFor === selectedMock.mockId;
                var logState = (typeof mockLogState !== 'undefined' && mockLogState[selectedMock.mockId])
                    ? mockLogState[selectedMock.mockId]
                    : { total: 0, entries: [] };
                logCard.appendChild(el('div', { className: 'bowire-sources-section', style: 'display:flex;align-items:center;gap:8px' },
                    el('span', { textContent: 'Live request log' }),
                    el('span', { className: 'bowire-home-section-count', textContent: logState.total + ' request' + (logState.total === 1 ? '' : 's') }),
                    el('button', {
                        className: 'bowire-empty-card-action',
                        textContent: logOpen ? 'Pause polling' : 'Start polling',
                        onClick: function () {
                            if (mockLogOpenFor === selectedMock.mockId) {
                                mockLogOpenFor = null;
                                if (typeof stopMockLogPolling === 'function') stopMockLogPolling(selectedMock.mockId);
                            } else {
                                mockLogOpenFor = selectedMock.mockId;
                                if (typeof startMockLogPolling === 'function') startMockLogPolling(selectedMock.mockId);
                            }
                            render();
                        }
                    })
                ));
                if (logOpen) {
                    if (!logState.entries.length) {
                        logCard.appendChild(el('p', {
                            className: 'bowire-sources-hint',
                            textContent: 'No requests yet. Fire one against ' + url + ' and it shows up here.'
                        }));
                    } else {
                        var ul = el('ul', { className: 'bowire-mocks-log-list', style: 'margin:8px 0 0;padding:0;list-style:none' });
                        logState.entries.slice(0, 20).forEach(function (e, i) {
                            var li = el('li', { style: 'padding:6px 0;border-top:1px solid var(--bowire-border-subtle);font-size:12px;font-family:var(--bowire-font-mono)' });
                            li.textContent = '[' + (e.timestamp || '?') + '] ' + (e.method || 'REQ') + ' ' + (e.path || '/') + ' → ' + (e.status || '?');
                            ul.appendChild(li);
                        });
                        logCard.appendChild(ul);
                    }
                }
                mockMainWrap.appendChild(logCard);
            }

            mockMain.appendChild(mockMainWrap);
            return mockMain;
        }

        // #133 Phase 2 — Recordings rail mode owns the main pane.
        // Sidebar shows the recordings list; main pane shows the
        // selected recording's detail (steps + actions). When no
        // recording is selected (fresh entry, or after a delete),
        // an empty card guides the operator toward the next step.
        if (railMode === 'recordings') {
            var recMain = el('div', { id: 'bowire-main-recordings', className: 'bowire-main bowire-main-recordings' });
            var selectedRec = recordingsList.find(function (r) { return r.id === recordingManagerSelectedId; });
            if (selectedRec && typeof renderRecordingDetail === 'function') {
                recMain.appendChild(renderRecordingDetail(selectedRec));
            } else {
                var emptyWrap = el('div', { style: 'padding:32px' });
                var noRecs = recordingsList.length === 0;
                emptyWrap.appendChild(renderEmptyCard({
                    icon: 'recording',
                    headline: noRecs ? 'No recordings yet' : 'Pick a recording',
                    body: noRecs
                        ? 'Recordings capture a sequence of live calls so you can replay them, build mocks, or run them as benchmarks. Start one, then invoke methods from Discover.'
                        : 'Pick a recording from the sidebar list to see its steps and actions.',
                    actions: noRecs ? [
                        {
                            label: 'Start recording',
                            primary: true,
                            onClick: function () {
                                startRecording();
                                railMode = 'discover';
                                try { localStorage.setItem('bowire_rail_mode', 'discover'); } catch { /* ignore */ }
                                render();
                            }
                        },
                        {
                            label: 'Browse Discover',
                            onClick: function () {
                                railMode = 'discover';
                                try { localStorage.setItem('bowire_rail_mode', 'discover'); } catch { /* ignore */ }
                                render();
                            }
                        }
                    ] : []
                }));
                recMain.appendChild(emptyWrap);
            }
            return recMain;
        }

        // #133 Phase 2 — Security rail mode owns the main pane.
        // When the operator picks the 🛡️ Security icon on the rail,
        // the main pane swaps to the Security surface (threat-model,
        // tier toggle, ranked endpoints, template generator) that
        // used to live in the right-side drawer. Sidebar still shows
        // the services tree so the operator can drill into a method
        // from the same screen without losing the security view.
        if (railMode === 'workspaces') {
            return renderWorkspaceDetailMain();
        }

        if (railMode === 'sources') {
            return renderSourcesDetailMain();
        }

        if (railMode === 'security') {
            var secMain = el('div', { id: 'bowire-main-security', className: 'bowire-main bowire-main-security' });
            if (window.__bowireAi && typeof window.__bowireAi.renderSecurityPanel === 'function') {
                secMain.appendChild(window.__bowireAi.renderSecurityPanel());
            } else {
                secMain.appendChild(el('p', {
                    className: 'bowire-ai-empty',
                    style: 'padding:24px',
                    textContent: 'Security tools need Kuestenlogik.Bowire.Ai installed in the workbench process. Install the package + restart, or switch back to Discover via the rail.'
                }));
            }
            return secMain;
        }

        // #92 — Sources management view. When the operator clicks
        // the topbar connection pill (or otherwise sets sidebarView
        // to 'sources'), the main pane swaps to the full-width URL
        // + schema editor. Reuses the existing renderUrlBarRow
        // helper at unlocked / editable width; lockServerUrl still
        // forces readonly.
        if (sidebarView === 'sources') {
            var srcMain = el('div', { id: 'bowire-main-sources', className: 'bowire-main bowire-main-sources' });
            var srcWrap = el('div', { className: 'bowire-sources-wrap' });
            srcWrap.appendChild(el('h2', { className: 'bowire-sources-title', textContent: 'Sources' }));
            srcWrap.appendChild(el('p', {
                className: 'bowire-sources-subtitle',
                textContent: config.lockServerUrl
                    ? 'URLs configured by the host — read-only.'
                    : 'Discovery URLs and schema files. Drop a schema below to import; type a URL to add it.'
            }));
            srcWrap.appendChild(el('h3', { className: 'bowire-sources-section', textContent: 'Discovery URLs' }));
            // Reuse the sidebar's URL bar at full main-pane width.
            // The renderUrlBarRow already understands the locked vs.
            // editable cases.
            srcWrap.appendChild(renderUrlBarRow(!!config.lockServerUrl));
            // Future: add schema-file drop zone, catalogue source
            // chip (#136), per-URL origin badge.
            srcWrap.appendChild(el('p', {
                className: 'bowire-sources-hint',
                textContent: 'Schema-file management and catalogue providers (#92, #136) land in subsequent commits.'
            }));
            srcMain.appendChild(srcWrap);
            return srcMain;
        }

        // ID encodes the current view mode so morphdom fully replaces
        // the main pane when switching between environments editor,
        // freeform builder, landing page, and the normal request/response
        // layout — preventing stale DOM and wrong closures.
        var mainViewKey = sidebarView === 'environments'
            ? 'env'
            : sidebarView === 'flows'
                ? 'flows-' + (flowEditorSelectedId || 'none')
                : sidebarView === 'proxy'
                    ? 'proxy-' + (proxyFlowSelectedId || 'none')
                    : freeformRequest
                        ? 'freeform'
                        : selectedMethod
                            ? (selectedService ? selectedService.name : '') + '-' + selectedMethod.name
                            : 'landing';
        const main = el('div', { id: 'bowire-main-' + mainViewKey, className: 'bowire-main' });

        // When the sidebar is in Environments view, the main pane
        // shows the full-width variable editor instead of the
        // request/response layout. This gives enough room for
        // multi-line values (JSON, certs, tokens).
        if (sidebarView === 'environments') {
            main.appendChild(renderEnvironmentEditor());
            return main;
        }

        // Flow canvas — visual node editor
        if (sidebarView === 'flows') {
            main.appendChild(renderFlowCanvas());
            return main;
        }

        // Proxy view — captured-flow detail pane
        if (sidebarView === 'proxy') {
            main.appendChild(renderProxyMainPane());
            return main;
        }

        // Header bar
        const header = el('div', { className: 'bowire-header' });
        const toggleBtn = el('button', {
            id: 'bowire-toggle-sidebar-btn',
            className: 'bowire-toggle-sidebar',
            onClick: function () { sidebarCollapsed = !sidebarCollapsed; render(); },
            innerHTML: svgIcon('sidebar'),
            title: 'Toggle sidebar',
            'aria-label': 'Toggle sidebar'
        });
        header.appendChild(toggleBtn);

        if (selectedMethod) {
            // Breadcrumb: Protocol > Service. The method name is rendered
            // separately as headerName below (at a much larger size), so
            // leaving it out of the breadcrumb avoids the visual stutter
            // that used to put two `listen`s next to each other.
            var breadcrumb = el('div', { className: 'bowire-breadcrumb' });
            if (selectedService && selectedService.source) {
                var proto = protocols.find(function (p) { return p.id === selectedService.source; });
                if (proto) {
                    breadcrumb.appendChild(el('span', {
                        className: 'bowire-breadcrumb-item clickable',
                        innerHTML: proto.icon ? '<span class="bowire-breadcrumb-icon">' + proto.icon + '</span>' : '',
                        onClick: function () {
                            // Close the active tab — goes back to landing
                            // if no tabs remain, or switches to an adjacent tab.
                            if (activeTabId) {
                                closeTab(activeTabId);
                            } else {
                                selectedMethod = null;
                                selectedService = null;
                                render();
                            }
                        }
                    }));
                    // BUG fix: this used to use 'bowire-breadcrumb-icon'
                    // (the 12×12 icon slot) for a multi-character text label,
                    // which let the protocol name overflow its container and
                    // collide with whatever sat to the right of the breadcrumb.
                    breadcrumb.appendChild(el('span', { className: 'bowire-breadcrumb-item', textContent: proto.name }));
                    breadcrumb.appendChild(el('span', { className: 'bowire-breadcrumb-sep', textContent: '\u203A' }));
                }
            }
            if (selectedService) {
                // Service is the breadcrumb's terminal segment now \u2014 the
                // method name is rendered separately as headerName below
                // (and at a much larger size), so duplicating it inside
                // the breadcrumb just produced a `\u2026 \u203A listen   listen`
                // visual stutter at narrow widths.
                // Trim the namespace prefix only when it looks like a
                // proto-style FQN (e.g. `weather.WeatherService` → `WeatherService`).
                // Service names that *end* with a dotted token (e.g. `Socket.IO`,
                // `Akka.Actor.Tap`) keep their full name — splitting them would
                // produce nonsense like `Socket.IO` → `IO`.
                var svcDisplayName = selectedService.name;
                if (svcDisplayName.includes('.')) {
                    var lastToken = svcDisplayName.split('.').pop();
                    if (lastToken.length >= 4) svcDisplayName = lastToken;
                }

                // When the service name matches the plugin's display name
                // exactly (e.g. Socket.IO plugin exposes one service literally
                // named 'Socket.IO'), drop this segment so the breadcrumb
                // doesn't read 'Socket.IO > Socket.IO'. Multi-service
                // protocols like gRPC have per-call distinct names so this
                // only kicks in for single-service plugins.
                var protoForCheck = selectedService.source
                    ? protocols.find(function (p) { return p.id === selectedService.source; })
                    : null;
                var skipServiceSegment = protoForCheck && protoForCheck.name === svcDisplayName;

                if (!skipServiceSegment) breadcrumb.appendChild(el('span', {
                    className: 'bowire-breadcrumb-item current',
                    textContent: svcDisplayName,
                    title: selectedService.name,
                    onClick: function () {
                        // Expand this service in the sidebar, close active tab
                        expandedServices.add(selectedService.name);
                        persistExpandedServices();
                        if (activeTabId) {
                            closeTab(activeTabId);
                        } else {
                            selectedMethod = null;
                            render();
                        }
                    }
                }));
            }

            var headerName = el('div', { className: 'bowire-header-method' + (selectedMethod.deprecated ? ' deprecated' : '') });
            headerName.appendChild(document.createTextNode(selectedMethod.name));
            if (selectedMethod.deprecated) {
                headerName.appendChild(el('span', { className: 'bowire-header-deprecated', textContent: 'DEPRECATED' }));
            }

            // Stack breadcrumb + method-name + path + summary vertically
            // inside the same column-flex container. Previously breadcrumb
            // sat in a sibling row to the right of the toggle button, so a
            // short Protocol > Service trail and a short method name landed
            // on the same horizontal axis and overlapped at narrower widths.
            const info = el('div', { className: 'bowire-header-info' },
                breadcrumb,
                headerName,
                el('div', { className: 'bowire-header-path', textContent: selectedMethod.fullName })
            );
            // Optional one-line summary or first line of description
            var summary = selectedMethod.summary || selectedMethod.description;
            if (summary) {
                var firstLine = String(summary).split('\n')[0].trim();
                if (firstLine.length > 0) {
                    info.appendChild(el('div', { className: 'bowire-header-summary', textContent: firstLine }));
                }
            }
            header.appendChild(info);
            header.appendChild(el('span', {
                className: 'bowire-header-badge',
                dataset: { type: methodBadgeType(selectedMethod) },
                textContent: selectedMethod.httpMethod || methodBadgeLabel(selectedMethod.methodType)
            }));

            // #156 — favorite star in the method header. Toggle the
            // current method's favorite state from where the operator
            // is looking at it instead of having to navigate to Home.
            // Filled glyph = favorited, outline = not. Reads + writes
            // the same workspace-scoped store the Home page consumes.
            if (typeof isFavorite === 'function' && typeof toggleFavorite === 'function') {
                try {
                    var svcName = selectedService.name;
                    var mthName = selectedMethod.name;
                    var isStar = isFavorite(svcName, mthName);
                    header.appendChild(el('button', {
                        className: 'bowire-header-fav-btn' + (isStar ? ' active' : ''),
                        title: isStar ? 'Remove from favorites' : 'Add to favorites',
                        'aria-label': isStar ? 'Remove from favorites' : 'Add to favorites',
                        onClick: function () {
                            toggleFavorite(svcName, mthName);
                            render();
                        },
                        innerHTML: svgIcon(isStar ? 'starFilled' : 'star')
                    }));
                } catch (e) { console.warn('[favorites] header-star failed', e); }
            }

            // #296 — "Add to…" quick-action menu. Sits between the
            // favorite star and the hint chip. The trigger is a
            // single icon-only button; the popup lists the canonical
            // cross-feature destinations (Collection, Preset,
            // Benchmark, Recording). Each action snapshots the LIVE
            // request state at click-time (not render-time) — morphdom
            // preserves the dropdown node across renders so any captured
            // closure values would get stale.
            try {
                var svcNameForMenu = selectedService.name;
                var mthNameForMenu = selectedMethod.name;
                var addToWrap = el('div', { className: 'bowire-header-addto-wrap' });
                addToWrap.appendChild(el('button', {
                    id: 'bowire-header-addto-btn',
                    className: 'bowire-header-addto-btn' + (methodAddToMenuOpen ? ' active' : ''),
                    title: 'Add this method to…',
                    'aria-label': 'Add this method to…',
                    'aria-haspopup': 'menu',
                    'aria-expanded': methodAddToMenuOpen ? 'true' : 'false',
                    onClick: function (e) {
                        e.stopPropagation();
                        methodAddToMenuOpen = !methodAddToMenuOpen;
                        render();
                    },
                    innerHTML: svgIcon('plus')
                }));

                if (methodAddToMenuOpen) {
                    var menu = el('div', {
                        className: 'bowire-header-addto-menu',
                        role: 'menu',
                        onClick: function (e) { e.stopPropagation(); }
                    });

                    function _closeAddToMenu() {
                        methodAddToMenuOpen = false;
                    }
                    function _snapshotRequest() {
                        var liveSvc = selectedService;
                        var liveMth = selectedMethod;
                        if (!liveSvc || !liveMth) return null;
                        var body = (Array.isArray(requestMessages) && requestMessages[0]) || '{}';
                        var meta = {};
                        var metaRows = document.querySelectorAll('.bowire-metadata-row');
                        for (var mi = 0; mi < metaRows.length; mi++) {
                            var inputs = metaRows[mi].querySelectorAll('.bowire-metadata-input');
                            if (inputs.length === 2 && inputs[0].value.trim()) {
                                meta[inputs[0].value.trim()] = inputs[1].value;
                            }
                        }
                        return {
                            service: liveSvc.name,
                            method: liveMth.name,
                            methodType: liveMth.methodType || 'Unary',
                            protocol: liveSvc.source || selectedProtocol || 'grpc',
                            body: body,
                            messages: Array.isArray(requestMessages) ? requestMessages.slice() : [body],
                            metadata: Object.keys(meta).length > 0 ? meta : null,
                            serverUrl: liveSvc.originUrl || (Array.isArray(serverUrls) && serverUrls[0]) || null
                        };
                    }

                    // ---- Collection section ----
                    menu.appendChild(el('div', { className: 'bowire-header-addto-section', textContent: 'Collection' }));
                    var existingCols = (typeof collectionsList !== 'undefined' && Array.isArray(collectionsList)) ? collectionsList : [];
                    if (existingCols.length > 0) {
                        existingCols.slice(0, 6).forEach(function (col) {
                            menu.appendChild(el('button', {
                                className: 'bowire-header-addto-item',
                                role: 'menuitem',
                                onClick: function () {
                                    var snap = _snapshotRequest();
                                    if (!snap) return;
                                    addToCollection(col.id, snap);
                                    toast('Added to "' + col.name + '"', 'success');
                                    _closeAddToMenu();
                                    render();
                                }
                            },
                                el('span', { className: 'bowire-header-addto-item-icon', innerHTML: svgIcon('list') }),
                                el('span', { textContent: col.name }),
                                el('span', { className: 'bowire-header-addto-item-meta', textContent: col.items.length + (col.items.length === 1 ? ' entry' : ' entries') })
                            ));
                        });
                        if (existingCols.length > 6) {
                            menu.appendChild(el('div', {
                                className: 'bowire-header-addto-item-meta',
                                style: 'padding:4px 12px 0',
                                textContent: '+ ' + (existingCols.length - 6) + ' more in Collections'
                            }));
                        }
                    }
                    menu.appendChild(el('button', {
                        className: 'bowire-header-addto-item bowire-header-addto-item-create',
                        role: 'menuitem',
                        onClick: function () {
                            bowirePrompt('Collection name', {
                                title: 'New collection',
                                placeholder: 'e.g. Smoke tests',
                                confirmText: 'Create'
                            }).then(function (name) {
                                if (name === null) return;
                                var trimmed = String(name || '').trim();
                                var col = createCollection(trimmed || undefined);
                                var snap = _snapshotRequest();
                                if (snap) addToCollection(col.id, snap);
                                toast('Saved to "' + col.name + '"', 'success');
                                _closeAddToMenu();
                                render();
                            });
                        }
                    },
                        el('span', { className: 'bowire-header-addto-item-icon', innerHTML: svgIcon('plus') }),
                        el('span', { textContent: 'New collection…' })
                    ));

                    // ---- Other destinations ----
                    menu.appendChild(el('div', { className: 'bowire-header-addto-section', textContent: 'Send to' }));

                    menu.appendChild(el('button', {
                        className: 'bowire-header-addto-item',
                        role: 'menuitem',
                        onClick: function () {
                            var snap = _snapshotRequest();
                            if (!snap) return;
                            bowirePrompt('Preset name', {
                                title: 'Save as preset',
                                placeholder: snap.method + ' preset',
                                confirmText: 'Save'
                            }).then(function (name) {
                                if (!name) return;
                                if (typeof savePresetFromSnapshot === 'function') {
                                    savePresetFromSnapshot('discover', String(name).trim(), snap);
                                    toast('Preset saved', 'success');
                                }
                                _closeAddToMenu();
                                render();
                            });
                        }
                    },
                        el('span', { className: 'bowire-header-addto-item-icon', innerHTML: svgIcon('layers') }),
                        el('span', { textContent: 'Save as preset' })
                    ));

                    if (typeof createBenchmarkSpec === 'function') {
                        menu.appendChild(el('button', {
                            className: 'bowire-header-addto-item',
                            role: 'menuitem',
                            onClick: function () {
                                var snap = _snapshotRequest();
                                if (!snap) return;
                                var spec = createBenchmarkSpec({
                                    name: snap.service + '.' + snap.method,
                                    service: snap.service,
                                    method: snap.method,
                                    protocol: snap.protocol,
                                    body: snap.body,
                                    metadata: snap.metadata || {}
                                });
                                if (typeof benchmarksSelectedId !== 'undefined' && spec) {
                                    benchmarksSelectedId = spec.id;
                                }
                                railMode = 'benchmarks';
                                try { localStorage.setItem('bowire_rail_mode', 'benchmarks'); } catch { /* ignore */ }
                                toast('Benchmark created', 'success');
                                _closeAddToMenu();
                                render();
                            }
                        },
                            el('span', { className: 'bowire-header-addto-item-icon', innerHTML: svgIcon('lightning') }),
                            el('span', { textContent: 'Create benchmark' })
                        ));
                    }

                    if (typeof startRecording === 'function') {
                        var recActive = (typeof recordingActive === 'boolean' && recordingActive)
                            || (typeof activeRecordingId !== 'undefined' && activeRecordingId);
                        menu.appendChild(el('button', {
                            className: 'bowire-header-addto-item' + (recActive ? ' bowire-header-addto-item-disabled' : ''),
                            role: 'menuitem',
                            disabled: recActive ? true : undefined,
                            title: recActive ? 'Already recording — new calls land in the active recording' : 'Start a new recording; subsequent calls land there',
                            onClick: function () {
                                if (recActive) return;
                                startRecording();
                                toast('Recording started — invoke the method to capture', 'success');
                                _closeAddToMenu();
                                render();
                            }
                        },
                            el('span', { className: 'bowire-header-addto-item-icon', innerHTML: svgIcon('record') }),
                            el('span', { textContent: recActive ? 'Recording in progress' : 'Start recording' })
                        ));
                    }

                    addToWrap.appendChild(menu);
                }
                header.appendChild(addToWrap);
                // svcNameForMenu / mthNameForMenu kept in case future
                // items need the labels at construction time.
                void svcNameForMenu; void mthNameForMenu;
            } catch (e) { console.warn('[addto] header-menu failed', e); }

            // #114 — inline hint chip at the method header. Surfaces the
            // deterministic hint engine's results at the place where the
            // operator looks for help (the method they're about to
            // invoke) instead of burying them in the Assistant drawer's
            // static list. Click opens the drawer to read the full
            // text; the chip itself is a one-glance affordance.
            // Phase 2 — only hints WITH surface='header' (or no surface
            // → defaults to header) appear here. Response/auth-targeted
            // hints render inline at their own surface; the drawer
            // still shows the full unfiltered list as the history view.
            if (typeof evaluateHintsForSurface === 'function') {
                try {
                    var hints = evaluateHintsForSurface('header');
                    if (hints && hints.length > 0) {
                        header.appendChild(el('button', {
                            className: 'bowire-header-hint-chip',
                            title: hints.length + ' hint' + (hints.length === 1 ? '' : 's') + ' for this method — click to open the Assistant',
                            'aria-label': 'Open Assistant hints (' + hints.length + ')',
                            onClick: function () {
                                aiDrawerOpen = true;
                                try { localStorage.setItem('bowire_ai_drawer_open', '1'); } catch { /* ignore */ }
                                render();
                            }
                        },
                            el('span', { className: 'bowire-header-hint-chip-icon', innerHTML: svgIcon('spark') }),
                            el('span', { className: 'bowire-header-hint-chip-count', textContent: String(hints.length) })
                        ));
                    }
                } catch { /* hint engine may throw on partial state — fail silent */ }
            }

            // Copy invoke URL — useful in embedded mode where there's no
            // visible URL bar. Builds a curl-friendly URL from the method.
            header.appendChild(el('button', {
                id: 'bowire-header-copy-url-btn',
                className: 'bowire-header-copy-url',
                title: 'Copy invoke URL',
                'aria-label': 'Copy invoke URL',
                innerHTML: svgIcon('copy'),
                onClick: function () {
                    var base = (selectedService && selectedService.originUrl)
                        || window.location.origin;
                    var invokeUrl = base + '/' + config.prefix + '/api/invoke'
                        + '?service=' + encodeURIComponent(selectedService ? selectedService.name : '')
                        + '&method=' + encodeURIComponent(selectedMethod.name);
                    navigator.clipboard.writeText(invokeUrl).then(function () {
                        toast('Copied invoke URL', 'success');
                    });
                }
            }));
        } else {
            header.appendChild(el('div', { className: 'bowire-header-info' },
                el('div', { className: 'bowire-header-method', textContent: config.title }),
                el('div', { className: 'bowire-header-path', textContent: 'Select a method from the sidebar' })
            ));
        }

        main.appendChild(header);

        // Request tab bar — visible when 2+ tabs are open so the user
        // #123 — tab strip is always present once there's at least one
        // open tab, plus a "+" button so the operator can spawn a
        // fresh blank tab without going through the sidebar. The
        // previous "show only when >=2" heuristic hid the affordance
        // exactly when the user needed to discover it.
        if (requestTabs.length >= 1) {
            var tabBar = el('div', { id: 'bowire-request-tabs', className: 'bowire-request-tabs' });
            var tabScroll = el('div', { className: 'bowire-request-tabs-scroll' });
            for (var ti = 0; ti < requestTabs.length; ti++) {
                (function (tab) {
                    var isActive = tab.id === activeTabId;
                    var dir = methodDirection(tab.method);
                    var proto = (tab.service && tab.service.source) || 'default';
                    var tabEl = el('div', {
                        className: 'bowire-request-tab' + (isActive ? ' active' : ''),
                        title: tab.serviceKey + ' / ' + tab.methodKey,
                        'data-protocol': proto,
                        'data-direction': dir,
                        onClick: function () { switchTab(tab.id); }
                    },
                        el('span', {
                            className: 'bowire-request-tab-proto',
                            dataset: { type: methodBadgeType(tab.method), direction: dir },
                            textContent: methodBadgeText(tab.method)
                        }),
                        el('span', {
                            className: 'bowire-request-tab-name',
                            textContent: tab.methodKey
                        }),
                        el('button', {
                            className: 'bowire-request-tab-close',
                            innerHTML: svgIcon('close'),
                            title: 'Close tab (Ctrl+W)',
                            onClick: function (e) {
                                e.stopPropagation();
                                closeTab(tab.id);
                            }
                        })
                    );
                    tabScroll.appendChild(tabEl);
                })(requestTabs[ti]);
            }
            // "+" tab — opens a freeform request pane (no service /
            // method picked yet). The user fills in URL + method by
            // hand or via the omnibox once #124 lands.
            tabScroll.appendChild(el('button', {
                className: 'bowire-request-tab-new',
                title: 'New tab (Ctrl+T)',
                onClick: function () {
                    startFreeformRequest();
                },
                innerHTML: svgIcon('plus')
            }));
            tabBar.appendChild(tabScroll);
            main.appendChild(tabBar);
        }

        // Proto-only warning banner
        if (selectedService && selectedService.source === 'proto') {
            var protoBanner = el('div', { className: 'bowire-proto-banner' },
                el('span', { innerHTML: svgIcon('info'), style: 'width:14px;height:14px;display:flex;flex-shrink:0' }),
                el('span', { textContent: 'Schema loaded from .proto file. Invocation requires gRPC Server Reflection enabled on the target server.' })
            );
            main.appendChild(protoBanner);
        }

        // Freeform request builder — manual request without discovery
        if (freeformRequest) {
            main.appendChild(renderFreeformRequestBuilder());
            return main;
        }

        if (!selectedMethod) {
            // Context-sensitive empty-state landing page — see landing.js for
            // the seven states (first-run / loading / discovery-failed /
            // editable-no-services / wrong-protocol-tab / multi-url-partial /
            // ready) and their detection rules. The renderer reads global
            // state (serverUrls, services, isLoadingServices, ...) and
            // dispatches to the matching renderState* function.
            renderLandingPage(main);
            return main;
        }

        // Content area: request + response panes. #135 split-mode
        // attribute drives whether the two panes sit side-by-side
        // (horizontal) or stacked (vertical) — CSS rules below the
        // attribute selector flip the flex-direction.
        const content = el('div', {
            className: 'bowire-content bowire-content-enter',
            'data-split': splitMode,
        });
        var reqPane = renderRequestPane();
        var resPane = renderResponsePane();
        var divider = el('div', { className: 'bowire-pane-divider' });
        content.appendChild(reqPane);
        content.appendChild(divider);
        content.appendChild(resPane);
        main.appendChild(content);

        // Initialize resizable pane divider after DOM attachment
        requestAnimationFrame(function () { initResizer(divider, reqPane, resPane); });

        // Action bar
        main.appendChild(renderActionBar());

        return main;
    }

    function saveMessageEditors() {
        var editors = $$('.bowire-message-editor');
        if (editors.length > 0) {
            requestMessages = editors.map(function (e) { return e.value; });
        } else {
            var single = $('.bowire-editor');
            if (single && single.value.trim()) {
                requestMessages = [single.value];
            }
        }
    }

    function renderRequestPane() {
        // Save current editor content before re-render
        saveMessageEditors();

        // ID includes the selected method so morphdom fully replaces the
        // pane when switching methods instead of reusing stale DOM with
        // wrong closures/editors from the previous method.
        var reqMethodKey = (selectedService ? selectedService.name : '')
            + '-' + (selectedMethod ? selectedMethod.name : '');
        const pane = el('div', { id: 'bowire-request-pane-' + reqMethodKey, className: 'bowire-pane' });

        // Channel methods use single-message mode (one message at a time)
        const isMultiMessage = selectedMethod && (selectedMethod.clientStreaming === true) && !isChannelMethod();

        // Tabs — every tab button and its paired content pane get a
        // stable id so morphdom uses keyed matching across renders
        // instead of matching by position. Without ids, switching
        // tabs (which swaps the content panes' `.active` class AND
        // rebuilds the body/metadata/schema sub-trees) would make
        // morphdom slide nodes by index and drag listeners onto the
        // wrong targets — the reason a second click on "Request
        // Body" after visiting Metadata couldn't re-activate the
        // body tab (old listener was attached to a node that now
        // represented metadata).
        const tabs = el('div', { id: 'bowire-request-tabs', className: 'bowire-tabs' });
        const bodyTabLabel = isMultiMessage ? 'Messages (' + requestMessages.length + ')' : 'Request Body';
        const bodyTab = el('div', {
            id: 'bowire-request-tab-body',
            className: `bowire-tab ${activeRequestTab === 'body' ? 'active' : ''}`,
            textContent: bodyTabLabel,
            onClick: function () { activeRequestTab = 'body'; render(); }
        });
        const metaTab = el('div', {
            id: 'bowire-request-tab-metadata',
            className: `bowire-tab ${activeRequestTab === 'metadata' ? 'active' : ''}`,
            textContent: 'Metadata',
            onClick: function () { activeRequestTab = 'metadata'; render(); }
        });
        const schemaTab = el('div', {
            id: 'bowire-request-tab-schema',
            className: `bowire-tab ${activeRequestTab === 'schema' ? 'active' : ''}`,
            textContent: 'Schema',
            onClick: function () { activeRequestTab = 'schema'; render(); }
        });

        // Form/JSON input-mode toggle used to live here as a pair of
        // pill buttons in the request-pane tab strip — always visible,
        // even when the user was on Metadata/Schema/History/Code where
        // it had no effect. Now it's part of the Body sub-tab strip
        // (#85), so the chrome only appears where it's actionable and
        // matches the GraphQL [Query/Variables/Selection set] pattern.
        var hasInputFields = selectedMethod && selectedMethod.inputType && selectedMethod.inputType.fields && selectedMethod.inputType.fields.length > 0;
        const historyTab = el('div', {
            id: 'bowire-request-tab-history',
            className: `bowire-tab ${activeRequestTab === 'history' ? 'active' : ''}`,
            textContent: 'History',
            onClick: function () { activeRequestTab = 'history'; render(); }
        });
        const codeTab = el('div', {
            id: 'bowire-request-tab-code',
            className: `bowire-tab ${activeRequestTab === 'code' ? 'active' : ''}`,
            textContent: 'Code',
            onClick: function () { activeRequestTab = 'code'; render(); }
        });
        const scriptsTab = el('div', {
            id: 'bowire-request-tab-scripts',
            className: `bowire-tab ${activeRequestTab === 'scripts' ? 'active' : ''}`,
            textContent: 'Scripts',
            onClick: function () { activeRequestTab = 'scripts'; render(); }
        });
        tabs.appendChild(bodyTab);
        tabs.appendChild(metaTab);
        tabs.appendChild(schemaTab);
        tabs.appendChild(codeTab);
        tabs.appendChild(scriptsTab);
        tabs.appendChild(historyTab);
        pane.appendChild(tabs);

        // Channel status bar (Duplex / Client Streaming)
        if (isChannelMethod()) {
            var statusClass = duplexConnected ? 'bowire-channel-connected' : 'bowire-channel-disconnected';
            var dotClass = duplexConnected ? 'bowire-pulse-dot' : 'bowire-channel-dot-grey';
            var statusText = duplexConnected ? 'Channel Open' : (statusInfo && statusInfo.status === 'Completed' ? 'Completed' : 'Disconnected');
            var channelStatus = el('div', { className: 'bowire-channel-status ' + statusClass },
                el('span', { className: dotClass }),
                el('span', { textContent: statusText })
            );
            if (duplexConnected || sentCount > 0 || receivedCount > 0) {
                var counters = el('div', { className: 'bowire-channel-counters' },
                    el('div', { className: 'bowire-channel-counter' },
                        el('span', { textContent: 'Sent: ' }),
                        el('span', { className: 'bowire-counter-sent', textContent: String(sentCount) })
                    ),
                    el('div', { className: 'bowire-channel-counter' },
                        el('span', { textContent: 'Received: ' }),
                        el('span', { className: 'bowire-counter-received', textContent: String(receivedCount) })
                    )
                );
                channelStatus.appendChild(counters);
            }
            pane.appendChild(channelStatus);
        }

        // Body tab — stable id paired with #bowire-request-tab-body
        // so morphdom keyed-matches this pane and never swaps it with
        // another tab-content pane by position accidentally.
        const bodyContent = el('div', { id: 'bowire-request-tab-body-content', className: `bowire-tab-content ${activeRequestTab === 'body' ? 'active' : ''}` });

        // Sub-tabs within the Body pane (#85). For GraphQL methods the
        // historical layout stacked Selection set + Query + Variables
        // form vertically; that eats screen real-estate when users
        // typically work in one surface at a time. We compute which
        // sub-tabs apply for the current method below, render a strip
        // when ≥ 2 apply (so single-surface protocols don't get
        // useless chrome), and gate each surface's render block on
        // the active sub-tab id.
        var bodySubTabs = [];
        var isGqlMethod = isGraphQLMethod();
        var hasGqlSelectionSet = isGqlMethod && selectedMethod && selectedMethod.outputType
            && selectedMethod.outputType.fields && selectedMethod.outputType.fields.length > 0;
        var hasGqlQuery = isGqlMethod && selectedMethod;
        var hasInputFormFields = selectedMethod && selectedMethod.inputType
            && selectedMethod.inputType.fields && selectedMethod.inputType.fields.length > 0;

        if (isGqlMethod) {
            // Order matters: Query first as the default sub-tab (it's
            // what gets POSTed); Variables next (most-edited secondary);
            // Selection set last (high-level structure).
            if (hasGqlQuery) bodySubTabs.push({ id: 'query', label: 'Query' });
            if (hasInputFormFields) bodySubTabs.push({ id: 'form', label: 'Variables' });
            if (hasGqlSelectionSet) bodySubTabs.push({ id: 'selection', label: 'Selection set' });
        } else if (hasInputFormFields && !isMultiMessage && !isChannelMethod()) {
            // REST / gRPC unary / JSON-RPC / OData / SOAP — the existing
            // Form vs. JSON toggle that lived in the top-pane tab strip
            // (mode-buttons next to Body/Metadata/Schema). Surface it as
            // Body sub-tabs instead so the chrome is consistent with the
            // GraphQL case and the toggle only appears when Body is the
            // active tab. We mirror the click into requestInputMode so
            // every existing code path that branches on it (sync helpers,
            // render gates further down) keeps working.
            // Protocol-specific labels match the conventions of each
            // ecosystem so the sub-tab strip reads native (#85): gRPC
            // calls a request a "Message" (single proto envelope);
            // JSON-RPC calls them "Params" (per the JSON-RPC 2.0 spec);
            // REST / SignalR / WebSocket / OData / SOAP fall back to
            // the generic "Form" / "Body" pair. JSON stays "JSON"
            // everywhere because it's the wire format, not a protocol
            // term.
            var src = selectedService && selectedService.source;
            var formLabel, jsonLabel;
            if (src === 'grpc') {
                formLabel = 'Message';
                jsonLabel = 'JSON';
            } else if (src === 'jsonrpc') {
                formLabel = 'Params';
                jsonLabel = 'JSON';
            } else if (src === 'rest' || src === 'odata') {
                formLabel = 'Form';
                jsonLabel = 'Body';
            } else {
                formLabel = 'Form';
                jsonLabel = 'JSON';
            }
            bodySubTabs.push({ id: 'form', label: formLabel });
            bodySubTabs.push({ id: 'json', label: jsonLabel });
            // Mirror activeBodySubTab → requestInputMode so the lower
            // render gates pick the right surface. The reverse mirror
            // happens in the legacy buttons' onClick (kept for now in
            // case anything still reads them); when those buttons are
            // dropped the mirror becomes one-way.
            if (activeBodySubTab === 'form' || activeBodySubTab === 'json') {
                requestInputMode = activeBodySubTab;
            }
        }

        // If the active sub-tab isn't applicable for this method
        // (switched from a method with a Selection set to one without,
        // for example), snap back to the first applicable tab.
        if (bodySubTabs.length >= 2 && !bodySubTabs.some(function (t) { return t.id === activeBodySubTab; })) {
            activeBodySubTab = bodySubTabs[0].id;
        }

        // Render the sub-tab strip when ≥ 2 sub-tabs apply.
        if (bodySubTabs.length >= 2) {
            var subTabBar = el('div', { className: 'bowire-sub-tabs', role: 'tablist' });
            for (var sti = 0; sti < bodySubTabs.length; sti++) {
                (function (t) {
                    subTabBar.appendChild(el('button', {
                        id: 'bowire-body-subtab-' + t.id,
                        className: 'bowire-sub-tab' + (activeBodySubTab === t.id ? ' active' : ''),
                        role: 'tab',
                        textContent: t.label,
                        onClick: function () {
                            // Form ↔ JSON swap needs the existing sync
                            // helpers so values stay in step. The legacy
                            // top-strip toggle did the same; preserving
                            // it here so the sub-tab is a drop-in.
                            if (activeBodySubTab === 'form' && t.id === 'json') {
                                syncFormToJson();
                            } else if (activeBodySubTab === 'json' && t.id === 'form') {
                                syncJsonToForm();
                            }
                            activeBodySubTab = t.id;
                            if (t.id === 'form' || t.id === 'json') {
                                requestInputMode = t.id;
                            }
                            render();
                        }
                    }));
                })(bodySubTabs[sti]);
            }
            bodyContent.appendChild(subTabBar);
        }

        // showSurface(name) is the gate for every surface block below.
        // Returns true when (a) there's no sub-tab strip (single
        // surface — render unconditionally), or (b) the active sub-tab
        // matches the requested surface. Keeps the existing per-protocol
        // render blocks intact — we only wrap them in the gate.
        function showSurface(name) {
            return bodySubTabs.length < 2 || activeBodySubTab === name;
        }

        // GraphQL: Selection-set picker pane above the query editor.
        // Renders a checkbox tree of the discovered output type. Toggling
        // a field updates graphqlSelections and re-renders so the query
        // editor below picks up the new selection automatically (provided
        // the user hasn't manually overridden the query — in that case the
        // override wins, which is a deliberate "manual edit > picker"
        // ordering).
        if (hasGqlSelectionSet && showSurface('selection')) {
            var selKey = graphqlMethodKey();
            var sels = getGraphQLSelections();

            var selHeader = el('div', { className: 'bowire-pane-header' },
                el('span', { className: 'bowire-pane-title', textContent: 'Selection set' }),
                el('div', { className: 'bowire-pane-actions' },
                    el('button', {
                        id: 'bowire-graphql-select-all-btn',
                        className: 'bowire-pane-btn',
                        textContent: 'Select all (top-level)',
                        onClick: function () {
                            for (var i = 0; i < selectedMethod.outputType.fields.length; i++) {
                                var f = selectedMethod.outputType.fields[i];
                                setGraphQLSelection(f.name, true);
                            }
                            // Drop any user query override so the new
                            // selection takes effect on the next render.
                            delete graphqlQueryOverrides[selKey];
                            render();
                        }
                    }),
                    el('button', {
                        id: 'bowire-graphql-clear-btn',
                        className: 'bowire-pane-btn',
                        textContent: 'Clear',
                        onClick: function () {
                            graphqlSelections[selKey] = {};
                            delete graphqlQueryOverrides[selKey];
                            render();
                        }
                    })
                )
            );

            var selTree = renderGraphQLSelectionTree(selectedMethod.outputType, '', sels, 0);
            var selBox = el('div', { className: 'bowire-graphql-selection-pane' }, selHeader, selTree);
            bodyContent.appendChild(selBox);
        }

        // GraphQL: Query editor pane above the variables form/JSON. Lets the
        // user override the auto-generated operation string with their own
        // selection set. Stored per-method in graphqlQueryOverrides — when
        // the user clicks "Reset to default" we drop the override and the
        // pane regenerates from the discovered method shape.
        if (hasGqlQuery && showSurface('query')) {
            var queryKey = graphqlMethodKey();
            var queryHeader = el('div', { className: 'bowire-pane-header' },
                el('span', { className: 'bowire-pane-title', textContent: 'GraphQL Query' }),
                el('div', { className: 'bowire-pane-actions' },
                    el('button', {
                        id: 'bowire-graphql-reset-query-btn',
                        className: 'bowire-pane-btn',
                        textContent: 'Reset to default',
                        onClick: function () {
                            delete graphqlQueryOverrides[queryKey];
                            render();
                        }
                    })
                )
            );
            var queryEditor = el('textarea', {
                className: 'bowire-graphql-query-editor',
                spellcheck: 'false',
                placeholder: 'GraphQL operation — edit to change the selection set'
            });
            queryEditor.value = getGraphQLQuery();
            queryEditor.addEventListener('input', function () {
                graphqlQueryOverrides[queryKey] = this.value;
            });
            queryEditor.addEventListener('keydown', function (e) {
                if (e.key === 'Tab') {
                    e.preventDefault();
                    var s = this.selectionStart, en = this.selectionEnd;
                    this.value = this.value.substring(0, s) + '  ' + this.value.substring(en);
                    this.selectionStart = this.selectionEnd = s + 2;
                    graphqlQueryOverrides[queryKey] = this.value;
                }
                if (e.key === 'Enter' && (e.ctrlKey || e.metaKey)) {
                    e.preventDefault();
                    handleExecute();
                }
            });
            var queryBox = el('div', { className: 'bowire-graphql-query-pane' }, queryHeader, queryEditor);
            bodyContent.appendChild(queryBox);
        }

        // WebSocket: outgoing frame type toggle + binary file upload. Sits
        // above the message editor so the user can pick text vs binary and
        // optionally drop a file in before clicking Send.
        if (isWebSocketMethod() && isChannelMethod()) {
            var wsHeader = el('div', { className: 'bowire-pane-header' },
                el('span', { className: 'bowire-pane-title', textContent: 'Frame type' }),
                el('div', { className: 'bowire-pane-actions' },
                    el('button', {
                        id: 'bowire-ws-frame-text-btn',
                        className: 'bowire-ws-frame-btn' + (websocketFrameType === 'text' ? ' active' : ''),
                        textContent: 'Text',
                        onClick: function () { websocketFrameType = 'text'; websocketPendingBinary = null; render(); }
                    }),
                    el('button', {
                        id: 'bowire-ws-frame-binary-btn',
                        className: 'bowire-ws-frame-btn' + (websocketFrameType === 'binary' ? ' active' : ''),
                        textContent: 'Binary',
                        onClick: function () { websocketFrameType = 'binary'; render(); }
                    })
                )
            );
            var wsBox = el('div', { className: 'bowire-ws-frame-pane' }, wsHeader);

            if (websocketFrameType === 'binary') {
                var fileInput = el('input', { type: 'file', className: 'bowire-ws-file-input' });
                fileInput.addEventListener('change', function () {
                    var file = this.files && this.files[0];
                    if (!file) return;
                    var reader = new FileReader();
                    reader.onload = function () {
                        // reader.result is a data URL: "data:...;base64,XXXX"
                        var url = reader.result || '';
                        var commaIdx = url.indexOf(',');
                        websocketPendingBinary = commaIdx >= 0 ? url.substring(commaIdx + 1) : '';
                        toast('Loaded ' + file.name + ' (' + file.size + ' bytes) — click Send to transmit', 'success');
                        render();
                    };
                    reader.readAsDataURL(file);
                });
                var fileRow = el('div', { className: 'bowire-ws-file-row' },
                    el('label', { className: 'bowire-ws-file-label', textContent: 'Pick a file → Send sends it as a single binary frame' }),
                    fileInput
                );
                wsBox.appendChild(fileRow);

                if (websocketPendingBinary) {
                    wsBox.appendChild(el('div', { className: 'bowire-ws-pending', textContent:
                        '\u2713 Binary payload ready (' + websocketPendingBinary.length + ' base64 chars)' }));
                }
            }

            bodyContent.appendChild(wsBox);
        }

        // For GraphQL methods the form/JSON editor surface lives behind
        // the 'form' sub-tab. Skip the whole block (multi-message,
        // form, JSON variants) when GraphQL is active and the user is
        // on a different sub-tab — that surface re-renders when they
        // switch back. Non-GraphQL methods fall through unchanged so
        // REST/gRPC/JSON-RPC keep their existing single-surface layout.
        var skipFormSurface = isGqlMethod && bodySubTabs.length >= 2 && activeBodySubTab !== 'form';
        if (skipFormSurface) {
            // Intentional no-op — sub-tab gate excludes this surface.
        } else if (isMultiMessage) {
            // Multi-message mode: header with Format All + Template All
            const paneHeader = el('div', { className: 'bowire-pane-header' },
                el('span', { className: 'bowire-pane-title', textContent: 'JSON Messages' }),
                el('div', { className: 'bowire-pane-actions' },
                    el('button', {
                        id: 'bowire-multi-msg-format-all-btn',
                        className: 'bowire-pane-btn',
                        textContent: 'Format All',
                        onClick: function () {
                            var editors = $$('.bowire-message-editor');
                            for (var i = 0; i < editors.length; i++) {
                                editors[i].value = formatJson(editors[i].value);
                                requestMessages[i] = editors[i].value;
                            }
                        }
                    }),
                    el('button', {
                        id: 'bowire-multi-msg-template-all-btn',
                        className: 'bowire-pane-btn',
                        textContent: 'Template All',
                        onClick: function () {
                            var editors = $$('.bowire-message-editor');
                            var tmpl = selectedMethod ? generateDefaultJson(selectedMethod.inputType, 0) : '{}';
                            for (var i = 0; i < editors.length; i++) {
                                editors[i].value = tmpl;
                                requestMessages[i] = tmpl;
                            }
                        }
                    })
                )
            );
            bodyContent.appendChild(paneHeader);

            // Message list container
            var messageList = el('div', { className: 'bowire-message-list' });

            for (var idx = 0; idx < requestMessages.length; idx++) {
                (function (i) {
                    var msgItem = el('div', { className: 'bowire-message-item' });

                    // Message header with number and remove button
                    var msgHeader = el('div', { className: 'bowire-message-header' },
                        el('span', { textContent: 'Message #' + (i + 1) })
                    );
                    if (requestMessages.length > 1) {
                        msgHeader.appendChild(el('button', {
                            id: 'bowire-msg-remove-' + i,
                            className: 'bowire-message-remove',
                            textContent: '\u00d7',
                            title: 'Remove message',
                            onClick: function () {
                                saveMessageEditors();
                                requestMessages.splice(i, 1);
                                render();
                            }
                        }));
                    }
                    msgItem.appendChild(msgHeader);

                    // Message editor textarea
                    var msgEditor = el('textarea', {
                        className: 'bowire-message-editor',
                        placeholder: 'Enter JSON message...',
                        spellcheck: 'false'
                    });
                    msgEditor.value = requestMessages[i] || '{}';
                    msgEditor.addEventListener('input', function () {
                        requestMessages[i] = this.value;
                    });
                    msgEditor.addEventListener('keydown', function (e) {
                        if (e.key === 'Tab') {
                            e.preventDefault();
                            var start = this.selectionStart;
                            var end = this.selectionEnd;
                            this.value = this.value.substring(0, start) + '  ' + this.value.substring(end);
                            this.selectionStart = this.selectionEnd = start + 2;
                            requestMessages[i] = this.value;
                        }
                        if (e.key === 'Enter' && (e.ctrlKey || e.metaKey)) {
                            e.preventDefault();
                            handleExecute();
                        }
                    });
                    msgItem.appendChild(msgEditor);
                    // Schema-based autocomplete for streaming message editors
                    if (selectedMethod && selectedMethod.inputType) {
                        attachBodyAutocomplete(msgEditor, selectedMethod.inputType);
                    }
                    // Per-message validation status — same component as
                    // the single-editor variant so a multi-message
                    // streaming send pinpoints which message is broken
                    // instead of failing the whole batch silently.
                    var msgJsonStatus = el('div', { className: 'bowire-json-status empty' });
                    msgItem.appendChild(msgJsonStatus);
                    attachJsonValidator(msgEditor, msgJsonStatus);
                    messageList.appendChild(msgItem);
                })(idx);
            }

            // Add Message button
            messageList.appendChild(el('button', {
                id: 'bowire-add-message-btn',
                className: 'bowire-add-message',
                onClick: function () {
                    saveMessageEditors();
                    var tmpl = selectedMethod ? generateDefaultJson(selectedMethod.inputType, 0) : '{}';
                    requestMessages.push(tmpl);
                    render();
                }
            },
                el('span', { textContent: '+' }),
                el('span', { textContent: 'Add Message' })
            ));

            bodyContent.appendChild(messageList);
        } else if (requestInputMode === 'form' && hasInputFields) {
            // Single-message mode: Form view
            const paneHeader = el('div', { className: 'bowire-pane-header' },
                el('span', { className: 'bowire-pane-title', textContent: 'Form' }),
                el('div', { className: 'bowire-pane-actions' },
                    el('button', {
                        id: 'bowire-form-reset-btn',
                        className: 'bowire-pane-btn',
                        textContent: 'Reset',
                        onClick: function () {
                            // Drop both the live form values and the
                            // cached per-method state so a return to
                            // this method starts from a clean default.
                            formValues = {};
                            if (selectedService && selectedMethod) {
                                clearMethodState(selectedService.name, selectedMethod.name);
                            }
                            render();
                        }
                    })
                )
            );
            bodyContent.appendChild(paneHeader);
            const body = el('div', { className: 'bowire-pane-body' });
            body.appendChild(renderFormFields(selectedMethod.inputType, '', 0));
            bodyContent.appendChild(body);
        } else {
            // Single-message mode: JSON editor (Unary / Server Streaming)
            const paneHeader = el('div', { className: 'bowire-pane-header' },
                el('span', { className: 'bowire-pane-title', textContent: 'JSON' }),
                el('div', { className: 'bowire-pane-actions' },
                    el('button', {
                        id: 'bowire-json-format-btn',
                        className: 'bowire-pane-btn',
                        textContent: 'Format',
                        onClick: function () {
                            var ed = $('.bowire-editor');
                            if (ed) { ed.value = formatJson(ed.value); requestMessages[0] = ed.value; }
                        }
                    }),
                    el('button', {
                        id: 'bowire-json-template-btn',
                        className: 'bowire-pane-btn',
                        textContent: 'Template',
                        onClick: function () {
                            var ed = $('.bowire-editor');
                            if (ed && selectedMethod) {
                                ed.value = generateDefaultJson(selectedMethod.inputType, 0);
                                requestMessages[0] = ed.value;
                            }
                        }
                    }),
                    el('button', {
                        id: 'bowire-json-import-btn',
                        className: 'bowire-pane-btn',
                        textContent: 'Import',
                        title: 'Import JSON from file (or drag & drop)',
                        onClick: function () {
                            var input = document.createElement('input');
                            input.type = 'file';
                            input.accept = '.json,application/json';
                            input.onchange = function () {
                                var file = input.files[0];
                                if (!file) return;
                                var reader = new FileReader();
                                reader.onload = function (ev) {
                                    var text = ev.target.result;
                                    try { text = JSON.stringify(JSON.parse(text), null, 2); } catch {}
                                    var ed = $('.bowire-editor');
                                    if (ed) { ed.value = text; requestMessages[0] = text; }
                                    toast('Imported ' + file.name, 'success');
                                };
                                reader.readAsText(file);
                            };
                            input.click();
                        }
                    }),
                    el('button', {
                        id: 'bowire-json-grpcurl-btn',
                        className: 'bowire-pane-btn',
                        textContent: 'grpcurl',
                        title: 'Copy request as grpcurl command',
                        onClick: function () {
                            if (!selectedService || !selectedMethod) return;
                            var body = requestMessages[0] || '{}';
                            var target = (selectedService && selectedService.originUrl) || getPrimaryServerUrl() || 'localhost:5001';
                            var plaintext = target.startsWith('http://');
                            var host = target.replace(/^https?:\/\//, '');
                            var cmd = 'grpcurl';
                            if (plaintext) cmd += ' -plaintext';
                            try { cmd += " -d '" + JSON.stringify(JSON.parse(body)).replace(/'/g, "'\\''") + "'"; }
                            catch { cmd += " -d '" + body.replace(/\n/g, ' ').replace(/'/g, "'\\''") + "'"; }
                            cmd += ' ' + host + ' ' + selectedService.name + '/' + selectedMethod.name;
                            navigator.clipboard.writeText(cmd).then(function () { toast('Copied grpcurl command', 'success'); });
                        }
                    })
                )
            );
            bodyContent.appendChild(paneHeader);
            const body = el('div', { className: 'bowire-pane-body' });
            const defaultJson = selectedMethod ? generateDefaultJson(selectedMethod.inputType, 0) : '{}';
            const editor = el('textarea', {
                className: 'bowire-editor',
                placeholder: 'Enter JSON request body...',
                spellcheck: 'false'
            });
            editor.value = (requestMessages[0] && requestMessages[0].trim()) ? requestMessages[0] : defaultJson;
            editor.addEventListener('input', function () {
                requestMessages[0] = this.value;
            });
            editor.addEventListener('keydown', function (e) {
                if (e.key === 'Tab') {
                    e.preventDefault();
                    const start = this.selectionStart;
                    const end = this.selectionEnd;
                    this.value = this.value.substring(0, start) + '  ' + this.value.substring(end);
                    this.selectionStart = this.selectionEnd = start + 2;
                    requestMessages[0] = this.value;
                }
                if (e.key === 'Enter' && (e.ctrlKey || e.metaKey)) {
                    e.preventDefault();
                    handleExecute();
                }
            });
            // Drag & Drop JSON file onto editor
            editor.addEventListener('dragover', function (e) {
                e.preventDefault();
                e.stopPropagation();
                editor.classList.add('bowire-editor-dragover');
            });
            editor.addEventListener('dragleave', function (e) {
                e.preventDefault();
                editor.classList.remove('bowire-editor-dragover');
            });
            editor.addEventListener('drop', function (e) {
                e.preventDefault();
                e.stopPropagation();
                editor.classList.remove('bowire-editor-dragover');
                var file = e.dataTransfer.files[0];
                if (file) {
                    var reader = new FileReader();
                    reader.onload = function (ev) {
                        var text = ev.target.result;
                        try { text = JSON.stringify(JSON.parse(text), null, 2); } catch {}
                        editor.value = text;
                        requestMessages[0] = text;
                        toast('Imported ' + file.name, 'success');
                    };
                    reader.readAsText(file);
                }
            });

            body.appendChild(editor);
            // Schema-based autocomplete for field names in the JSON editor
            if (selectedMethod && selectedMethod.inputType) {
                attachBodyAutocomplete(editor, selectedMethod.inputType);
            }
            // Inline JSON validation status. Lives directly under the
            // editor and flips green/red as the user types so syntax
            // errors surface before they hit Execute and round-trip a
            // 4xx/5xx through the wire.
            const jsonStatus = el('div', { className: 'bowire-json-status empty' });
            body.appendChild(jsonStatus);
            attachJsonValidator(editor, jsonStatus);
            bodyContent.appendChild(body);
        }
        pane.appendChild(bodyContent);

        // Metadata tab
        const metaContent = el('div', { id: 'bowire-request-tab-metadata-content', className: `bowire-tab-content ${activeRequestTab === 'metadata' ? 'active' : ''}` });
        const metaEditor = el('div', { className: 'bowire-metadata-editor' });

        // Preserve existing metadata rows
        const existingRows = $$('.bowire-metadata-row');
        if (existingRows.length > 0) {
            for (const row of existingRows) {
                const inputs = row.querySelectorAll('.bowire-metadata-input');
                if (inputs.length === 2) {
                    metaEditor.appendChild(createMetadataRow(inputs[0].value, inputs[1].value));
                }
            }
        } else {
            metaEditor.appendChild(createMetadataRow('', ''));
        }

        metaEditor.appendChild(el('button', {
            id: 'bowire-metadata-add-btn',
            className: 'bowire-metadata-add',
            textContent: '+ Add header',
            onClick: function () {
                this.parentElement.insertBefore(createMetadataRow('', ''), this);
            }
        }));
        metaContent.appendChild(metaEditor);
        pane.appendChild(metaContent);

        // Schema tab
        const schemaContent = el('div', { id: 'bowire-request-tab-schema-content', className: `bowire-tab-content ${activeRequestTab === 'schema' ? 'active' : ''}` });
        const schemaDiv = el('div', { className: 'bowire-schema' });

        if (selectedMethod) {
            // Input type
            const inputSection = el('div', { className: 'bowire-schema-section' });
            inputSection.appendChild(el('div', { className: 'bowire-schema-title' },
                el('span', { textContent: 'Input' }),
                el('span', { className: 'bowire-schema-type-name', textContent: selectedMethod.inputType.fullName })
            ));
            const inputFields = renderSchemaFields(selectedMethod.inputType.fields, 0);
            if (inputFields) inputSection.appendChild(inputFields);
            else inputSection.appendChild(el('div', { className: 'bowire-schema-field', textContent: 'No fields (empty message)' }));
            schemaDiv.appendChild(inputSection);

            // Output type
            const outputSection = el('div', { className: 'bowire-schema-section' });
            outputSection.appendChild(el('div', { className: 'bowire-schema-title' },
                el('span', { textContent: 'Output' }),
                el('span', { className: 'bowire-schema-type-name', textContent: selectedMethod.outputType.fullName })
            ));
            const outputFields = renderSchemaFields(selectedMethod.outputType.fields, 0);
            if (outputFields) outputSection.appendChild(outputFields);
            else outputSection.appendChild(el('div', { className: 'bowire-schema-field', textContent: 'No fields (empty message)' }));
            schemaDiv.appendChild(outputSection);
        }

        schemaContent.appendChild(schemaDiv);
        pane.appendChild(schemaContent);

        // History tab
        const historyContent = el('div', { id: 'bowire-request-tab-history-content', className: `bowire-tab-content ${activeRequestTab === 'history' ? 'active' : ''}` });
        const allHistory = getHistory();
        const methodFiltered = (selectedMethod && !showAllHistory)
            ? allHistory.filter(function (h) { return h.service === selectedService.name && h.method === selectedMethod.name; })
            : allHistory;
        const filtered = filterHistoryEntries(methodFiltered);

        if (allHistory.length === 0) {
            historyContent.appendChild(el('div', { className: 'bowire-empty-state', style: 'padding: 40px' },
                el('div', { className: 'bowire-empty-desc', textContent: 'No request history yet. Execute a request to see it here.' })
            ));
        } else {
            // Search + status filter bar — shown whenever there is any
            // history at all, so users can scan failures or grep across
            // every recorded request.
            const searchBar = el('div', { className: 'bowire-history-search-bar' });

            const searchInput = el('input', {
                className: 'bowire-history-search-input',
                type: 'search',
                placeholder: 'Search history (service, method, body, status)…'
            });
            searchInput.value = historySearchQuery;
            searchInput.addEventListener('input', function () {
                historySearchQuery = this.value;
                // Don't full-render on every keystroke — only re-render the
                // history list to keep typing snappy. Easiest path: render()
                // recomputes the whole pane, which is acceptable here because
                // the input has its own DOM identity preserved by Bowire's
                // diff-free rerender. We re-focus to prevent the cursor jump.
                var pos = this.selectionStart;
                render();
                requestAnimationFrame(function () {
                    var fresh = document.querySelector('.bowire-history-search-input');
                    if (fresh) {
                        fresh.focus();
                        try { fresh.setSelectionRange(pos, pos); } catch (e) { /* ignore */ }
                    }
                });
            });
            searchBar.appendChild(searchInput);

            const filterPills = el('div', { className: 'bowire-history-filter-pills' });
            const buckets = [
                { id: 'all', label: 'All' },
                { id: 'ok', label: 'OK' },
                { id: 'error', label: 'Errors' }
            ];
            for (var bi = 0; bi < buckets.length; bi++) {
                (function (bucket) {
                    filterPills.appendChild(el('button', {
                        id: 'bowire-history-filter-' + bucket.id,
                        className: 'bowire-history-filter-pill' + (historyStatusFilter === bucket.id ? ' active' : ''),
                        textContent: bucket.label,
                        onClick: function () {
                            historyStatusFilter = bucket.id;
                            render();
                        }
                    }));
                })(buckets[bi]);
            }
            searchBar.appendChild(filterPills);

            historyContent.appendChild(searchBar);

            // Filter info bar
            if (selectedMethod && !showAllHistory) {
                const filterInfo = el('div', { className: 'bowire-history-filter-info' },
                    el('span', { textContent: filtered.length + ' call' + (filtered.length !== 1 ? 's' : '') + ' for ' + selectedMethod.name + ' (' + allHistory.length + ' total)' }),
                    el('button', {
                        id: 'bowire-history-show-all-btn',
                        className: 'bowire-history-show-all',
                        textContent: 'Show all',
                        onClick: function () { showAllHistory = true; render(); }
                    })
                );
                historyContent.appendChild(filterInfo);
            } else if (showAllHistory && selectedMethod) {
                const filterInfo = el('div', { className: 'bowire-history-filter-info' },
                    el('span', { textContent: allHistory.length + ' total calls' }),
                    el('button', {
                        id: 'bowire-history-filter-method-btn',
                        className: 'bowire-history-show-all',
                        textContent: 'Filter to ' + selectedMethod.name,
                        onClick: function () { showAllHistory = false; render(); }
                    })
                );
                historyContent.appendChild(filterInfo);
            }

            if (filtered.length === 0) {
                var emptyText = (historySearchQuery || historyStatusFilter !== 'all')
                    ? 'No history matches the current search / filter.'
                    : 'No history for this method yet.';
                historyContent.appendChild(el('div', { className: 'bowire-empty-state', style: 'padding: 40px' },
                    el('div', { className: 'bowire-empty-desc', textContent: emptyText })
                ));
            } else {
                const historyList = el('div', { className: 'bowire-history-list bowire-history-timeline' });
                var prevBody = null;
                for (const h of filtered) {
                    const methodName = h.method || 'Unknown';
                    const statusColor = grpcStatusClass(h.status);
                    const badgeType = h.methodType === 'Unary' ? 'unary' : h.methodType === 'ServerStreaming' ? 'server' : h.methodType === 'ClientStreaming' ? 'client' : 'duplex';
                    const ago = h.timestamp ? timeAgo(h.timestamp) : '';

                    // Build body preview
                    var bodyPreview = '';
                    if (h.body && !h.body.startsWith('(channel:')) {
                        try {
                            const parsed = JSON.parse(h.body);
                            bodyPreview = JSON.stringify(parsed);
                            if (bodyPreview.length > 80) bodyPreview = bodyPreview.substring(0, 77) + '...';
                        } catch {
                            bodyPreview = h.body.substring(0, 80);
                        }
                    } else if (h.body) {
                        bodyPreview = h.body;
                    }

                    const replayBtn = el('button', {
                        className: 'bowire-history-replay',
                        title: 'Replay this request',
                        'aria-label': 'Replay this request',
                        innerHTML: svgIcon('play'),
                        onClick: function (e) {
                            e.stopPropagation();
                            replayHistoryEntry(h);
                        }
                    });

                    const itemContent = el('div', { className: 'bowire-history-item-content' },
                        el('div', { className: 'bowire-history-item-row' },
                            el('span', {
                                className: 'bowire-history-badge',
                                style: 'background: var(--bowire-badge-' + badgeType + ')',
                                textContent: shortType(h.methodType || 'Unary')
                            }),
                            el('span', { className: 'bowire-history-method', textContent: h.service?.split('.').pop() + '.' + methodName }),
                            el('span', { className: `bowire-history-status bowire-status-dot ${statusColor}` }),
                            el('span', { className: 'bowire-history-time', textContent: (h.durationMs || 0) + 'ms' }),
                            el('span', { className: 'bowire-history-ago', textContent: ago }),
                            replayBtn
                        )
                    );

                    if (bodyPreview && bodyPreview !== '{}') {
                        itemContent.appendChild(el('div', { className: 'bowire-history-body-preview', textContent: bodyPreview }));
                    }

                    // Diff badge: show when body changed from previous entry
                    var bodyChanged = prevBody !== null && bodyPreview !== prevBody;
                    if (bodyChanged) {
                        itemContent.appendChild(el('span', {
                            className: 'bowire-history-diff-badge',
                            title: 'Request body changed from previous call',
                            textContent: '\u0394 body changed'
                        }));
                    }
                    prevBody = bodyPreview;

                    const item = el('div', {
                        className: 'bowire-history-item',
                        onClick: function () {
                            // Find and select the method, populate editor
                            for (const svc of services) {
                                for (const m of svc.methods) {
                                    if (svc.name === h.service && m.name === h.method) {
                                        openTab(svc, m);
                                        // After opening the tab, populate the
                                        // editor with the history entry's body.
                                        activeRequestTab = 'body';
                                        if (h.messages && h.messages.length > 0) {
                                            requestMessages = h.messages.slice();
                                        } else if (h.body && !h.body.startsWith('(channel:')) {
                                            requestMessages = [h.body];
                                        }
                                        requestInputMode = 'json';
                                        render();
                                        return;
                                    }
                                }
                            }
                        }
                    }, itemContent);
                    historyList.appendChild(item);
                }
                historyContent.appendChild(historyList);
            }

            historyContent.appendChild(el('button', {
                id: 'bowire-history-clear-btn',
                className: 'bowire-history-clear',
                textContent: 'Clear history',
                onClick: clearHistory
            }));
        }
        pane.appendChild(historyContent);

        // Code tab — generate request snippets in C# / Python / curl /
        // grpcurl / wscat / fetch based on the current method's protocol.
        const codeContent = el('div', { id: 'bowire-request-tab-code-content', className: `bowire-tab-content ${activeRequestTab === 'code' ? 'active' : ''}` });
        if (selectedMethod) {
            var availLangs = getCodeExportLanguages();
            var activeLang = resolveCodeExportLang();
            if (activeLang) codeExportLang = activeLang;

            var langPills = el('div', { className: 'bowire-code-lang-pills' });
            for (var li = 0; li < availLangs.length; li++) {
                (function (lang) {
                    langPills.appendChild(el('button', {
                        id: 'bowire-code-lang-' + lang.id,
                        className: 'bowire-code-lang-pill' + (codeExportLang === lang.id ? ' active' : ''),
                        textContent: lang.label,
                        onClick: function () { codeExportLang = lang.id; render(); }
                    }));
                })(availLangs[li]);
            }

            var snippet = generateCodeSnippet(codeExportLang);
            var codeBox = el('pre', { className: 'bowire-code-snippet' });
            codeBox.textContent = snippet;

            var copyBtn = el('button', {
                className: 'bowire-pane-btn',
                textContent: 'Copy',
                onClick: function () {
                    navigator.clipboard.writeText(snippet).then(function () {
                        toast('Snippet copied to clipboard', 'success');
                    });
                }
            });

            var codeHeader = el('div', { className: 'bowire-pane-header' },
                el('span', { className: 'bowire-pane-title', textContent: 'Generated snippet' }),
                el('div', { className: 'bowire-pane-actions' }, copyBtn)
            );

            codeContent.appendChild(codeHeader);
            codeContent.appendChild(langPills);
            codeContent.appendChild(codeBox);
            codeContent.appendChild(el('div', {
                className: 'bowire-code-hint',
                textContent: 'The snippet uses the current request body, metadata, and the active environment\u2019s {{var}} substitutions. Re-generates whenever you switch language or edit the form.'
            }));
        }
        pane.appendChild(codeContent);

        // Scripts tab — per-method pre-request / post-response scripts
        const scriptsContent = el('div', { id: 'bowire-request-tab-scripts-content', className: `bowire-tab-content ${activeRequestTab === 'scripts' ? 'active' : ''}` });
        if (selectedMethod && selectedService) {
            var scripts = getMethodScripts(selectedService.name, selectedMethod.name);

            // Pre-request script
            var preLabel = el('label', { className: 'bowire-script-label', textContent: 'Pre-request' });
            var preHint = el('div', { className: 'bowire-script-hint', textContent: 'Runs before the request is sent. Available variables: env (environment vars), vars (flow vars), request (has body, headers).' });
            var preArea = el('textarea', {
                id: 'bowire-script-pre',
                className: 'bowire-script-editor',
                placeholder: '// e.g. request.headers[\"Authorization\"] = \"Bearer \" + env.token;\n// vars.startTime = Date.now();',
                value: scripts.preScript,
                spellcheck: false
            });
            preArea.addEventListener('input', function () {
                setMethodScript(selectedService.name, selectedMethod.name, 'preScript', preArea.value);
            });

            // Post-response script
            var postLabel = el('label', { className: 'bowire-script-label', textContent: 'Post-response' });
            var postHint = el('div', { className: 'bowire-script-hint', textContent: 'Runs after the response is received. Available variables: env, vars, response (parsed body), assert(condition, message).' });
            var postArea = el('textarea', {
                id: 'bowire-script-post',
                className: 'bowire-script-editor',
                placeholder: '// e.g. assert(response.status === \"OK\", \"Expected OK status\");\n// vars.token = response.accessToken;',
                value: scripts.postScript,
                spellcheck: false
            });
            postArea.addEventListener('input', function () {
                setMethodScript(selectedService.name, selectedMethod.name, 'postScript', postArea.value);
            });

            scriptsContent.appendChild(preLabel);
            scriptsContent.appendChild(preHint);
            scriptsContent.appendChild(preArea);
            scriptsContent.appendChild(postLabel);
            scriptsContent.appendChild(postHint);
            scriptsContent.appendChild(postArea);
        }
        pane.appendChild(scriptsContent);

        return pane;
    }

    function createMetadataRow(key, value) {
        const row = el('div', { className: 'bowire-metadata-row' },
            el('input', {
                className: 'bowire-metadata-input',
                placeholder: 'Header name',
                value: key || ''
            }),
            el('input', {
                className: 'bowire-metadata-input',
                placeholder: 'Value',
                value: value || ''
            }),
            el('button', {
                className: 'bowire-metadata-remove',
                textContent: '\u00d7',
                onClick: function () { this.parentElement.remove(); }
            })
        );
        return row;
    }

    // ---- Streaming Output (Wireshark-style: list + detail pane) ----
    //
    // Layout:
    //   [toolbar]                     ← live indicator, count, auto-scroll, maximize
    //   [list-pane (scrollable)]      ← append-only chronological list of messages
    //   [splitter]                    ← drag-to-resize between list and detail
    //   [detail-pane]                 ← formatted body of the currently selected message
    //
    // Key invariant: while a stream is live, NEW messages append a single DOM node
    // to the list and (when auto-scroll is on) replace the detail pane content.
    // The full render() path is only used to build the structure on the first
    // message of a stream and on terminal state changes (done / error / cancel).

    function streamMessageRaw(msg) {
        if (!msg) return '';
        if (typeof msg.data === 'string') return msg.data;
        if (msg.data != null) {
            try { return JSON.stringify(msg.data); } catch { return String(msg.data); }
        }
        return '';
    }

    function streamMessageBytes(msg) {
        try { return new Blob([streamMessageRaw(msg)]).size; } catch { return streamMessageRaw(msg).length; }
    }

    function streamMessagePreview(msg, maxLen) {
        var raw = streamMessageRaw(msg);

        // WebSocket frames arrive wrapped as { type: 'text'|'binary', text|base64, bytes }.
        // The JSON envelope is plumbing — show the caller the *payload* so the
        // preview is useful at a glance (text, base64, or the JSON if the payload
        // is itself JSON).
        var trimmed = raw.trim();
        if (trimmed.startsWith('{')) {
            try {
                var parsed = JSON.parse(trimmed);
                if (parsed && typeof parsed === 'object') {
                    if (parsed.type === 'text' && typeof parsed.text === 'string') {
                        var inner = parsed.text.trim();
                        // If the text itself is JSON, pretty-compact it.
                        if (inner.startsWith('{') || inner.startsWith('[')) {
                            try { inner = JSON.stringify(JSON.parse(inner)); } catch { /* keep as-is */ }
                        }
                        return clamp(inner, maxLen);
                    }
                    if (parsed.type === 'binary' && typeof parsed.base64 === 'string') {
                        return clamp('[binary] ' + parsed.base64, maxLen);
                    }
                }
            } catch { /* fall through to raw preview */ }
        }

        var oneLine = raw.replace(/\s+/g, ' ').trim();
        return clamp(oneLine, maxLen);
    }

    function clamp(s, maxLen) {
        return s.length > maxLen ? s.substring(0, maxLen) + '\u2026' : s;
    }

    // Filter predicate for the stream list. Two modes:
    //   1. Substring  — "abc"            matches anywhere in the payload.
    //   2. Key-scoped — "topic:abc"      matches only when a JSON property
    //      named `topic` (case-insensitive, at any nesting depth) has a
    //      stringified value that contains `abc`.
    // Falls back to substring when the message isn't valid JSON so the
    // shorthand stays useful for raw text frames too.
    function streamMessageMatchesFilter(msg) {
        var q = (streamFilterQuery || '').trim();
        if (!q) return true;
        var raw = streamMessageRaw(msg);
        var keyMatch = q.match(/^([\w.\-]+)\s*:\s*(.+)$/);
        if (keyMatch) {
            var key = keyMatch[1].toLowerCase();
            var want = keyMatch[2].toLowerCase();
            try {
                var data = JSON.parse(raw);
                return streamJsonHasKeyValue(data, key, want);
            } catch {
                // Not JSON — keep the prefix-aware string searchable as
                // a substring so users aren't stuck typing escapes.
                return raw.toLowerCase().indexOf(q.toLowerCase()) !== -1;
            }
        }
        return raw.toLowerCase().indexOf(q.toLowerCase()) !== -1;
    }

    function streamJsonHasKeyValue(node, key, want) {
        if (node === null || typeof node !== 'object') return false;
        if (Array.isArray(node)) {
            for (var i = 0; i < node.length; i++) {
                if (streamJsonHasKeyValue(node[i], key, want)) return true;
            }
            return false;
        }
        for (var k in node) {
            if (!Object.prototype.hasOwnProperty.call(node, k)) continue;
            var v = node[k];
            if (k.toLowerCase() === key) {
                var s = (typeof v === 'string') ? v : JSON.stringify(v);
                if (s !== undefined && s.toLowerCase().indexOf(want) !== -1) return true;
            }
            if (streamJsonHasKeyValue(v, key, want)) return true;
        }
        return false;
    }

    function streamEffectiveIndex() {
        // null/-1 means "follow latest"; resolve to the last message index.
        if (streamSelectedIndex == null) return streamMessages.length - 1;
        if (streamSelectedIndex < 0 || streamSelectedIndex >= streamMessages.length) {
            return streamMessages.length - 1;
        }
        return streamSelectedIndex;
    }

    function buildStreamListItem(msg, idx) {
        var raw = streamMessageRaw(msg);
        var wsType = detectWebSocketFrameType(raw);
        var disPdu = detectDisPdu(raw);
        var udp = disPdu ? null : detectUdpDatagram(raw);
        var surgewaveTap = (disPdu || udp) ? null : detectSurgewaveTapEvent(raw);
        // Phase 3.1 — multi-select click handler. Ctrl/Cmd toggles
        // membership; Shift extends a range from the last anchor;
        // plain click replaces with a single-member selection. The
        // helper centralises the snapshot dispatch so the call site
        // here stays narrow.
        var item = el('div', {
            className: 'bowire-stream-list-item'
                + (msg && msg.id && streamSelectedIds.has(msg.id) ? ' multi-selected' : ''),
            dataset: { idx: String(idx), frameId: (msg && msg.id) || '' },
            onClick: function (e) {
                handleStreamFrameClick(idx, e);
            }
        });
        item.appendChild(el('span', {
            className: 'bowire-stream-list-idx',
            textContent: '#' + (idx + 1)
        }));
        if (wsType) {
            item.appendChild(el('span', {
                className: 'bowire-ws-frame-badge bowire-ws-frame-' + wsType,
                textContent: wsType.toUpperCase()
            }));
        }
        if (surgewaveTap) {
            item.appendChild(el('span', {
                className: 'bowire-surgewave-tap-badge bowire-surgewave-tap-' + surgewaveTap.event,
                textContent: surgewaveTap.event.toUpperCase(),
                title: formatSurgewaveTapTooltip(surgewaveTap)
            }));
            item.appendChild(el('span', {
                className: 'bowire-stream-list-preview',
                textContent: formatSurgewaveTapPreview(surgewaveTap)
            }));
        } else if (disPdu) {
            item.appendChild(el('span', {
                className: 'bowire-dis-pdu-badge bowire-dis-pdu-' + disPdu.pduType.toLowerCase(),
                textContent: formatDisPduTypeLabel(disPdu.pduType),
                title: 'PDU type ' + disPdu.pduTypeId + ' (' + disPdu.pduType + ')'
            }));
            item.appendChild(el('span', {
                className: 'bowire-stream-list-preview',
                textContent: formatDisPreview(disPdu)
            }));
        } else if (udp) {
            item.appendChild(el('span', {
                className: 'bowire-udp-badge' + (udp.text !== null ? ' bowire-udp-text' : ' bowire-udp-binary'),
                textContent: udp.text !== null ? 'UDP' : 'UDP·BIN',
                title: udp.text !== null
                    ? 'UTF-8 text datagram from ' + udp.source
                    : 'Binary datagram from ' + udp.source
            }));
            item.appendChild(el('span', {
                className: 'bowire-stream-list-preview',
                textContent: formatUdpPreview(udp)
            }));
        } else {
            item.appendChild(el('span', {
                className: 'bowire-stream-list-preview',
                textContent: streamMessagePreview(msg, 200)
            }));
        }
        item.appendChild(el('span', {
            className: 'bowire-stream-list-size',
            textContent: streamMessageBytes(msg) + ' B'
        }));
        return item;
    }

    // One-line summary of a Surgewave tap event for the list preview:
    //   produced:   'orders/p0@42 · alice · "…payload preview…"'
    //   consumed:   'orders/p0@42 → group-a, group-b'
    //   rejected:   'orders · bob · acl-deny'
    //   rebalanced: 'orders · group-a, group-b'
    function formatSurgewaveTapPreview(tap) {
        var parts = [];
        if (tap.topic) {
            var loc = tap.topic;
            if (tap.partition != null && tap.partition >= 0) loc += '/p' + tap.partition;
            if (tap.offset != null) loc += '@' + tap.offset;
            parts.push(loc);
        }
        if (tap.event === 'consumed' || tap.event === 'rebalanced') {
            if (tap.consumers && tap.consumers.length > 0) {
                parts.push((tap.event === 'consumed' ? '→ ' : '') + tap.consumers.join(', '));
            }
        } else {
            if (tap.principal) parts.push(tap.principal);
            if (tap.event === 'rejected' && tap.reason) {
                parts.push(tap.reason);
            } else if (tap.value && tap.value.length > 0) {
                var compact = tap.value.replace(/\s+/g, ' ').trim();
                if (compact.length > 120) compact = compact.slice(0, 120) + '…';
                parts.push('"' + compact + '"');
            }
        }
        return parts.length > 0 ? parts.join(' · ') : tap.event;
    }

    function formatSurgewaveTapTooltip(tap) {
        switch (tap.event) {
            case 'produced': return 'Broker accepted a produce' +
                (tap.principal ? ' from ' + tap.principal : '');
            case 'consumed': return 'Broker dispatched to consumer group(s)' +
                (tap.consumers ? ': ' + tap.consumers.join(', ') : '');
            case 'rejected': return 'Broker rejected a produce' +
                (tap.reason ? ' — ' + tap.reason : '');
            case 'rebalanced': return 'Consumer group finished rebalancing';
            default: return tap.event;
        }
    }

    // One-line summary of a UDP datagram:
    //   text:   '10.0.0.5:53812 · "GET /api HTTP/1.1"'
    //   binary: '10.0.0.5:53812 · 144 bytes binary'
    function formatUdpPreview(udp) {
        if (udp.text !== null && udp.text.length > 0) {
            var compact = udp.text.replace(/\s+/g, ' ').trim();
            if (compact.length > 160) compact = compact.slice(0, 160) + '…';
            return udp.source + ' · "' + compact + '"';
        }
        return udp.source + ' · ' + udp.bytes + ' bytes binary';
    }

    // Turn "EntityState" into "ENTITY STATE" — readable in the badge
    // without wasting width on CamelCase runs.
    function formatDisPduTypeLabel(pduType) {
        return pduType
            .replace(/([a-z0-9])([A-Z])/g, '$1 $2')
            .replace(/([A-Z])([A-Z][a-z])/g, '$1 $2')
            .toUpperCase();
    }

    // One-line summary of a DIS PDU for the list preview:
    //   EntityState: "1:1:500 MERKAVA · Friendly · 1.1.225.1.1.0.0"
    //   other types: "exercise 1 · N bytes"
    function formatDisPreview(disPdu) {
        if (disPdu.pduType === 'EntityState') {
            var parts = [];
            if (disPdu.entityId) parts.push(disPdu.entityId);
            if (disPdu.marking) parts.push('"' + disPdu.marking + '"');
            if (disPdu.force) parts.push(disPdu.force);
            if (disPdu.entityType) parts.push(disPdu.entityType);
            if (parts.length > 0) return parts.join(' · ');
        }
        var meta = [];
        if (typeof disPdu.exerciseId === 'number') meta.push('exercise ' + disPdu.exerciseId);
        if (typeof disPdu.length === 'number') meta.push(disPdu.length + ' wire bytes');
        return meta.length > 0 ? meta.join(' · ') : disPdu.pduType;
    }

    function buildStreamDetailContent(msg, idx) {
        var raw = streamMessageRaw(msg);
        var wsType = detectWebSocketFrameType(raw);
        var bytes = streamMessageBytes(msg);

        var header = el('div', {
            className: 'bowire-stream-detail-header',
            id: 'bowire-stream-detail-header'
        });
        header.appendChild(el('span', {
            className: 'bowire-stream-detail-title',
            textContent: 'Message #' + (idx + 1)
        }));
        header.appendChild(el('span', { className: 'bowire-stream-detail-sep', textContent: '\u2022' }));
        header.appendChild(el('span', {
            className: 'bowire-stream-detail-meta',
            textContent: bytes + ' bytes'
        }));
        if (wsType) {
            header.appendChild(el('span', { className: 'bowire-stream-detail-sep', textContent: '\u2022' }));
            header.appendChild(el('span', {
                className: 'bowire-ws-frame-badge bowire-ws-frame-' + wsType,
                textContent: wsType.toUpperCase()
            }));
        }

        var disPdu = detectDisPdu(raw);
        var udp = disPdu ? null : detectUdpDatagram(raw);
        var surgewaveTap = (disPdu || udp) ? null : detectSurgewaveTapEvent(raw);
        if (surgewaveTap) {
            header.appendChild(el('span', { className: 'bowire-stream-detail-sep', textContent: '•' }));
            header.appendChild(el('span', {
                className: 'bowire-surgewave-tap-badge bowire-surgewave-tap-' + surgewaveTap.event,
                textContent: surgewaveTap.event.toUpperCase(),
                title: formatSurgewaveTapTooltip(surgewaveTap)
            }));
            if (surgewaveTap.topic) {
                header.appendChild(el('span', { className: 'bowire-stream-detail-sep', textContent: '•' }));
                header.appendChild(el('span', {
                    className: 'bowire-stream-detail-meta',
                    textContent: surgewaveTap.topic + (surgewaveTap.partition != null && surgewaveTap.partition >= 0 ? ' · p' + surgewaveTap.partition : '')
                }));
            }
            if (surgewaveTap.principal) {
                header.appendChild(el('span', { className: 'bowire-stream-detail-sep', textContent: '•' }));
                header.appendChild(el('span', {
                    className: 'bowire-stream-detail-meta',
                    textContent: '@' + surgewaveTap.principal
                }));
            }
            if (surgewaveTap.event === 'rejected' && surgewaveTap.reason) {
                header.appendChild(el('span', { className: 'bowire-stream-detail-sep', textContent: '•' }));
                header.appendChild(el('span', {
                    className: 'bowire-stream-detail-meta',
                    textContent: surgewaveTap.reason
                }));
            }
        }
        if (disPdu) {
            header.appendChild(el('span', { className: 'bowire-stream-detail-sep', textContent: '•' }));
            header.appendChild(el('span', {
                className: 'bowire-dis-pdu-badge bowire-dis-pdu-' + disPdu.pduType.toLowerCase(),
                textContent: formatDisPduTypeLabel(disPdu.pduType),
                title: 'PDU type ' + disPdu.pduTypeId + ' (' + disPdu.pduType + ')'
            }));
            if (disPdu.entityId) {
                header.appendChild(el('span', { className: 'bowire-stream-detail-sep', textContent: '•' }));
                header.appendChild(el('span', {
                    className: 'bowire-stream-detail-meta',
                    textContent: disPdu.entityId
                }));
            }
            if (disPdu.marking) {
                header.appendChild(el('span', { className: 'bowire-stream-detail-sep', textContent: '•' }));
                header.appendChild(el('span', {
                    className: 'bowire-stream-detail-meta',
                    textContent: '"' + disPdu.marking + '"'
                }));
            }
            if (disPdu.force) {
                header.appendChild(el('span', { className: 'bowire-stream-detail-sep', textContent: '•' }));
                header.appendChild(el('span', {
                    className: 'bowire-dis-force-badge bowire-dis-force-' + disPdu.force.toLowerCase(),
                    textContent: disPdu.force
                }));
            }
        }

        if (udp) {
            header.appendChild(el('span', { className: 'bowire-stream-detail-sep', textContent: '•' }));
            header.appendChild(el('span', {
                className: 'bowire-udp-badge' + (udp.text !== null ? ' bowire-udp-text' : ' bowire-udp-binary'),
                textContent: udp.text !== null ? 'UDP' : 'UDP·BIN'
            }));
            header.appendChild(el('span', { className: 'bowire-stream-detail-sep', textContent: '•' }));
            header.appendChild(el('span', {
                className: 'bowire-stream-detail-meta',
                textContent: udp.source
            }));
        }

        // Spacer pushes the toggle to the far right of the header
        header.appendChild(el('span', { style: 'flex:1' }));

        // "Show list / Hide list" — lives in the detail header
        // because it controls the preview size of this message:
        // the user expands the detail to see the full payload,
        // then collapses back when done inspecting.
        var maxBtn = el('button', {
            className: 'bowire-stream-toolbar-btn' + (streamDetailMaximized ? ' is-on' : ''),
            id: 'bowire-stream-maximize-btn',
            title: streamDetailMaximized
                ? 'Show the message list'
                : 'Hide the message list — full detail view',
            onClick: function () {
                streamDetailMaximized = !streamDetailMaximized;
                var out = document.getElementById('bowire-stream-output');
                if (out) out.classList.toggle('is-maximized', streamDetailMaximized);
                var btn = document.getElementById('bowire-stream-maximize-btn');
                if (btn) {
                    btn.classList.toggle('is-on', streamDetailMaximized);
                    btn.title = streamDetailMaximized
                        ? 'Show the message list'
                        : 'Hide the message list — full detail view';
                    var label = btn.querySelector('span');
                    if (label) label.textContent = streamDetailMaximized ? '\u25A3 Show list' : '\u25A1 Hide list';
                }
            }
        });
        maxBtn.appendChild(el('span', {
            textContent: streamDetailMaximized ? '\u25A3 Show list' : '\u25A1 Hide list'
        }));
        header.appendChild(maxBtn);

        // DIS and UDP envelopes both carry the raw on-wire bytes as
        // base64. Show a hex dump underneath the JSON view so users
        // can inspect the wire format directly; for UDP text payloads
        // we also show the decoded string between envelope and hex so
        // the eye lands there first. Non-DIS / non-UDP messages get
        // the original single-<pre> highlighted JSON body.
        if (disPdu || udp) {
            var body = el('div', {
                className: 'bowire-stream-detail-body bowire-stream-detail-body-bytes',
                id: 'bowire-stream-detail-body'
            });
            var envelopePre = el('pre', { className: 'bowire-stream-detail-envelope' });
            envelopePre.innerHTML = highlightJson(raw);
            body.appendChild(envelopePre);

            if (udp && udp.text !== null) {
                body.appendChild(el('div', {
                    className: 'bowire-bytes-section-label',
                    textContent: 'UTF-8 payload (' + udp.bytes + ' bytes):'
                }));
                var textPre = el('pre', { className: 'bowire-udp-text-pane' });
                textPre.textContent = udp.text;
                body.appendChild(textPre);
            }

            var byteCount = disPdu ? (disPdu.bytes || 0) : udp.bytes;
            var rawB64 = disPdu ? disPdu.raw : udp.raw;
            body.appendChild(el('div', {
                className: 'bowire-bytes-section-label',
                textContent: 'On-wire bytes (' + byteCount + '):'
            }));
            var hexPre = el('pre', { className: 'bowire-dis-hex-dump' });
            hexPre.textContent = formatDisHexDump(rawB64);
            body.appendChild(hexPre);

            return { header: header, body: body };
        }

        // Switch from highlightJson() to renderJsonTree() so the body
        // carries the .bowire-json-tree-label[data-json-path] markers
        // that the Phase 4 semantics decorator anchors badges onto.
        // The previous <pre> + highlightJson path produced a monospace
        // block with no per-key anchors, so badges never appeared on
        // streaming-frame detail panes. The visual difference is
        // minimal — renderJsonTree wraps each row in a .bowire-json-tree
        // row that the existing response-pane uses too.
        var body = el('div', {
            className: 'bowire-stream-detail-body',
            id: 'bowire-stream-detail-body'
        });
        body.innerHTML = renderJsonTree(raw);
        if (typeof bowireDecorateResponseTreeForSemantics === 'function'
            && selectedService && selectedMethod) {
            try {
                bowireDecorateResponseTreeForSemantics(
                    body, selectedService.name, selectedMethod.name);
            } catch (e) { console.error('[bowire-semantics] decorate stream-detail', e); }
        }

        return { header: header, body: body };
    }

    // Decode base64 into a canonical hex dump:
    //   00000000  06 01 01 01 00 00 00 00  00 90 00 00 00 01 00 01  |................|
    // Returns a plain-text multi-line string, ready for a <pre> block.
    function formatDisHexDump(base64) {
        var bytes;
        try {
            var bin = atob(base64);
            bytes = new Uint8Array(bin.length);
            for (var i = 0; i < bin.length; i++) bytes[i] = bin.charCodeAt(i);
        } catch (e) {
            return '(base64 decode failed)';
        }
        var out = [];
        for (var offset = 0; offset < bytes.length; offset += 16) {
            var hex = [];
            var ascii = [];
            for (var j = 0; j < 16; j++) {
                if (offset + j < bytes.length) {
                    var b = bytes[offset + j];
                    hex.push(('0' + b.toString(16)).slice(-2));
                    ascii.push(b >= 0x20 && b < 0x7F ? String.fromCharCode(b) : '.');
                } else {
                    hex.push('  ');
                    ascii.push(' ');
                }
                if (j === 7) hex.push('');
            }
            out.push(
                ('0000000' + offset.toString(16)).slice(-8) + '  ' +
                hex.join(' ') + '  |' + ascii.join('') + '|'
            );
        }
        return out.join('\n');
    }

    function renderStreamingOutput() {
        // Outer container — referenced by appendStreamMessage / selectStreamMessage
        // / updateStreamDetail to find the live DOM nodes.
        var output = el('div', {
            className: 'bowire-response-output streaming' + (streamDetailMaximized ? ' is-maximized' : ''),
            id: 'bowire-stream-output'
        });
        output.style.setProperty('--bowire-stream-list-pct', streamListSizePct + '%');

        // ---- Toolbar ----
        var toolbar = el('div', { className: 'bowire-stream-toolbar' });
        var statusDot = el('span', {
            className: 'bowire-stream-status-dot' + (isExecuting ? ' live' : '')
        });
        toolbar.appendChild(statusDot);
        toolbar.appendChild(el('span', {
            className: 'bowire-stream-status-text',
            textContent: isExecuting ? 'Streaming' : 'Stream ended'
        }));
        var hasFilter = (streamFilterQuery || '').trim().length > 0;

        toolbar.appendChild(el('span', { className: 'bowire-stream-toolbar-spacer' }));

        // Filter toggle button. The actual input lives in a panel
        // below the toolbar (see renderStreamFilterPanel) so its
        // variable width + the growing digit count can't shift the
        // right-hand buttons around as messages roll in. Stays
        // highlighted while a filter is active even when the panel
        // is closed, so users don't lose track of an applied
        // predicate.
        var filterToggle = el('button', {
            className: 'bowire-stream-toolbar-btn' + ((streamFilterPanelOpen || hasFilter) ? ' is-on' : ''),
            id: 'bowire-stream-filter-toggle',
            title: streamFilterPanelOpen
                ? 'Hide filter panel'
                : (hasFilter ? 'Filter is active \u2014 click to edit' : 'Open the filter panel'),
            onClick: function () {
                streamFilterPanelOpen = !streamFilterPanelOpen;
                render();
                if (streamFilterPanelOpen) {
                    var inp = document.getElementById('bowire-stream-filter-input');
                    if (inp) inp.focus();
                }
            }
        });
        filterToggle.appendChild(el('span', {
            textContent: hasFilter
                ? '\u25bc Filter \u00b7 on'
                : '\u25bc Filter'
        }));
        toolbar.appendChild(filterToggle);

        // "Follow latest" — when on, every new arrival auto-selects
        // and scrolls to the newest message. When off, the view stays
        // pinned to whatever the user last clicked. The old label
        // "Auto-scroll" was unclear because it sounded like a list
        // feature, but it also controls the detail pane.
        var autoBtn = el('button', {
            className: 'bowire-stream-toolbar-btn' + (streamAutoScroll ? ' is-on' : ''),
            id: 'bowire-stream-autoscroll-btn',
            title: streamAutoScroll
                ? 'Following the latest message \u2014 click to pin the current selection'
                : 'Pinned to current selection \u2014 click to follow the latest',
            onClick: function () { setStreamAutoScroll(!streamAutoScroll); }
        });
        autoBtn.appendChild(el('span', {
            textContent: streamAutoScroll ? '\u25C9 Follow latest' : '\u25CB Pinned'
        }));
        toolbar.appendChild(autoBtn);

        // Phase 3.1 \u2014 layout-mode toggle. Visible only when the active
        // method has a split-eligible widget AND the user has opted into
        // tab mode (the default for those methods is split, so the
        // toggle on the widget pane header is enough \u2014 but once the
        // user has switched to tab there's no widget pane and we need
        // an alternate escape hatch).
        var toolbarToggle = renderStreamingToolbarLayoutToggle();
        if (toolbarToggle) toolbar.appendChild(toolbarToggle);

        output.appendChild(toolbar);

        // ---- Filter panel (collapsible) ----
        // Structured as rows so future "advanced" filter modes (e.g.
        // pick from known message keys, multiple key:value rules) can
        // append additional `.bowire-stream-filter-row` children without
        // reshaping the layout. Only mounted when the toggle is open so
        // closed state contributes zero height.
        if (streamFilterPanelOpen) {
            var panel = el('div', {
                className: 'bowire-stream-filter-panel',
                id: 'bowire-stream-filter-panel'
            });
            var basicRow = el('div', { className: 'bowire-stream-filter-row' });
            var filterInput = el('input', {
                type: 'text',
                className: 'bowire-stream-filter',
                id: 'bowire-stream-filter-input',
                placeholder: 'Filter messages…  (e.g. topic:foo, status:200, or any substring)',
                value: streamFilterQuery,
                spellcheck: 'false',
                onInput: function (e) {
                    streamFilterQuery = e.target.value;
                    render();
                    var inp = document.getElementById('bowire-stream-filter-input');
                    if (inp) {
                        inp.focus();
                        inp.selectionStart = inp.selectionEnd = inp.value.length;
                    }
                }
            });
            basicRow.appendChild(filterInput);
            if (hasFilter) {
                var clearBtn = el('button', {
                    className: 'bowire-stream-filter-clear',
                    type: 'button',
                    title: 'Clear filter',
                    onClick: function () {
                        streamFilterQuery = '';
                        render();
                    }
                });
                clearBtn.appendChild(el('span', { textContent: '✕' }));
                basicRow.appendChild(clearBtn);
            }
            panel.appendChild(basicRow);
            output.appendChild(panel);
        }

        // ---- Count line ----
        // Sits directly above the list, on its own row, so the
        // monotonic growth of the message count never reflows the
        // toolbar buttons. Shows "X / Y messages" while a filter
        // hides some rows.
        var visibleCount = streamMessages.filter(streamMessageMatchesFilter).length;
        var countText;
        if (hasFilter) {
            countText = visibleCount + ' / ' + streamMessages.length + ' messages';
        } else {
            countText = streamMessages.length + (streamMessages.length === 1 ? ' message' : ' messages');
        }
        output.appendChild(el('div', {
            className: 'bowire-stream-count-row',
            id: 'bowire-stream-count',
            textContent: countText
        }));

        // ---- List pane ----
        var listPane = el('div', {
            className: 'bowire-stream-list-pane',
            id: 'bowire-stream-list-pane'
        });
        var list = el('div', {
            className: 'bowire-stream-list',
            id: 'bowire-stream-list'
        });
        for (var i = 0; i < streamMessages.length; i++) {
            // Skip messages that don't match the filter — the indices
            // stay absolute (used by selectStreamMessage), so the
            // detail pane and existing selection still work.
            if (!streamMessageMatchesFilter(streamMessages[i])) continue;
            list.appendChild(buildStreamListItem(streamMessages[i], i));
        }
        listPane.appendChild(list);
        output.appendChild(listPane);

        // ---- Splitter ----
        var splitter = el('div', {
            className: 'bowire-stream-splitter',
            id: 'bowire-stream-splitter',
            title: 'Drag to resize'
        });
        output.appendChild(splitter);

        // ---- Detail pane ----
        var detailPane = el('div', {
            className: 'bowire-stream-detail-pane',
            id: 'bowire-stream-detail-pane'
        });
        var effIdx = streamEffectiveIndex();
        if (effIdx >= 0) {
            var built = buildStreamDetailContent(streamMessages[effIdx], effIdx);
            detailPane.appendChild(built.header);
            detailPane.appendChild(built.body);
        }
        output.appendChild(detailPane);

        // ---- Post-mount setup ----
        // Selection class on the active item; auto-scroll to bottom; splitter
        // drag handler; auto-scroll abandon detection. All run after the DOM
        // is attached so getElementById finds the freshly inserted nodes.
        requestAnimationFrame(function () {
            updateStreamSelection();
            if (streamAutoScroll) scrollStreamListToBottom();
            attachStreamScrollListener();
            attachStreamSplitterDrag();
        });

        return output;
    }

    // ---- Phase 3.1: streaming pane composed with split-mode widgets ----
    //
    // When the active method has annotations that match a widget whose
    // default layout is `split-horizontal` (the map widget on
    // coordinate.wgs84 is the only one in v1.3.1), wrap the streaming
    // pane and the widget pane in a split-pane primitive so the user
    // can multi-select frames AND watch the map react in real time.
    //
    // Falls back to the plain streaming output when:
    //   - no extension is registered
    //   - the extension framework hasn't loaded yet
    //   - no method is selected (would happen on a freeform request)
    //   - the user has overridden the layout to `tab` (the widget
    //     stays mountable via Phase-4's right-click menu, but the
    //     streaming pane goes back to its original shape).
    //
    // The widget(s) mount asynchronously via `mountWidgetsForMethod`
    // (fetches `/api/semantics/effective`), so the widget slot starts
    // empty and fills in once the annotation lookup resolves. The
    // streaming pane is fully rendered + interactive while that's
    // pending — the split layout is the only thing that depends on a
    // synchronous "is this method map-eligible?" answer.
    //
    // The cleanup function returned by `mountWidgetsForMethod` is
    // tracked on the wrapper so the next render() can tear down
    // viewers cleanly. Without that, every render() would leave the
    // previous mount's frames$ subscription dangling.
    var bowireWidgetUnmounts = [];
    function disposeWidgetMounts() {
        for (var i = 0; i < bowireWidgetUnmounts.length; i++) {
            try { bowireWidgetUnmounts[i](); } catch (e) { console.error('[bowire-widget]', e); }
        }
        bowireWidgetUnmounts = [];
    }

    function renderStreamingPaneWithWidgets() {
        var fw = window.__bowireExtFramework;
        var layout = window.__bowireLayout;

        // The straightforward fallbacks — no extension framework, no
        // selected method, etc. — return the plain streaming output
        // unchanged. Anything that wants the split layout is on the
        // happy path below.
        if (!fw || !layout || !selectedService || !selectedMethod) {
            disposeWidgetMounts();
            return renderStreamingOutput();
        }

        // Synchronously decide whether the active method is split-
        // eligible. The decision is based on the extension's default
        // layout per kind PLUS the per-(service, method, widget) user
        // override stored in localStorage. We don't actually know
        // which extensions are mountable until /api/semantics/effective
        // resolves — but the split decision is per-kind, so a single
        // "is any registered extension's default split-horizontal?"
        // check is enough.
        //
        // For v1.3.1 the only split-default kind is `coordinate.wgs84`.
        // Future kinds extend this set in layout.js → defaultLayoutForKind.
        var splitKindExt = fw.preferredExtension('coordinate.wgs84');
        var saved = splitKindExt
            ? layout.loadWidgetLayout(selectedService.name, selectedMethod.name, splitKindExt.id, splitKindExt.kind)
            : null;
        var splitActive = !!(splitKindExt
            && saved
            && (saved.mode === 'split-horizontal' || saved.mode === 'split-vertical'));

        if (!splitActive) {
            disposeWidgetMounts();
            // Even when no extension is registered for a split-default
            // kind, the framework may still have placeholder cards to
            // mount — annotations exist for an unregistered kind and
            // the workbench wants to invite the user to install the
            // matching package (Phase 3-R). Append a thin slot AFTER
            // the streaming output and call mountWidgetsForMethod
            // against it; if there's nothing to mount the slot stays
            // empty and folds away.
            var streamingOut = renderStreamingOutput();
            var wrapper = el('div', { className: 'bowire-streaming-with-placeholders' });
            wrapper.appendChild(streamingOut);
            var placeholderSlot = el('div', { className: 'bowire-placeholder-slot' });
            wrapper.appendChild(placeholderSlot);
            var cleanup = fw.mountWidgetsForMethod(
                selectedService.name, selectedMethod.name, placeholderSlot);
            if (typeof cleanup === 'function') {
                bowireWidgetUnmounts.push(cleanup);
            }
            return wrapper;
        }

        // Build the split-pane host. We re-create the wrapper on every
        // render — morphdom diffs it against the previous wrapper at
        // the same position — but the split-pane primitive owns the
        // divider drag listeners only for the lifetime of THIS wrapper.
        // dispose() runs on every cleanup pass via disposeWidgetMounts.
        disposeWidgetMounts();

        var host = el('div', { className: 'bowire-widget-split-host' });
        var pane = layout.createSplitPane(host, {
            orientation: saved.mode === 'split-vertical' ? 'vertical' : 'horizontal',
            initialRatio: typeof saved.ratio === 'number' ? saved.ratio : 0.5,
            storageKey: 'bowire_widget_split_ratio:' + splitKindExt.id
        });
        bowireWidgetUnmounts.push(function () { pane.dispose(); });

        // Left slot: the streaming-frames pane (Wireshark list + detail).
        pane.firstSlot.appendChild(renderStreamingOutput());

        // Right slot: the widget pane with its own header (title +
        // layout toggle). The actual viewer DOM is attached
        // asynchronously by mountWidgetsForMethod, which fetches the
        // effective annotations first.
        var widgetPane = el('div', {
            className: 'bowire-widget-pane' + (widgetPaneMaximized ? ' is-maximized' : '')
        });
        var widgetHeader = el('div', { className: 'bowire-widget-pane-header' },
            el('span', {
                className: 'bowire-widget-pane-header-title',
                textContent: (splitKindExt.viewer && splitKindExt.viewer.label) || splitKindExt.kind
            }),
            el('button', {
                className: 'bowire-widget-pane-maximize',
                title: widgetPaneMaximized
                    ? 'Restore widget to its slot (Esc)'
                    : 'Maximize widget to fill the window',
                onClick: function () {
                    // Toggle the CSS class directly without calling
                    // render(). Every render() pass tears the widget
                    // pane down via disposeWidgetMounts() and re-runs
                    // mountWidgetsForMethod, which for a MapLibre
                    // viewer means losing the WebGL canvas + the
                    // basemap tiles + the camera state — defeats the
                    // purpose of maximizing. Direct DOM manipulation
                    // preserves the live MapLibre instance; its
                    // internal ResizeObserver picks up the new
                    // viewport-filling geometry and calls
                    // map.resize() automatically.
                    widgetPaneMaximized = !widgetPaneMaximized;
                    var pane = document.querySelector('.bowire-widget-pane');
                    if (pane) pane.classList.toggle('is-maximized', widgetPaneMaximized);
                    this.innerHTML = bowireLayoutIcon(widgetPaneMaximized ? 'minimize' : 'maximize');
                    this.title = widgetPaneMaximized
                        ? 'Restore widget to its slot (Esc)'
                        : 'Maximize widget to fill the window';
                },
                innerHTML: bowireLayoutIcon(widgetPaneMaximized ? 'minimize' : 'maximize')
            }),
            el('button', {
                className: 'bowire-widget-layout-toggle',
                title: 'Toggle layout (Tab ↔ Split)',
                onClick: function () {
                    var current = saved.mode;
                    var nextMode = layout.cycleLayoutMode(current, splitKindExt.kind);
                    layout.saveWidgetLayout(selectedService.name, selectedMethod.name, splitKindExt.id, {
                        mode: nextMode,
                        ratio: pane.getRatio()
                    });
                    render();
                },
                innerHTML: bowireLayoutIcon('layout-split')
            })
        );
        var widgetBody = el('div', { className: 'bowire-widget-pane-body' });
        widgetPane.appendChild(widgetHeader);
        widgetPane.appendChild(widgetBody);
        pane.secondSlot.appendChild(widgetPane);

        // Kick off the asynchronous mount. mountWidgetsForMethod
        // resolves /api/semantics/effective, finds pairing matches,
        // and calls each viewer's mount() on its own slot. We return
        // the cleanup so the next render() can dispose this mount.
        var widgetCleanup = fw.mountWidgetsForMethod(
            selectedService.name, selectedMethod.name, widgetBody);
        if (typeof widgetCleanup === 'function') {
            bowireWidgetUnmounts.push(widgetCleanup);
        }

        return host;
    }

    /**
     * Unary counterpart to renderStreamingPaneWithWidgets. The unary
     * response pane defaults to "JSON tree only" (no widgets), but
     * when the response shape carries annotations a registered viewer
     * cares about — TacticalAPI's GetSituationObjects returns lat/lon
     * pairs, REST GETs against geo APIs do the same — we want the
     * same split-pane affordance the streaming path already gives
     * users: JSON tree on the left, the widget (map) on the right.
     *
     * Caller passes the already-built response output element so we
     * don't reproduce its GraphQL-errors banner / MCP-content
     * banner logic here — only the wrapping changes. Returns the
     * `outputElement` unchanged when:
     *   • the extension framework / layout primitive isn't available
     *   • no registered extension claims the active method's kind
     *   • the user has the per-method layout set to `tab` (no split)
     *
     * The unary response was already dispatched as a single synthetic
     * frame from api.js, so the framework's replay-cache covers the
     * mount race (widget attaches after the dispatch fired — same
     * trick the streaming path uses).
     */
    function renderResponseWithWidgets(outputElement) {
        var fw = window.__bowireExtFramework;
        var layout = window.__bowireLayout;
        if (!fw || !layout || !selectedService || !selectedMethod) {
            disposeWidgetMounts();
            return outputElement;
        }

        var splitKindExt = fw.preferredExtension('coordinate.wgs84');
        var saved = splitKindExt
            ? layout.loadWidgetLayout(selectedService.name, selectedMethod.name, splitKindExt.id, splitKindExt.kind)
            : null;
        var splitActive = !!(splitKindExt
            && saved
            && (saved.mode === 'split-horizontal' || saved.mode === 'split-vertical'));

        if (!splitActive) {
            // Single-pane: the response output keeps its full width.
            // Append a placeholder slot AFTER the output so the
            // framework can mount placeholder cards for unregistered
            // kinds (Phase 3-R extension-discovery hint). If nothing
            // mounts the slot folds away.
            disposeWidgetMounts();
            var wrapper = el('div', { className: 'bowire-unary-with-placeholders' });
            wrapper.appendChild(outputElement);
            var placeholderSlot = el('div', { className: 'bowire-placeholder-slot' });
            wrapper.appendChild(placeholderSlot);
            var cleanup = fw.mountWidgetsForMethod(
                selectedService.name, selectedMethod.name, placeholderSlot);
            if (typeof cleanup === 'function') {
                bowireWidgetUnmounts.push(cleanup);
            }
            return wrapper;
        }

        // Split mode — same construction as the streaming pane: JSON
        // output on the left, widget pane on the right, sharing a
        // resizable divider. The widget pane keeps its own header so
        // the maximize toggle and layout cycle work the same way
        // they do in streaming mode.
        disposeWidgetMounts();
        var host = el('div', { className: 'bowire-widget-split-host' });
        var pane = layout.createSplitPane(host, {
            orientation: saved.mode === 'split-vertical' ? 'vertical' : 'horizontal',
            initialRatio: typeof saved.ratio === 'number' ? saved.ratio : 0.5,
            storageKey: 'bowire_widget_split_ratio:' + splitKindExt.id
        });
        bowireWidgetUnmounts.push(function () { pane.dispose(); });

        pane.firstSlot.appendChild(outputElement);

        var widgetPane = el('div', {
            className: 'bowire-widget-pane' + (widgetPaneMaximized ? ' is-maximized' : '')
        });
        var widgetHeader = el('div', { className: 'bowire-widget-pane-header' },
            el('span', {
                className: 'bowire-widget-pane-header-title',
                textContent: (splitKindExt.viewer && splitKindExt.viewer.label) || splitKindExt.kind
            }),
            el('button', {
                className: 'bowire-widget-pane-maximize',
                title: widgetPaneMaximized
                    ? 'Restore widget to its slot (Esc)'
                    : 'Maximize widget to fill the window',
                onClick: function () {
                    widgetPaneMaximized = !widgetPaneMaximized;
                    var p = document.querySelector('.bowire-widget-pane');
                    if (p) p.classList.toggle('is-maximized', widgetPaneMaximized);
                    this.innerHTML = bowireLayoutIcon(widgetPaneMaximized ? 'minimize' : 'maximize');
                    this.title = widgetPaneMaximized
                        ? 'Restore widget to its slot (Esc)'
                        : 'Maximize widget to fill the window';
                },
                innerHTML: bowireLayoutIcon(widgetPaneMaximized ? 'minimize' : 'maximize')
            }),
            el('button', {
                className: 'bowire-widget-layout-toggle',
                title: 'Toggle layout (Tab ↔ Split)',
                onClick: function () {
                    var nextMode = layout.cycleLayoutMode(saved.mode, splitKindExt.kind);
                    layout.saveWidgetLayout(selectedService.name, selectedMethod.name, splitKindExt.id, {
                        mode: nextMode,
                        ratio: pane.getRatio()
                    });
                    render();
                },
                innerHTML: bowireLayoutIcon('layout-split')
            })
        );
        var widgetBody = el('div', { className: 'bowire-widget-pane-body' });
        widgetPane.appendChild(widgetHeader);
        widgetPane.appendChild(widgetBody);
        pane.secondSlot.appendChild(widgetPane);

        var widgetCleanup = fw.mountWidgetsForMethod(
            selectedService.name, selectedMethod.name, widgetBody);
        if (typeof widgetCleanup === 'function') {
            bowireWidgetUnmounts.push(widgetCleanup);
        }

        return host;
    }

    /**
     * Render the small toolbar button that appears in the streaming
     * pane's toolbar when the active method has a split-eligible
     * widget AND the user has currently opted into `tab` mode (no
     * widget pane → no header → toolbar is the only place left to
     * surface the toggle). Returns null in every other case so the
     * caller can skip appending.
     */
    function renderStreamingToolbarLayoutToggle() {
        var fw = window.__bowireExtFramework;
        var layout = window.__bowireLayout;
        if (!fw || !layout || !selectedService || !selectedMethod) return null;
        var splitKindExt = fw.preferredExtension('coordinate.wgs84');
        if (!splitKindExt) return null;
        var saved = layout.loadWidgetLayout(
            selectedService.name, selectedMethod.name, splitKindExt.id, splitKindExt.kind);
        if (saved.mode !== 'tab') return null;
        return el('button', {
            className: 'bowire-widget-layout-toggle',
            title: 'Switch to split layout (' + (splitKindExt.viewer && splitKindExt.viewer.label || splitKindExt.kind) + ')',
            onClick: function () {
                layout.saveWidgetLayout(selectedService.name, selectedMethod.name, splitKindExt.id, {
                    mode: 'split-horizontal',
                    ratio: typeof saved.ratio === 'number' ? saved.ratio : 0.5
                });
                render();
            },
            innerHTML: bowireLayoutIcon('layout-split')
        });
    }

    /**
     * Inline-SVG layout icons. Two states: tab (stacked rectangles)
     * and split (side-by-side rectangles). Matches the visual style
     * of svgIcon() in helpers.js (16x16 viewBox, currentColor,
     * 2px stroke) so the workbench's icon family stays consistent.
     */
    function bowireLayoutIcon(name) {
        switch (name) {
            case 'layout-tab':
                return '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">'
                    + '<rect x="3" y="5" width="18" height="14" rx="2"/>'
                    + '<line x1="3" y1="9" x2="21" y2="9"/>'
                    + '</svg>';
            case 'maximize':
                // Four outward-pointing brackets — universal "expand" glyph.
                return '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">'
                    + '<polyline points="4 9 4 4 9 4"/>'
                    + '<polyline points="20 9 20 4 15 4"/>'
                    + '<polyline points="20 15 20 20 15 20"/>'
                    + '<polyline points="4 15 4 20 9 20"/>'
                    + '</svg>';
            case 'minimize':
                // Four inward-pointing brackets — the inverse of maximize.
                return '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">'
                    + '<polyline points="9 4 9 9 4 9"/>'
                    + '<polyline points="15 4 15 9 20 9"/>'
                    + '<polyline points="15 20 15 15 20 15"/>'
                    + '<polyline points="9 20 9 15 4 15"/>'
                    + '</svg>';
            case 'layout-split':
            default:
                return '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2">'
                    + '<rect x="3" y="5" width="18" height="14" rx="2"/>'
                    + '<line x1="12" y1="5" x2="12" y2="19"/>'
                    + '</svg>';
        }
    }

    function appendStreamMessage(parsed) {
        // Fast-path called from sseSource.onmessage / channel onmessage AFTER
        // the message has been pushed onto streamMessages. Returns true when
        // the structure exists and the append succeeded; false means the
        // caller should fall back to a full render() (typically the very
        // first message of a stream).
        var output = document.getElementById('bowire-stream-output');
        if (!output) return false;
        var list = document.getElementById('bowire-stream-list');
        if (!list) return false;

        var idx = streamMessages.length - 1;
        var msg = streamMessages[idx];
        // Filtered out: skip the DOM append but still update the count
        // and (if auto-scroll is on) the detail pane — the user should
        // still see "X / Y" climb so they know the stream is alive.
        var matches = streamMessageMatchesFilter(msg);
        if (matches) {
            var item = buildStreamListItem(msg, idx);
            list.appendChild(item);
        }

        // Update count badge — when a filter is active show "X / Y".
        var count = document.getElementById('bowire-stream-count');
        if (count) {
            var hasFilter = (streamFilterQuery || '').trim().length > 0;
            if (hasFilter) {
                var visible = streamMessages.filter(streamMessageMatchesFilter).length;
                count.textContent = visible + ' / ' + streamMessages.length + ' messages';
            } else {
                count.textContent = streamMessages.length + (streamMessages.length === 1 ? ' message' : ' messages');
            }
        }

        if (streamAutoScroll) {
            // Follow latest: shift selection forward and refresh the detail pane.
            streamSelectedIndex = null;
            updateStreamDetail();
            updateStreamSelection();
            scrollStreamListToBottom();
        }
        return true;
    }

    function selectStreamMessage(idx, fromUserClick) {
        if (idx < 0 || idx >= streamMessages.length) return;
        streamSelectedIndex = idx;
        if (fromUserClick && streamAutoScroll) {
            // The user took control — stop following the latest.
            setStreamAutoScroll(false);
        }
        updateStreamDetail();
        updateStreamSelection();
    }

    // ---- Phase 3.1: multi-select handler + snapshot dispatch ----
    //
    // Three click modes, matching what every Wireshark-style UI does:
    //   plain click       → replace selection with this one frame
    //   Ctrl/Cmd click    → toggle membership
    //   Shift click       → extend a contiguous range from the anchor
    //                       (the last single-clicked row)
    // The detail pane keeps tracking the most recently clicked row —
    // that's `streamSelectedIndex`, set by selectStreamMessage. The
    // multi-select set lives in `streamSelectedIds` and is read by the
    // map widget through the `ctx.selection$` async iterable. We emit
    // ONE `bowire:frames-selection-changed` event per logical change
    // (carrying the full N-snapshot) so widgets don't have to
    // accumulate state — see selection-stream wiring in extensions.js.
    function handleStreamFrameClick(idx, e) {
        if (idx < 0 || idx >= streamMessages.length) return;
        var msg = streamMessages[idx];
        var id = msg && msg.id;

        if (e && (e.ctrlKey || e.metaKey) && id != null) {
            // Toggle this frame in/out of the selected set. The detail
            // pane still snaps to whatever the user clicked so the
            // single-frame inspection flow keeps working.
            if (streamSelectedIds.has(id)) streamSelectedIds.delete(id);
            else streamSelectedIds.add(id);
            streamSelectionAnchorIdx = idx;
            selectStreamMessage(idx, true);
            dispatchFramesSelectionSnapshot();
            refreshMultiSelectClasses();
            return;
        }

        if (e && e.shiftKey && streamSelectionAnchorIdx != null) {
            // Range select between anchor and clicked index. Replaces
            // the current set rather than additive-extending — most
            // users expect "Shift defines the new range from anchor".
            var lo = Math.min(streamSelectionAnchorIdx, idx);
            var hi = Math.max(streamSelectionAnchorIdx, idx);
            streamSelectedIds = new Set();
            for (var i = lo; i <= hi; i++) {
                var rid = streamMessages[i] && streamMessages[i].id;
                if (rid != null) streamSelectedIds.add(rid);
            }
            selectStreamMessage(idx, true);
            dispatchFramesSelectionSnapshot();
            refreshMultiSelectClasses();
            return;
        }

        // Plain click — replace selection with just this frame.
        if (id != null) {
            streamSelectedIds = new Set([id]);
        } else {
            streamSelectedIds = new Set();
        }
        streamSelectionAnchorIdx = idx;
        selectStreamMessage(idx, true);
        dispatchFramesSelectionSnapshot();
        refreshMultiSelectClasses();
    }

    /**
     * Send the current snapshot to the extension framework. The
     * framework re-broadcasts it as a `bowire:frames-selection-changed`
     * DOM event, which every active widget's `ctx.selection$` iterable
     * pulls from. ONE event per logical change — never one per delta.
     */
    function dispatchFramesSelectionSnapshot() {
        if (!window.__bowireExtFramework
            || typeof window.__bowireExtFramework.recordSelectionSnapshot !== 'function') {
            return;
        }
        var ids = [];
        streamSelectedIds.forEach(function (v) { ids.push(v); });
        window.__bowireExtFramework.recordSelectionSnapshot(ids);
    }

    /**
     * Surgically toggle the .multi-selected class on each list item.
     * We don't go through the full render() path because the
     * Streaming-Frames pane is on the hot path (one row per frame, up
     * to thousands per second); the existing append fast-path skips
     * render() entirely. Class-toggling matches that performance
     * envelope and keeps morphdom out of the loop.
     */
    function refreshMultiSelectClasses() {
        var list = document.getElementById('bowire-stream-list');
        if (!list) return;
        var children = list.children;
        for (var i = 0; i < children.length; i++) {
            var c = children[i];
            var fid = c && c.dataset ? c.dataset.frameId : '';
            if (fid && streamSelectedIds.has(fid)) c.classList.add('multi-selected');
            else c.classList.remove('multi-selected');
        }
    }

    function updateStreamDetail() {
        var pane = document.getElementById('bowire-stream-detail-pane');
        if (!pane) return;
        var idx = streamEffectiveIndex();
        if (idx < 0) return;
        var built = buildStreamDetailContent(streamMessages[idx], idx);
        pane.replaceChildren(built.header, built.body);
    }

    function updateStreamSelection() {
        var list = document.getElementById('bowire-stream-list');
        if (!list) return;
        var sel = streamEffectiveIndex();
        // Single-point remove + single-point add. Avoids walking the entire
        // list on every message (O(N) → O(1) for long streams) and
        // guarantees only one node ever carries .selected at a time — no
        // in-between state where multiple siblings look highlighted.
        var currentSelected = list.querySelector('.bowire-stream-list-item.selected');
        if (currentSelected) currentSelected.classList.remove('selected');
        if (sel >= 0) {
            var target = list.children[sel];
            if (target && target.classList) target.classList.add('selected');
        }
    }

    function scrollStreamListToBottom() {
        var pane = document.getElementById('bowire-stream-list-pane');
        if (!pane) return;
        // Mark the next scroll event as programmatic so the abandon-detection
        // listener doesn't switch off auto-scroll.
        streamProgrammaticScroll = true;
        pane.scrollTop = pane.scrollHeight;
    }

    function setStreamAutoScroll(on) {
        streamAutoScroll = !!on;
        var btn = document.getElementById('bowire-stream-autoscroll-btn');
        if (btn) {
            btn.classList.toggle('is-on', streamAutoScroll);
            btn.title = streamAutoScroll
                ? 'Following the latest message \u2014 click to pin the current selection'
                : 'Pinned to current selection \u2014 click to follow the latest';
            var label = btn.querySelector('span');
            if (label) label.textContent = streamAutoScroll ? '\u25C9 Follow latest' : '\u25CB Pinned';
        }
        if (streamAutoScroll) {
            streamSelectedIndex = null;
            updateStreamDetail();
            updateStreamSelection();
            scrollStreamListToBottom();
        }
    }

    var streamProgrammaticScroll = false;
    function attachStreamScrollListener() {
        var pane = document.getElementById('bowire-stream-list-pane');
        if (!pane || pane.dataset.scrollHooked === '1') return;
        pane.dataset.scrollHooked = '1';
        pane.addEventListener('scroll', function () {
            if (streamProgrammaticScroll) {
                streamProgrammaticScroll = false;
                return;
            }
            // Slack/Discord-style: scrolling away from the bottom abandons
            // auto-scroll; scrolling back to the bottom re-enables it.
            var atBottom = (pane.scrollHeight - pane.scrollTop - pane.clientHeight) < 6;
            if (!atBottom && streamAutoScroll) {
                setStreamAutoScroll(false);
            } else if (atBottom && !streamAutoScroll) {
                setStreamAutoScroll(true);
            }
        });
    }

    function attachStreamSplitterDrag() {
        var splitter = document.getElementById('bowire-stream-splitter');
        var output = document.getElementById('bowire-stream-output');
        if (!splitter || !output || splitter.dataset.dragHooked === '1') return;
        splitter.dataset.dragHooked = '1';
        splitter.addEventListener('mousedown', function (e) {
            e.preventDefault();
            e.stopPropagation();
            document.body.style.cursor = 'ns-resize';
            document.body.style.userSelect = 'none';

            // Freeze auto-scroll for the duration of the drag. Without this,
            // new messages arriving mid-drag would shift the list under the
            // cursor and move the selection highlight — the user perceives
            // this as "the splitter jumped one entry down". Restore the
            // original state on mouseup so auto-scroll picks up right where
            // it was (and catches up with any messages queued during drag).
            var autoScrollBefore = streamAutoScroll;
            streamAutoScroll = false;

            var rect = output.getBoundingClientRect();
            function onMove(ev) {
                var rel = (ev.clientY - rect.top) / rect.height;
                var pct = Math.max(10, Math.min(90, Math.round(rel * 100)));
                streamListSizePct = pct;
                output.style.setProperty('--bowire-stream-list-pct', pct + '%');
            }
            function onUp() {
                document.removeEventListener('mousemove', onMove);
                document.removeEventListener('mouseup', onUp);
                document.body.style.cursor = '';
                document.body.style.userSelect = '';
                // Restore auto-scroll. If it was on before, catch up to the
                // latest message that arrived while we were dragging so the
                // user doesn't end up with a stale detail pane.
                if (autoScrollBefore) {
                    streamAutoScroll = true;
                    var btn = document.getElementById('bowire-stream-autoscroll-btn');
                    if (btn) btn.classList.add('is-on');
                    streamSelectedIndex = null;
                    updateStreamDetail();
                    updateStreamSelection();
                    scrollStreamListToBottom();
                }
            }
            document.addEventListener('mousemove', onMove);
            document.addEventListener('mouseup', onUp);
        });
    }

    // Expose the append fast-path so api.js / protocols.js can call it from
    // their respective onmessage handlers without going through render().
    window.bowireAppendStreamMessage = appendStreamMessage;

    function renderResponsePane() {
        // ID includes the selected method so morphdom fully replaces the
        // pane when switching methods instead of reusing stale DOM with
        // wrong closures from the previous method.
        var resMethodKey = (selectedService ? selectedService.name : '')
            + '-' + (selectedMethod ? selectedMethod.name : '');
        const pane = el('div', { id: 'bowire-response-pane-' + resMethodKey, className: 'bowire-pane' });

        // Tabs
        const tabs = el('div', { className: 'bowire-tabs' });
        tabs.appendChild(el('div', {
            id: 'bowire-response-tab-response',
            className: `bowire-tab ${activeResponseTab === 'response' ? 'active' : ''}`,
            textContent: 'Response',
            onClick: function () { activeResponseTab = 'response'; render(); }
        }));
        tabs.appendChild(el('div', {
            id: 'bowire-response-tab-headers',
            className: `bowire-tab ${activeResponseTab === 'headers' ? 'active' : ''}`,
            textContent: 'Response Metadata',
            onClick: function () { activeResponseTab = 'headers'; render(); }
        }));
        // Performance + Tests tabs — only meaningful for unary methods
        if (selectedMethod && selectedMethod.methodType === 'Unary') {
            tabs.appendChild(el('div', {
                id: 'bowire-response-tab-performance',
                className: `bowire-tab ${activeResponseTab === 'performance' ? 'active' : ''}`,
                textContent: 'Performance',
                onClick: function () { activeResponseTab = 'performance'; render(); }
            }));
            // Tests tab with pass/fail counter when assertions exist
            var testsLabel = 'Tests';
            var summary = selectedService && selectedMethod
                ? getAssertionSummary(selectedService.name, selectedMethod.name)
                : null;
            if (summary && summary.total > 0) {
                if (summary.untested === summary.total) {
                    testsLabel = 'Tests (' + summary.total + ')';
                } else if (summary.failed > 0) {
                    testsLabel = 'Tests (' + summary.passed + '/' + summary.total + ' \u2717)';
                } else {
                    testsLabel = 'Tests (' + summary.passed + '/' + summary.total + ' \u2713)';
                }
            }
            tabs.appendChild(el('div', {
                id: 'bowire-response-tab-tests',
                className: `bowire-tab bowire-tab-tests ${activeResponseTab === 'tests' ? 'active' : ''}`
                    + (summary && summary.failed > 0 ? ' has-failure' : '')
                    + (summary && summary.failed === 0 && summary.passed === summary.total && summary.total > 0 ? ' all-pass' : ''),
                textContent: testsLabel,
                onClick: function () { activeResponseTab = 'tests'; render(); }
            }));
        }
        // AI moved out of the response-pane tab strip into a workbench-
        // wide drawer (#90). The drawer renders alongside the workbench
        // body, not under the response tabs — see renderAiDrawer in
        // render-env-auth.js. Toggle button lives in the topbar.
        pane.appendChild(tabs);

        // #114 Phase 2 — inline hint banners at the response surface.
        // Hints flagged with surface='response' (slow-response,
        // large-response, response-status-mismatch, …) land here so
        // the operator reads them at the place where the signal
        // originated, instead of having to flip to the Assistant
        // drawer. The drawer's list keeps the full unfiltered view.
        if (typeof renderInlineHintBanners === 'function') {
            try {
                var rBanners = renderInlineHintBanners('response');
                if (rBanners) pane.appendChild(rBanners);
            } catch (e) { console.warn('[hints] response banners failed', e); }
        }

        // Response tab
        const respContent = el('div', { className: `bowire-tab-content ${activeResponseTab === 'response' ? 'active' : ''}` });
        const respHeader = el('div', { className: 'bowire-pane-header' },
            el('span', { className: 'bowire-pane-title', textContent: 'Result' }),
            el('div', { className: 'bowire-pane-actions' },
                // JSON / Tree view toggle. Only meaningful when there's
                // structured data to render (responseData present, no
                // active stream). The tree view uses native <details>
                // for collapsible nodes, so it costs no JS state.
                (function () {
                    if (!responseData || streamMessages.length > 0) return el('span');
                    return el('button', {
                        className: 'bowire-pane-btn' + (responseViewMode === 'tree' ? ' active' : ''),
                        title: responseViewMode === 'tree'
                            ? 'Switch to JSON view'
                            : 'Switch to collapsible tree view',
                        textContent: responseViewMode === 'tree' ? 'JSON' : 'Tree',
                        onClick: function () {
                            responseViewMode = responseViewMode === 'tree' ? 'json' : 'tree';
                            render();
                        }
                    });
                })(),
                // Copy — for streaming, a dropdown lets the user choose
                // between the currently selected message or the entire
                // list. For unary responses there's only one result so
                // a plain button suffices.
                (function () {
                    if (streamMessages.length > 0) {
                        var wrapper = el('div', { className: 'bowire-dropdown-wrapper' });
                        var btn = el('button', {
                            className: 'bowire-pane-btn',
                            textContent: 'Copy \u25BE',
                            onClick: function (e) {
                                e.stopPropagation();
                                var menu = wrapper.querySelector('.bowire-dropdown-menu');
                                if (menu) menu.classList.toggle('visible');
                            }
                        });
                        var menu = el('div', { className: 'bowire-dropdown-menu' },
                            el('div', {
                                className: 'bowire-dropdown-item',
                                textContent: 'Copy selected message',
                                onClick: function () {
                                    var idx = streamEffectiveIndex();
                                    var text = idx >= 0 ? streamMessageRaw(streamMessages[idx]) : '';
                                    navigator.clipboard.writeText(text).then(function () {
                                        toast('Copied message #' + (idx + 1), 'success');
                                    });
                                    wrapper.querySelector('.bowire-dropdown-menu').classList.remove('visible');
                                }
                            }),
                            el('div', {
                                className: 'bowire-dropdown-item',
                                textContent: 'Copy all messages',
                                onClick: function () {
                                    var text = streamMessages.map(function (m) {
                                        return streamMessageRaw(m);
                                    }).join('\n');
                                    navigator.clipboard.writeText(text).then(function () {
                                        toast('Copied ' + streamMessages.length + ' messages', 'success');
                                    });
                                    wrapper.querySelector('.bowire-dropdown-menu').classList.remove('visible');
                                }
                            })
                        );
                        wrapper.appendChild(btn);
                        wrapper.appendChild(menu);
                        document.addEventListener('click', function () { menu.classList.remove('visible'); });
                        return wrapper;
                    }
                    return el('button', {
                        className: 'bowire-pane-btn',
                        textContent: 'Copy',
                        onClick: function () {
                            var text = responseData || '';
                            navigator.clipboard.writeText(text).then(function () { toast('Copied to clipboard', 'success'); });
                        }
                    });
                })(),
                (function () {
                    var wrapper = el('div', { className: 'bowire-dropdown-wrapper' });
                    var btn = el('button', {
                        className: 'bowire-pane-btn',
                        textContent: 'Download \u25BE',
                        onClick: function (e) {
                            e.stopPropagation();
                            var menu = wrapper.querySelector('.bowire-dropdown-menu');
                            if (menu) menu.classList.toggle('visible');
                        }
                    });
                    var menu = el('div', { className: 'bowire-dropdown-menu' },
                        el('div', {
                            className: 'bowire-dropdown-item',
                            textContent: 'JSON',
                            onClick: function () { downloadResponse('json'); wrapper.querySelector('.bowire-dropdown-menu').classList.remove('visible'); }
                        }),
                        el('div', {
                            className: 'bowire-dropdown-item',
                            textContent: 'Proto Binary',
                            onClick: function () { downloadResponse('proto'); wrapper.querySelector('.bowire-dropdown-menu').classList.remove('visible'); }
                        })
                    );
                    wrapper.appendChild(btn);
                    wrapper.appendChild(menu);
                    // Close dropdown on outside click
                    document.addEventListener('click', function () { menu.classList.remove('visible'); });
                    return wrapper;
                })(),
                (function () {
                    // Compare button — opens the multi-snapshot diff view
                    // when at least two responses have been captured for this
                    // method. Hidden otherwise so the header stays clean.
                    var snaps = getResponseSnapshots();
                    if (snaps.length < 2) return el('span');
                    return el('button', {
                        className: 'bowire-pane-btn' + (diffViewOpen ? ' active' : ''),
                        textContent: diffViewOpen ? 'Close Diff' : 'Compare',
                        title: 'Compare responses for this method (' + snaps.length + ' snapshots)',
                        onClick: function () {
                            diffViewOpen = !diffViewOpen;
                            if (!diffViewOpen) {
                                diffSnapshotA = null;
                                diffSnapshotB = null;
                            }
                            render();
                        }
                    });
                })(),
                el('button', {
                    className: 'bowire-pane-btn',
                    textContent: 'grpcurl',
                    title: 'Copy as grpcurl command',
                    onClick: function () {
                        if (!selectedService || !selectedMethod) { toast('No method selected', 'info'); return; }
                        var svc = selectedService.name;
                        var m = selectedMethod.name;
                        var body = requestMessages[0] || '{}';
                        var target = (selectedService && selectedService.originUrl) || getPrimaryServerUrl() || 'localhost:5001';
                        // Strip scheme for grpcurl
                        var plaintext = target.startsWith('http://');
                        var host = target.replace(/^https?:\/\//, '');

                        var cmd = 'grpcurl';
                        if (plaintext) cmd += ' -plaintext';
                        try {
                            var parsed = JSON.parse(body);
                            var compact = JSON.stringify(parsed);
                            cmd += " -d '" + compact.replace(/'/g, "'\\''") + "'";
                        } catch {
                            cmd += " -d '" + body.replace(/\n/g, ' ').replace(/'/g, "'\\''") + "'";
                        }
                        cmd += ' ' + host + ' ' + svc + '/' + m;

                        navigator.clipboard.writeText(cmd).then(function () { toast('Copied grpcurl command', 'success'); });
                    }
                })
            )
        );
        respContent.appendChild(respHeader);

        const respBody = el('div', { className: 'bowire-pane-body' });

        if (isExecuting && streamMessages.length === 0) {
            respBody.appendChild(el('div', { className: 'bowire-loading' },
                el('div', { className: 'bowire-spinner' }),
                el('span', { className: 'bowire-loading-text', textContent: 'Executing...' })
            ));
        } else if (responseError) {
            // #91 — render structured problem+json when the upstream
            // returned one; fall back to plain text for legacy
            // strings and exception messages.
            const output = el('div', { className: 'bowire-response-output error' });
            var prob = (typeof responseError === 'object')
                ? normalizeProblem(responseError)
                : null;
            if (prob) {
                renderProblem(prob, output);
            } else {
                output.textContent = (typeof responseError === 'string')
                    ? responseError
                    : (problemTitle(responseError, 'Request failed'));
            }
            respBody.appendChild(output);
        } else if (streamMessages.length > 0) {
            // Wireshark-style: append-only list + selectable detail pane.
            // The full DOM is built once per render() (start / done / error);
            // new in-flight messages take the appendStreamMessage() fast path
            // and never trigger a sidebar/main re-render.
            //
            // Phase 3.1: when the active method carries annotations
            // that mount a widget defaulting to split-horizontal (the
            // map widget on coordinate.wgs84 is the only consumer in
            // v1.3.1), the streaming list and the widget share the
            // pane via the split-pane primitive instead of competing
            // for the same tab slot. Falls through to the original
            // single-pane render when no such widget is mountable —
            // identical behaviour for every other method.
            respBody.appendChild(renderStreamingPaneWithWidgets());
        } else if (diffViewOpen && getResponseSnapshots().length >= 2) {
            // Multi-snapshot diff view — replaces the normal response body
            // with snapshot selectors and a line-by-line comparison.
            respBody.appendChild(renderResponseDiff());
        } else if (responseData) {
            // GraphQL: surface a non-empty `errors` array as a banner above
            // the body so the user doesn't have to scan the JSON. The body
            // itself still renders verbatim — we don't drop the data field.
            var gqlErrors = detectGraphQLErrors(responseData);
            if (gqlErrors) {
                var banner = el('div', { className: 'bowire-graphql-errors-banner' },
                    el('div', { className: 'bowire-graphql-errors-title', textContent:
                        '\u26A0 GraphQL errors (' + gqlErrors.length + ')' })
                );
                for (var ei = 0; ei < gqlErrors.length; ei++) {
                    var errMsg = (gqlErrors[ei] && gqlErrors[ei].message) || JSON.stringify(gqlErrors[ei]);
                    banner.appendChild(el('div', { className: 'bowire-graphql-errors-item', textContent: errMsg }));
                }
                respBody.appendChild(banner);
            }

            // MCP: tool responses are wrapped as { content: [{type, text}, ...] }.
            // Show the concatenated text payload by default with a toggle to
            // see the raw envelope. Resources / prompts and any non-content
            // shape fall through to the raw view automatically.
            var mcpContent = detectMcpContent(responseData);
            if (mcpContent && !mcpRawEnvelope) {
                var header = el('div', { className: 'bowire-mcp-content-header' },
                    el('span', { className: 'bowire-mcp-content-title', textContent:
                        mcpContent.isError ? '\u26A0 Tool error' : 'Tool result' }),
                    el('span', { className: 'bowire-mcp-content-meta', textContent:
                        mcpContent.count + (mcpContent.count === 1 ? ' item' : ' items') }),
                    el('button', {
                        className: 'bowire-pane-btn',
                        textContent: 'Show raw envelope',
                        onClick: function () { mcpRawEnvelope = true; render(); }
                    })
                );
                var textBox = el('pre', {
                    className: 'bowire-mcp-content-body' + (mcpContent.isError ? ' error' : ''),
                    textContent: mcpContent.text
                });
                respBody.appendChild(el('div', { className: 'bowire-mcp-content' }, header, textBox));
            } else {
                if (mcpContent && mcpRawEnvelope) {
                    var rawHeader = el('div', { className: 'bowire-mcp-content-header' },
                        el('span', { className: 'bowire-mcp-content-title', textContent: 'Raw envelope' }),
                        el('button', {
                            className: 'bowire-pane-btn',
                            textContent: 'Show tool result',
                            onClick: function () { mcpRawEnvelope = false; render(); }
                        })
                    );
                    respBody.appendChild(el('div', { className: 'bowire-mcp-content' }, rawHeader));
                }
                // Interactive JSON: each key/value carries its dotted
                // path; clicking copies the matching ${response.X}
                // chaining variable so the user can paste it straight
                // into the next request body. Title attribute on each
                // pickable span gives the hover hint. The tree-view
                // variant reuses the same click handler — both
                // renderers emit the same data-json-path attributes.
                const output = el('div', {
                    className: 'bowire-response-output is-interactive'
                        + (responseViewMode === 'tree' ? ' is-tree' : ''),
                    title: 'Click any value to copy as ${response.X}',
                    onClick: function (e) {
                        var target = e.target.closest('[data-json-path]');
                        if (!target) return;
                        var raw = target.getAttribute('data-json-path') || '';
                        var chainVar = raw ? '${response.' + raw + '}' : '${response}';
                        navigator.clipboard.writeText(chainVar).then(
                            function () { toast('Copied: ' + chainVar, 'success'); },
                            function () { toast('Copy failed', 'error'); }
                        );
                    }
                });
                output.innerHTML = responseViewMode === 'tree'
                    ? renderJsonTree(responseData)
                    : highlightJsonInteractive(responseData);
                // Wrap the response output in a split-pane host when a
                // registered viewer claims the active method's kind
                // (e.g. MapLibre on coordinate.wgs84). For unary RPCs
                // whose response carries lat/lon — TacticalAPI's
                // GetSituationObjects, REST GETs against geo APIs —
                // this surfaces the same map widget the streaming
                // path already mounted, without forcing the user to
                // switch to a streaming method. Falls through to the
                // single-pane `output` element on the standard path
                // (no registered widget / per-method layout = tab),
                // so non-geo responses look exactly as before.
                respBody.appendChild(renderResponseWithWidgets(output));

                // Phase 4 — mount per-leaf semantic badges + right-click
                // handlers. The decoration runs asynchronously (it
                // depends on /api/semantics/effective which the
                // extensions framework already fetches for the active
                // method). Failure is silent — the response tree is
                // perfectly usable without the badges.
                if (typeof bowireDecorateResponseTreeForSemantics === 'function'
                    && selectedService && selectedMethod) {
                    try {
                        bowireDecorateResponseTreeForSemantics(
                            output, selectedService.name, selectedMethod.name);
                    } catch (e) { console.error('[bowire-semantics] decorate', e); }
                }
            }
        } else {
            respBody.appendChild(el('div', { className: 'bowire-no-response' },
                el('div', { className: 'bowire-no-response-icon', innerHTML: svgIcon('play') }),
                el('div', { textContent: 'Send a request to see the response' }),
                el('div', { style: 'font-size: 11px; opacity: 0.6', textContent: 'Press Ctrl+Enter to execute' })
            ));
        }

        respContent.appendChild(respBody);
        pane.appendChild(respContent);

        // Response metadata tab
        const headersContent = el('div', { className: `bowire-tab-content ${activeResponseTab === 'headers' ? 'active' : ''}` });
        const headersBody = el('div', { className: 'bowire-pane-body' });

        if (statusInfo && statusInfo.metadata && Object.keys(statusInfo.metadata).length > 0) {
            const meta = el('div', { className: 'bowire-schema', style: 'padding: 16px' });
            for (const [k, v] of Object.entries(statusInfo.metadata)) {
                meta.appendChild(el('div', { className: 'bowire-schema-field' },
                    el('span', { className: 'bowire-schema-field-name', textContent: k }),
                    el('span', { className: 'bowire-schema-field-type', textContent: v })
                ));
            }
            headersBody.appendChild(meta);
        } else {
            headersBody.appendChild(el('div', { className: 'bowire-empty-state', style: 'padding: 40px' },
                el('div', { className: 'bowire-empty-desc', textContent: 'No response metadata available.' })
            ));
        }

        headersContent.appendChild(headersBody);
        pane.appendChild(headersContent);

        // Performance + Tests tabs — only for unary methods
        if (selectedMethod && selectedMethod.methodType === 'Unary') {
            var perfContent = el('div', { className: 'bowire-tab-content ' + (activeResponseTab === 'performance' ? 'active' : '') });
            perfContent.appendChild(renderPerformanceTab());
            pane.appendChild(perfContent);

            var testsContent = el('div', { className: 'bowire-tab-content ' + (activeResponseTab === 'tests' ? 'active' : '') });
            testsContent.appendChild(renderTestsTab());
            pane.appendChild(testsContent);
        }

        return pane;
    }

