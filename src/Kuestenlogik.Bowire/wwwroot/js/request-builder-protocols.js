// @generated
// request-builder-protocols.js — Per-protocol layout helpers for the
// Request-builder bar (#291). Renderer + execute handlers for gRPC /
// MCP / MQTT / WebSocket / SSE plug into the rbLayouts registry
// declared in request-builder.js. This fragment is concatenated AFTER
// request-builder.js so its registration calls are no-ops on this
// load — instead the descriptors in request-builder.js reference these
// helpers by name (function declarations are hoisted within the IIFE).
//
// Layout for each protocol:
//   1. Second-control renderer  (the picker between Protocol ▾ and URL)
//   2. Per-tab body renderers   (Message / Topic / Frame / …)
//   3. Execute handler          (wire dispatch, history capture)
//
// See request-builder.js for the registry contract.

    // ============================================================
    // gRPC
    // ============================================================

    // ---- Service/Method picker (second-control) ----
    //
    // Two-stage typeahead: pick a Service from discovered gRPC services
    // (via `services` populated by api.js's discovery), then pick a
    // Method. When discovery hasn't run (no schema), the operator can
    // type a freeform `Service/Method` string.
    var rbGrpcMenuOpen = false;
    function _renderGrpcMethodPicker(fr) {
        var ps = rbProtoState(fr);
        var wrap = el('div', { className: 'bowire-request-builder-grpc-picker-wrap' });
        var label = ps.service && ps.method
            ? (ps.service + '/' + ps.method)
            : (ps.service ? (ps.service + '/…') : 'Service / Method');
        var btn = el('button', {
            type: 'button',
            className: 'bowire-request-builder-grpc-picker-btn'
                + (rbGrpcMenuOpen ? ' is-open' : ''),
            title: 'Select a gRPC service + method',
            'aria-haspopup': 'listbox',
            'aria-expanded': rbGrpcMenuOpen ? 'true' : 'false',
            onClick: function (e) {
                e.stopPropagation();
                rbGrpcMenuOpen = !rbGrpcMenuOpen;
                render();
            }
        },
            el('span', { className: 'bowire-request-builder-grpc-picker-label', textContent: label }),
            el('span', { className: 'bowire-request-builder-grpc-picker-caret',
                innerHTML: svgIcon('chevronDown') })
        );
        wrap.appendChild(btn);
        if (rbGrpcMenuOpen) {
            wrap.appendChild(_renderGrpcMenu(fr, ps));
        }
        return wrap;
    }

    function _renderGrpcMenu(fr, ps) {
        var menu = el('div', {
            className: 'bowire-request-builder-grpc-menu',
            role: 'listbox',
            onClick: function (e) { e.stopPropagation(); }
        });
        // Freehand row at the top — always available, lets the operator
        // type `Service/Method` when discovery hasn't seen the target.
        var freeRow = el('div', { className: 'bowire-request-builder-grpc-free' });
        var svcIn = el('input', {
            type: 'text', className: 'bowire-request-builder-grpc-free-input',
            value: ps.service || '', placeholder: 'Service',
            onInput: function (e) { ps.service = e.target.value; }
        });
        var mthIn = el('input', {
            type: 'text', className: 'bowire-request-builder-grpc-free-input',
            value: ps.method || '', placeholder: 'Method',
            onInput: function (e) { ps.method = e.target.value; }
        });
        freeRow.appendChild(svcIn);
        freeRow.appendChild(el('span', { className: 'bowire-request-builder-grpc-free-sep', textContent: '/' }));
        freeRow.appendChild(mthIn);
        menu.appendChild(freeRow);

        // Discovered gRPC services — collected from `services` whose
        // `source === 'grpc'`. Other sources (rest/mqtt/sse/…) live in
        // their own protocols' pickers.
        var grpcSvcs = [];
        try {
            if (typeof services !== 'undefined' && Array.isArray(services)) {
                grpcSvcs = services.filter(function (s) { return s && s.source === 'grpc'; });
            }
        } catch (_) {}

        if (grpcSvcs.length === 0) {
            menu.appendChild(el('div', {
                className: 'bowire-request-builder-grpc-empty',
                textContent: 'No discovered gRPC services. Enter a URL above and run discovery, or type freeform Service/Method.'
            }));
        } else {
            grpcSvcs.forEach(function (svc) {
                var methods = (svc.methods || []).filter(function (m) {
                    // Stick to unary for the bar — streaming has its own
                    // workbench mode; the bar is single-call shape.
                    return !m.methodType || m.methodType === 'Unary';
                });
                if (methods.length === 0) return;
                menu.appendChild(el('div', {
                    className: 'bowire-request-builder-grpc-svc',
                    textContent: svc.name
                }));
                methods.forEach(function (m) {
                    var isActive = ps.service === svc.name && ps.method === m.name;
                    menu.appendChild(el('button', {
                        type: 'button',
                        className: 'bowire-request-builder-grpc-method'
                            + (isActive ? ' is-selected' : ''),
                        role: 'option',
                        'aria-selected': isActive ? 'true' : 'false',
                        onClick: function () {
                            ps.service = svc.name;
                            ps.method = m.name;
                            rbGrpcMenuOpen = false;
                            render();
                        }
                    }, el('span', { textContent: m.name })));
                });
            });
        }
        return menu;
    }

    function _renderGrpcMessageTab(fr, ps) {
        var wrap = el('div', { className: 'bowire-request-builder-grpc-message-wrap' });
        wrap.appendChild(el('div', {
            className: 'bowire-request-builder-script-hint',
            textContent: 'Proto-typed request message as JSON. Use {{var}} for env-var substitution.'
        }));
        var ta = el('textarea', {
            className: 'bowire-editor bowire-request-builder-script-editor',
            placeholder: '{\n  "id": "42"\n}',
            spellcheck: 'false'
        });
        ta.value = ps.message || '{}';
        ta.addEventListener('input', function () { ps.message = this.value; });
        wrap.appendChild(ta);
        var st = el('div', { className: 'bowire-json-status empty' });
        wrap.appendChild(st);
        try { if (typeof attachJsonValidator === 'function') attachJsonValidator(ta, st); }
        catch (_) { /* validator may not be loaded — non-fatal */ }
        return wrap;
    }

    function _renderGrpcDeadlineTab(fr, ps) {
        var wrap = el('div', { className: 'bowire-request-builder-deadline-wrap' });
        wrap.appendChild(el('div', {
            className: 'bowire-request-builder-script-hint',
            textContent: 'gRPC deadline (ms). After this elapsed the server should return DEADLINE_EXCEEDED. 0 = no deadline.'
        }));
        var input = el('input', {
            type: 'number', min: '0', step: '500',
            className: 'bowire-request-builder-deadline-input',
            value: String(ps.deadlineMs != null ? ps.deadlineMs : 30000),
            onInput: function (e) {
                var n = parseInt(e.target.value, 10);
                ps.deadlineMs = isNaN(n) ? 0 : n;
            }
        });
        wrap.appendChild(input);
        return wrap;
    }

    async function _executeGrpcRequest(fr) {
        if (!fr.serverUrl || !fr.serverUrl.trim()) {
            if (typeof toast === 'function') toast('Enter a gRPC server URL', 'error');
            return;
        }
        var ps = rbProtoState(fr);
        if (!ps.service || !ps.method) {
            if (typeof toast === 'function') toast('Pick a Service and Method (or type Service/Method)', 'error');
            return;
        }

        // Resolve vars in URL + message.
        var url = fr.serverUrl;
        try { if (typeof substituteVars === 'function') url = substituteVars(url); } catch (_) {}
        var body = ps.message || '{}';
        try { if (typeof substituteVars === 'function') body = substituteVars(body); } catch (_) {}

        // Metadata KV → object + auth-derived entries.
        var metadata = _kvToObject(ps.metadata || []);
        _applyHoppAuthToMetadata(fr, metadata);

        isExecuting = true;
        responseData = null;
        responseError = null;
        if (typeof markJobActive === 'function') markJobActive('request-builder', ps.service + '/' + ps.method);
        render();

        var fullName = '[gRPC] ' + ps.service + '/' + ps.method;
        if (typeof addConsoleEntry === 'function') {
            addConsoleEntry({ type: 'request', method: fullName, body: body });
        }

        var historyOutcome = { status: null, durationMs: null, ok: false };
        var historyStartMs = performance.now();

        try {
            var invokeUrl = config.prefix + '/api/invoke?serverUrl=' + encodeURIComponent(url);
            var resp = await fetch(invokeUrl, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({
                    service: ps.service,
                    method: ps.method,
                    messages: [body],
                    metadata: Object.keys(metadata).length > 0 ? metadata : null,
                    protocol: 'grpc',
                    deadlineMs: ps.deadlineMs || undefined
                })
            });
            var result = await resp.json();
            historyOutcome.durationMs = result && result.duration_ms != null
                ? result.duration_ms : Math.round(performance.now() - historyStartMs);
            if (result.title) {
                responseError = result;
                historyOutcome.status = result.status != null ? result.status : 'Error';
                historyOutcome.ok = false;
            } else {
                responseData = result.response;
                historyOutcome.status = result.status || 'OK';
                historyOutcome.ok = true;
            }
        } catch (e) {
            responseError = e.message;
            historyOutcome.status = 'NetworkError';
            historyOutcome.ok = false;
            historyOutcome.durationMs = Math.round(performance.now() - historyStartMs);
        }

        try { pushHoppHistoryEntry(fr, historyOutcome); }
        catch (e) { console.warn('[request-builder-history] gRPC push failed', e); }

        isExecuting = false;
        if (typeof markJobDone === 'function') markJobDone('request-builder', ps.service + '/' + ps.method);
        render();
    }

    // ============================================================
    // MCP
    // ============================================================

    var rbMcpKindMenuOpen = false;
    function _renderMcpKindPicker(fr) {
        var ps = rbProtoState(fr);
        var KINDS = [
            { id: 'tool',     label: 'Tool'     },
            { id: 'resource', label: 'Resource' },
            { id: 'prompt',   label: 'Prompt'   }
        ];
        var wrap = el('div', { className: 'bowire-request-builder-mcp-kind-wrap' });
        var current = KINDS.find(function (k) { return k.id === ps.method; }) || KINDS[0];
        var btn = el('button', {
            type: 'button',
            className: 'bowire-request-builder-mcp-kind-btn'
                + (rbMcpKindMenuOpen ? ' is-open' : ''),
            'aria-haspopup': 'listbox',
            'aria-expanded': rbMcpKindMenuOpen ? 'true' : 'false',
            onClick: function (e) {
                e.stopPropagation();
                rbMcpKindMenuOpen = !rbMcpKindMenuOpen;
                render();
            }
        },
            el('span', { textContent: current.label }),
            el('span', { className: 'bowire-request-builder-mcp-kind-caret',
                innerHTML: svgIcon('chevronDown') })
        );
        wrap.appendChild(btn);
        if (rbMcpKindMenuOpen) {
            var menu = el('div', {
                className: 'bowire-request-builder-mcp-kind-menu',
                role: 'listbox',
                onClick: function (e) { e.stopPropagation(); }
            });
            KINDS.forEach(function (k) {
                menu.appendChild(el('button', {
                    type: 'button',
                    className: 'bowire-request-builder-mcp-kind-item'
                        + (k.id === current.id ? ' is-selected' : ''),
                    role: 'option',
                    onClick: function () {
                        ps.method = k.id;
                        rbMcpKindMenuOpen = false;
                        render();
                    }
                }, el('span', { textContent: k.label })));
            });
            wrap.appendChild(menu);
        }
        return wrap;
    }

    function _renderMcpArgumentsTab(fr, ps) {
        var wrap = el('div', { className: 'bowire-request-builder-mcp-args-wrap' });
        wrap.appendChild(el('div', {
            className: 'bowire-request-builder-script-hint',
            textContent: 'Tool / Resource / Prompt name plus its JSON arguments. Tool name maps to MCP tools/call, resource to resources/read, prompt to prompts/get.'
        }));
        wrap.appendChild(el('input', {
            type: 'text',
            className: 'bowire-request-builder-mcp-name-input',
            placeholder: 'Tool / Resource / Prompt name',
            value: ps.name || '',
            onInput: function (e) { ps.name = e.target.value; }
        }));
        var ta = el('textarea', {
            className: 'bowire-editor bowire-request-builder-script-editor',
            placeholder: '{\n  "query": "{{search}}"\n}',
            spellcheck: 'false'
        });
        ta.value = ps.arguments || '{}';
        ta.addEventListener('input', function () { ps.arguments = this.value; });
        wrap.appendChild(ta);
        return wrap;
    }

    async function _executeMcpRequest(fr) {
        if (!fr.serverUrl || !fr.serverUrl.trim()) {
            if (typeof toast === 'function') toast('Enter an MCP endpoint URL', 'error');
            return;
        }
        var ps = rbProtoState(fr);
        if (!ps.name || !ps.name.trim()) {
            if (typeof toast === 'function') toast('Enter a ' + (ps.method || 'tool') + ' name', 'error');
            return;
        }

        var url = fr.serverUrl;
        try { if (typeof substituteVars === 'function') url = substituteVars(url); } catch (_) {}
        var argsJson = ps.arguments || '{}';
        try { if (typeof substituteVars === 'function') argsJson = substituteVars(argsJson); } catch (_) {}

        var metadata = _kvToObject(ps.metadata || []);
        _applyHoppAuthToMetadata(fr, metadata);

        // MCP method routes:
        //   tool     → tools/call    with name + arguments
        //   resource → resources/read with uri = name
        //   prompt   → prompts/get   with name + arguments
        var mcpMethod;
        var envelope;
        switch (ps.method) {
            case 'resource':
                mcpMethod = 'resources/read';
                envelope = { uri: ps.name };
                break;
            case 'prompt':
                mcpMethod = 'prompts/get';
                envelope = { name: ps.name, arguments: _safeParseJsonObject(argsJson) };
                break;
            case 'tool':
            default:
                mcpMethod = 'tools/call';
                envelope = { name: ps.name, arguments: _safeParseJsonObject(argsJson) };
                break;
        }

        isExecuting = true;
        responseData = null;
        responseError = null;
        if (typeof markJobActive === 'function') markJobActive('request-builder', mcpMethod);
        render();

        var fullName = '[MCP] ' + mcpMethod;
        if (typeof addConsoleEntry === 'function') {
            addConsoleEntry({ type: 'request', method: fullName, body: JSON.stringify(envelope) });
        }

        var historyOutcome = { status: null, durationMs: null, ok: false };
        var historyStartMs = performance.now();

        try {
            var invokeUrl = config.prefix + '/api/invoke?serverUrl=' + encodeURIComponent(url);
            var resp = await fetch(invokeUrl, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({
                    service: 'mcp',
                    method: mcpMethod,
                    messages: [JSON.stringify(envelope)],
                    metadata: Object.keys(metadata).length > 0 ? metadata : null,
                    protocol: 'mcp'
                })
            });
            var result = await resp.json();
            historyOutcome.durationMs = result && result.duration_ms != null
                ? result.duration_ms : Math.round(performance.now() - historyStartMs);
            if (result.title) {
                responseError = result;
                historyOutcome.status = result.status != null ? result.status : 'Error';
                historyOutcome.ok = false;
            } else {
                responseData = result.response;
                historyOutcome.status = result.status || 'OK';
                historyOutcome.ok = true;
            }
        } catch (e) {
            responseError = e.message;
            historyOutcome.status = 'NetworkError';
            historyOutcome.ok = false;
            historyOutcome.durationMs = Math.round(performance.now() - historyStartMs);
        }

        try { pushHoppHistoryEntry(fr, historyOutcome); }
        catch (e) { console.warn('[request-builder-history] MCP push failed', e); }

        isExecuting = false;
        if (typeof markJobDone === 'function') markJobDone('request-builder', mcpMethod);
        render();
    }

    function _safeParseJsonObject(raw) {
        try { var p = JSON.parse(raw || '{}'); return (p && typeof p === 'object') ? p : {}; }
        catch (_) { return {}; }
    }

    // ============================================================
    // MQTT
    // ============================================================

    function _renderMqttActionPicker(fr) {
        var ps = rbProtoState(fr);
        var wrap = el('div', { className: 'bowire-request-builder-mqtt-action-wrap' });
        ['publish', 'subscribe'].forEach(function (a) {
            wrap.appendChild(el('button', {
                type: 'button',
                className: 'bowire-request-builder-mqtt-action-btn'
                    + (ps.action === a ? ' is-active' : ''),
                'data-action': a,
                onClick: function () {
                    ps.action = a;
                    render();
                }
            }, el('span', { textContent: a === 'publish' ? 'Publish' : 'Subscribe' })));
        });
        return wrap;
    }

    function _renderMqttTopicTab(fr, ps) {
        var wrap = el('div', { className: 'bowire-request-builder-mqtt-topic-wrap' });
        wrap.appendChild(el('div', {
            className: 'bowire-request-builder-script-hint',
            textContent: 'MQTT topic (slash-separated; + matches one level, # matches the rest). Use {{var}} for env-var substitution.'
        }));
        wrap.appendChild(el('input', {
            type: 'text',
            className: 'bowire-request-builder-mqtt-topic-input',
            placeholder: 'topic/foo/bar',
            value: ps.topic || '',
            onInput: function (e) { ps.topic = e.target.value; }
        }));
        return wrap;
    }

    function _renderMqttPayloadTab(fr, ps) {
        var wrap = el('div', { className: 'bowire-request-builder-mqtt-payload-wrap' });
        wrap.appendChild(el('div', {
            className: 'bowire-request-builder-script-hint',
            textContent: 'Message payload (string/JSON). Ignored on Subscribe.'
        }));
        var ta = el('textarea', {
            className: 'bowire-editor bowire-request-builder-script-editor',
            placeholder: 'hello world',
            spellcheck: 'false'
        });
        ta.value = ps.payload || '';
        ta.addEventListener('input', function () { ps.payload = this.value; });
        wrap.appendChild(ta);
        return wrap;
    }

    function _renderMqttQosTab(fr, ps) {
        var wrap = el('div', { className: 'bowire-request-builder-mqtt-qos-wrap' });
        wrap.appendChild(el('div', {
            className: 'bowire-request-builder-script-hint',
            textContent: 'Quality-of-service (0 = at-most-once, 1 = at-least-once, 2 = exactly-once) and retain flag (broker keeps last message for new subscribers).'
        }));
        var qosRow = el('div', { className: 'bowire-request-builder-mqtt-qos-row' });
        [0, 1, 2].forEach(function (q) {
            qosRow.appendChild(el('button', {
                type: 'button',
                className: 'bowire-request-builder-mqtt-qos-btn'
                    + (ps.qos === q ? ' is-active' : ''),
                onClick: function () { ps.qos = q; render(); }
            }, el('span', { textContent: 'QoS ' + q })));
        });
        wrap.appendChild(qosRow);
        var retainRow = el('label', { className: 'bowire-request-builder-mqtt-retain-row' });
        retainRow.appendChild(el('input', {
            type: 'checkbox',
            checked: ps.retain ? 'checked' : undefined,
            onChange: function (e) { ps.retain = e.target.checked; }
        }));
        retainRow.appendChild(el('span', { textContent: ' Retain' }));
        wrap.appendChild(retainRow);
        return wrap;
    }

    async function _executeMqttRequest(fr) {
        var ps = rbProtoState(fr);
        if (!fr.serverUrl || !fr.serverUrl.trim()) {
            if (typeof toast === 'function') toast('Enter a broker URL (mqtt:// or mqtts://)', 'error');
            return;
        }
        if (!ps.topic || !ps.topic.trim()) {
            if (typeof toast === 'function') toast('Enter a topic', 'error');
            return;
        }

        var url = fr.serverUrl;
        try { if (typeof substituteVars === 'function') url = substituteVars(url); } catch (_) {}
        var topic = ps.topic;
        try { if (typeof substituteVars === 'function') topic = substituteVars(topic); } catch (_) {}

        var metadata = _kvToObject(ps.metadata || []);
        _applyHoppAuthToMetadata(fr, metadata);
        // Carry QoS + Retain through the metadata bag — the MQTT plugin
        // unpacks them server-side (mqtt-qos / mqtt-retain conventions).
        metadata['x-bowire-mqtt-qos'] = String(ps.qos || 0);
        if (ps.retain) metadata['x-bowire-mqtt-retain'] = 'true';

        // Subscribe → toggle stream lifecycle. Publish → fire-and-forget.
        if (ps.action === 'subscribe') {
            if (rbConnState.mqttSubscribed) {
                // Disconnect path — close the streaming source.
                try { if (rbConnState.mqttSource) rbConnState.mqttSource.close(); } catch (_) {}
                rbConnState.mqttSource = null;
                rbConnState.mqttSubscribed = false;
                if (typeof toast === 'function') toast('Unsubscribed from ' + topic, 'info');
                render();
                return;
            }
            // Connect via /api/invoke/stream — server-side the MQTT plugin
            // implements InvokeStreamAsync to push messages as SSE.
            var streamUrl = config.prefix + '/api/invoke/stream'
                + '?service=mqtt'
                + '&method=' + encodeURIComponent(topic)
                + '&messages=' + encodeURIComponent('[]')
                + '&protocol=mqtt'
                + '&metadata=' + encodeURIComponent(JSON.stringify(metadata))
                + '&serverUrl=' + encodeURIComponent(url);
            try {
                var src = new EventSource(streamUrl);
                rbConnState.mqttSource = src;
                rbConnState.mqttSubscribed = true;
                rbConnState.mqttFrames = [];
                src.onmessage = function (ev) {
                    rbConnState.mqttFrames.push({ data: ev.data, ts: Date.now() });
                    if (rbConnState.mqttFrames.length > 500) rbConnState.mqttFrames.shift();
                    render();
                };
                src.onerror = function () {
                    rbConnState.mqttFrames.push({ data: '[stream error]', ts: Date.now(), err: true });
                    rbConnState.mqttSubscribed = false;
                    try { src.close(); } catch (_) {}
                    rbConnState.mqttSource = null;
                    render();
                };
                if (typeof toast === 'function') toast('Subscribed to ' + topic, 'success');
            } catch (e) {
                if (typeof toast === 'function') toast('MQTT subscribe failed: ' + e.message, 'error');
            }
            try { pushHoppHistoryEntry(fr, { status: 'Subscribed', durationMs: 0, ok: true }); }
            catch (_) {}
            render();
            return;
        }

        // Publish path — fire one message through /api/invoke.
        var payload = ps.payload || '';
        try { if (typeof substituteVars === 'function') payload = substituteVars(payload); } catch (_) {}

        isExecuting = true;
        responseData = null;
        responseError = null;
        if (typeof markJobActive === 'function') markJobActive('request-builder', 'mqtt-publish');
        render();

        var fullName = '[MQTT pub] ' + topic;
        if (typeof addConsoleEntry === 'function') {
            addConsoleEntry({ type: 'request', method: fullName, body: payload });
        }

        var historyOutcome = { status: null, durationMs: null, ok: false };
        var historyStartMs = performance.now();
        try {
            var invokeUrl = config.prefix + '/api/invoke?serverUrl=' + encodeURIComponent(url);
            var resp = await fetch(invokeUrl, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({
                    service: 'mqtt',
                    method: topic,
                    messages: [payload],
                    metadata: metadata,
                    protocol: 'mqtt'
                })
            });
            var result = await resp.json();
            historyOutcome.durationMs = result && result.duration_ms != null
                ? result.duration_ms : Math.round(performance.now() - historyStartMs);
            if (result.title) {
                responseError = result;
                historyOutcome.status = result.status != null ? result.status : 'Error';
            } else {
                responseData = result.response || '(published)';
                historyOutcome.status = result.status || 'OK';
                historyOutcome.ok = true;
            }
        } catch (e) {
            responseError = e.message;
            historyOutcome.status = 'NetworkError';
            historyOutcome.durationMs = Math.round(performance.now() - historyStartMs);
        }
        try { pushHoppHistoryEntry(fr, historyOutcome); }
        catch (_) {}
        isExecuting = false;
        if (typeof markJobDone === 'function') markJobDone('request-builder', 'mqtt-publish');
        render();
    }

    // ============================================================
    // WebSocket
    // ============================================================

    function _renderWsConnectControl(fr) {
        var wrap = el('div', { className: 'bowire-request-builder-ws-control-wrap' });
        var stateLabel = rbConnState.wsState === 'open'
            ? 'Connected'
            : rbConnState.wsState === 'connecting'
                ? 'Connecting…'
                : rbConnState.wsState === 'closing'
                    ? 'Closing…'
                    : 'Disconnected';
        wrap.appendChild(el('span', {
            className: 'bowire-request-builder-ws-state-chip'
                + ' is-' + rbConnState.wsState,
            textContent: stateLabel
        }));
        return wrap;
    }

    function _renderWsFrameTab(fr, ps) {
        var wrap = el('div', { className: 'bowire-request-builder-ws-frame-wrap' });
        wrap.appendChild(el('div', {
            className: 'bowire-request-builder-script-hint',
            textContent: 'Outgoing frame payload. Click "Send frame" (Execute) while connected to push it down the socket. Incoming frames land in the response pane.'
        }));
        var ta = el('textarea', {
            className: 'bowire-editor bowire-request-builder-script-editor',
            placeholder: '{"action":"ping"}',
            spellcheck: 'false'
        });
        ta.value = ps.frame || '';
        ta.addEventListener('input', function () { ps.frame = this.value; });
        wrap.appendChild(ta);
        return wrap;
    }

    async function _executeWsRequest(fr) {
        var ps = rbProtoState(fr);
        var url = fr.serverUrl;
        try { if (typeof substituteVars === 'function') url = substituteVars(url); } catch (_) {}

        // State machine: idle → connecting → open → (send frames) → closing → closed.
        if (rbConnState.wsSocket && rbConnState.wsState === 'open') {
            // Already open: either Send the frame OR Disconnect — we
            // pick Send when there's frame content, Disconnect otherwise.
            // (The button label flips to 'Send frame' whenever the socket
            // is open; explicit Disconnect lives in the response pane.)
            var frame = ps.frame || '';
            try { if (typeof substituteVars === 'function') frame = substituteVars(frame); } catch (_) {}
            if (!frame.trim()) {
                if (typeof toast === 'function') toast('Frame is empty — enter a payload first', 'error');
                return;
            }
            try {
                rbConnState.wsSocket.send(frame);
                rbConnState.wsFrames.push({ dir: 'send', data: frame, ts: Date.now() });
                render();
            } catch (e) {
                if (typeof toast === 'function') toast('Send failed: ' + e.message, 'error');
            }
            return;
        }

        // If a socket exists but state != open, we're either connecting
        // or closing; treat the click as a disconnect intent.
        if (rbConnState.wsSocket) {
            try { rbConnState.wsSocket.close(); } catch (_) {}
            rbConnState.wsSocket = null;
            rbConnState.wsState = 'closed';
            render();
            return;
        }

        // Connect.
        if (!url || !url.trim()) {
            if (typeof toast === 'function') toast('Enter a ws:// or wss:// URL', 'error');
            return;
        }
        try {
            rbConnState.wsState = 'connecting';
            rbConnState.wsFrames = [];
            render();
            // Optional subprotocols list — comma-separated.
            var subs = (ps.subprotocols || '').split(',')
                .map(function (s) { return s.trim(); })
                .filter(Boolean);
            var ws = subs.length > 0 ? new WebSocket(url, subs) : new WebSocket(url);
            rbConnState.wsSocket = ws;
            ws.onopen = function () {
                rbConnState.wsState = 'open';
                rbConnState.wsFrames.push({ dir: 'open', data: 'Connected to ' + url, ts: Date.now() });
                render();
            };
            ws.onmessage = function (ev) {
                rbConnState.wsFrames.push({ dir: 'recv', data: String(ev.data || ''), ts: Date.now() });
                if (rbConnState.wsFrames.length > 500) rbConnState.wsFrames.shift();
                render();
            };
            ws.onerror = function () {
                rbConnState.wsFrames.push({ dir: 'error', data: '[socket error]', ts: Date.now() });
                render();
            };
            ws.onclose = function (ev) {
                rbConnState.wsFrames.push({
                    dir: 'close',
                    data: 'Closed' + (ev.code ? ' (' + ev.code + ')' : '')
                        + (ev.reason ? ' — ' + ev.reason : ''),
                    ts: Date.now()
                });
                rbConnState.wsState = 'closed';
                rbConnState.wsSocket = null;
                render();
            };
            try { pushHoppHistoryEntry(fr, { status: 'Connected', durationMs: 0, ok: true }); }
            catch (_) {}
        } catch (e) {
            if (typeof toast === 'function') toast('WebSocket connect failed: ' + e.message, 'error');
            rbConnState.wsState = 'closed';
            rbConnState.wsSocket = null;
            render();
        }
    }

    // ============================================================
    // SSE
    // ============================================================

    function _renderSseReconnectTab(fr, ps) {
        var wrap = el('div', { className: 'bowire-request-builder-sse-reconnect-wrap' });
        wrap.appendChild(el('div', {
            className: 'bowire-request-builder-script-hint',
            textContent: 'Browser-native EventSource auto-reconnects on close; this hint mirrors the server-sent retry: directive. Adjust withCredentials for cookie-bearing endpoints.'
        }));
        var row = el('div', { className: 'bowire-request-builder-sse-reconnect-row' });
        row.appendChild(el('span', { textContent: 'Retry (ms): ' }));
        row.appendChild(el('input', {
            type: 'number', min: '500', step: '500',
            value: String(ps.reconnectMs != null ? ps.reconnectMs : 3000),
            className: 'bowire-request-builder-deadline-input',
            onInput: function (e) {
                var n = parseInt(e.target.value, 10);
                ps.reconnectMs = isNaN(n) ? 3000 : n;
            }
        }));
        wrap.appendChild(row);
        var credRow = el('label', { className: 'bowire-request-builder-sse-cred-row' });
        credRow.appendChild(el('input', {
            type: 'checkbox',
            checked: ps.withCredentials ? 'checked' : undefined,
            onChange: function (e) { ps.withCredentials = e.target.checked; }
        }));
        credRow.appendChild(el('span', { textContent: ' withCredentials (send cookies)' }));
        wrap.appendChild(credRow);
        return wrap;
    }

    async function _executeSseRequest(fr) {
        var ps = rbProtoState(fr);
        if (rbConnState.sseSource) {
            try { rbConnState.sseSource.close(); } catch (_) {}
            rbConnState.sseSource = null;
            if (typeof toast === 'function') toast('SSE disconnected', 'info');
            render();
            return;
        }
        if (!fr.serverUrl || !fr.serverUrl.trim()) {
            if (typeof toast === 'function') toast('Enter an SSE URL', 'error');
            return;
        }
        var url = fr.serverUrl;
        try { if (typeof substituteVars === 'function') url = substituteVars(url); } catch (_) {}
        rbConnState.sseEvents = [];
        try {
            var src = new EventSource(url, { withCredentials: !!ps.withCredentials });
            rbConnState.sseSource = src;
            src.onmessage = function (ev) {
                rbConnState.sseEvents.push({
                    event: 'message', data: String(ev.data || ''),
                    id: ev.lastEventId || null, ts: Date.now()
                });
                if (rbConnState.sseEvents.length > 500) rbConnState.sseEvents.shift();
                render();
            };
            src.onerror = function () {
                rbConnState.sseEvents.push({
                    event: 'error', data: '[stream error]', ts: Date.now()
                });
                render();
            };
            try { pushHoppHistoryEntry(fr, { status: 'Connected', durationMs: 0, ok: true }); }
            catch (_) {}
            if (typeof toast === 'function') toast('SSE connected', 'success');
            render();
        } catch (e) {
            if (typeof toast === 'function') toast('SSE connect failed: ' + e.message, 'error');
        }
    }

    // ============================================================
    // Auth → metadata helper (shared by gRPC / MCP / MQTT / WS)
    // ============================================================
    //
    // Reads fr._requestBuilder.authKind+authData and stamps the
    // appropriate header(s) onto the metadata bag. Mirrors the REST
    // path's auth-derivation logic so every protocol gets the same
    // Bearer/Basic/API-key surface from the Auth sub-tab.
    function _applyHoppAuthToMetadata(fr, metadata) {
        ensureHoppState(fr);
        var kind = fr._requestBuilder.authKind;
        var data = fr._requestBuilder.authData || {};
        if (kind === 'bearer' && data.token) {
            var tok = data.token;
            try { if (typeof substituteVars === 'function') tok = substituteVars(tok); } catch (_) {}
            metadata['Authorization'] = 'Bearer ' + tok;
        } else if (kind === 'basic') {
            var u = data.username || '', p = data.password || '';
            try {
                if (typeof substituteVars === 'function') {
                    u = substituteVars(u); p = substituteVars(p);
                }
            } catch (_) {}
            try { metadata['Authorization'] = 'Basic ' + btoa(u + ':' + p); }
            catch (_) {}
        } else if (kind === 'apikey' && data.key) {
            var av = data.value || '';
            try { if (typeof substituteVars === 'function') av = substituteVars(av); } catch (_) {}
            metadata[data.key.trim()] = av;
        }
    }

    // ============================================================
    // Per-protocol response-pane adaptations (Phase E)
    // ============================================================
    //
    // Override the default request-builder response pane based on the
    // active protocol — REST shows status + body (current behaviour),
    // streaming protocols show a frame log, MQTT/SSE show the message
    // stream. Hooked through _renderHoppResponse via rbActiveLayout.
    function _renderHoppResponseForLayout(fr) {
        var layout = rbActiveLayout(fr) || rbLayouts.rest;
        if (layout && typeof layout.renderResponse === 'function') {
            var node = layout.renderResponse(fr);
            if (node) return node;
        }
        return null;
    }

    // Register response-pane overrides on the streaming layouts. The
    // request/response layouts (REST, gRPC, MCP, GraphQL) keep the
    // default — they all populate responseData / responseError on
    // success and the default pane already handles them.
    if (rbLayouts.websocket) {
        rbLayouts.websocket.renderResponse = function () {
            return _renderWsFrameLog();
        };
    }
    if (rbLayouts.mqtt) {
        rbLayouts.mqtt.renderResponse = function () {
            return _renderMqttFrameLog();
        };
    }
    if (rbLayouts.sse) {
        rbLayouts.sse.renderResponse = function () {
            return _renderSseEventLog();
        };
    }

    function _renderWsFrameLog() {
        var pane = el('div', { className: 'bowire-request-builder-response is-streaming' });
        var head = el('div', { className: 'bowire-pane-heading' });
        head.appendChild(el('span', { textContent: 'Frames' }));
        head.appendChild(el('span', {
            className: 'bowire-request-builder-ws-state-chip is-' + rbConnState.wsState,
            textContent: rbConnState.wsState
        }));
        if (rbConnState.wsSocket) {
            head.appendChild(el('button', {
                type: 'button',
                className: 'bowire-request-builder-disconnect-btn',
                textContent: 'Disconnect',
                onClick: function () {
                    try { rbConnState.wsSocket.close(); } catch (_) {}
                }
            }));
        }
        pane.appendChild(head);
        if (rbConnState.wsFrames.length === 0) {
            pane.appendChild(el('div', {
                className: 'bowire-response-empty',
                textContent: 'No frames yet. Click Connect to open the socket.'
            }));
            return pane;
        }
        var list = el('div', { className: 'bowire-request-builder-stream-list' });
        rbConnState.wsFrames.slice().reverse().forEach(function (f) {
            list.appendChild(el('div', {
                className: 'bowire-request-builder-stream-row is-' + f.dir
            },
                el('span', { className: 'bowire-request-builder-stream-dir',
                    textContent: _wsDirLabel(f.dir) }),
                el('span', { className: 'bowire-request-builder-stream-data',
                    textContent: f.data }),
                el('span', { className: 'bowire-request-builder-stream-ts',
                    textContent: _formatRelativeTs(f.ts) })
            ));
        });
        pane.appendChild(list);
        return pane;
    }

    function _wsDirLabel(dir) {
        switch (dir) {
            case 'send':  return '→';
            case 'recv':  return '←';
            case 'open':  return '✓';
            case 'close': return '×';
            case 'error': return '!';
            default:      return '·';
        }
    }

    function _renderMqttFrameLog() {
        var pane = el('div', { className: 'bowire-request-builder-response is-streaming' });
        var head = el('div', { className: 'bowire-pane-heading' });
        head.appendChild(el('span', { textContent: 'Messages' }));
        if (rbConnState.mqttSubscribed) {
            head.appendChild(el('span', {
                className: 'bowire-request-builder-ws-state-chip is-open',
                textContent: 'Subscribed'
            }));
        }
        pane.appendChild(head);
        if (responseError) {
            var errOut = el('div', { className: 'bowire-response-output error' });
            errOut.textContent = (typeof responseError === 'string')
                ? responseError : (responseError.title || 'Error');
            pane.appendChild(errOut);
        }
        if (responseData && !rbConnState.mqttSubscribed) {
            // Publish-result path — show the published-ack body.
            var out = el('div', { className: 'bowire-response-output' });
            out.textContent = String(responseData);
            pane.appendChild(out);
        }
        if (rbConnState.mqttFrames.length === 0 && !responseData && !responseError) {
            pane.appendChild(el('div', {
                className: 'bowire-response-empty',
                textContent: rbConnState.mqttSubscribed
                    ? 'Subscribed — waiting for messages…'
                    : 'Click Publish to send a message, or switch to Subscribe.'
            }));
            return pane;
        }
        var list = el('div', { className: 'bowire-request-builder-stream-list' });
        rbConnState.mqttFrames.slice().reverse().forEach(function (f) {
            list.appendChild(el('div', {
                className: 'bowire-request-builder-stream-row is-recv'
                    + (f.err ? ' is-error' : '')
            },
                el('span', { className: 'bowire-request-builder-stream-dir', textContent: '←' }),
                el('span', { className: 'bowire-request-builder-stream-data', textContent: f.data }),
                el('span', { className: 'bowire-request-builder-stream-ts',
                    textContent: _formatRelativeTs(f.ts) })
            ));
        });
        pane.appendChild(list);
        return pane;
    }

    function _renderSseEventLog() {
        var pane = el('div', { className: 'bowire-request-builder-response is-streaming' });
        var head = el('div', { className: 'bowire-pane-heading' });
        head.appendChild(el('span', { textContent: 'Events' }));
        if (rbConnState.sseSource) {
            head.appendChild(el('span', {
                className: 'bowire-request-builder-ws-state-chip is-open',
                textContent: 'Connected'
            }));
            head.appendChild(el('button', {
                type: 'button',
                className: 'bowire-request-builder-disconnect-btn',
                textContent: 'Disconnect',
                onClick: function () {
                    try { rbConnState.sseSource.close(); } catch (_) {}
                    rbConnState.sseSource = null;
                    render();
                }
            }));
        }
        pane.appendChild(head);
        if (rbConnState.sseEvents.length === 0) {
            pane.appendChild(el('div', {
                className: 'bowire-response-empty',
                textContent: rbConnState.sseSource
                    ? 'Connected — waiting for events…'
                    : 'Click Connect to open the event stream.'
            }));
            return pane;
        }
        var list = el('div', { className: 'bowire-request-builder-stream-list' });
        rbConnState.sseEvents.slice().reverse().forEach(function (ev) {
            list.appendChild(el('div', {
                className: 'bowire-request-builder-stream-row is-recv'
                    + (ev.event === 'error' ? ' is-error' : '')
            },
                el('span', { className: 'bowire-request-builder-stream-dir',
                    textContent: ev.event || 'message' }),
                el('span', { className: 'bowire-request-builder-stream-data', textContent: ev.data }),
                el('span', { className: 'bowire-request-builder-stream-ts',
                    textContent: _formatRelativeTs(ev.ts) })
            ));
        });
        pane.appendChild(list);
        return pane;
    }

    // ---- Outside-click close for gRPC + MCP menus ----
    document.addEventListener('click', function (e) {
        var changed = false;
        if (rbGrpcMenuOpen) {
            var w = e.target.closest && e.target.closest('.bowire-request-builder-grpc-picker-wrap');
            if (!w) { rbGrpcMenuOpen = false; changed = true; }
        }
        if (rbMcpKindMenuOpen) {
            var w2 = e.target.closest && e.target.closest('.bowire-request-builder-mcp-kind-wrap');
            if (!w2) { rbMcpKindMenuOpen = false; changed = true; }
        }
        if (changed) render();
    });
