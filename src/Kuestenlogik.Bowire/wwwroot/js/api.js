    // ---- API Calls ----
    async function fetchServices() {
        // Mark discovery in flight + clear stale errors so the empty-state
        // landing page (landing.js) can render the loading state and the
        // failure card based on fresh state. The render() call below makes
        // the spinner instantly visible — without it, the user would see
        // the previous landing state until discovery completed.
        isLoadingServices = true;
        discoveryErrors = {};
        render();

        // Fetch protocols list once — this is identity, doesn't depend on URL
        try {
            var protocolsResp = await fetch(`${config.prefix}/api/protocols`);
            if (protocolsResp.ok) protocols = await protocolsResp.json();
        } catch (_) { /* protocols endpoint optional */ }

        // Always run embedded discovery (no URL) first — this finds
        // gRPC (via reflection), SignalR (via hub metadata), REST (via
        // API explorer), WebSocket, SSE, etc. on the host itself.
        try {
            const resp = await fetch(`${config.prefix}/api/services`);
            if (!resp.ok) throw new Error(`HTTP ${resp.status}`);
            services = await resp.json();
        } catch (e) {
            services = [];
            discoveryErrors['(embedded)'] = e.message;
        }

        // Then fan out per configured URL for protocols that need a
        // remote address: MQTT (broker URL), OData ($metadata URL),
        // standalone gRPC servers, etc. Results are merged and
        // de-duplicated by (source + name).
        if (serverUrls.length > 0) {
            var urlResults = await fetchServicesForAllUrls();
            // De-dupe: skip URL results whose (source, name) already
            // appeared in the embedded set to avoid duplicates.
            var seen = new Set();
            for (var ei = 0; ei < services.length; ei++) {
                seen.add(services[ei].source + '::' + services[ei].name);
            }
            for (var ui = 0; ui < urlResults.length; ui++) {
                var key = urlResults[ui].source + '::' + urlResults[ui].name;
                if (!seen.has(key)) {
                    services.push(urlResults[ui]);
                    seen.add(key);
                }
            }
        }

        // Discovery is done — flip the in-flight flag before the auto-select
        // and the trailing render() so landing.js knows to leave the loading
        // state on the next paint.
        isLoadingServices = false;

        // Protocol filter (multi-select via chips + popup) defaults to
        // empty = "show all". No auto-select here — if the user previously
        // saved a filter in localStorage (loaded on boot), protocolFilter
        // already reflects it. Drop entries whose protocol is no longer in
        // the service set so stale filters don't hide everything.
        if (protocolFilter.size > 0) {
            var validIds = new Set(services.map(function (s) { return s.source; }));
            var changed = false;
            protocolFilter.forEach(function (id) {
                if (!validIds.has(id)) { protocolFilter.delete(id); changed = true; }
            });
            if (changed) {
                persistProtocolFilter();
                refreshSelectedProtocolFromFilter();
            }
        }

        // Auto-expand all services if there are few — but only when
        // the user hasn't already set their own expanded-state (don't
        // fight the persisted localStorage preference on reload).
        var visibleServices = getFilteredServices();
        if (visibleServices.length <= 5 && expandedServices.size === 0) {
            for (const s of visibleServices) expandedServices.add(s.name);
            persistExpandedServices();
        }
        render();
    }

    // ---- Schema Watch Mode ----
    // Re-discovers services every N seconds and shows a delta toast
    // when the schema changes. Useful during API development where
    // the schema moves frequently.
    var schemaWatchInterval = null;
    var schemaWatchPreviousCount = -1;

    function startSchemaWatch(intervalMs) {
        stopSchemaWatch();
        intervalMs = intervalMs || 15000;
        schemaWatchInterval = setInterval(async function () {
            var prevServices = services.slice();
            var prevMethodCount = prevServices.reduce(function (acc, s) {
                return acc + (s.methods ? s.methods.length : 0);
            }, 0);
            var prevServiceCount = prevServices.length;

            await fetchServices();

            var newMethodCount = services.reduce(function (acc, s) {
                return acc + (s.methods ? s.methods.length : 0);
            }, 0);
            var newServiceCount = services.length;

            if (schemaWatchPreviousCount >= 0) {
                var svcDelta = newServiceCount - prevServiceCount;
                var methodDelta = newMethodCount - prevMethodCount;
                if (svcDelta !== 0 || methodDelta !== 0) {
                    var parts = [];
                    if (svcDelta > 0) parts.push('+' + svcDelta + ' service' + (svcDelta !== 1 ? 's' : ''));
                    if (svcDelta < 0) parts.push(svcDelta + ' service' + (svcDelta !== -1 ? 's' : ''));
                    if (methodDelta > 0) parts.push('+' + methodDelta + ' method' + (methodDelta !== 1 ? 's' : ''));
                    if (methodDelta < 0) parts.push(methodDelta + ' method' + (methodDelta !== -1 ? 's' : ''));
                    toast('Schema changed: ' + parts.join(', '), 'info');
                    addConsoleEntry({ type: 'response', method: 'Schema Watch', status: 'Changed', body: parts.join(', ') });
                }
            }
            schemaWatchPreviousCount = newMethodCount;
        }, intervalMs);
        toast('Schema watch started (every ' + (intervalMs / 1000) + 's)', 'info');
    }

    function stopSchemaWatch() {
        if (schemaWatchInterval) {
            clearInterval(schemaWatchInterval);
            schemaWatchInterval = null;
            toast('Schema watch stopped', 'info');
        }
    }

    function isSchemaWatchActive() {
        return schemaWatchInterval !== null;
    }

    async function fetchServicesForAllUrls() {
        var results = await Promise.all(serverUrls.map(fetchServicesForUrl));
        // Flatten while preserving the per-URL discovery order
        var merged = [];
        for (var i = 0; i < results.length; i++) {
            for (var j = 0; j < results[i].length; j++) merged.push(results[i][j]);
        }
        return merged;
    }

    async function fetchServicesForUrl(url) {
        connectionStatuses[url] = 'connecting';
        // MQTT discovery scans topics for ~3s — log so the user
        // knows why it takes longer than HTTP-based protocols.
        var isMqtt = url.indexOf('mqtt') !== -1;
        if (isMqtt) {
            addConsoleEntry({ type: 'request', method: 'MQTT Discovery', status: 'Scanning', body: 'Subscribing to # at ' + url + ' (up to 3s)...' });
        }
        try {
            const resp = await fetch(`${config.prefix}/api/services${serverUrlParam(false, url)}`);
            if (!resp.ok) {
                connectionStatuses[url] = 'error';
                discoveryErrors[url] = 'HTTP ' + resp.status + ' ' + resp.statusText;
                return [];
            }
            var data = await resp.json();
            // The endpoint returns an error object on failure even with 200
            if (data && data.error) {
                connectionStatuses[url] = 'error';
                discoveryErrors[url] = data.error;
                return [];
            }
            connectionStatuses[url] = 'connected';
            if (isMqtt && Array.isArray(data)) {
                var topicCount = data.reduce(function (acc, s) { return acc + (s.methods ? s.methods.length : 0); }, 0);
                addConsoleEntry({ type: 'response', method: 'MQTT Discovery', status: 'OK', body: topicCount + ' topics discovered at ' + url });
            }
            // Make sure every service is tagged with its origin so per-service
            // routing works even if the server forgot to set it.
            if (Array.isArray(data)) {
                for (var i = 0; i < data.length; i++) {
                    if (!data[i].originUrl) data[i].originUrl = url;
                }
                return data;
            }
            return [];
        } catch (e) {
            connectionStatuses[url] = 'error';
            discoveryErrors[url] = e.message || 'Connection failed';
            return [];
        }
    }

    function getFilteredServices() {
        var list = services;

        // Source-mode filter (only when the URL is not locked — locked mode
        // bypasses the source selector entirely and shows everything).
        // The "Schema Files" tab shows everything that came from a user-
        // uploaded file (.proto, .json, .yaml). The "Server URL" tab shows
        // everything else.
        if (!config.lockServerUrl) {
            if (sourceMode === 'proto') {
                list = list.filter(function (s) { return s.isUploaded === true; });
            } else {
                list = list.filter(function (s) { return s.isUploaded !== true; });
            }
        }

        // Protocol filter — multi-select (union / OR). Empty set means
        // "no filter, show everything". Filter only applies in URL mode
        // because uploaded schemas come from the proto/OpenAPI upload
        // pane and don't need protocol filtering.
        if (protocolFilter.size > 0 && sourceMode !== 'proto') {
            list = list.filter(function (s) { return protocolFilter.has(s.source); });
        }

        // Plugin enable toggle — drop services from disabled plugins.
        // Separate from protocolFilter (which is an explicit include-set)
        // so users can disable a plugin once and not have to maintain
        // exclusions in every filter slot they use.
        if (sourceMode !== 'proto') {
            list = list.filter(function (s) {
                return !s.source || isProtocolEnabled(s.source);
            });
        }

        return list;
    }

    async function invokeUnary(service, method, messages, metadata) {
        isExecuting = true;
        markJobActive(service, method);
        responseData = null;
        responseError = null;
        streamMessages = [];
        statusInfo = null;
        render();

        var fullName = service + '/' + method;
        addConsoleEntry({ type: 'request', method: fullName, body: messages[0] || '{}' });

        try {
            // If the user picked "via HTTP" on a transcoded gRPC method, attach
            // the inline TranscodedMethod hint so BowireApiEndpoints dispatches
            // through HttpInvoker instead of the gRPC plugin.
            var transcodedMethod = undefined;
            if (methodSupportsTranscoding(selectedMethod)
                && getTranscodingMode(service, method) === 'http')
            {
                var fields = [];
                if (selectedMethod.inputType && Array.isArray(selectedMethod.inputType.fields)) {
                    fields = selectedMethod.inputType.fields.map(function (f) {
                        return { name: f.name, type: f.type, source: f.source || 'body' };
                    });
                }
                transcodedMethod = {
                    httpMethod: selectedMethod.httpMethod,
                    httpPath: selectedMethod.httpPath,
                    fields: fields
                };
            }

            // Route to the URL the service was discovered from (multi-URL safety)
            const resp = await fetch(`${config.prefix}/api/invoke${serverUrlParamForService(selectedService, false)}`, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({
                    service, method, messages,
                    metadata: metadata || null,
                    protocol: (selectedService && selectedService.source) || selectedProtocol || undefined,
                    transcodedMethod: transcodedMethod
                })
            });

            const result = await resp.json();
            if (result.error) {
                responseError = result.error;
                statusInfo = { status: 'Error', durationMs: 0, responseSize: 0 };
                addConsoleEntry({ type: 'error', method: fullName, status: 'Error', body: result.error });
            } else {
                responseData = result.response;
                captureResponse(result.response); // for ${response.X} chaining
                captureResponseForDiff(service, method, result.response, result.status, result.duration_ms);
                var reqSize = new Blob([JSON.stringify({ service: service, method: method, messages: messages })]).size;
                statusInfo = {
                    status: result.status,
                    durationMs: result.duration_ms,
                    metadata: result.metadata,
                    requestSize: reqSize,
                    responseSize: result.response ? new Blob([result.response]).size : 0
                };
                addConsoleEntry({
                    type: 'response',
                    method: fullName,
                    status: result.status,
                    durationMs: result.duration_ms,
                    body: result.response
                });

                // Run any test assertions configured for this method
                runAssertions(service, method, result.status, lastResponseJson);

                // ---- Post-response script ----
                runPostResponseScript(service, method, lastResponseJson);
            }

            addHistory({
                service,
                method,
                methodType: selectedMethod?.methodType || 'Unary',
                body: messages[0] || '{}',
                messages: messages.slice(),
                status: statusInfo?.status || 'Error',
                durationMs: statusInfo?.durationMs || 0
            });

            // Recorder hook — also push the captured invocation onto the active
            // recording (silent no-op when no recording is active). The unary
            // path captures the response too, so the Convert-to-Tests and HAR
            // export paths have something to assert / dump.
            // httpPath / httpVerb are populated for protocols that carry them
            // (REST, gRPC-HTTP-transcoding) so the Phase-1 mock server can
            // match incoming wire requests against recorded steps.
            captureRecordingStep({
                protocol: (selectedService && selectedService.source) || selectedProtocol || 'grpc',
                service,
                method,
                methodType: selectedMethod?.methodType || 'Unary',
                serverUrl: (selectedService && selectedService.originUrl) || (serverUrls[0] || null),
                body: messages[0] || '{}',
                messages: messages.slice(),
                metadata: metadata || null,
                status: statusInfo?.status || 'Error',
                durationMs: statusInfo?.durationMs || 0,
                response: responseData,
                // Protocols with a binary wire form distinct from their JSON
                // response body (gRPC today) populate this base64 field so
                // the Phase-1b mock server can re-emit the wire bytes 1:1.
                responseBinary: (result && typeof result === 'object' && result.response_binary) || null,
                // gRPC schema (FileDescriptorSet) captured at discovery time.
                // Attached to every step from this service so the Phase-1c
                // mock server can expose gRPC Server Reflection.
                schemaDescriptor: selectedService?.schemaDescriptor || null,
                httpPath: selectedMethod?.httpPath || null,
                httpVerb: selectedMethod?.httpMethod || null
            });
        } catch (e) {
            responseError = e.message;
            statusInfo = { status: 'NetworkError', durationMs: 0 };
            addConsoleEntry({ type: 'error', method: fullName, status: 'NetworkError', body: e.message });
        }

        isExecuting = false;
        markJobDone(service, method);
        render();
    }

    function invokeStreaming(service, method, messages, metadata) {
        isExecuting = true;
        markJobActive(service, method);
        responseData = null;
        responseError = null;
        streamMessages = [];
        // Reset stream UI state so each new stream starts at the defaults
        // (auto-scroll on, follow latest, detail pane not maximized).
        streamSelectedIndex = null;
        streamAutoScroll = true;
        streamDetailMaximized = false;
        statusInfo = { status: 'Streaming', durationMs: 0 };
        render();

        const startTime = performance.now();
        const messagesJson = JSON.stringify(messages);
        // Pick the protocol the same way the unary path does: the service's
        // own origin first, then the global filter, else skip the param and
        // let the backend's default plugin handle it. Without this, streams
        // against a SignalR Hub were routed to the gRPC plugin and failed.
        var protocolForStream = (selectedService && selectedService.source) || selectedProtocol;
        var protocolParam = protocolForStream ? `&protocol=${encodeURIComponent(protocolForStream)}` : '';
        var metadataParam = metadata && Object.keys(metadata).length > 0
            ? `&metadata=${encodeURIComponent(JSON.stringify(metadata))}`
            : '';
        const url = `${config.prefix}/api/invoke/stream?service=${encodeURIComponent(service)}&method=${encodeURIComponent(method)}&messages=${encodeURIComponent(messagesJson)}${protocolParam}${metadataParam}${serverUrlParamForService(selectedService, true)}`;

        var fullName = service + '/' + method;
        addConsoleEntry({ type: 'request', method: fullName, status: 'Streaming', body: messages[0] || '{}' });

        sseSource = new EventSource(url);

        sseSource.onmessage = function (event) {
            // Record wall-clock arrival so recordings persist the real
            // client-side cadence of the stream — needed for Phase-2 mock
            // replay timing. Server-side offset lives inside `parsed` as
            // `timestampMs` (see BowireInvokeEndpoints.cs).
            var receivedAt = Date.now();
            try {
                const parsed = JSON.parse(event.data);
                parsed._clientReceivedAtMs = receivedAt;
                streamMessages.push(parsed);
                // Chaining: capture the inner data payload (last message wins)
                if (parsed && parsed.data !== undefined) captureResponse(parsed.data);
                addConsoleEntry({ type: 'stream', method: fullName, body: parsed.data || event.data });
                // Fast-path: surgically append a single list item to the
                // streaming output instead of nuking the whole app DOM. The
                // first message of a stream still falls through to render()
                // because the container doesn't exist yet.
                if (!window.bowireAppendStreamMessage || !window.bowireAppendStreamMessage()) {
                    render();
                }
            } catch {
                streamMessages.push({ index: streamMessages.length, data: event.data, _clientReceivedAtMs: receivedAt });
                addConsoleEntry({ type: 'stream', method: fullName, body: event.data });
                if (!window.bowireAppendStreamMessage || !window.bowireAppendStreamMessage()) {
                    render();
                }
            }
        };

        sseSource.addEventListener('done', function () {
            const elapsed = Math.round(performance.now() - startTime);
            statusInfo = { status: 'OK', durationMs: elapsed };
            isExecuting = false;
            markJobDone(service, method);
            sseSource.close();
            sseSource = null;

            addConsoleEntry({ type: 'response', method: fullName, status: 'Completed', durationMs: elapsed });

            // ---- Post-response script (streaming) ----
            var streamResponseObj = streamMessages.length > 0 ? streamMessages[streamMessages.length - 1] : null;
            if (streamResponseObj && streamResponseObj.data !== undefined) {
                try { streamResponseObj = JSON.parse(streamResponseObj.data); } catch {}
            }
            runPostResponseScript(service, method, streamResponseObj);

            addHistory({
                service,
                method,
                methodType: selectedMethod?.methodType || 'ServerStreaming',
                body: messages[0] || '{}',
                messages: messages.slice(),
                status: 'OK',
                durationMs: elapsed
            });

            // Recorder hook — captured for HAR export, the recording log,
            // AND for Phase-2c mock replay: receivedMessages preserves the
            // full frame sequence with per-frame timestampMs so the mock
            // server can reproduce the original stream cadence.
            captureRecordingStep({
                protocol: (selectedService && selectedService.source) || selectedProtocol || 'grpc',
                service,
                method,
                methodType: selectedMethod?.methodType || 'ServerStreaming',
                serverUrl: (selectedService && selectedService.originUrl) || (serverUrls[0] || null),
                body: messages[0] || '{}',
                messages: messages.slice(),
                metadata: metadata || null,
                status: 'OK',
                durationMs: elapsed,
                response: streamMessages.length > 0 ? streamMessages[streamMessages.length - 1] : null,
                receivedMessages: streamMessages.map(function (m, i) {
                    return {
                        index: (m && typeof m.index === 'number') ? m.index : i,
                        timestampMs: (m && typeof m.timestampMs === 'number') ? m.timestampMs : null,
                        data: m ? m.data : null,
                        // Protocols with a distinct binary wire form (gRPC)
                        // supply per-frame wire bytes via the envelope's
                        // responseBinary field. The Phase-2d mock-server
                        // replay path emits them 1:1 without re-encoding.
                        responseBinary: (m && typeof m.responseBinary === 'string') ? m.responseBinary : null
                    };
                }),
                httpPath: selectedMethod?.httpPath || null,
                httpVerb: selectedMethod?.httpMethod || null
            });

            render();
        });

        sseSource.addEventListener('error', function (e) {
            if (sseSource.readyState === EventSource.CLOSED) return;
            const elapsed = Math.round(performance.now() - startTime);
            responseError = 'Stream error occurred.';
            statusInfo = { status: 'Error', durationMs: elapsed };
            isExecuting = false;
            markJobDone(service, method);
            sseSource.close();
            sseSource = null;
            addConsoleEntry({ type: 'error', method: fullName, status: 'Error', body: 'Stream error occurred', durationMs: elapsed });
            render();
        });
    }

    function stopStreaming() {
        if (sseSource) {
            sseSource.close();
            sseSource = null;
        }
        isExecuting = false;
        if (selectedService && selectedMethod) {
            markJobDone(selectedService.name, selectedMethod.name);
        }
        if (statusInfo) statusInfo.status = 'Cancelled';
        render();
    }

    // ---- Channel Operations (Duplex / Client Streaming) ----

    function isChannelMethod() {
        return selectedMethod && (selectedMethod.methodType === 'Duplex' || selectedMethod.methodType === 'ClientStreaming');
    }

