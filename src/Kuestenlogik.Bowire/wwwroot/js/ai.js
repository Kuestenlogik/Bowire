// ai.js — AI side-panel: deterministic hint engine (Phase 1) +
// IChatClient-backed local chat (Phase 2).
//
// The ADR (docs/architecture/ai-integration.md) lays out four phases:
//
//   1. Hint engine        — no LLM, rule-based suggestions from live UI state.
//   2. Local-model mode   — Ollama / LM Studio auto-detect + Mode-2 features.
//   3. BYOK cloud         — Anthropic / OpenAI / OpenRouter / Azure OpenAI.
//   4. MCP-client mode    — Bowire as MCP client, routes through user's host.
//
// Phase 1 stays untouched: rule-based hints that need no network. Phase 2
// adds three endpoint calls fronted by the optional Kuestenlogik.Bowire.Ai
// package:
//
//   GET  /api/ai/probe-local    — 300ms probe of Ollama / LM Studio
//   GET  /api/ai/status         — does the host have an IChatClient?
//   POST /api/ai/chat           — proxy a chat completion
//
// When the package isn't installed every call returns 404 / 503 and the
// panel falls back to the Phase-1 hint engine + a docs link — the host
// stays usable. When it is installed, the footer flips to a live status
// line and a tiny chat composer renders under the hints.

    // ---------- hint catalogue ----------
    // Each hint:
    //   id       — stable identifier (for filtering / dismiss-once later).
    //   level    — 'info' | 'warn' | 'tip'. Cosmetic for now, drives styling.
    //   surface  — where the hint should render (default 'header'):
    //                'header'    — method-header chip + Assistant drawer
    //                'response'  — inline banner above the response pane
    //                'auth'      — inline banner at the Auth panel
    //              Surface-targeted hints land at the spot the operator
    //              looks for help, instead of all queueing into a single
    //              drawer list (#114 Phase 2). The Assistant drawer's
    //              list still shows every fired hint regardless of
    //              surface — that stays as the index / history view.
    //   match    — (ctx) -> bool. ctx is the snapshot built by collectContext().
    //   render   — (ctx) -> string. Plain text, may include the method name.
    //
    // Adding a new hint = appending one object. The rules read only what
    // collectContext() exposes -- so the contract for adding rules is one file.

    var BOWIRE_AI_HINTS = [
        {
            id: 'no-method-selected',
            level: 'info',
            match: function (c) { return !c.method; },
            render: function () {
                return 'Pick a method in the sidebar to see context-aware hints here.';
            }
        },
        // ---- Phase 2 observation hints (#149) ----
        // Each rule reads from a concrete signal in the context. No
        // procedural how-to; only 'I noticed X, you might want to Y'.
        {
            id: 'slow-response',
            level: 'warn',
            surface: 'response',
            match: function (c) { return !!c.method && c.responseDurationMs >= 2000; },
            render: function (c) {
                return c.method.name + ' took '
                    + (c.responseDurationMs / 1000).toFixed(1)
                    + 's. Consider checking server load, network distance, or whether the request shape is forcing a full table scan.';
            }
        },
        {
            id: 'large-response',
            level: 'tip',
            surface: 'response',
            match: function (c) { return !!c.method && c.responseBytes >= 100 * 1024; },
            render: function (c) {
                var kb = Math.round(c.responseBytes / 1024);
                return 'Response body is ' + kb + ' KB. Most APIs page or filter at this scale — check for a `limit`, `pageSize`, or `fields` parameter to slice the payload.';
            }
        },
        {
            id: 'repeated-error',
            level: 'warn',
            match: function (c) { return !!c.method && c.consecutiveErrors >= 3; },
            render: function (c) {
                return c.consecutiveErrors + ' consecutive '
                    + (c.recentSameError || 'error')
                    + ' responses from ' + c.method.name
                    + '. Retry isn’t helping — fix the request shape, auth, or server state before firing again.';
            }
        },
        {
            id: 'response-status-mismatch',
            level: 'info',
            surface: 'response',
            match: function (c) {
                return !!c.method && !!c.actualStatus && !!c.expectedStatus
                    && c.actualStatus !== c.expectedStatus
                    && c.actualStatus !== 'Error' && c.actualStatus !== 'NetworkError';
            },
            render: function (c) {
                return 'Schema declared HTTP ' + c.expectedStatus
                    + ' but the server returned ' + c.actualStatus
                    + '. Either the schema is stale (regenerate) or the server contract drifted.';
            }
        },
        {
            id: 'auth-header-ineffective',
            level: 'warn',
            surface: 'auth',
            match: function (c) {
                // Approximation without per-call header capture: a 401
                // / 403 on a method whose name suggests it should be
                // authenticated. Refined when request-header tracking
                // lands as its own hook.
                if (!c.method) return false;
                var s = String(c.actualStatus || '');
                return (s === '401' || s === '403' || s === 'Unauthorized' || s === 'Forbidden')
                    && c.callsToThisMethodInLastMinute >= 2;
            },
            render: function () {
                return 'Auth-rejected '
                    + 'after multiple attempts. Token may be expired, scope-too-narrow, or wrong bearer prefix — re-check the Authorization header before retrying.';
            }
        },
        {
            id: 'recording-step-duplicate',
            level: 'tip',
            match: function (c) { return c.recordingStepsForMethod >= 3; },
            render: function (c) {
                return c.recordingStepsForMethod + ' steps of '
                    + c.method.name
                    + ' in the active recording. If the calls are interchangeable, you only need one — extras inflate the replay without adding coverage.';
            }
        },
        {
            id: 'unstable-response-shape',
            level: 'warn',
            surface: 'response',
            match: function (c) { return c.shapeDriftCount >= 2; },
            render: function (c) {
                return c.method.name + ' returned '
                    + c.shapeDriftCount + ' different response shapes across recent calls — fields appear or disappear between calls. Tests that bind to specific keys will be flaky; consider asserting shape before value.';
            }
        },
        {
            id: 'grpc-empty-response',
            level: 'warn',
            surface: 'response',
            match: function (c) {
                return c.method && c.method.protocol === 'grpc' && c.hasResponse
                    && c.responseText !== null && c.responseText.length === 0;
            },
            render: function (c) {
                return 'This gRPC method returned an empty response — check Server Reflection is enabled on '
                    + (c.serverUrl || 'the host')
                    + ' and that the method actually streams / returns a body.';
            }
        },
        {
            id: 'repeat-call-quickaction',
            level: 'tip',
            match: function (c) {
                return c.method && c.callsToThisMethodInLastMinute >= 3;
            },
            render: function (c) {
                return 'You called ' + c.method.name + ' ' + c.callsToThisMethodInLastMinute
                    + ' times in the last minute — save it as a recording step so you can replay it later.';
            }
        },
        {
            id: 'response-extra-fields',
            level: 'warn',
            surface: 'response',
            match: function (c) {
                return c.method && c.method.protocol === 'rest' && c.responseHasUnknownKeys;
            },
            render: function (c) {
                return 'The response carries fields the schema does not declare ('
                    + c.responseUnknownKeys.slice(0, 3).join(', ')
                    + (c.responseUnknownKeys.length > 3 ? ', …' : '')
                    + '). The schema may be stale — re-run discovery against ' + (c.serverUrl || 'the host') + '.';
            }
        },
        {
            id: 'streaming-method-tab-hint',
            level: 'tip',
            match: function (c) {
                return c.method && /Stream|Channel|Subscribe/i.test(c.method.methodType || '');
            },
            render: function () {
                return 'Streaming method — the Response tab shows frames as they arrive. Hit "Record" before invoking to capture every frame for later replay.';
            }
        },
        {
            id: 'mock-replay-context',
            level: 'tip',
            match: function (c) { return c.runningMockCount > 0; },
            render: function (c) {
                return 'You have ' + c.runningMockCount + ' mock'
                    + (c.runningMockCount === 1 ? '' : 's') + ' running — open the Mocks panel to point a second workbench at them and compare responses.';
            }
        },
        {
            id: 'recording-ready-to-mock',
            level: 'tip',
            match: function (c) { return c.recordingsCount > 0 && c.runningMockCount === 0; },
            render: function (c) {
                return 'You have ' + c.recordingsCount + ' recording'
                    + (c.recordingsCount === 1 ? '' : 's') + ' captured — open the Recordings manager and click "Run as mock" to replay them as a live endpoint.';
            }
        },
        {
            id: 'auth-method-no-token',
            level: 'warn',
            surface: 'auth',
            match: function (c) {
                return c.hasResponse && c.lastStatusCode === 401;
            },
            render: function () {
                return 'The last call returned 401 — set or refresh the auth helper in the Environment panel before retrying.';
            }
        }
    ];

    // ---------- state collection ----------
    // #149 — in-memory response-shape memory per method. Tracks the
    // last few response key-sets so the unstable-shape hint can
    // detect a method whose response shape varies call-to-call.
    // Bounded at 5 entries per method to keep memory tiny.
    var responseShapeMemory = {};
    function noteResponseShape(method, response) {
        if (!method || !response || typeof response !== 'object') return;
        var key = (method.fullName || method.name || '');
        if (!key) return;
        try {
            var shape = Object.keys(response).sort().join(',');
            var arr = responseShapeMemory[key] || (responseShapeMemory[key] = []);
            arr.push(shape);
            if (arr.length > 5) arr.shift();
        } catch { /* shape extraction is best-effort */ }
    }

    function collectContext() {
        var method = (typeof selectedMethod !== 'undefined') ? selectedMethod : null;
        var service = (typeof selectedService !== 'undefined') ? selectedService : null;
        var lastResponse = (typeof lastResponseJson !== 'undefined') ? lastResponseJson : null;
        var recordings = (typeof recordingsList !== 'undefined') ? recordingsList : [];

        var responseText = '';
        try { responseText = lastResponse == null ? '' : (typeof lastResponse === 'string' ? lastResponse : JSON.stringify(lastResponse)); }
        catch { responseText = ''; }

        // Remember this method's response shape so the unstable-
        // shape hint has at least one prior to compare against.
        if (method && lastResponse && typeof lastResponse === 'object') {
            noteResponseShape(method, lastResponse);
        }

        // Schema-vs-response known-key set (cheap REST-only check).
        var schemaKeys = [];
        var unknownKeys = [];
        var hasUnknownKeys = false;
        if (method && method.responseSchema && method.responseSchema.properties && lastResponse && typeof lastResponse === 'object') {
            schemaKeys = Object.keys(method.responseSchema.properties);
            unknownKeys = Object.keys(lastResponse).filter(function (k) { return schemaKeys.indexOf(k) === -1; });
            hasUnknownKeys = unknownKeys.length > 0;
        }

        // Call-frequency: walk in-memory call history if available
        var callsRecent = 0;
        try {
            if (Array.isArray(window.__bowireCallHistory) && method) {
                var cutoff = Date.now() - 60 * 1000;
                callsRecent = window.__bowireCallHistory.filter(function (entry) {
                    return entry && entry.method === method.name && entry.timestamp >= cutoff;
                }).length;
            }
        } catch { /* ignore */ }

        var runningMockCount = 0;
        try {
            if (window.__bowireMocks) {
                runningMockCount = (window.__bowireMocks.list() || []).length;
            }
        } catch { /* ignore */ }

        // Latest history entry for this method — duration, status,
        // size derived from there. Cheaper than maintaining a
        // separate lastResponseDurationMs / lastResponseBytes pair.
        var latestForMethod = null;
        var consecutiveErrors = 0;
        var recentSameError = null;
        try {
            if (typeof getHistory === 'function' && method) {
                var hist = getHistory();
                for (var hi = 0; hi < hist.length; hi++) {
                    if (hist[hi].method !== method.name) continue;
                    if (!latestForMethod) latestForMethod = hist[hi];
                    var isOk = (typeof isHistoryEntryOk === 'function') ? isHistoryEntryOk(hist[hi]) : false;
                    if (!isOk) {
                        consecutiveErrors++;
                        if (!recentSameError) recentSameError = String(hist[hi].status || 'Error');
                        else if (String(hist[hi].status || 'Error') !== recentSameError) break;
                    } else {
                        break;
                    }
                }
            }
        } catch { /* history not available */ }

        // Compare current response shape to recent shapes for the
        // same method. 'Unstable' = 2+ distinct shapes recorded.
        var shapeDriftCount = 0;
        try {
            if (method) {
                var key = method.fullName || method.name || '';
                var shapes = responseShapeMemory[key] || [];
                if (shapes.length >= 2) {
                    var unique = {};
                    for (var si = 0; si < shapes.length; si++) unique[shapes[si]] = true;
                    shapeDriftCount = Object.keys(unique).length;
                }
            }
        } catch { /* ignore */ }

        // Count steps in the active recording that hit the same
        // (service, method) so the duplicate-step hint can fire.
        var recordingStepsForMethod = 0;
        try {
            if (typeof recordingActiveId !== 'undefined' && recordingActiveId && method) {
                var rec = recordings.find(function (r) { return r.id === recordingActiveId; });
                if (rec && Array.isArray(rec.steps)) {
                    recordingStepsForMethod = rec.steps.filter(function (s) {
                        return s.method === method.name && s.service === (service && service.name);
                    }).length;
                }
            }
        } catch { /* ignore */ }

        // Schema-declared status code. Only present when the method's
        // schema carries it (REST OpenAPI), used by the mismatch hint.
        var expectedStatus = null;
        try {
            if (method && method.responseSchema && method.responseSchema.statusCode) {
                expectedStatus = String(method.responseSchema.statusCode);
            }
        } catch { /* ignore */ }

        return {
            method: method,
            service: service,
            serverUrl: method && method.serverUrl ? method.serverUrl : null,
            hasResponse: lastResponse !== null && lastResponse !== undefined,
            responseText: responseText,
            responseBytes: responseText.length,
            responseDurationMs: latestForMethod ? Number(latestForMethod.durationMs || 0) : 0,
            actualStatus: latestForMethod ? String(latestForMethod.status || '') : null,
            expectedStatus: expectedStatus,
            lastStatusCode: (typeof lastResponseStatusCode !== 'undefined') ? lastResponseStatusCode : null,
            responseHasUnknownKeys: hasUnknownKeys,
            responseUnknownKeys: unknownKeys,
            callsToThisMethodInLastMinute: callsRecent,
            consecutiveErrors: consecutiveErrors,
            recentSameError: recentSameError,
            shapeDriftCount: shapeDriftCount,
            recordingStepsForMethod: recordingStepsForMethod,
            runningMockCount: runningMockCount,
            recordingsCount: recordings.length
        };
    }

    function evaluateHints() {
        var ctx = collectContext();
        var fired = [];
        for (var i = 0; i < BOWIRE_AI_HINTS.length; i++) {
            var h = BOWIRE_AI_HINTS[i];
            try {
                if (h.match(ctx)) {
                    fired.push({
                        id: h.id,
                        level: h.level,
                        // Default surface is 'header' so existing untyped
                        // hints continue rendering on the method-header
                        // chip — backwards-compatible with the Phase 1
                        // (#114) chip we shipped earlier.
                        surface: h.surface || 'header',
                        text: h.render(ctx)
                    });
                }
            } catch { /* hint may throw on unexpected state shape — ignore */ }
        }
        return fired;
    }

    // #114 Phase 2 — surface-filtered hint lookup. Per-surface
    // renderers (response pane, auth panel, …) call this with their
    // surface name; only matching-surface hints come back. The
    // Assistant drawer keeps calling evaluateHints() unfiltered so
    // its list remains the global index / history view.
    function evaluateHintsForSurface(surface) {
        if (!surface) return [];
        return evaluateHints().filter(function (h) { return h.surface === surface; });
    }

    // #114 Phase 2 — universal inline hint-banner renderer. Returns a
    // div with one .bowire-inline-hint per fired hint (or null when
    // no hints fire, so callers can skip an empty wrapper). Used by
    // both the response pane and the auth panel — same widget, same
    // styling, different surface filter. Each banner shows level
    // colour, hint text, and a "Open Assistant" affordance for deeper
    // context. Dismissal lives in the issue body's Phase 3 — for now,
    // hints stay until their match() rule stops firing.
    function renderInlineHintBanners(surface) {
        var hints = evaluateHintsForSurface(surface);
        if (!hints || hints.length === 0) return null;
        var wrap = el('div', { className: 'bowire-inline-hint-wrap' });
        hints.forEach(function (h) {
            var row = el('div', {
                className: 'bowire-inline-hint bowire-inline-hint-' + (h.level || 'info'),
                role: 'note'
            });
            row.appendChild(el('span', {
                className: 'bowire-inline-hint-icon',
                innerHTML: svgIcon('spark')
            }));
            row.appendChild(el('span', {
                className: 'bowire-inline-hint-text',
                textContent: h.text
            }));
            row.appendChild(el('button', {
                className: 'bowire-inline-hint-open',
                title: 'Open the Assistant drawer',
                textContent: 'Open Assistant',
                onClick: function () {
                    aiDrawerOpen = true;
                    try { localStorage.setItem('bowire_ai_drawer_open', '1'); } catch { /* ignore */ }
                    render();
                }
            }));
            wrap.appendChild(row);
        });
        return wrap;
    }

    // ---------- Phase 2 status + chat plumbing ----------
    // Cached so a repaint doesn't re-fetch on every keystroke. Refreshed
    // on demand via window.__bowireAi.refreshStatus().
    var aiStatus = null;        // { hasClient, providerId, endpoint, model, autoDetectLocal } | null
    var aiProbe = null;         // { ollama: { endpoint, provider, models[] }|null, lmstudio: {...}|null } | null
    var aiInflightStatus = null;
    var aiInflightProbe = null;
    var chatHistory = [];       // [{ role: 'user'|'assistant', content: '...' }]
    var chatBusy = false;
    // #109 — Phase 3 gate: when true, the chat request marks
    // context.allowInvoke = true and the backend registers the
    // bowire_invoke tool. When false (default), the tool isn't even
    // offered to the model. Session-only — NEVER persisted to
    // localStorage; the user must opt in each fresh session.
    var aiAllowInvoke = false;
    // UX progress feedback for in-flight chat requests. Local models
    // doing tool calls take 30-45 s with llama3.2:3b — without a
    // visible "still thinking" indicator the user assumes the request
    // hung. chatPendingSince is the ms-timestamp the request fired;
    // chatAbort holds the AbortController so the Cancel button can
    // tear it down; chatTickHandle is the 1 s setInterval that
    // re-renders the elapsed-seconds counter. All three live as a
    // single state group — set together in sendChat, cleared together
    // in the .finally.
    var chatPendingSince = null;
    var chatAbort = null;
    var chatTickHandle = null;

    // #59 Threat-model state. Populated by runThreatModel(); rendered
    // by rerenderThreatModel(). Endpoint index lets per-row buttons
    // look up the original endpoint payload by its id so we don't
    // have to round-trip through the model's verbatim string.
    var threatState = {
        running: false,
        lastRun: 0,
        lastInputCount: 0,
        truncated: false,
        modelId: null,
        ranked: null,        // [{ endpointId, risk, why, suggestedTemplates }]
        endpointIndex: {},   // { endpointId: { path, verb, protocol, serverUrl, ... } }
        error: null
    };

    function collectThreatEndpoints() {
        // Walks the workbench's discovered service surface and emits a
        // flat list of endpoints with enough context for the model to
        // rank by risk. Source of truth is `services` (set by api.js
        // after /api/services), each service carries `methods`.
        var endpoints = [];
        var index = {};
        var src = (typeof services !== 'undefined') ? services : [];
        for (var i = 0; i < src.length; i++) {
            var svc = src[i];
            if (!svc || !Array.isArray(svc.methods)) continue;
            for (var j = 0; j < svc.methods.length; j++) {
                var m = svc.methods[j];
                if (!m) continue;
                var id = 's' + i + 'm' + j;
                var path = m.httpPath || m.name || (svc.name + '/' + (m.name || j));
                var verb = m.httpVerb || (m.methodType || null);
                var protocol = svc.source || svc.protocol || null;
                var endpoint = {
                    endpointId: id,
                    path: path,
                    verb: verb,
                    protocol: protocol,
                    service: svc.name || null,
                    serverUrl: m.serverUrl || svc.originUrl || null,
                    inputShape: m.inputType && m.inputType.name ? m.inputType.name : null,
                    authState: null
                };
                endpoints.push(endpoint);
                index[id] = endpoint;
            }
        }
        return { endpoints: endpoints, index: index };
    }

    // #60 — Nuclei template suggestion plumbing. Same class enum as
    // the server endpoint's KnownTemplateClasses; mirror by hand so
    // the frontend doesn't need a separate /api/ai/template-classes
    // round-trip.
    var BOWIRE_AI_TEMPLATE_CLASSES = [
        'auth-bypass', 'idor', 'mass-assignment', 'parameter-tampering',
        'injection-sqli', 'injection-cmdi', 'injection-template',
        'ssrf', 'path-traversal', 'open-redirect'
    ];

    function bowireBuildTemplateGenerator(endpoint, defaultClass) {
        var wrap = el('div', { className: 'bowire-ai-template-trigger' });
        var classSelect = el('select', { className: 'bowire-ai-template-class' });
        BOWIRE_AI_TEMPLATE_CLASSES.forEach(function (c) {
            var opt = el('option', { value: c, textContent: c });
            if (c === defaultClass) opt.selected = true;
            classSelect.appendChild(opt);
        });
        var genBtn = el('button', {
            type: 'button',
            className: 'bowire-ai-template-gen-btn',
            textContent: 'Generate template',
            onClick: function () {
                var cls = classSelect.value;
                var outputId = 'bowire-ai-template-out-' + endpoint.endpointId;
                var output = document.getElementById(outputId);
                if (!output) return;
                output.style.display = 'block';
                output.replaceChildren();
                output.appendChild(el('div', {
                    className: 'bowire-ai-template-status',
                    textContent: 'Generating ' + cls + ' template…'
                }));
                genBtn.setAttribute('disabled', 'disabled');
                bowireGenerateTemplate(endpoint, cls)
                    .then(function (result) { bowireRenderTemplateOutput(output, result); })
                    .finally(function () { genBtn.removeAttribute('disabled'); });
            }
        });
        wrap.appendChild(classSelect);
        wrap.appendChild(genBtn);
        return wrap;
    }

    function bowireGenerateTemplate(endpoint, cls) {
        var payload = {
            path: endpoint.path,
            'class': cls,
            verb: endpoint.verb || null,
            protocol: endpoint.protocol || null,
            service: endpoint.service || null,
            inputShape: endpoint.inputShape || null,
            authState: endpoint.authState || null
        };
        return fetch(aiPrefix() + '/api/ai/template-suggest', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(payload)
        })
            .then(function (r) { return r.json().then(function (b) { return { ok: r.ok, status: r.status, body: b }; }); })
            .catch(function (err) {
                return { ok: false, status: 0, body: { title: 'Network error: ' + (err && err.message ? err.message : err) } };
            });
    }

    function bowireRenderTemplateOutput(host, result) {
        host.replaceChildren();
        if (!result.ok) {
            host.appendChild(el('div', {
                className: 'bowire-ai-template-error',
                textContent: '⚠ ' + problemTitle(result.body, 'request failed')
            }));
            return;
        }
        var pre = el('pre', { className: 'bowire-ai-template-yaml' });
        pre.appendChild(el('code', { textContent: result.body.yaml || '(empty)' }));
        host.appendChild(pre);

        var actions = el('div', { className: 'bowire-ai-template-actions' });
        var copyBtn = el('button', {
            type: 'button',
            className: 'bowire-ai-template-copy',
            textContent: 'Copy YAML',
            onClick: function () {
                if (!navigator.clipboard) return;
                navigator.clipboard.writeText(result.body.yaml).then(function () {
                    copyBtn.textContent = 'Copied';
                    setTimeout(function () { copyBtn.textContent = 'Copy YAML'; }, 1500);
                });
            }
        });
        var saveBtn = el('button', {
            type: 'button',
            className: 'bowire-ai-template-save',
            textContent: 'Save to ~/.bowire/templates',
            onClick: function () {
                saveBtn.setAttribute('disabled', 'disabled');
                saveBtn.textContent = 'Saving…';
                fetch(aiPrefix() + '/api/ai/template-save', {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({
                        filename: result.body.suggestedFilename,
                        yaml: result.body.yaml
                    })
                })
                    .then(function (r) { return r.json().then(function (b) { return { ok: r.ok, body: b }; }); })
                    .then(function (resp) {
                        saveBtn.removeAttribute('disabled');
                        if (resp.ok) {
                            saveBtn.textContent = 'Saved ' + (resp.body.path || '');
                            setTimeout(function () {
                                saveBtn.textContent = 'Save to ~/.bowire/templates';
                            }, 3000);
                        } else {
                            saveBtn.textContent = '⚠ ' + problemTitle(resp.body, 'save failed');
                        }
                    })
                    .catch(function (err) {
                        saveBtn.removeAttribute('disabled');
                        saveBtn.textContent = '⚠ ' + (err && err.message ? err.message : 'save failed');
                    });
            }
        });
        actions.appendChild(copyBtn);
        actions.appendChild(saveBtn);
        host.appendChild(actions);

        if (result.body.modelId) {
            host.appendChild(el('div', {
                className: 'bowire-ai-template-meta',
                textContent: 'via ' + result.body.modelId + ' · suggested filename: ' + (result.body.suggestedFilename || '(none)')
            }));
        }
    }

    // Tick handle for the threat-model "Ranking… (Ns)" counter.
    // Same pattern as chatTickHandle — re-renders the threat section
    // once per second while a request is in flight so the user can
    // see elapsed time. Local-model threat ranking takes 10-30 s;
    // without a counter the button just sits at "Ranking…" and looks
    // hung.
    var threatTickHandle = null;
    // #112 — heuristic vs. AI tier toggle. Default is heuristic so
    // Security works without an AI client; flipping the toggle in
    // the Security drawer routes the next Run through /api/ai/threat-model
    // instead. Session-only, never persisted to localStorage — the
    // user opts in each fresh session to avoid surprise spend / latency
    // when reopening the workbench.
    var threatUseAi = false;
    function runThreatModel() {
        if (threatState.running) return Promise.resolve(threatState);
        var collected = collectThreatEndpoints();
        if (collected.endpoints.length === 0) {
            threatState.error = 'No discovered endpoints to rank yet. Connect to a server first.';
            return Promise.resolve(threatState);
        }
        threatState.running = true;
        threatState.startedAt = Date.now();
        threatState.error = null;
        threatState.endpointIndex = collected.index;
        threatState.tier = threatUseAi ? 'ai' : 'heuristic';
        if (threatTickHandle) clearInterval(threatTickHandle);
        threatTickHandle = setInterval(function () {
            try { if (rerenderThreatModelExternal) rerenderThreatModelExternal(); } catch { /* harmless */ }
        }, 1000);
        // Both endpoints sit under the same Bowire mount prefix —
        // /api/ai/* for the AI tier (gated on the AI package) and
        // /api/security/* for the heuristic tier (core, always
        // available). aiPrefix() collapses the embedded vs standalone
        // case so the same JS works in both.
        var url = aiPrefix() + (threatUseAi ? '/api/ai/threat-model' : '/api/security/threat-model');
        return fetch(url, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({
                endpoints: collected.endpoints,
                topN: 10
            })
        })
            .then(function (resp) { return resp.json().then(function (b) { return { ok: resp.ok, status: resp.status, body: b }; }); })
            .then(function (resp) {
                threatState.running = false;
                if (threatTickHandle) { clearInterval(threatTickHandle); threatTickHandle = null; }
                if (!resp.ok) {
                    threatState.error = problemTitle(resp.body, 'Request failed (HTTP ' + resp.status + ').');
                    threatState.ranked = null;
                    return threatState;
                }
                threatState.lastRun = Date.now();
                threatState.lastInputCount = resp.body.inputCount || collected.endpoints.length;
                threatState.truncated = !!resp.body.truncated;
                threatState.modelId = resp.body.modelId || null;
                threatState.ranked = Array.isArray(resp.body.ranked) ? resp.body.ranked : [];
                return threatState;
            })
            .catch(function (err) {
                threatState.running = false;
                if (threatTickHandle) { clearInterval(threatTickHandle); threatTickHandle = null; }
                threatState.error = 'Network error: ' + (err && err.message ? err.message : err);
                threatState.ranked = null;
                return threatState;
            });
    }

    function aiPrefix() {
        // Match mocks.js / api.js: every endpoint lives under the same
        // base path as the workbench HTML mount.
        return (typeof config !== 'undefined' && config && config.prefix) ? config.prefix : '';
    }

    function refreshAiStatus() {
        if (aiInflightStatus) return aiInflightStatus;
        // #116 Phase 3 — pass workspaceId so the resolver returns
        // override > global > defaults for the active workspace.
        var statusWsParam = '';
        try { if (typeof activeWorkspaceId === 'string' && activeWorkspaceId) statusWsParam = '?workspaceId=' + encodeURIComponent(activeWorkspaceId); }
        catch { /* prologue not loaded yet — skip */ }
        aiInflightStatus = fetch(aiPrefix() + '/api/ai/status' + statusWsParam)
            .then(function (r) { return r.ok ? r.json() : null; })
            .then(function (data) { aiStatus = data; return data; })
            .catch(function () { aiStatus = null; return null; })
            .finally(function () { aiInflightStatus = null; });
        return aiInflightStatus;
    }

    function refreshAiProbe() {
        if (aiInflightProbe) return aiInflightProbe;
        aiInflightProbe = fetch(aiPrefix() + '/api/ai/probe-local')
            .then(function (r) { return r.ok ? r.json() : null; })
            .then(function (data) { aiProbe = data; return data; })
            .catch(function () { aiProbe = null; return null; })
            .finally(function () { aiInflightProbe = null; });
        return aiInflightProbe;
    }

    /**
     * Snapshot the live workbench state so the AI's system prompt can
     * ground the model in what the user is actually looking at (#89
     * Phase 1). Pure read of the existing closure-scoped globals —
     * no extra fetches.
     *
     * Output is small on purpose: a small model needs concise context
     * more than a complete API surface dump. Top-level service +
     * method counts always; the selected method's full schema only
     * when one is picked (that's where the user's attention is).
     */
    function collectWorkbenchContext() {
        var urlState = (typeof serverUrls !== 'undefined' && Array.isArray(serverUrls))
            ? serverUrls.map(function (u) {
                var status = (typeof connectionStatuses !== 'undefined' && connectionStatuses)
                    ? (connectionStatuses[u] || 'unknown') : 'unknown';
                return { url: u, status: status };
            }) : [];

        var svcSummary = [];
        if (typeof services !== 'undefined' && Array.isArray(services)) {
            for (var i = 0; i < services.length; i++) {
                var s = services[i];
                svcSummary.push({
                    name: s.name,
                    protocol: s.source,
                    methods: (s.methods || []).map(function (m) { return m.name; }),
                });
            }
        }

        var selected = null;
        if (typeof selectedService !== 'undefined' && selectedService
            && typeof selectedMethod !== 'undefined' && selectedMethod) {
            selected = {
                service: selectedService.name,
                method: selectedMethod.name,
                protocol: selectedService.source,
                methodType: selectedMethod.methodType,
                description: selectedMethod.description,
                inputType: selectedMethod.inputType ? {
                    name: selectedMethod.inputType.fullName || selectedMethod.inputType.name,
                    fields: (selectedMethod.inputType.fields || []).map(function (f) {
                        return { name: f.name, type: f.type, optional: !!f.optional };
                    }),
                } : null,
                outputType: selectedMethod.outputType ? {
                    name: selectedMethod.outputType.fullName || selectedMethod.outputType.name,
                } : null,
                originUrl: selectedService.originUrl,
            };
        }

        var recent = [];
        try {
            if (typeof getHistory === 'function') {
                var h = getHistory() || [];
                for (var ri = 0; ri < Math.min(h.length, 5); ri++) {
                    var e = h[ri];
                    recent.push({
                        service: e.service,
                        method: e.method,
                        status: e.status,
                        // Drop body excerpts — they're often large + privacy-sensitive.
                    });
                }
            }
        } catch { /* defensive */ }

        return {
            urls: urlState,
            services: svcSummary,
            selected: selected,
            recent: recent,
            ai: aiStatus ? { model: aiStatus.model, provider: aiStatus.providerId, endpoint: aiStatus.endpoint } : null,
        };
    }

    /**
     * Project the JS-side workbench-context snapshot down to the shape
     * the backend's WorkbenchContext record (BowireAiEndpoints.cs)
     * deserialises. Mostly a 1:1 mapping; renames a few field names
     * to match C# casing conventions.
     */
    function serializeContextForBackend(ctx) {
        if (!ctx) return null;
        return {
            allowInvoke: aiAllowInvoke,
            serverUrls: (ctx.urls || []).map(function (u) { return u.url; }),
            services: (ctx.services || []).map(function (s) {
                return {
                    name: s.name,
                    protocol: s.protocol,
                    originUrl: ctx.selected && ctx.selected.service === s.name ? ctx.selected.originUrl : null,
                    methods: (s.methods || []).map(function (mName) {
                        // For the selected method we have full schema in
                        // ctx.selected; for everything else we only have
                        // the name. Surface what we have so the model can
                        // at least enumerate, and tools (describe_method)
                        // return "more detail not in snapshot" when asked
                        // about a non-selected method.
                        if (ctx.selected && ctx.selected.service === s.name && ctx.selected.method === mName) {
                            var sel = ctx.selected;
                            return {
                                name: mName,
                                description: sel.description,
                                methodType: sel.methodType,
                                inputTypeName: sel.inputType ? sel.inputType.name : null,
                                inputFields: sel.inputType ? sel.inputType.fields : null,
                                outputTypeName: sel.outputType ? sel.outputType.name : null,
                            };
                        }
                        return { name: mName };
                    }),
                };
            }),
            recent: (ctx.recent || []).map(function (r) {
                return { service: r.service, method: r.method, status: r.status };
            }),
        };
    }

    /**
     * Build a system prompt from a workbench-context snapshot. The
     * prompt tells the model what Bowire is, names the loaded
     * services + the selected method, and asks the model to answer
     * with that context rather than going general-knowledge.
     *
     * Returns null when the context is genuinely empty (no URLs, no
     * services, no method selected) — at that point the model has
     * nothing workbench-specific to ground in and a generic system
     * prompt just costs tokens.
     */
    function buildSystemPrompt(ctx) {
        if (!ctx) return null;
        var hasAnything = (ctx.urls && ctx.urls.length)
            || (ctx.services && ctx.services.length)
            || ctx.selected
            || (ctx.recent && ctx.recent.length);
        if (!hasAnything) return null;

        var lines = [
            'You are the AI assistant embedded in Bowire, a multi-protocol API workbench (gRPC, REST, GraphQL, MQTT, SignalR, WebSocket, MCP, …). The user is currently testing APIs through Bowire. Ground your answers in what they have loaded; do not invent endpoints or methods that are not listed below. When the user asks about a method by name (case-insensitive, prefix-match), refer to the matching entry from the list. Suggest the next concrete action (which method to invoke, which header to add, which auth to configure) rather than generic web advice.',
            '',
            '## Workbench state',
            ''
        ];

        if (ctx.urls && ctx.urls.length) {
            lines.push('Connected URLs:');
            for (var ui = 0; ui < ctx.urls.length; ui++) {
                lines.push('  - ' + ctx.urls[ui].url + '  (' + ctx.urls[ui].status + ')');
            }
            lines.push('');
        }

        if (ctx.services && ctx.services.length) {
            lines.push('Discovered services:');
            for (var si = 0; si < ctx.services.length; si++) {
                var svc = ctx.services[si];
                lines.push('  - ' + svc.name + '  [' + svc.protocol + ']  — methods: ' + svc.methods.join(', '));
            }
            lines.push('');
        }

        if (ctx.selected) {
            var sel = ctx.selected;
            lines.push('Selected method (this is what the user is looking at right now):');
            lines.push('  ' + sel.service + '.' + sel.method + '  [' + sel.protocol + (sel.methodType ? ', ' + sel.methodType : '') + ']');
            if (sel.description) lines.push('  Description: ' + sel.description);
            if (sel.inputType) {
                lines.push('  Input (' + sel.inputType.name + '):');
                for (var fi = 0; fi < sel.inputType.fields.length; fi++) {
                    var f = sel.inputType.fields[fi];
                    lines.push('    - ' + f.name + ': ' + f.type + (f.optional ? '  (optional)' : ''));
                }
            }
            if (sel.outputType) lines.push('  Output: ' + sel.outputType.name);
            if (sel.originUrl) lines.push('  Origin URL: ' + sel.originUrl);
            lines.push('');
        }

        if (ctx.recent && ctx.recent.length) {
            lines.push('Recent calls:');
            for (var ki = 0; ki < ctx.recent.length; ki++) {
                var r = ctx.recent[ki];
                lines.push('  - ' + r.service + '.' + r.method + '  → ' + (r.status || 'unknown'));
            }
            lines.push('');
        }

        if (ctx.ai) {
            lines.push('AI provider: ' + (ctx.ai.provider || 'unknown') + ', model: ' + (ctx.ai.model || 'unknown') + ', endpoint: ' + (ctx.ai.endpoint || 'unknown'));
        }

        return lines.join('\n');
    }

    function sendChat(text) {
        if (!text || chatBusy) return Promise.resolve(null);
        chatBusy = true;
        chatPendingSince = Date.now();
        chatAbort = new AbortController();
        // Re-render once per second so the elapsed-time counter on the
        // "Thinking…" bubble visibly ticks. setInterval is fine here
        // — the only cost is one DOM diff per tick and the chat panel
        // is small. Cleared in .finally.
        chatTickHandle = setInterval(function () {
            try { rerenderChatExternal(); } catch { /* harmless */ }
        }, 1000);
        chatHistory.push({ role: 'user', content: text });
        // Two grounding paths every turn (#89 Phase 1 + Phase 2):
        //  - System prompt with a workbench overview (loaded URLs +
        //    service names + selected method's schema). Catches every
        //    question that doesn't need a tool call.
        //  - The same snapshot serialised as `context` in the request
        //    body so the backend's MCP-style tools (#108) can read it
        //    on demand without burning tokens to keep huge schemas in
        //    the prompt itself.
        // Both rebuild every turn — the workbench state moves, the
        // model should see the snapshot that matches the question.
        var wbCtx = collectWorkbenchContext();
        var sysPrompt = buildSystemPrompt(wbCtx);
        var outgoing = sysPrompt
            ? [{ role: 'system', content: sysPrompt }].concat(chatHistory.filter(function (m) {
                // Skip transient assistant-side problem renders + tool-call
                // bubbles — they're UI artifacts, not conversation turns.
                return !m.problem && !m.toolCalls;
            }))
            : chatHistory.filter(function (m) { return !m.problem && !m.toolCalls; });
        // Backend-facing shape — strip the UI's role names down to the
        // {role, content} pair the endpoint deserialises. Drop assistant
        // entries that only carried a problem card (no text content).
        outgoing = outgoing.map(function (m) {
            return { role: m.role, content: m.content || '' };
        });
        return fetch(aiPrefix() + '/api/ai/chat', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            signal: chatAbort.signal,
            body: JSON.stringify({
                messages: outgoing,
                context: serializeContextForBackend(wbCtx),
            })
        })
            .then(function (r) { return r.json().then(function (b) { return { ok: r.ok, status: r.status, body: b }; }); })
            .then(function (resp) {
                if (resp.ok && resp.body && typeof resp.body.content === 'string') {
                    // Tool calls (#108 Phase 2) — show before the
                    // assistant text so the transcript reads "AI looked
                    // up X, then said Y". Pushed as their own bubble
                    // so the transcript filter skips them on the next
                    // turn (no point re-grounding via prior tool calls).
                    if (Array.isArray(resp.body.toolCalls) && resp.body.toolCalls.length > 0) {
                        chatHistory.push({
                            role: 'assistant',
                            toolCalls: resp.body.toolCalls,
                        });
                        // #109 Phase 4 — dispatch on tool name. The
                        // open_method tool's server-side body just
                        // validates the request; the actual navigation
                        // is client-side here. Looking at the tool name
                        // (not a side-channel) keeps the protocol
                        // dumb-pipe: the same response shape the AI
                        // returned drives the UI.
                        resp.body.toolCalls.forEach(function (tc) {
                            if (tc.name === 'bowire_open_method'
                                && tc.arguments && tc.arguments.service && tc.arguments.method
                                && typeof services !== 'undefined' && typeof openTab === 'function') {
                                var svc = services.find(function (s) {
                                    return s.name && s.name.toLowerCase() === tc.arguments.service.toLowerCase();
                                });
                                if (!svc || !svc.methods) return;
                                var m = svc.methods.find(function (x) {
                                    return x.name && x.name.toLowerCase() === tc.arguments.method.toLowerCase();
                                });
                                if (m) {
                                    try { openTab(svc, m); render(); } catch { /* ignore */ }
                                }
                            }
                        });
                    }
                    chatHistory.push({ role: 'assistant', content: resp.body.content });
                } else {
                    // Server returned a problem+json (or legacy { error }).
                    // Carry the normalized problem so the renderer can
                    // surface the structured form (title + details +
                    // links). Fallback to text when the body is empty
                    // (network blip — caught case below should be hit
                    // instead but be defensive).
                    var problem = normalizeProblem(resp.body) || normalizeProblem('Assistant request failed (HTTP ' + resp.status + ').');
                    chatHistory.push({ role: 'assistant', problem: problem });
                }
            })
            .catch(function (err) {
                // AbortError is the user clicking Cancel — surface as
                // a quiet "canceled" line instead of a Network-error
                // problem card; the cancel was deliberate.
                if (err && err.name === 'AbortError') {
                    chatHistory.push({
                        role: 'assistant',
                        content: '(canceled)',
                    });
                } else {
                    chatHistory.push({
                        role: 'assistant',
                        problem: normalizeProblem('Network error: ' + (err && err.message ? err.message : err))
                    });
                }
            })
            .finally(function () {
                chatBusy = false;
                chatPendingSince = null;
                chatAbort = null;
                if (chatTickHandle) {
                    clearInterval(chatTickHandle);
                    chatTickHandle = null;
                }
            });
    }

    // Module-scope hooks the per-second tick closures call into.
    // Assigned by the matching rerenderX() function inside
    // renderAiPanel() — that's where the host DOM lives. Lets the
    // setInterval ticks refresh the pending/elapsed UI without
    // re-running the whole renderAiPanel.
    var rerenderChatExternal = function () { /* no-op until panel mounted */ };
    var rerenderThreatModelExternal = function () { /* no-op until panel mounted */ };

    // ---------- panel ----------
    function renderAiPanel() {
        var panel = el('div', { className: 'bowire-ai-panel' });

        // #109 — invocation alert. Surfaces only when the master
        // toggle in Settings → Assistant is OFF and the operator
        // hasn't dismissed it this session. Sits at the top of the
        // panel so it reads as a drawer-wide status banner under the
        // tab/title strip.
        if (typeof aiAllowInvoke !== 'undefined' && !aiAllowInvoke
            && aiStatus && aiStatus.hasClient
            && typeof renderAlertBar === 'function') {
            // Inline toggle here — same chrome as Settings → Assistant
            // so the operator can flip the master switch without
            // navigating away. State stays session-only per the #109
            // design (cold-start always back to off).
            var alertBar = renderAlertBar({
                severity: 'warning',
                text: 'Assistant is currently in observe-only mode. It can read context but not dispatch calls. Enable to allow AI to invoke calls directly.',
                inlineToggle: {
                    value: !!aiAllowInvoke,
                    title: 'Allow AI to invoke methods (session only, audited)',
                    // Flipping to ON resolves the warning — let the bar
                    // fade itself out so the operator sees the
                    // resolution rather than the panel snapping.
                    dismissOnToggleTo: true,
                    onChange: function (newVal) {
                        // Update state only — don't call render(). A
                        // synchronous render would tear the bar out of
                        // the DOM before the CSS fade plays. The fade
                        // helper handles removal; the next natural
                        // render (e.g. sending a chat message) picks
                        // up the new state. Confirm via a toast with a
                        // deep-link into Settings so the operator can
                        // re-toggle later without hunting the sidebar.
                        aiAllowInvoke = newVal;
                        if (typeof toast === 'function') {
                            var msg = newVal
                                ? 'AI invocation enabled — session only, audited'
                                : 'AI invocation disabled — observe-only mode';
                            toast(msg, 'info', {
                                action: {
                                    label: 'Open Settings',
                                    onClick: function () {
                                        if (typeof openSettings === 'function') openSettings('ai');
                                    }
                                }
                            });
                        }
                    }
                },
                dismissKey: 'bowire_ai_invoke_alert_dismissed',
                permanentDismissKey: 'bowire_ai_invoke_alert_permanent',
                dismissLabel: 'AI observe-only banner'
            });
            if (alertBar) panel.appendChild(alertBar);
        }

        // Header subtitle dropped — drawer chrome already shows
        // 'Assistant' title; footer at the bottom shows the
        // connection state via the rerenderFooter helper which
        // tracks aiStatus / aiProbe live. Header text computed at
        // initial render was getting out of sync with the
        // async-updated footer (issue: header said 'not installed'
        // while footer said 'Connected: ollama').

        // Per-method context hints — now rendered with the same
        // renderAlertBar shape as the observe-only banner above so the
        // operator sees one consistent "notification" type instead of
        // two visually different "this is also a hint" formats.
        // Severity maps cleanly: 'warn' → warning, 'tip'/'info' → info.
        // Each hint is dismissable for the session via a stable
        // hint-id key, mirroring the toast-stack dismiss UX.
        var hints = evaluateHints();
        if (hints.length === 0) {
            panel.appendChild(el('p', {
                className: 'bowire-ai-empty',
                textContent: 'No hints fire from the current workbench state. Pick a method, fire a request, or open a recording — the panel surfaces context-aware suggestions as you go.'
            }));
        } else if (typeof renderAlertBar === 'function') {
            var hintStack = el('div', { className: 'bowire-ai-hint-list' });
            hints.forEach(function (h) {
                var severity = h.level === 'warn' ? 'warning' : 'info';
                var bar = renderAlertBar({
                    severity: severity,
                    text: h.text,
                    dismissKey: 'bowire_ai_hint_' + h.id + '_dismissed',
                    permanentDismissKey: 'bowire_ai_hint_' + h.id + '_permanent',
                    dismissLabel: 'Assistant hint: ' + (h.id || 'context')
                });
                if (bar) hintStack.appendChild(bar);
            });
            panel.appendChild(hintStack);
        }

        // Threat-model + template-suggest moved to the Security drawer
        // (#111). The AI drawer stays focused on conversational
        // surfaces — hints + chat — so the mental model is clean:
        // "AI = assistant", "Security = scanner / analysis tools".

        // Phase 2 chat surface. Renders only when /api/ai/status reports
        // hasClient=true; otherwise the footer becomes a probe-driven
        // "Detected: <provider>" affordance plus a link to install the
        // Kuestenlogik.Bowire.Ai package.
        var chatHost = el('div', { className: 'bowire-ai-chat-host' });
        panel.appendChild(chatHost);

        function rerenderChat() {
            chatHost.replaceChildren();
            if (!aiStatus || !aiStatus.hasClient) return;

            // #109 — invoke-permission alert. Mounted at the very top
            // of the panel (renderAiPanel inserts it before the hint
            // list) via renderAlertBar — see panel-level mount below.

            var transcript = el('div', { className: 'bowire-ai-chat-transcript' });
            chatHistory.forEach(function (m) {
                var bubble = el('div', { className: 'bowire-ai-chat-msg bowire-ai-chat-msg-' + m.role });
                // Structured-error bubble (#88): render the Problem
                // card instead of the plain assistant content. The
                // prefix "AI: " is still useful to anchor the bubble
                // in the conversation.
                if (m.problem) {
                    bubble.appendChild(el('div', { className: 'bowire-ai-chat-prefix', textContent: 'AI:' }));
                    renderProblem(m.problem, bubble);
                } else if (m.toolCalls) {
                    // Tool-call trace bubble (#108 Phase 2). Renders
                    // each tool call as a small "consulted X" line so
                    // the user can see what the AI actually looked up
                    // before answering. Stays collapsed by default —
                    // open a <details> for the raw arguments JSON.
                    bubble.classList.add('bowire-ai-chat-msg-tools');
                    m.toolCalls.forEach(function (tc) {
                        var prettyName = tc.name
                            .replace(/^bowire_/, '')
                            .replace(/_/g, ' ');
                        var line = el('div', { className: 'bowire-ai-chat-tool' });
                        line.appendChild(el('span', { className: 'bowire-ai-chat-tool-icon', textContent: '🔧' }));
                        line.appendChild(el('span', {
                            className: 'bowire-ai-chat-tool-name',
                            textContent: 'Consulted ' + prettyName,
                        }));
                        if (tc.arguments && Object.keys(tc.arguments).length > 0) {
                            var args = el('details', { className: 'bowire-ai-chat-tool-args' });
                            args.appendChild(el('summary', { textContent: 'args' }));
                            args.appendChild(el('pre', { textContent: JSON.stringify(tc.arguments, null, 2) }));
                            line.appendChild(args);
                        }
                        bubble.appendChild(line);
                    });
                } else {
                    bubble.textContent = (m.role === 'user' ? 'You: ' : 'AI: ') + m.content;
                }
                transcript.appendChild(bubble);
            });

            // "Thinking…" bubble while a request is in flight (UX
            // quick-fix). Local models with tool-calling can take
            // 30-45 s on llama3.2:3b — without a visible counter the
            // user assumes the request hung. The bubble shows elapsed
            // seconds (ticking via the 1 s setInterval set in
            // sendChat) and a Cancel button that aborts the fetch.
            if (chatBusy && chatPendingSince) {
                var elapsedSec = Math.max(0, Math.floor((Date.now() - chatPendingSince) / 1000));
                var pendingBubble = el('div', { className: 'bowire-ai-chat-msg bowire-ai-chat-msg-assistant bowire-ai-chat-pending' });
                pendingBubble.appendChild(el('span', { className: 'bowire-ai-chat-pending-dot' }));
                pendingBubble.appendChild(el('span', {
                    className: 'bowire-ai-chat-pending-text',
                    textContent: 'AI: thinking… (' + elapsedSec + 's)',
                }));
                pendingBubble.appendChild(el('button', {
                    type: 'button',
                    className: 'bowire-ai-chat-pending-cancel',
                    textContent: 'Cancel',
                    onClick: function () { if (chatAbort) chatAbort.abort(); }
                }));
                transcript.appendChild(pendingBubble);
            }

            chatHost.appendChild(transcript);

            // Submit handler reads the current input value via DOM
            // query (form.querySelector) instead of the captured
            // `input` ref. morphdom may preserve the live form node
            // across renders while replacing its children — in that
            // case the captured `input` reference points at a
            // detached textarea whose value is always empty. Reading
            // via querySelector at submit-time stays correct under
            // either morphdom path (kept or replaced).
            function _submitChat() {
                var formEl = document.querySelector('.bowire-ai-chat-form');
                if (!formEl) return;
                var inputEl = formEl.querySelector('.bowire-ai-chat-input');
                if (!inputEl) return;
                var text = (inputEl.value || '').trim();
                if (!text || chatBusy) return;
                inputEl.value = '';
                sendChat(text).then(rerenderChatExternal);
                rerenderChatExternal();
            }
            var form = el('form', {
                className: 'bowire-ai-chat-form',
                onSubmit: function (e) {
                    e.preventDefault();
                    _submitChat();
                }
            });
            var input = el('textarea', {
                className: 'bowire-ai-chat-input',
                placeholder: 'Ask the model — Bowire ships the workbench context as a system prompt.',
                rows: 2
            });
            var send = el('button', {
                type: 'submit',
                className: 'bowire-ai-chat-send',
                textContent: chatBusy ? 'Sending…' : 'Send',
                // Belt + braces: explicit click handler in addition to
                // the form-submit so even if the form's onSubmit is
                // dropped or the button somehow loses its submit
                // semantics, the click path still works.
                onClick: function (e) {
                    e.preventDefault();
                    _submitChat();
                }
            });
            if (chatBusy) send.setAttribute('disabled', 'disabled');
            form.appendChild(input);
            form.appendChild(send);
            chatHost.appendChild(form);
        }
        // Hand the tick interval (sendChat) a stable hook that always
        // re-runs the *current* rerenderChat closure. Reassigned on
        // every renderAiPanel() so a re-mounted drawer doesn't keep
        // calling a stale closure that targets a detached chatHost.
        rerenderChatExternal = rerenderChat;
        rerenderChat();

        var footer = el('div', { className: 'bowire-ai-footer' });
        function rerenderFooter() {
            footer.replaceChildren();
            if (aiStatus && aiStatus.hasClient) {
                // Connection state now lives as a status dot in the
                // drawer chrome header (renderAiDrawer in
                // render-env-auth.js) with hover tooltip for the
                // provider / model details. Footer skips the long
                // 'Connected: …' string here so the info shows once,
                // up top.
                return;
            }
            // No IChatClient registered. Two sub-cases:
            //   (a) The Kuestenlogik.Bowire.Ai package isn't installed
            //       (status endpoint 404'd → aiStatus === null).
            //   (b) Package is installed but no provider connected yet
            //       (status 200 with hasClient=false).
            var pkgMissing = aiStatus === null;
            footer.appendChild(el('span', {
                className: 'bowire-ai-status bowire-ai-status-idle',
                textContent: pkgMissing
                    ? 'No AI provider — install Kuestenlogik.Bowire.Ai to enable chat (Ollama / LM Studio / BYOK cloud).'
                    : 'AI package installed, no model connected.'
            }));
            // Probe-driven detection: surfaces the running Ollama /
            // LM-Studio instance on the same host so the user gets a
            // one-line "detected llama3.2:3b" affordance.
            if (aiProbe) {
                ['ollama', 'lmstudio'].forEach(function (key) {
                    var hit = aiProbe[key];
                    if (!hit || !hit.endpoint) return;
                    var models = Array.isArray(hit.models) ? hit.models.filter(function (m) { return !!m; }) : [];
                    var preview = models.length > 0 ? models.slice(0, 3).join(', ') + (models.length > 3 ? ', …' : '') : '(no models loaded)';
                    footer.appendChild(el('div', {
                        className: 'bowire-ai-detected',
                        textContent: 'Detected ' + hit.provider + ' at ' + hit.endpoint + ' — ' + preview
                    }));
                });
            }
        }
        rerenderFooter();
        panel.appendChild(footer);

        // Kick the status + probe fetches on first paint; once they land
        // the panel re-renders its footer + chat host so the user sees the
        // live state without manual refresh. Both calls are idempotent +
        // cheap so re-running on every panel paint is fine.
        refreshAiStatus().then(function () {
            rerenderFooter();
            rerenderChat();
        });
        // Probe is opt-in via the AutoDetectLocal flag; the endpoint
        // short-circuits and returns { skipped } when disabled, so the
        // fetch stays cheap even then.
        refreshAiProbe().then(rerenderFooter);

        return panel;
    }

    // #111 — Security drawer surface. Lives parallel to the AI drawer.
    // Hosts the threat-model + per-row template generator that used to
    // live inside the AI drawer; the AI drawer keeps Hints + Chat.
    // The two drawers are independent (own toggles, own state), so a
    // user running a scan keeps the chat available for "explain this
    // finding" without losing scroll position.
    function renderSecurityPanel() {
        var panel = el('div', { className: 'bowire-security-panel' });

        var header = el('div', { className: 'bowire-ai-header' });
        header.appendChild(el('h3', { textContent: 'Security' }));
        header.appendChild(el('span', {
            className: 'bowire-ai-mode',
            textContent: aiStatus && aiStatus.hasClient
                ? 'AI-assisted ranking — opt-in heuristic tier (#112) in v1.10'
                : 'Configure AI in Settings to enable ranking'
        }));
        panel.appendChild(header);

        var threatHost = el('div', { className: 'bowire-ai-threat-host' });
        panel.appendChild(threatHost);

        function rerenderThreatModel() {
            threatHost.replaceChildren();

            var section = el('div', { className: 'bowire-ai-threat-section' });
            section.appendChild(el('h4', { className: 'bowire-ai-threat-title', textContent: 'Threat model' }));
            section.appendChild(el('p', {
                className: 'bowire-ai-threat-help',
                textContent: 'Rank discovered endpoints by attack-surface risk. Heuristic by default — no AI required; toggle to use the AI if configured.'
            }));

            // #112 — tier toggle. Default heuristic; opt in to AI.
            // AI option is disabled when no IChatClient is registered.
            var hasAi = !!(aiStatus && aiStatus.hasClient);
            var tierRow = el('div', { className: 'bowire-ai-threat-tierrow' });
            var heurBtn = el('button', {
                type: 'button',
                className: 'bowire-ai-threat-tier' + (!threatUseAi ? ' active' : ''),
                textContent: 'Heuristic',
                title: 'Deterministic rule engine — sub-millisecond, no model required',
                onClick: function () {
                    if (threatState.running) return;
                    threatUseAi = false;
                    rerenderThreatModel();
                }
            });
            var aiBtn = el('button', {
                type: 'button',
                className: 'bowire-ai-threat-tier' + (threatUseAi ? ' active' : ''),
                textContent: 'AI-assisted',
                title: hasAi
                    ? 'Send to the configured AI for semantic ranking on top of the heuristic rules'
                    : 'Configure a model in Settings → AI to enable',
                onClick: function () {
                    if (threatState.running || !hasAi) return;
                    threatUseAi = true;
                    rerenderThreatModel();
                }
            });
            if (!hasAi) aiBtn.setAttribute('disabled', 'disabled');
            tierRow.appendChild(heurBtn);
            tierRow.appendChild(aiBtn);
            section.appendChild(tierRow);

            var runRow = el('div', { className: 'bowire-ai-threat-runrow' });
            var elapsedThreatSec = threatState.running && threatState.startedAt
                ? Math.max(0, Math.floor((Date.now() - threatState.startedAt) / 1000))
                : 0;
            var runBtn = el('button', {
                type: 'button',
                className: 'bowire-ai-threat-run',
                textContent: threatState.running
                    ? 'Ranking… (' + elapsedThreatSec + 's)'
                    : 'Run threat model',
                onClick: function () { runThreatModel().then(rerenderThreatModel); }
            });
            if (threatState.running) runBtn.setAttribute('disabled', 'disabled');
            runRow.appendChild(runBtn);
            var statusLine = el('span', { className: 'bowire-ai-threat-meta' });
            if (threatState.error) {
                statusLine.textContent = '⚠ ' + threatState.error;
                statusLine.classList.add('err');
            } else if (threatState.lastRun) {
                statusLine.textContent = threatState.lastInputCount + ' endpoint(s) considered'
                    + (threatState.truncated ? ' (truncated to first 200)' : '')
                    + (threatState.modelId ? ' · ' + threatState.modelId : '');
            }
            runRow.appendChild(statusLine);
            section.appendChild(runRow);

            if (threatState.ranked && threatState.ranked.length > 0) {
                var list = el('ol', { className: 'bowire-ai-threat-list' });
                threatState.ranked.forEach(function (row) {
                    var endpoint = threatState.endpointIndex[row.endpointId];
                    var li = el('li', { className: 'bowire-ai-threat-row' });
                    var rowHeader = el('div', { className: 'bowire-ai-threat-row-head' });
                    var scoreClass = row.risk >= 8 ? 'high' : row.risk >= 5 ? 'medium' : 'low';
                    rowHeader.appendChild(el('span', {
                        className: 'bowire-ai-threat-score score-' + scoreClass,
                        textContent: row.risk + '/10'
                    }));
                    rowHeader.appendChild(el('code', {
                        className: 'bowire-ai-threat-endpoint',
                        textContent: endpoint
                            ? ((endpoint.verb ? endpoint.verb + ' ' : '') + (endpoint.path || row.endpointId))
                            : row.endpointId
                    }));
                    li.appendChild(rowHeader);
                    if (row.why) {
                        li.appendChild(el('div', { className: 'bowire-ai-threat-why', textContent: row.why }));
                    }
                    // #112 — render the rule trace for heuristic rows
                    // as a collapsible details so users can audit which
                    // rules fired against which endpoint. AI rows have
                    // no ruleTrace; the model's `why` carries the
                    // reasoning text.
                    if (row.ruleTrace && row.ruleTrace.length > 0) {
                        var traceEl = el('details', { className: 'bowire-ai-threat-trace' });
                        traceEl.appendChild(el('summary', { textContent: row.ruleTrace.length + ' rule' + (row.ruleTrace.length === 1 ? '' : 's') + ' fired' }));
                        var traceList = el('ul');
                        row.ruleTrace.forEach(function (line) {
                            traceList.appendChild(el('li', { textContent: line }));
                        });
                        traceEl.appendChild(traceList);
                        li.appendChild(traceEl);
                    }
                    if (row.suggestedTemplates && row.suggestedTemplates.length > 0) {
                        var tplWrap = el('div', { className: 'bowire-ai-threat-templates' });
                        tplWrap.appendChild(el('span', {
                            className: 'bowire-ai-threat-templates-label',
                            textContent: 'Suggested:'
                        }));
                        row.suggestedTemplates.forEach(function (t) {
                            tplWrap.appendChild(el('span', {
                                className: 'bowire-ai-threat-template-chip',
                                textContent: t
                            }));
                        });
                        li.appendChild(tplWrap);
                    }
                    if (endpoint && endpoint.path) {
                        var actionRow = el('div', { className: 'bowire-ai-threat-scan' });
                        var scanBtn = el('button', {
                            type: 'button',
                            className: 'bowire-ai-threat-scan-btn',
                            textContent: 'Copy bowire scan command',
                            onClick: (function (ep, templates) {
                                return function () {
                                    var cmd = 'bowire scan --url ' + (ep.serverUrl || '<server>')
                                        + ' --path ' + ep.path
                                        + (templates.length > 0 ? ' --templates ' + templates.join(',') : '');
                                    if (navigator.clipboard) {
                                        navigator.clipboard.writeText(cmd).then(function () {
                                            scanBtn.textContent = 'Copied';
                                            setTimeout(function () {
                                                scanBtn.textContent = 'Copy bowire scan command';
                                            }, 1500);
                                        });
                                    }
                                };
                            })(endpoint, row.suggestedTemplates || [])
                        });
                        actionRow.appendChild(scanBtn);
                        var defaultClass = (row.suggestedTemplates && row.suggestedTemplates[0]) || 'idor';
                        actionRow.appendChild(bowireBuildTemplateGenerator(endpoint, defaultClass));
                        li.appendChild(actionRow);
                        var outputBox = el('div', { className: 'bowire-ai-template-output' });
                        outputBox.id = 'bowire-ai-template-out-' + endpoint.endpointId;
                        outputBox.style.display = 'none';
                        li.appendChild(outputBox);
                    }
                    list.appendChild(li);
                });
                section.appendChild(list);
            }

            threatHost.appendChild(section);
        }
        rerenderThreatModelExternal = rerenderThreatModel;
        rerenderThreatModel();

        return panel;
    }

    // Expose for render-main.js
    window.__bowireAi = {
        renderPanel: renderAiPanel,
        renderSecurityPanel: renderSecurityPanel,
        evaluateHints: evaluateHints,
        hintCount: function () { return evaluateHints().length; },
        // Phase 2 surface. Lets render layers (and tests) read or reset
        // the status / probe / chat cache without touching internals.
        getStatus: function () { return aiStatus; },
        getProbe: function () { return aiProbe; },
        refreshStatus: refreshAiStatus,
        refreshProbe: refreshAiProbe,
        resetChat: function () { chatHistory.length = 0; },
        // Screenshot-helper seams. Used by scripts/capture-screenshots.js
        // to seed the panel with deterministic demo data without needing
        // a real IChatClient on the marketing CI runner. Prefix `_` so
        // they're clearly out of the supported API surface.
        _seedStatus: function (st) { aiStatus = st; },
        _seedProbe: function (pr) { aiProbe = pr; },
        _seedChat: function (history) {
            chatHistory.length = 0;
            if (Array.isArray(history)) {
                history.forEach(function (m) { chatHistory.push(m); });
            }
        },
        _seedThreatModel: function (state) {
            if (!state) {
                threatState.ranked = null;
                threatState.lastRun = 0;
                return;
            }
            threatState.ranked = state.ranked || [];
            threatState.lastInputCount = state.inputCount || (state.ranked || []).length;
            threatState.truncated = !!state.truncated;
            threatState.modelId = state.modelId || null;
            threatState.endpointIndex = state.endpointIndex || {};
            threatState.lastRun = Date.now();
            threatState.error = null;
        },
        _seedTemplate: function (endpointId, body) {
            var outputId = 'bowire-ai-template-out-' + endpointId;
            var output = document.getElementById(outputId);
            if (!output) return false;
            output.style.display = 'block';
            bowireRenderTemplateOutput(output, { ok: true, body: body });
            return true;
        }
    };

    // ---- #125 Phase 4 — ai.* prefetch ----
    //
    // Async path that populates the _aiVarsCache before send-time so
    // substituteVars (sync) can resolve {{ai.NAME}} normally. Call
    // sites: execute.js / runBenchmarkSpec / perf-diff before they
    // substituteVars the template.
    //
    // The model gets a tiny single-shot prompt with the variable name
    // plus a context hint (the currently-selected service + method
    // when available). Answer is expected to be ONE value, no prose;
    // we accept the first non-empty line as the value. Empty / errored
    // responses leave the cache slot unset so the user sees the
    // unresolved placeholder, not a silent empty string.
    //
    // Concurrency: a per-name Promise lives in _aiVarsInflight so
    // multiple substitutions in the same template share one AI call.
    //
    // No-op when:
    //   - AI module not loaded (no chat client)
    //   - The template contains no {{ai.X}} refs
    //   - The cache slot for a referenced name is already populated

    async function prefetchAiVars(templates) {
        if (typeof templates === 'string') templates = [templates];
        if (!Array.isArray(templates) || templates.length === 0) return;

        var names = new Set();
        var re = /\{\{\s*ai\.([^}\s]+)\s*\}\}/g;
        templates.forEach(function (t) {
            if (typeof t !== 'string') return;
            var m;
            while ((m = re.exec(t)) !== null) {
                names.add(m[1]);
            }
        });
        if (names.size === 0) return;

        var pending = [];
        names.forEach(function (name) {
            // Cache hit → nothing to do.
            if (typeof resolveAiVar === 'function' && resolveAiVar(name) !== null) return;
            // In-flight dedup.
            if (_aiVarsInflight[name]) {
                pending.push(_aiVarsInflight[name]);
                return;
            }
            // Compose a tiny single-shot prompt. We DON'T use sendChat
            // (which writes to chatHistory and renders the drawer) —
            // this is an unattended substitution call. Direct fetch
            // against /api/ai/chat keeps it out of the visible UI.
            var p = _runAiPrefetch(name).then(function (value) {
                if (value !== null && value !== undefined && String(value).length > 0) {
                    _aiVarsCache[name] = String(value);
                }
            }).catch(function () {
                // Leave cache unset; substituteVars returns the
                // placeholder, operator sees the unresolved state.
            }).finally(function () {
                delete _aiVarsInflight[name];
            });
            _aiVarsInflight[name] = p;
            pending.push(p);
        });
        await Promise.all(pending);
    }

    async function _runAiPrefetch(name) {
        // Context hint — the selected method, if any. The model sees
        // it but isn't forced to use it; the value just has to be
        // reasonable for the variable name.
        var ctx = '';
        try {
            if (typeof selectedMethod !== 'undefined' && selectedMethod) {
                ctx = ' (in the context of calling ' + selectedMethod.name + ')';
            }
        } catch { /* selectedMethod may not be in scope */ }

        var prompt = 'Suggest one plausible value for a variable named "' + name + '"'
            + ctx + '. Reply with ONLY the value, no prose, no quotes, no surrounding text.';

        var resp = await fetch(config.prefix + '/api/ai/chat', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({
                messages: [{ role: 'user', content: prompt }]
            })
        });
        if (!resp.ok) return null;
        var data = await resp.json();
        // The endpoint returns a chat-message envelope. Grab the
        // first non-empty line and strip surrounding quotes / spaces.
        var raw = (data && data.content) || (data && data.message && data.message.content) || '';
        if (!raw) return null;
        var first = String(raw).split('\n').find(function (l) { return l.trim().length > 0; });
        if (!first) return null;
        return first.trim().replace(/^["']|["']$/g, '');
    }

    // Expose at module scope so execute.js + benchmarks.js +
    // perf-diff can await it before their substituteVars calls.
    window.bowirePrefetchAiVars = prefetchAiVars;
