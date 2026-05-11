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
                respSection.appendChild(el('div', { className: 'bowire-response-output error', textContent: responseError }));
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
            if (result.error) {
                responseError = result.error;
                statusInfo = { status: 'Error', durationMs: result.duration_ms || 0 };
                addConsoleEntry({ type: 'error', method: fullName, status: 'Error', body: result.error });
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
        var selectedEnv = isGlobals ? null : envs.find(function (e) { return e.id === envSidebarSelectedId; });
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

    function renderMain() {
        // ID encodes the current view mode so morphdom fully replaces
        // the main pane when switching between environments editor,
        // freeform builder, landing page, and the normal request/response
        // layout — preventing stale DOM and wrong closures.
        var mainViewKey = sidebarView === 'environments'
            ? 'env'
            : sidebarView === 'flows'
                ? 'flows-' + (flowEditorSelectedId || 'none')
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
        // can switch between simultaneously open methods. Each tab
        // shows the method name, a protocol badge, and a close button.
        if (requestTabs.length >= 2) {
            var tabBar = el('div', { id: 'bowire-request-tabs', className: 'bowire-request-tabs' });
            var tabScroll = el('div', { className: 'bowire-request-tabs-scroll' });
            for (var ti = 0; ti < requestTabs.length; ti++) {
                (function (tab) {
                    var isActive = tab.id === activeTabId;
                    var tabEl = el('div', {
                        className: 'bowire-request-tab' + (isActive ? ' active' : ''),
                        title: tab.serviceKey + ' / ' + tab.methodKey,
                        onClick: function () { switchTab(tab.id); }
                    },
                        el('span', {
                            className: 'bowire-request-tab-proto',
                            dataset: { type: methodBadgeType(tab.method) },
                            textContent: methodBadgeText(tab.method)
                        }),
                        el('span', {
                            className: 'bowire-request-tab-name',
                            textContent: tab.methodKey
                        }),
                        el('button', {
                            className: 'bowire-request-tab-close',
                            innerHTML: svgIcon('close'),
                            title: 'Close tab',
                            onClick: function (e) {
                                e.stopPropagation();
                                closeTab(tab.id);
                            }
                        })
                    );
                    tabScroll.appendChild(tabEl);
                })(requestTabs[ti]);
            }
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

        // Content area: request + response panes
        const content = el('div', { className: 'bowire-content bowire-content-enter' });
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

        // Input mode toggle (Form / JSON)
        var hasInputFields = selectedMethod && selectedMethod.inputType && selectedMethod.inputType.fields && selectedMethod.inputType.fields.length > 0;
        if (hasInputFields && !isMultiMessage) {
            var modeToggle = el('div', { className: 'bowire-input-mode-toggle' },
                el('button', {
                    id: 'bowire-input-mode-form-btn',
                    className: 'bowire-input-mode-btn' + (requestInputMode === 'form' ? ' active' : ''),
                    textContent: 'Form',
                    onClick: function () {
                        if (requestInputMode === 'json') {
                            syncJsonToForm();
                        }
                        requestInputMode = 'form';
                        render();
                    }
                }),
                el('button', {
                    id: 'bowire-input-mode-json-btn',
                    className: 'bowire-input-mode-btn' + (requestInputMode === 'json' ? ' active' : ''),
                    textContent: 'JSON',
                    onClick: function () {
                        if (requestInputMode === 'form') {
                            syncFormToJson();
                        }
                        requestInputMode = 'json';
                        render();
                    }
                })
            );
            tabs.appendChild(modeToggle);
        }
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

        // GraphQL: Selection-set picker pane above the query editor.
        // Renders a checkbox tree of the discovered output type. Toggling
        // a field updates graphqlSelections and re-renders so the query
        // editor below picks up the new selection automatically (provided
        // the user hasn't manually overridden the query — in that case the
        // override wins, which is a deliberate "manual edit > picker"
        // ordering).
        if (isGraphQLMethod() && selectedMethod && selectedMethod.outputType
            && selectedMethod.outputType.fields && selectedMethod.outputType.fields.length > 0) {
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
        if (isGraphQLMethod()) {
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

        if (isMultiMessage) {
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
                textContent: 'The snippet uses the current request body, metadata, and the active environment\u2019s ${var} substitutions. Re-generates whenever you switch language or edit the form.'
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

    // Filter predicate for the stream list. Empty query matches every
    // message — that's the default. The substring match runs against
    // the raw payload (already stringified for non-string data) so it
    // catches both keys and values inside structured messages.
    function streamMessageMatchesFilter(msg) {
        var q = (streamFilterQuery || '').trim();
        if (!q) return true;
        var hay = streamMessageRaw(msg).toLowerCase();
        return hay.indexOf(q.toLowerCase()) !== -1;
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

        var body = el('pre', {
            className: 'bowire-stream-detail-body',
            id: 'bowire-stream-detail-body'
        });
        body.innerHTML = highlightJson(raw);

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
        // When a filter is active the count reads "X / Y" so the user
        // sees both the matched and total message count at a glance.
        var visibleCount = streamMessages.filter(streamMessageMatchesFilter).length;
        var hasFilter = (streamFilterQuery || '').trim().length > 0;
        toolbar.appendChild(el('span', {
            className: 'bowire-stream-count',
            id: 'bowire-stream-count',
            textContent: hasFilter
                ? (visibleCount + ' / ' + streamMessages.length + ' messages')
                : (streamMessages.length + (streamMessages.length === 1 ? ' message' : ' messages'))
        }));

        // Filter input — substring match across each message's raw
        // payload. Updates streamFilterQuery on every keystroke and
        // triggers a render so the list reflects the new predicate.
        // The selection stays put even when the active item is
        // filtered out, so the detail pane keeps showing context.
        var filterInput = el('input', {
            type: 'text',
            className: 'bowire-stream-filter',
            id: 'bowire-stream-filter-input',
            placeholder: 'Filter messages\u2026',
            value: streamFilterQuery,
            spellcheck: 'false',
            onInput: function (e) {
                streamFilterQuery = e.target.value;
                render();
                // Restore caret position so morphdom doesn't snap focus.
                var inp = document.getElementById('bowire-stream-filter-input');
                if (inp) {
                    inp.focus();
                    inp.selectionStart = inp.selectionEnd = inp.value.length;
                }
            }
        });
        toolbar.appendChild(filterInput);

        toolbar.appendChild(el('span', { className: 'bowire-stream-toolbar-spacer' }));

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
            return renderStreamingOutput();
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
        var widgetPane = el('div', { className: 'bowire-widget-pane' });
        var widgetHeader = el('div', { className: 'bowire-widget-pane-header' },
            el('span', {
                className: 'bowire-widget-pane-header-title',
                textContent: (splitKindExt.viewer && splitKindExt.viewer.label) || splitKindExt.kind
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
        pane.appendChild(tabs);

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
            const output = el('div', { className: 'bowire-response-output error' });
            output.textContent = responseError;
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
                respBody.appendChild(output);

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

