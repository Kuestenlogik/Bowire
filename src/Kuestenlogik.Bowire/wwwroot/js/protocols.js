    // ---- GraphQL helpers ----
    function isGraphQLMethod() {
        return selectedService && selectedService.source === 'graphql';
    }

    function graphqlMethodKey() {
        if (!selectedService || !selectedMethod) return '';
        return selectedService.name + '::' + selectedMethod.name;
    }

    // Per-method GraphQL selection-set state. Each entry maps a dotted
    // field path (e.g. "id", "address.zip", "posts.title") to true/false.
    // If a method has no entry yet we default to "all scalar fields at the
    // top level checked, nested objects collapsed".
    let graphqlSelections = {};

    function getGraphQLSelections() {
        var key = graphqlMethodKey();
        if (graphqlSelections[key]) return graphqlSelections[key];
        // Build defaults: select every top-level scalar.
        var defaults = {};
        if (selectedMethod && selectedMethod.outputType && selectedMethod.outputType.fields) {
            for (var i = 0; i < selectedMethod.outputType.fields.length; i++) {
                var f = selectedMethod.outputType.fields[i];
                if (f.type !== 'message') defaults[f.name] = true;
            }
        }
        graphqlSelections[key] = defaults;
        return defaults;
    }

    function setGraphQLSelection(path, value) {
        var key = graphqlMethodKey();
        if (!graphqlSelections[key]) graphqlSelections[key] = {};
        if (value) graphqlSelections[key][path] = true;
        else delete graphqlSelections[key][path];
    }

    /**
     * Walk the discovered output type and produce a GraphQL selection set
     * string from the user's checked-field map. Honours the same path
     * format as setGraphQLSelection: top-level fields use their bare name,
     * nested fields use a dotted path. Empty objects fall back to
     * `__typename` so the resulting query is always valid.
     */
    function buildGraphQLSelectionSet(messageInfo, selections, prefix, indent) {
        if (!messageInfo || !messageInfo.fields || messageInfo.fields.length === 0) {
            return indent + '__typename';
        }
        var lines = [];
        for (var i = 0; i < messageInfo.fields.length; i++) {
            var f = messageInfo.fields[i];
            var path = prefix ? prefix + '.' + f.name : f.name;
            if (!selections[path]) continue;

            if (f.type === 'message' && f.messageType) {
                var nested = buildGraphQLSelectionSet(f.messageType, selections, path, indent + '  ');
                lines.push(indent + f.name + ' {\n' + nested + '\n' + indent + '}');
            } else {
                lines.push(indent + f.name);
            }
        }
        if (lines.length === 0) return indent + '__typename';
        return lines.join('\n');
    }

    // Build the auto-generated operation string for the currently selected
    // method. Mirrors the C# GraphQLQueryBuilder so the JS pane can show the
    // same default the server would build for variables-only invocations.
    function buildGraphQLDefaultQuery() {
        if (!isGraphQLMethod() || !selectedMethod) return '';
        var op = selectedService.name === 'Mutation' ? 'mutation'
            : selectedService.name === 'Subscription' ? 'subscription'
            : 'query';
        var name = selectedMethod.name;
        var fields = (selectedMethod.inputType && selectedMethod.inputType.fields) || [];

        var line = op + ' ' + name;
        if (fields.length > 0) {
            line += '(' + fields.map(function (f) {
                var t;
                switch (f.type) {
                    case 'int32': case 'int64': case 'uint32': case 'uint64': t = 'Int'; break;
                    case 'float': case 'double': t = 'Float'; break;
                    case 'bool': t = 'Boolean'; break;
                    case 'message': t = (f.messageType && f.messageType.name) || 'JSON'; break;
                    default: t = 'String';
                }
                if (f.isRepeated) t = '[' + t + '!]';
                if (f.required) t += '!';
                return '$' + f.name + ': ' + t;
            }).join(', ') + ')';
        }
        line += ' {\n  ' + name;
        if (fields.length > 0) {
            line += '(' + fields.map(function (f) { return f.name + ': $' + f.name; }).join(', ') + ')';
        }

        // Use the visual selection-set picker if the method has a non-empty
        // discovered output type. Falls back to __typename when nothing is
        // selected (or the schema didn't expose the return type).
        var sels = getGraphQLSelections();
        var selBody = buildGraphQLSelectionSet(selectedMethod.outputType, sels, '', '    ');
        line += ' {\n' + selBody + '\n  }\n}';
        return line;
    }

    function getGraphQLQuery() {
        var key = graphqlMethodKey();
        if (graphqlQueryOverrides[key] !== undefined) return graphqlQueryOverrides[key];
        return buildGraphQLDefaultQuery();
    }

    // Wrap variables JSON + the (possibly user-edited) operation string into
    // the standard GraphQL request shape. The plugin recognises this shape
    // and forwards the query verbatim instead of regenerating it.
    function wrapAsGraphQLRequest(variablesJson) {
        var query = getGraphQLQuery();
        var vars = {};
        try {
            var parsed = JSON.parse(variablesJson || '{}');
            if (parsed && typeof parsed === 'object') vars = parsed;
        } catch (e) { /* keep empty */ }
        return JSON.stringify({ query: query, variables: vars });
    }

    // ---- WebSocket helpers ----
    function isWebSocketMethod() {
        return selectedService && selectedService.source === 'websocket';
    }

    // Inspect a stream message envelope and return its frame type ('text',
    // 'binary', 'close', 'error') if the JSON looks like a WebSocket frame.
    // Used by the response viewer to render a coloured badge per message.
    // Per-plugin enable toggle. Reads from localStorage under the same
    // key namespace as every other plugin setting
    // ('bowire_plugin_<id>_enabled'); missing means enabled by default
    // so installing a new plugin is visible immediately. Consumers:
    // - Settings → Plugins section (render + write)
    // - renderPluginSettings category list (hide disabled plugins)
    // - render-env-auth protocol filter suggestions (hide chips)
    // - landing-page protocol switcher (hide buttons)
    // - api.js services list (drop services from disabled plugins)
    function isProtocolEnabled(id) {
        try {
            var stored = localStorage.getItem('bowire_plugin_' + id + '_enabled');
            return stored !== 'false';
        } catch (e) { return true; }
    }

    function setProtocolEnabled(id, enabled) {
        try {
            localStorage.setItem('bowire_plugin_' + id + '_enabled', enabled ? 'true' : 'false');
        } catch (e) { /* storage full / private mode — toggle won't persist, render still works */ }
    }

    // Returns the subset of `protocols` (the global loaded at boot) that
    // the user has not disabled. Use this anywhere you render a list of
    // plugins for the user; the full `protocols` array stays available
    // for code that needs the authoritative list (plugin settings
    // category sidebar, about dialog, etc.).
    function enabledProtocols() {
        return protocols.filter(function (p) { return isProtocolEnabled(p.id); });
    }

    function detectWebSocketFrameType(data) {
        if (!data || typeof data !== 'string') return null;
        try {
            var parsed = JSON.parse(data);
            if (parsed && typeof parsed === 'object' && typeof parsed.type === 'string') {
                if (parsed.type === 'text' || parsed.type === 'binary' || parsed.type === 'close' || parsed.type === 'error') {
                    return parsed.type;
                }
            }
        } catch (e) { /* not JSON */ }
        return null;
    }

    // Recognise the JSON envelope that the Bowire.Protocol.Udp plugin
    // emits on its InvokeStreamAsync feed. Envelope shape:
    //   { source, bytes, text?, raw }
    // Distinct from the DIS envelope (no pduType / pduTypeId) so the
    // two detectors stay orthogonal and the stream-output renderer can
    // pick the right preview for each.
    function detectUdpDatagram(data) {
        if (!data || typeof data !== 'string') return null;
        var trimmed = data.trim();
        if (trimmed.length === 0 || trimmed.charAt(0) !== '{') return null;
        try {
            var parsed = JSON.parse(trimmed);
            if (!parsed || typeof parsed !== 'object') return null;
            if (typeof parsed.source !== 'string') return null;
            if (typeof parsed.raw !== 'string') return null;
            if (typeof parsed.bytes !== 'number') return null;
            // DIS envelopes also carry raw + bytes but add pduType /
            // pduTypeId — if those are present, leave detection to the
            // DIS path.
            if (typeof parsed.pduType === 'string') return null;
            return {
                source: parsed.source,
                bytes: parsed.bytes,
                text: typeof parsed.text === 'string' ? parsed.text : null,
                raw: parsed.raw
            };
        } catch (e) { /* not JSON */ }
        return null;
    }

    // Recognise the JSON envelope that the Bowire.Protocol.Dis plugin
    // emits on its InvokeStreamAsync feed. Returns the decoded envelope
    // fields (pduType / entityId / marking / force / entityType / raw /
    // bytes) or null when the message is not a DIS envelope. The list
    // item and detail pane use this to show a DIS-specific badge and
    // pretty-format the preview line.
    function detectDisPdu(data) {
        if (!data || typeof data !== 'string') return null;
        var trimmed = data.trim();
        if (trimmed.length === 0 || trimmed.charAt(0) !== '{') return null;
        try {
            var parsed = JSON.parse(trimmed);
            if (!parsed || typeof parsed !== 'object') return null;
            if (typeof parsed.pduType !== 'string') return null;
            if (typeof parsed.pduTypeId !== 'number') return null;
            if (typeof parsed.raw !== 'string') return null;
            return {
                pduType: parsed.pduType,
                pduTypeId: parsed.pduTypeId,
                protocolVersion: parsed.protocolVersion,
                exerciseId: parsed.exerciseId,
                length: parsed.length,
                entityId: parsed.entityId,
                marking: parsed.marking,
                force: parsed.force,
                entityType: parsed.entityType,
                bytes: parsed.bytes,
                raw: parsed.raw
            };
        } catch (e) { /* not JSON */ }
        return null;
    }

    // Recognise the JSON envelope that the Bowire.Protocol.Surgewave plugin
    // emits on `surgewave://embedded` streams — a superset of the normal
    // consume envelope with an `event` kind string plus broker-scoped
    // fields (principal, reason, consumers[]). Returns the decoded
    // fields used by the badge renderer, or null when the message
    // isn't a tap event.
    //
    // The `event` field (produced/consumed/rejected/rebalanced) is the
    // unambiguous discriminator — the Kafka / non-tap Surgewave envelopes
    // don't carry it.
    function detectSurgewaveTapEvent(data) {
        if (!data || typeof data !== 'string') return null;
        var trimmed = data.trim();
        if (trimmed.length === 0 || trimmed.charAt(0) !== '{') return null;
        try {
            var parsed = JSON.parse(trimmed);
            if (!parsed || typeof parsed !== 'object') return null;
            if (typeof parsed.event !== 'string') return null;
            var kind = parsed.event.toLowerCase();
            if (kind !== 'produced' && kind !== 'consumed' &&
                kind !== 'rejected' && kind !== 'rebalanced') return null;
            return {
                event: kind,
                topic: typeof parsed.topic === 'string' ? parsed.topic : null,
                partition: typeof parsed.partition === 'number' ? parsed.partition : null,
                offset: typeof parsed.offset === 'number' ? parsed.offset : null,
                principal: typeof parsed.principal === 'string' ? parsed.principal : null,
                reason: typeof parsed.reason === 'string' ? parsed.reason : null,
                consumers: Array.isArray(parsed.consumers) ? parsed.consumers : null,
                value: typeof parsed.value === 'string' ? parsed.value : null,
                bytes: typeof parsed.bytes === 'number' ? parsed.bytes : null
            };
        } catch (e) { /* not JSON */ }
        return null;
    }

    // True when a parsed unary response object carries a non-empty
    // GraphQL `errors` array. Used to render a warning banner above the
    // response body.
    function detectGraphQLErrors(jsonText) {
        if (!isGraphQLMethod() || !jsonText) return null;
        try {
            var parsed = JSON.parse(jsonText);
            if (parsed && typeof parsed === 'object' && Array.isArray(parsed.errors) && parsed.errors.length > 0) {
                return parsed.errors;
            }
        } catch (e) { /* not JSON */ }
        return null;
    }

    // ---- MCP helpers ----
    function isMcpMethod() {
        return selectedService && selectedService.source === 'mcp';
    }

    // Per-method preference: show the unwrapped tool content or the raw
    // JSON-RPC envelope. Defaults to "unwrapped" because the envelope is
    // mostly noise — users typically want to see the tool's actual output.
    let mcpRawEnvelope = false;

    // MCP tool responses are wrapped as `{ content: [{ type, text|...}, ...], isError? }`
    // — concat all text items into a single string for the primary view.
    // Returns null when the body isn't an MCP envelope (e.g. resources/read,
    // prompts/get, or anything else) so the caller falls back to raw JSON.
    function detectMcpContent(jsonText) {
        if (!isMcpMethod() || !jsonText) return null;
        try {
            var parsed = JSON.parse(jsonText);
            if (!parsed || typeof parsed !== 'object') return null;
            if (!Array.isArray(parsed.content)) return null;

            var parts = [];
            var hasNonText = false;
            for (var i = 0; i < parsed.content.length; i++) {
                var item = parsed.content[i];
                if (item && item.type === 'text' && typeof item.text === 'string') {
                    parts.push(item.text);
                } else {
                    hasNonText = true;
                    parts.push(JSON.stringify(item, null, 2));
                }
            }

            return {
                text: parts.join('\n'),
                isError: parsed.isError === true,
                hasNonText: hasNonText,
                count: parsed.content.length
            };
        } catch (e) {
            return null;
        }
    }

    // Convert the editor's text content (or a pending file payload) into the
    // typed frame envelope the WebSocket plugin recognises on the wire.
    function wrapAsWebSocketFrame(rawText) {
        if (websocketFrameType === 'binary') {
            if (websocketPendingBinary) {
                var payload = websocketPendingBinary;
                websocketPendingBinary = null;
                return JSON.stringify({ type: 'binary', base64: payload });
            }
            // Treat the editor text as the bytes to encode (UTF-8 → base64).
            try {
                var bytes = new TextEncoder().encode(rawText || '');
                var bin = '';
                for (var i = 0; i < bytes.length; i++) bin += String.fromCharCode(bytes[i]);
                return JSON.stringify({ type: 'binary', base64: btoa(bin) });
            } catch (e) {
                return JSON.stringify({ type: 'binary', base64: '' });
            }
        }
        return JSON.stringify({ type: 'text', text: rawText || '' });
    }

    async function channelConnect() {
        if (!selectedMethod || !selectedService) return;
        if (duplexConnected) return;

        channelError = null;
        streamMessages = [];
        // Reset stream UI state for the channel run.
        streamSelectedIndex = null;
        streamAutoScroll = true;
        streamDetailMaximized = false;
        responseData = null;
        responseError = null;
        statusInfo = null;

        // Collect metadata from the metadata tab + apply auth helper from active env
        var metadataRows = $$('.bowire-metadata-row');
        var channelMetadata = {};
        for (var ri = 0; ri < metadataRows.length; ri++) {
            var inputs = metadataRows[ri].querySelectorAll('.bowire-metadata-input');
            if (inputs.length === 2 && inputs[0].value.trim()) {
                channelMetadata[inputs[0].value.trim()] = substituteVars(inputs[1].value);
            }
        }
        channelMetadata = await applyAuth(channelMetadata);
        var hasMetadata = Object.keys(channelMetadata).length > 0;

        try {
            var resp = await fetch(config.prefix + '/api/channel/open' + serverUrlParamForService(selectedService, false), {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({
                    service: selectedService.name,
                    method: selectedMethod.name,
                    protocol: (selectedService && selectedService.source) || selectedProtocol || undefined,
                    metadata: hasMetadata ? channelMetadata : undefined
                })
            });

            var fullName = selectedService.name + '/' + selectedMethod.name;

            var result = await resp.json();
            if (result.error) {
                channelError = result.error;
                addConsoleEntry({ type: 'error', method: fullName, status: 'Channel open failed', body: result.error });
                toast('Channel open failed: ' + result.error, 'error');
                render();
                return;
            }

            duplexChannelId = result.channelId;
            duplexConnected = true;
            // Preserve the negotiated sub-protocol (WebSocket only; the
            // other channel protocols return null) under a reserved
            // metadata key. The mock's WebSocket replayer reads this
            // back so its AcceptWebSocketAsync(subProtocol) call mirrors
            // the original handshake — clients that pin a sub-protocol
            // connect successfully.
            if (result.subProtocol) {
                channelMetadata = Object.assign({}, channelMetadata, { _subprotocol: result.subProtocol });
            }
            markJobActive(selectedService.name, selectedMethod.name);
            sentCount = 0;
            receivedCount = 0;
            streamMessages = [];
            sentMessages = [];
            channelStartMs = (typeof performance !== 'undefined' && performance.now)
                ? performance.now() : Date.now();
            statusInfo = { status: 'Connected', durationMs: 0 };
            addConsoleEntry({ type: 'channel', method: fullName, status: 'Connected', body: 'Channel opened' });

            // Start SSE listener for responses
            var sseUrl = config.prefix + '/api/channel/' + duplexChannelId + '/responses';
            duplexSseSource = new EventSource(sseUrl);

            duplexSseSource.onmessage = function (event) {
                try {
                    var parsed = JSON.parse(event.data);
                    streamMessages.push(parsed);
                    if (parsed && parsed.data !== undefined) captureResponse(parsed.data);
                    addConsoleEntry({ type: 'stream', method: fullName, body: parsed.data || event.data });
                    receivedCount++;
                    // Fast-path: append-only DOM update on the streaming output
                    // instead of a full app re-render per message. Falls back
                    // to render() for the first message (container not built
                    // yet) and on first messages after channel reconnects.
                    if (!window.bowireAppendStreamMessage || !window.bowireAppendStreamMessage()) {
                        render();
                    }
                    // Flash received counter
                    requestAnimationFrame(function () {
                        var el = document.querySelector('.bowire-counter-received');
                        if (el) {
                            el.classList.remove('bowire-counter-flash');
                            void el.offsetWidth; // force reflow
                            el.classList.add('bowire-counter-flash');
                        }
                    });
                } catch (e) {
                    streamMessages.push({ index: streamMessages.length, data: event.data });
                    receivedCount++;
                    if (!window.bowireAppendStreamMessage || !window.bowireAppendStreamMessage()) {
                        render();
                    }
                }
            };

            duplexSseSource.addEventListener('done', function (event) {
                var doneData = {};
                try { doneData = JSON.parse(event.data); } catch (e) {}
                statusInfo = {
                    status: 'Completed',
                    durationMs: doneData.durationMs || 0
                };
                duplexConnected = false;
                duplexSseSource.close();
                duplexSseSource = null;
                addConsoleEntry({ type: 'channel', method: fullName, status: 'Completed', durationMs: doneData.durationMs || 0, body: '(' + sentCount + ' sent, ' + receivedCount + ' received)' });

                addHistory({
                    service: selectedService.name,
                    method: selectedMethod.name,
                    methodType: selectedMethod.methodType,
                    body: '(channel: ' + sentCount + ' sent, ' + receivedCount + ' received)',
                    messages: [],
                    status: 'OK',
                    durationMs: doneData.durationMs || 0
                });

                // Recorder hook — channel runs are flagged with their actual
                // method type so replayRecording skips them (channels need
                // their own multi-roundtrip dispatch). Captured for HAR /
                // log purposes AND for Phase-2 mock replay: sentMessages /
                // receivedMessages carry per-frame timestampMs so the mock
                // can reproduce the duplex cadence on replay.
                captureRecordingStep({
                    protocol: (selectedService && selectedService.source) || selectedProtocol || 'grpc',
                    service: selectedService.name,
                    method: selectedMethod.name,
                    methodType: selectedMethod.methodType,
                    serverUrl: (selectedService && selectedService.originUrl) || (serverUrls[0] || null),
                    body: '(channel: ' + sentCount + ' sent, ' + receivedCount + ' received)',
                    messages: [],
                    metadata: (channelMetadata && Object.keys(channelMetadata).length > 0) ? channelMetadata : null,
                    status: 'OK',
                    durationMs: doneData.durationMs || 0,
                    response: null,
                    sentMessages: sentMessages.slice(),
                    receivedMessages: streamMessages.slice(),
                    // Populated for WebSocket (path + GET) so the Phase-2e
                    // mock-server matcher can pair incoming upgrade requests
                    // with the recorded channel step. Other duplex protocols
                    // (SignalR hub methods, MCP bidirectional) don't carry
                    // an HTTP-path identity; they stay null here.
                    httpPath: selectedMethod?.httpPath || null,
                    httpVerb: selectedMethod?.httpMethod || null
                });

                render();
            });

            duplexSseSource.addEventListener('error', function () {
                if (duplexSseSource && duplexSseSource.readyState === EventSource.CLOSED) return;
                channelError = 'Channel stream error.';
                statusInfo = { status: 'Error', durationMs: 0 };
                duplexConnected = false;
                if (duplexSseSource) {
                    duplexSseSource.close();
                    duplexSseSource = null;
                }
                addConsoleEntry({ type: 'error', method: fullName, status: 'Error', body: 'Channel stream error' });
                render();
            });

            render();
            toast('Channel opened', 'success');
        } catch (e) {
            channelError = e.message;
            addConsoleEntry({ type: 'error', method: (selectedService.name + '/' + selectedMethod.name), status: 'Channel open failed', body: e.message });
            toast('Channel open failed: ' + e.message, 'error');
            render();
        }
    }

    async function channelSend() {
        if (!duplexConnected || !duplexChannelId) return;

        // Get the current message from the editor or form
        var message;
        if (requestInputMode === 'form' && selectedMethod && selectedMethod.inputType) {
            // Same validation as the unary path so duplex/client-streaming
            // sends don't fire half-built messages at the channel.
            var channelErrors = validateForm(selectedMethod.inputType, '');
            if (Object.keys(channelErrors).length > 0) {
                formValidationErrors = channelErrors;
                var n = Object.keys(channelErrors).length;
                toast(n + (n === 1 ? ' field has' : ' fields have') + ' validation errors', 'error');
                render();
                return;
            }
            formValidationErrors = {};
            syncFormToJson();
            message = requestMessages[0] || '{}';
        } else {
            var editor = $('.bowire-editor') || $('.bowire-message-editor');
            message = editor ? editor.value || '{}' : '{}';
        }
        // Substitute ${var} placeholders from active environment
        message = substituteVars(message);

        // WebSocket: wrap raw text or pending binary payload into the typed
        // { type, text/base64 } frame envelope expected by the plugin.
        if (isWebSocketMethod()) {
            var raw = message;
            // The form mode wraps the value as { "data": "..." } — unpack so
            // the user gets the literal text frame they typed instead of a
            // JSON-encoded blob.
            if (requestInputMode === 'form') {
                try {
                    var parsed = JSON.parse(message);
                    if (parsed && typeof parsed === 'object' && typeof parsed.data === 'string') raw = parsed.data;
                } catch (e) { /* keep raw */ }
            }
            message = wrapAsWebSocketFrame(raw);
        }

        var sendFullName = selectedService.name + '/' + selectedMethod.name;
        addConsoleEntry({ type: 'send', method: sendFullName, body: message });

        // Record send timestamp relative to channel-open so recordings
        // preserve the client cadence for Phase-2 mock replay. Captured
        // locally (not round-tripped) because the server only echoes the
        // sequence number; the body and exact client-side timing are ours.
        var now = (typeof performance !== 'undefined' && performance.now)
            ? performance.now() : Date.now();
        var offsetMs = Math.round(now - channelStartMs);

        try {
            var resp = await fetch(config.prefix + '/api/channel/' + duplexChannelId + '/send', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ message: message })
            });

            var result = await resp.json();
            if (result.error) {
                addConsoleEntry({ type: 'error', method: sendFullName, status: 'Send failed', body: result.error });
                toast('Send failed: ' + result.error, 'error');
                return;
            }

            sentCount = result.sequence;
            sentMessages.push({ index: sentCount - 1, timestampMs: offsetMs, body: message });
            render();

            // Flash sent counter
            requestAnimationFrame(function () {
                var el = document.querySelector('.bowire-counter-sent');
                if (el) {
                    el.classList.remove('bowire-counter-flash');
                    void el.offsetWidth;
                    el.classList.add('bowire-counter-flash');
                }
            });
        } catch (e) {
            toast('Send failed: ' + e.message, 'error');
        }
    }

    async function channelClose() {
        if (!duplexChannelId) return;

        try {
            await fetch(config.prefix + '/api/channel/' + duplexChannelId + '/close', {
                method: 'POST'
            });
        } catch (e) {
            // Ignore close errors
        }

        // The SSE 'done' event will handle the rest
    }

    function channelDisconnect() {
        if (duplexSseSource) {
            duplexSseSource.close();
            duplexSseSource = null;
        }
        if (duplexChannelId) {
            // Fire-and-forget close
            fetch(config.prefix + '/api/channel/' + duplexChannelId + '/close', { method: 'POST' }).catch(function () {});
        }
        duplexConnected = false;
        if (selectedService && selectedMethod) {
            markJobDone(selectedService.name, selectedMethod.name);
            removeChannelFor(selectedService.name, selectedMethod.name);
        }
        duplexChannelId = null;
        if (!statusInfo || statusInfo.status === 'Connected') {
            statusInfo = { status: 'Disconnected', durationMs: 0 };
        }
        render();
    }

