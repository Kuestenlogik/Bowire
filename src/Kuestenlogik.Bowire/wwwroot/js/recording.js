    // ---- Recordings ----
    // Postman-style scenario recorder. The user clicks Record, makes a few
    // calls (any protocol — gRPC, REST, GraphQL, WebSocket, MCP, ...), clicks
    // Stop, and the captured sequence becomes a named, replayable recording.
    // The capture hook lives in invokeUnary / invokeStreaming / channelConnect
    // — anywhere addHistory is called we also push the same payload into the
    // active recording when isRecording() is true. Persistence is the same
    // localStorage-cache-with-disk-sync pattern used by environments.

    function isRecording() {
        return recordingActiveId !== null;
    }

    function nextRecordingId() {
        return 'rec_' + Date.now().toString(36) + Math.random().toString(36).slice(2, 6);
    }

    function nextStepId() {
        return 'step_' + Date.now().toString(36) + Math.random().toString(36).slice(2, 6);
    }

    function loadRecordings() {
        try {
            var raw = localStorage.getItem(RECORDINGS_KEY);
            var list = raw ? JSON.parse(raw) : [];
            return Array.isArray(list) ? list : [];
        } catch {
            return [];
        }
    }

    function persistRecordings() {
        try {
            localStorage.setItem(RECORDINGS_KEY, JSON.stringify(recordingsList));
        } catch { /* localStorage full / disabled — fall through */ }
        scheduleRecordingsDiskSync();
    }

    // Debounced PUT to /api/recordings — same 400ms debounce as environments
    // so the disk file stays in sync without thrashing on every step append.
    var _recordingsDiskSyncTimer = null;
    function scheduleRecordingsDiskSync() {
        if (_recordingsDiskSyncTimer) clearTimeout(_recordingsDiskSyncTimer);
        _recordingsDiskSyncTimer = setTimeout(function () {
            _recordingsDiskSyncTimer = null;
            var payload = JSON.stringify({ recordings: recordingsList });
            fetch(config.prefix + '/api/recordings', {
                method: 'PUT',
                headers: { 'Content-Type': 'application/json' },
                body: payload
            }).catch(function () { /* offline — localStorage still has it */ });
        }, 400);
    }

    // Pull recordings from disk on init so the list survives browser changes
    // and CLI usage. The disk file wins over the localStorage cache (the
    // localStorage cache only matters for instant updates between roundtrips).
    function loadRecordingsFromDisk() {
        return fetch(config.prefix + '/api/recordings')
            .then(function (r) { return r.ok ? r.json() : null; })
            .then(function (data) {
                if (data && Array.isArray(data.recordings)) {
                    recordingsList = data.recordings;
                    try { localStorage.setItem(RECORDINGS_KEY, JSON.stringify(recordingsList)); } catch { /* ignore */ }
                } else {
                    recordingsList = loadRecordings();
                }
            })
            .catch(function () {
                recordingsList = loadRecordings();
            });
    }

    function startRecording(name) {
        var rec = {
            id: nextRecordingId(),
            name: name || ('Recording ' + new Date().toLocaleString()),
            description: '',
            createdAt: Date.now(),
            // Forward-looking version tag — bumped when later phases add
            // fields the mock depends on. v1 = Phase-1a (REST unary only);
            // v2 = Phase-1b (adds responseBinary for gRPC-unary replay).
            // The mock-server loader accepts the versions it was built for
            // and rejects anything newer. See docs/concepts/mock-server-phase1.md.
            recordingFormatVersion: 2,
            steps: []
        };
        recordingsList.push(rec);
        recordingActiveId = rec.id;
        persistRecordings();
        addConsoleEntry({ type: 'response', method: rec.name, status: 'Recording started' });
        render();
    }

    function stopRecording() {
        var rec = recordingsList.find(function (r) { return r.id === recordingActiveId; });
        var name = rec ? rec.name : 'Recording';
        var stepCount = rec ? rec.steps.length : 0;
        recordingActiveId = null;
        persistRecordings();
        addConsoleEntry({ type: 'response', method: name, status: 'Recording stopped (' + stepCount + ' step' + (stepCount !== 1 ? 's' : '') + ')' });
        render();
    }

    /**
     * Append a captured step to the currently active recording. Called from
     * the addHistory call sites in invokeUnary / invokeStreaming /
     * channelConnect — same payload, just routed into the recording too.
     * Silent no-op when there is no active recording.
     */
    function captureRecordingStep(entry) {
        if (!isRecording()) return;
        var rec = recordingsList.find(function (r) { return r.id === recordingActiveId; });
        if (!rec) return;
        rec.steps.push(Object.assign({
            id: nextStepId(),
            capturedAt: Date.now()
        }, entry));
        persistRecordings();
        // Don't re-render here — addHistory's caller already calls render()
        // after the response has been processed.
    }

    function deleteRecording(id) {
        var idx = recordingsList.findIndex(function (r) { return r.id === id; });
        if (idx < 0) return;
        recordingsList.splice(idx, 1);
        if (recordingActiveId === id) recordingActiveId = null;
        if (recordingManagerSelectedId === id) recordingManagerSelectedId = null;
        persistRecordings();
        renderRecordingManager();
    }

    function renameRecording(id, name) {
        var rec = recordingsList.find(function (r) { return r.id === id; });
        if (!rec) return;
        rec.name = name;
        persistRecordings();
    }

    function deleteRecordingStep(recordingId, stepId) {
        var rec = recordingsList.find(function (r) { return r.id === recordingId; });
        if (!rec) return;
        var idx = rec.steps.findIndex(function (s) { return s.id === stepId; });
        if (idx < 0) return;
        rec.steps.splice(idx, 1);
        persistRecordings();
        renderRecordingManager();
    }

    // ---- Replay ----
    // Walk every captured step in order, re-invoke it through /api/invoke,
    // and accumulate pass/fail. Variable substitution uses the CURRENT env,
    // not the one that was active when the recording was made — recordings
    // are deliberately portable across environments. Streaming methods are
    // not replayed (they need a different endpoint and the user usually
    // wants the final response anyway), they're skipped with a status.

    // Plugin-check state. Non-null while the "missing plugins" modal
    // is open, holds: { recordingId, missing: [{id,packageId}],
    // installing: bool, installedAll: bool, error: string | null,
    // perPackage: { packageId → 'pending'|'installing'|'done'|'failed' } }.
    // Mirrors recordingReplayState's shape so re-renders are uniform.
    var pluginCheckState = null;

    async function fetchInstalledProtocols() {
        try {
            var r = await fetch(config.prefix + '/api/plugins/protocols');
            if (!r.ok) return null;
            return await r.json();
        } catch (e) {
            return null;
        }
    }

    function distinctRecordingProtocols(rec) {
        var seen = {};
        var out = [];
        for (var i = 0; i < rec.steps.length; i++) {
            var p = (rec.steps[i].protocol || '').trim().toLowerCase();
            if (!p || seen[p]) continue;
            seen[p] = true;
            out.push(p);
        }
        return out;
    }

    async function checkMissingPlugins(rec) {
        var info = await fetchInstalledProtocols();
        if (!info) return { missing: [], catalog: {} }; // assume ok on fetch failure
        var loaded = {};
        for (var i = 0; i < (info.loaded || []).length; i++) loaded[info.loaded[i].toLowerCase()] = true;
        var catalog = info.catalog || {};
        var protocols = distinctRecordingProtocols(rec);
        var missing = [];
        for (var j = 0; j < protocols.length; j++) {
            var id = protocols[j];
            if (loaded[id]) continue;
            missing.push({ id: id, packageId: catalog[id] || null });
        }
        return { missing: missing, catalog: catalog };
    }

    async function installMissingPlugin(packageId) {
        var resp = await fetch(config.prefix + '/api/plugins/install', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ packageId: packageId })
        });
        if (!resp.ok) {
            var msg = 'HTTP ' + resp.status;
            try { var body = await resp.json(); if (body && body.error) msg = body.error; } catch {}
            throw new Error(msg);
        }
        return await resp.json();
    }

    async function installAllMissing() {
        if (!pluginCheckState || pluginCheckState.installing) return;
        pluginCheckState.installing = true;
        pluginCheckState.error = null;
        renderRecordingManager();
        var anyFailed = false;
        for (var i = 0; i < pluginCheckState.missing.length; i++) {
            var entry = pluginCheckState.missing[i];
            if (!entry.packageId) {
                pluginCheckState.perPackage[entry.id] = 'failed';
                anyFailed = true;
                continue;
            }
            pluginCheckState.perPackage[entry.id] = 'installing';
            renderRecordingManager();
            try {
                await installMissingPlugin(entry.packageId);
                pluginCheckState.perPackage[entry.id] = 'done';
            } catch (e) {
                pluginCheckState.perPackage[entry.id] = 'failed';
                anyFailed = true;
            }
            renderRecordingManager();
        }
        pluginCheckState.installing = false;
        pluginCheckState.installedAll = !anyFailed;
        if (anyFailed) {
            pluginCheckState.error = 'Some installs failed. Check the workbench logs.';
        }
        renderRecordingManager();
    }

    function dismissPluginCheck() {
        pluginCheckState = null;
        renderRecordingManager();
    }

    async function replayRecording(id) {
        var rec = recordingsList.find(function (r) { return r.id === id; });
        if (!rec || rec.steps.length === 0) return;

        // Pre-flight: scan the recording for protocols whose plugins
        // aren't loaded in this Bowire host. If any are missing, pop
        // the "install plugins?" modal and bail; the modal's install
        // button retriggers the replay flow once the install lands +
        // the host is restarted.
        var check = await checkMissingPlugins(rec);
        if (check.missing.length > 0) {
            pluginCheckState = {
                recordingId: id,
                missing: check.missing,
                installing: false,
                installedAll: false,
                error: null,
                perPackage: {}
            };
            renderRecordingManager();
            return;
        }

        recordingReplayState = {
            recordingId: id,
            stepIndex: -1,
            status: 'running',
            results: []
        };
        renderRecordingManager();

        for (var i = 0; i < rec.steps.length; i++) {
            recordingReplayState.stepIndex = i;
            renderRecordingManager();

            var step = rec.steps[i];
            try {
                var result = await replaySingleStep(step);
                recordingReplayState.results.push({
                    stepId: step.id,
                    pass: result.pass,
                    status: result.status,
                    durationMs: result.durationMs,
                    response: result.response,
                    error: result.error
                });
            } catch (e) {
                recordingReplayState.results.push({
                    stepId: step.id,
                    pass: false,
                    status: 'NetworkError',
                    error: e.message
                });
            }
            renderRecordingManager();
        }

        recordingReplayState.status = 'done';
        renderRecordingManager();
    }

    function replaySingleStep(step) {
        // We only know how to replay unary calls server-side via /api/invoke.
        // Streaming and channel methods need their own dispatch and aren't
        // worth the wire complexity for a Postman-parity feature.
        if (step.methodType && step.methodType !== 'Unary') {
            return Promise.resolve({
                pass: false,
                status: 'Skipped (' + step.methodType + ')',
                durationMs: 0,
                error: 'Only Unary methods are replayed; streaming methods are skipped.'
            });
        }

        var messages = (step.messages && step.messages.length > 0)
            ? step.messages
            : [step.body || '{}'];

        // Apply current env-var substitution to each message + every metadata
        // value, so a recording made against ${baseUrl}/Dev replays cleanly
        // against ${baseUrl}/Prod after switching env.
        var substitutedMessages = messages.map(function (m) { return substituteVars(m); });
        var substitutedMeta = step.metadata
            ? Object.fromEntries(Object.entries(step.metadata).map(function (kv) {
                return [kv[0], substituteVars(String(kv[1]))];
            }))
            : null;

        // Server URL: prefer the step's recorded origin URL when present, else
        // fall back to the currently active server URL. This lets a single
        // recording span multiple URLs (e.g. orchestration scenario across
        // two services) and still replay correctly.
        var serverUrl = step.serverUrl || (serverUrls.length > 0 ? serverUrls[0] : '');

        var body = {
            service: step.service,
            method: step.method,
            messages: substitutedMessages,
            metadata: substitutedMeta,
            protocol: step.protocol || null
        };

        var url = config.prefix + '/api/invoke'
            + (serverUrl ? '?serverUrl=' + encodeURIComponent(serverUrl) : '');

        return fetch(url, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(body)
        }).then(function (resp) {
            return resp.json().then(function (json) {
                var ok = resp.ok && !json.error;
                return {
                    pass: ok,
                    status: json.status || (ok ? 'OK' : 'Error'),
                    durationMs: json.duration_ms || 0,
                    response: json.response,
                    error: json.error || null
                };
            });
        });
    }

    // ---- Export: Bowire JSON ----
    function exportRecordingAsJson(id) {
        var rec = recordingsList.find(function (r) { return r.id === id; });
        if (!rec) return;
        var blob = new Blob([JSON.stringify(rec, null, 2)], { type: 'application/json' });
        downloadBlob(blob, sanitizeFilename(rec.name) + '.blr');
    }

    // ---- Export: HAR 1.2 ----
    // HTTP Archive format — recognised by Chrome DevTools, Firefox DevTools,
    // Postman, Insomnia, Charles Proxy, Fiddler, etc. Each recorded step
    // becomes one HAR entry with synthetic request + response. We tag the
    // protocol name on the request comment so the gRPC/MCP/etc. context isn't
    // lost in translation, even though HAR itself only knows about HTTP.
    function exportRecordingAsHar(id) {
        var rec = recordingsList.find(function (r) { return r.id === id; });
        if (!rec) return;

        var entries = rec.steps.map(function (step) {
            var url = (step.serverUrl || (serverUrls[0] || 'http://recording.local'))
                + '/' + (step.service || '') + '/' + (step.method || '');
            var headers = step.metadata
                ? Object.entries(step.metadata).map(function (kv) {
                    return { name: kv[0], value: String(kv[1]) };
                })
                : [];
            var body = step.body || (step.messages && step.messages[0]) || '{}';
            var responseText = typeof step.response === 'string'
                ? step.response
                : JSON.stringify(step.response || {}, null, 2);
            var statusName = String(step.status || 'OK');
            var statusCode = parseInt(statusName, 10);
            if (isNaN(statusCode)) statusCode = (statusName === 'OK') ? 200 : 500;

            return {
                startedDateTime: new Date(step.capturedAt || Date.now()).toISOString(),
                time: step.durationMs || 0,
                request: {
                    method: 'POST',
                    url: url,
                    httpVersion: 'HTTP/1.1',
                    cookies: [],
                    headers: headers.concat([{ name: 'Content-Type', value: 'application/json' }]),
                    queryString: [],
                    headersSize: -1,
                    bodySize: body.length,
                    postData: {
                        mimeType: 'application/json',
                        text: body
                    },
                    comment: 'Bowire recorded step (protocol=' + (step.protocol || 'unknown') + ')'
                },
                response: {
                    status: statusCode,
                    statusText: statusName,
                    httpVersion: 'HTTP/1.1',
                    cookies: [],
                    headers: [{ name: 'Content-Type', value: 'application/json' }],
                    content: {
                        size: responseText.length,
                        mimeType: 'application/json',
                        text: responseText
                    },
                    redirectURL: '',
                    headersSize: -1,
                    bodySize: responseText.length
                },
                cache: {},
                timings: {
                    send: 0,
                    wait: step.durationMs || 0,
                    receive: 0
                }
            };
        });

        var har = {
            log: {
                version: '1.2',
                creator: { name: 'Bowire', version: '0.9.0' },
                pages: [],
                entries: entries,
                comment: 'Recording: ' + rec.name + ' (' + rec.steps.length + ' step' + (rec.steps.length !== 1 ? 's' : '') + ')'
            }
        };

        var blob = new Blob([JSON.stringify(har, null, 2)], { type: 'application/json' });
        downloadBlob(blob, sanitizeFilename(rec.name) + '.har');
    }

    // ---- Export: CI HTML Report ----
    // Self-contained HTML file with embedded styles. Designed to be
    // dropped straight into a CI artifact bucket — open in any browser
    // to inspect the recording: pass/fail summary at the top, per-step
    // detail with collapsed request/response below. If a replay has
    // been run for this recording the report uses those results;
    // otherwise it falls back to the captured statuses from the
    // original capture pass.
    function exportRecordingAsHtml(id) {
        var rec = recordingsList.find(function (r) { return r.id === id; });
        if (!rec) return;

        // Replay results win over captured results when present, because
        // they reflect the freshest pass against the current environment.
        var hasReplay = recordingReplayState
            && recordingReplayState.recordingId === id
            && recordingReplayState.results
            && recordingReplayState.results.length > 0;

        function statusForStep(step) {
            if (hasReplay) {
                var r = recordingReplayState.results.find(function (x) { return x.stepId === step.id; });
                if (r) {
                    return {
                        pass: !!r.pass,
                        statusText: r.status || (r.pass ? 'PASS' : 'FAIL'),
                        durationMs: r.durationMs || 0,
                        response: r.response,
                        error: r.error || null
                    };
                }
            }
            // No replay — derive pass from captured status name. Same
            // mapping isHistoryEntryOk uses elsewhere so the report
            // agrees with the in-app history view.
            var s = String(step.status || '');
            var pass = s === 'OK' || s === 'Connected' || s === 'Completed';
            var num = parseInt(s, 10);
            if (!isNaN(num)) pass = num < 400;
            return {
                pass: pass,
                statusText: s || 'unknown',
                durationMs: step.durationMs || 0,
                response: step.response,
                error: null
            };
        }

        var rows = rec.steps.map(function (step, i) { return { step: step, idx: i, st: statusForStep(step) }; });
        var passCount = rows.filter(function (r) { return r.st.pass; }).length;
        var failCount = rows.length - passCount;
        var totalDuration = rows.reduce(function (acc, r) { return acc + (r.st.durationMs || 0); }, 0);

        function esc(s) {
            return String(s == null ? '' : s)
                .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
                .replace(/"/g, '&quot;').replace(/'/g, '&#39;');
        }
        function fmtJson(v) {
            if (v == null) return '';
            if (typeof v === 'string') {
                try { return JSON.stringify(JSON.parse(v), null, 2); }
                catch { return v; }
            }
            try { return JSON.stringify(v, null, 2); }
            catch { return String(v); }
        }

        var generated = new Date();
        var rowsHtml = rows.map(function (r) {
            var bodyJson = fmtJson(r.step.body || (r.step.messages && r.step.messages[0]) || '');
            var respJson = fmtJson(r.st.response);
            var metaHtml = '';
            if (r.step.metadata && Object.keys(r.step.metadata).length > 0) {
                metaHtml = '<div class="meta"><strong>Headers</strong><pre>'
                    + esc(JSON.stringify(r.step.metadata, null, 2)) + '</pre></div>';
            }
            return ''
                + '<details class="step ' + (r.st.pass ? 'pass' : 'fail') + '">'
                + '<summary>'
                +   '<span class="idx">' + (r.idx + 1) + '</span>'
                +   '<span class="badge ' + (r.st.pass ? 'b-pass' : 'b-fail') + '">'
                +     (r.st.pass ? 'PASS' : 'FAIL') + '</span>'
                +   '<span class="proto">' + esc(r.step.protocol || 'grpc') + '</span>'
                +   '<span class="method">' + esc(r.step.service || '') + ' / ' + esc(r.step.method || '') + '</span>'
                +   '<span class="status">' + esc(r.st.statusText) + '</span>'
                +   '<span class="dur">' + (r.st.durationMs || 0) + ' ms</span>'
                + '</summary>'
                + '<div class="body">'
                +   (r.st.error ? '<div class="err"><strong>Error</strong><pre>' + esc(r.st.error) + '</pre></div>' : '')
                +   metaHtml
                +   '<div class="req"><strong>Request</strong><pre>' + esc(bodyJson) + '</pre></div>'
                +   '<div class="resp"><strong>Response</strong><pre>' + esc(respJson) + '</pre></div>'
                + '</div>'
                + '</details>';
        }).join('\n');

        var html = ''
            + '<!DOCTYPE html>\n<html lang="en"><head>'
            + '<meta charset="utf-8">'
            + '<title>' + esc(rec.name) + ' \u2014 Bowire Recording Report</title>'
            + '<style>'
            + 'body { font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", system-ui, sans-serif; '
            +   'background: #0e0e16; color: #e6e6f0; margin: 0; padding: 24px; line-height: 1.5; }'
            + 'h1 { font-size: 22px; margin: 0 0 4px; }'
            + 'h1 .name { color: #a78bfa; }'
            + '.subtitle { color: #8888a0; font-size: 13px; margin-bottom: 24px; }'
            + '.summary { display: flex; gap: 12px; margin-bottom: 24px; flex-wrap: wrap; }'
            + '.card { background: #1a1a25; border: 1px solid #2a2a3a; border-radius: 6px; padding: 12px 16px; min-width: 110px; }'
            + '.card .label { font-size: 11px; text-transform: uppercase; color: #8888a0; letter-spacing: 0.06em; }'
            + '.card .value { font-size: 22px; font-weight: 600; margin-top: 4px; }'
            + '.card.ok .value { color: #34d399; }'
            + '.card.bad .value { color: #f87171; }'
            + '.card.neutral .value { color: #fb923c; }'
            + '.step { background: #1a1a25; border: 1px solid #2a2a3a; border-left-width: 3px; '
            +   'border-radius: 4px; margin-bottom: 8px; }'
            + '.step.pass { border-left-color: #34d399; }'
            + '.step.fail { border-left-color: #f87171; }'
            + '.step summary { padding: 10px 14px; cursor: pointer; display: flex; align-items: center; gap: 12px; '
            +   'font-size: 13px; user-select: none; }'
            + '.step summary::-webkit-details-marker { display: none; }'
            + '.step .idx { color: #8888a0; min-width: 28px; }'
            + '.step .badge { padding: 2px 8px; border-radius: 3px; font-size: 10px; font-weight: 700; '
            +   'letter-spacing: 0.06em; }'
            + '.step .b-pass { background: #064e3b; color: #34d399; }'
            + '.step .b-fail { background: #7f1d1d; color: #f87171; }'
            + '.step .proto { background: #312e81; color: #c7d2fe; padding: 2px 8px; border-radius: 3px; '
            +   'font-size: 10px; text-transform: uppercase; }'
            + '.step .method { flex: 1; font-family: ui-monospace, "SF Mono", Consolas, monospace; }'
            + '.step .status { color: #c7c7d8; font-family: ui-monospace, monospace; font-size: 11px; }'
            + '.step .dur { color: #8888a0; font-family: ui-monospace, monospace; font-size: 11px; }'
            + '.step .body { padding: 0 14px 14px; border-top: 1px solid #2a2a3a; }'
            + '.step .body pre { background: #0e0e16; border: 1px solid #2a2a3a; border-radius: 4px; '
            +   'padding: 10px; overflow-x: auto; font-size: 12px; line-height: 1.45; }'
            + '.step .body strong { display: block; font-size: 11px; color: #8888a0; margin: 12px 0 4px; '
            +   'text-transform: uppercase; letter-spacing: 0.06em; }'
            + '.step .err pre { color: #f87171; border-color: #7f1d1d; }'
            + 'footer { color: #6a6a82; font-size: 11px; margin-top: 32px; text-align: center; }'
            + '</style></head><body>'
            + '<h1>Recording report \u2014 <span class="name">' + esc(rec.name) + '</span></h1>'
            + '<div class="subtitle">Generated ' + esc(generated.toISOString())
            +   ' \u00b7 ' + (hasReplay ? 'Replay results' : 'Captured statuses')
            +   ' \u00b7 ' + rec.steps.length + ' step' + (rec.steps.length !== 1 ? 's' : '') + '</div>'
            + '<div class="summary">'
            +   '<div class="card ' + (failCount === 0 ? 'ok' : 'bad') + '">'
            +     '<div class="label">Result</div>'
            +     '<div class="value">' + (failCount === 0 ? 'PASS' : 'FAIL') + '</div>'
            +   '</div>'
            +   '<div class="card ok"><div class="label">Passed</div><div class="value">' + passCount + '</div></div>'
            +   '<div class="card ' + (failCount === 0 ? 'neutral' : 'bad') + '">'
            +     '<div class="label">Failed</div><div class="value">' + failCount + '</div></div>'
            +   '<div class="card neutral"><div class="label">Total time</div><div class="value">' + totalDuration + ' ms</div></div>'
            + '</div>'
            + rowsHtml
            + '<footer>Generated by Bowire \u2014 https://github.com/Kuestenlogik/Bowire</footer>'
            + '</body></html>';

        var blob = new Blob([html], { type: 'text/html' });
        downloadBlob(blob, sanitizeFilename(rec.name) + '.report.html');
    }

    // ---- Convert recording → tests ----
    // For each step, write two assertions onto the (service, method) pair:
    //   1) status equals the captured status name
    //   2) response equals the captured response (deep eq)
    // Existing assertions on those methods are LEFT IN PLACE — we append, so
    // a user who's already written manual tests doesn't lose them. Returns
    // the number of new assertions added so the caller can show a toast.
    function convertRecordingToTests(id) {
        var rec = recordingsList.find(function (r) { return r.id === id; });
        if (!rec) return 0;

        var added = 0;
        for (var i = 0; i < rec.steps.length; i++) {
            var step = rec.steps[i];
            var existing = getTestsFor(step.service, step.method);

            existing.push({
                id: nextTestId(),
                path: 'status',
                op: 'eq',
                expected: String(step.status || 'OK')
            });
            added++;

            if (step.response !== undefined && step.response !== null) {
                existing.push({
                    id: nextTestId(),
                    path: 'response',
                    op: 'eq',
                    expected: typeof step.response === 'string'
                        ? step.response
                        : JSON.stringify(step.response)
                });
                added++;
            }

            setTestsFor(step.service, step.method, existing);
        }
        return added;
    }

    // ---- Small helpers ----
    function sanitizeFilename(name) {
        return String(name || 'recording').replace(/[^a-zA-Z0-9._-]/g, '_').slice(0, 80);
    }

    function downloadBlob(blob, filename) {
        var a = document.createElement('a');
        a.href = URL.createObjectURL(blob);
        a.download = filename;
        a.click();
        URL.revokeObjectURL(a.href);
    }

    // ---- Manager Modal ----
    function renderRecordingManager() {
        var existing = $('.bowire-recording-modal-overlay');
        if (existing) existing.remove();
        var existingPlugin = $('.bowire-plugin-check-overlay');
        if (existingPlugin) existingPlugin.remove();

        // Plugin-check modal renders independently — it can pop up
        // even when the recording manager isn't open (replay clicked
        // from elsewhere). Stack it above the manager overlay when
        // both are visible.
        renderPluginCheckModal();

        if (!recordingManagerOpen) return;

        var selected = recordingsList.find(function (r) { return r.id === recordingManagerSelectedId; });

        // Left panel: list of recordings
        var leftPanel = el('div', { className: 'bowire-env-modal-left' });

        leftPanel.appendChild(el('div', { className: 'bowire-env-list-header' },
            el('span', { textContent: 'Recordings' }),
            el('button', {
                className: 'bowire-env-add-btn',
                title: 'Start new recording',
                'aria-label': 'Start new recording',
                innerHTML: svgIcon('record'),
                onClick: function () {
                    if (isRecording()) {
                        stopRecording();
                    } else {
                        startRecording();
                    }
                    recordingManagerSelectedId = recordingActiveId;
                    renderRecordingManager();
                }
            })
        ));

        var list = el('div', { className: 'bowire-env-list' });
        if (recordingsList.length === 0) {
            list.appendChild(el('div', { className: 'bowire-env-list-empty',
                textContent: 'No recordings yet. Click ● in the action bar to start capturing a sequence of calls.' }));
        }
        for (var i = 0; i < recordingsList.length; i++) {
            (function (rec) {
                var isSelected = recordingManagerSelectedId === rec.id;
                var isActive = recordingActiveId === rec.id;
                var item = el('div', {
                    className: 'bowire-env-list-item' + (isSelected ? ' active' : '') + (isActive ? ' bowire-recording-active' : ''),
                    onClick: function () {
                        recordingManagerSelectedId = rec.id;
                        renderRecordingManager();
                    }
                },
                    el('span', { className: 'bowire-env-list-item-name', textContent: rec.name }),
                    el('span', { className: 'bowire-env-list-item-count', textContent: String(rec.steps.length) })
                );
                list.appendChild(item);
            })(recordingsList[i]);
        }
        leftPanel.appendChild(list);

        // Right panel: selected recording detail
        var rightPanel = el('div', { className: 'bowire-env-modal-right' });

        if (selected) {
            rightPanel.appendChild(renderRecordingDetail(selected));
        } else {
            rightPanel.appendChild(el('div', { className: 'bowire-env-empty',
                textContent: 'Select a recording on the left or click ● to start a new one.' }));
        }

        var modal = el('div', { className: 'bowire-env-modal bowire-recording-modal' },
            el('div', { className: 'bowire-env-modal-header' },
                el('div', { className: 'bowire-env-modal-title' },
                    el('span', { innerHTML: svgIcon('record'), className: 'bowire-env-modal-icon' }),
                    el('span', { textContent: 'Recordings' })
                ),
                el('button', {
                    className: 'bowire-env-modal-close',
                    innerHTML: svgIcon('close'),
                    title: 'Close (Esc)',
                    'aria-label': 'Close recordings',
                    onClick: function () { recordingManagerOpen = false; renderRecordingManager(); }
                })
            ),
            el('div', { className: 'bowire-env-modal-body' }, leftPanel, rightPanel)
        );

        var overlay = el('div', {
            className: 'bowire-env-modal-overlay bowire-recording-modal-overlay',
            role: 'dialog',
            'aria-modal': 'true',
            'aria-label': 'Recordings',
            onClick: function (e) {
                if (e.target === overlay) { recordingManagerOpen = false; renderRecordingManager(); }
            }
        }, modal);

        document.body.appendChild(overlay);
    }

    function renderPluginCheckModal() {
        if (!pluginCheckState) return;

        var allDone = pluginCheckState.installedAll;
        var modal = el('div', { className: 'bowire-env-modal bowire-plugin-check-modal' });

        // Header
        modal.appendChild(el('div', { className: 'bowire-env-modal-header' },
            el('h2', { className: 'bowire-env-modal-title',
                textContent: allDone ? 'Plugins installed' : 'Plugins required for replay' })));

        var body = el('div', { className: 'bowire-env-modal-body bowire-plugin-check-body' });

        if (allDone) {
            // Post-install success: tell the user the next step.
            body.appendChild(el('p', { className: 'bowire-plugin-check-intro',
                textContent: 'The plugins were installed into ~/.bowire/plugins/. Restart Bowire so the new protocols are picked up by the registry.' }));
        } else {
            body.appendChild(el('p', { className: 'bowire-plugin-check-intro',
                textContent: 'This recording references protocols that aren’t loaded in your Bowire host. Install the matching plugins to replay it.' }));
        }

        var listEl = el('ul', { className: 'bowire-plugin-check-list' });
        pluginCheckState.missing.forEach(function (entry) {
            var status = pluginCheckState.perPackage[entry.id] || (entry.packageId ? 'pending' : 'unknown');
            var statusBadge;
            if (status === 'installing') {
                statusBadge = el('span', { className: 'bowire-plugin-check-status installing', textContent: 'installing…' });
            } else if (status === 'done') {
                statusBadge = el('span', { className: 'bowire-plugin-check-status done', textContent: '✓ installed' });
            } else if (status === 'failed') {
                statusBadge = el('span', { className: 'bowire-plugin-check-status failed', textContent: '✗ failed' });
            } else if (status === 'unknown') {
                statusBadge = el('span', { className: 'bowire-plugin-check-status unknown',
                    textContent: 'third-party — install manually' });
            } else {
                statusBadge = el('span', { className: 'bowire-plugin-check-status pending', textContent: 'pending' });
            }

            var row = el('li', { className: 'bowire-plugin-check-row' },
                el('div', { className: 'bowire-plugin-check-row-text' },
                    el('strong', { textContent: entry.id }),
                    el('span', { className: 'bowire-plugin-check-pkg',
                        textContent: entry.packageId || '(no canonical package — bring your own NuGet)' })),
                statusBadge
            );
            listEl.appendChild(row);
        });
        body.appendChild(listEl);

        if (pluginCheckState.error) {
            body.appendChild(el('p', { className: 'bowire-plugin-check-error', textContent: pluginCheckState.error }));
        }

        // Footer buttons
        var footer = el('div', { className: 'bowire-plugin-check-footer' });
        if (allDone) {
            footer.appendChild(el('button', {
                className: 'bowire-env-modal-btn bowire-env-modal-btn-primary',
                textContent: 'Close',
                onClick: dismissPluginCheck
            }));
        } else {
            var installable = pluginCheckState.missing.some(function (m) { return m.packageId; });
            var installBtn = el('button', {
                className: 'bowire-env-modal-btn bowire-env-modal-btn-primary',
                textContent: pluginCheckState.installing ? 'Installing…' : 'Install plugins',
                disabled: !installable || pluginCheckState.installing,
                onClick: installAllMissing
            });
            footer.appendChild(installBtn);
            footer.appendChild(el('button', {
                className: 'bowire-env-modal-btn',
                textContent: 'Cancel',
                disabled: pluginCheckState.installing,
                onClick: dismissPluginCheck
            }));
        }
        modal.appendChild(body);
        modal.appendChild(footer);

        var overlay = el('div', {
            className: 'bowire-env-modal-overlay bowire-plugin-check-overlay',
            role: 'dialog',
            'aria-modal': 'true',
            'aria-label': 'Plugin check',
            onClick: function (e) {
                if (e.target === overlay && !pluginCheckState.installing) dismissPluginCheck();
            }
        }, modal);

        document.body.appendChild(overlay);
    }

    function renderRecordingDetail(rec) {
        var pane = el('div', { className: 'bowire-recording-detail' });

        // Header: editable name + action buttons
        var header = el('div', { className: 'bowire-recording-detail-header' });
        var nameInput = el('input', {
            id: 'bowire-recording-name-input',
            type: 'text',
            className: 'bowire-recording-name-input',
            value: rec.name,
            onChange: function (e) {
                renameRecording(rec.id, e.target.value);
            }
        });
        header.appendChild(nameInput);
        pane.appendChild(header);

        // Action toolbar
        var toolbar = el('div', { className: 'bowire-recording-toolbar' });
        var isReplaying = recordingReplayState && recordingReplayState.recordingId === rec.id && recordingReplayState.status === 'running';

        toolbar.appendChild(el('button', {
            className: 'bowire-recording-action-btn',
            disabled: isReplaying || rec.steps.length === 0,
            title: 'Replay every step in order with the current environment variables',
            onClick: function () { replayRecording(rec.id); }
        },
            el('span', { innerHTML: svgIcon('replay') }),
            el('span', { textContent: isReplaying ? 'Replaying…' : 'Replay' })
        ));

        toolbar.appendChild(el('button', {
            className: 'bowire-recording-action-btn',
            disabled: rec.steps.length === 0,
            title: 'Append status + response equality assertions to every (service, method) in this recording',
            onClick: function () {
                var added = convertRecordingToTests(rec.id);
                addConsoleEntry({ type: 'response', method: rec.name, status: 'Added ' + added + ' test assertions' });
                renderRecordingManager();
            }
        },
            el('span', { textContent: 'Convert to Tests' })
        ));

        toolbar.appendChild(el('button', {
            className: 'bowire-recording-action-btn',
            disabled: rec.steps.length === 0,
            title: 'Download as HAR 1.2 (Chrome DevTools / Postman / Insomnia compatible)',
            onClick: function () { exportRecordingAsHar(rec.id); }
        },
            el('span', { innerHTML: svgIcon('download') }),
            el('span', { textContent: 'Export HAR' })
        ));

        toolbar.appendChild(el('button', {
            className: 'bowire-recording-action-btn',
            disabled: rec.steps.length === 0,
            title: 'Download a self-contained HTML report (CI artifact friendly). Uses replay results when available, captured statuses otherwise.',
            onClick: function () { exportRecordingAsHtml(rec.id); }
        },
            el('span', { innerHTML: svgIcon('download') }),
            el('span', { textContent: 'Export HTML Report' })
        ));

        toolbar.appendChild(el('button', {
            className: 'bowire-recording-action-btn',
            disabled: rec.steps.length === 0,
            title: 'Download the raw Bowire recording JSON',
            onClick: function () { exportRecordingAsJson(rec.id); }
        },
            el('span', { textContent: 'Export JSON' })
        ));

        toolbar.appendChild(el('button', {
            className: 'bowire-recording-action-btn bowire-recording-action-danger',
            title: 'Delete this recording (cannot be undone)',
            onClick: function () {
                bowireConfirm('Delete recording "' + rec.name + '"?', function () {
                    deleteRecording(rec.id);
                    toast('Recording deleted', 'success');
                }, { title: 'Delete Recording', danger: true, confirmText: 'Delete' });
            }
        },
            el('span', { innerHTML: svgIcon('trash') }),
            el('span', { textContent: 'Delete' })
        ));

        pane.appendChild(toolbar);

        // Step list
        var stepsPane = el('div', { className: 'bowire-recording-steps' });
        if (rec.steps.length === 0) {
            stepsPane.appendChild(el('div', { className: 'bowire-recording-empty',
                textContent: rec.id === recordingActiveId
                    ? 'Recording in progress. Make a few calls and they will appear here.'
                    : 'This recording has no steps yet.' }));
        } else {
            for (var i = 0; i < rec.steps.length; i++) {
                stepsPane.appendChild(renderRecordingStepRow(rec, i));
            }
        }
        pane.appendChild(stepsPane);

        return pane;
    }

    function renderRecordingStepRow(rec, idx) {
        var step = rec.steps[idx];
        var replayResult = null;
        if (recordingReplayState && recordingReplayState.recordingId === rec.id) {
            replayResult = recordingReplayState.results.find(function (r) { return r.stepId === step.id; });
        }

        var statusBadge = el('span', {
            className: 'bowire-recording-step-status'
                + (replayResult ? (replayResult.pass ? ' pass' : ' fail') : ''),
            textContent: replayResult ? (replayResult.pass ? 'PASS' : 'FAIL') : (step.status || '?')
        });

        var protocolBadge = el('span', {
            className: 'bowire-recording-step-protocol',
            textContent: step.protocol || 'grpc'
        });

        var label = el('div', { className: 'bowire-recording-step-label' },
            el('span', { className: 'bowire-recording-step-index', textContent: String(idx + 1) + '.' }),
            protocolBadge,
            el('span', { className: 'bowire-recording-step-method',
                textContent: (step.service || '') + ' / ' + (step.method || '') }),
            statusBadge,
            step.durationMs ? el('span', { className: 'bowire-recording-step-duration',
                textContent: step.durationMs + 'ms' }) : null
        );

        var deleteBtn = el('button', {
            className: 'bowire-recording-step-delete',
            innerHTML: svgIcon('trash'),
            title: 'Remove this step',
            'aria-label': 'Remove this step',
            onClick: function (e) {
                e.stopPropagation();
                deleteRecordingStep(rec.id, step.id);
            }
        });

        var row = el('div', { className: 'bowire-recording-step' }, label, deleteBtn);
        return row;
    }
