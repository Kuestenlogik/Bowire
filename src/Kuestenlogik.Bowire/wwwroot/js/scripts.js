// @generated
// scripts.js — Protocol-typed pre/post-request script sandbox (#126).
//
// Two collapsible script editors per request tab: pre-request runs
// BEFORE the wire request leaves; post-response runs AFTER the
// response lands. Both are JavaScript-compatible, evaluated via
// `new Function(...)` so the host page's CSP applies (no eval'd
// network call dependency).
//
// What's available inside the script is shaped by the request's
// protocol — REST scripts can mutate `ctx.request.headers` +
// `ctx.request.query`; gRPC scripts get `ctx.metadata` +
// `ctx.deadline`; MQTT scripts get `ctx.publish.qos` /
// `ctx.publish.retain`. A small "always available" surface
// (`ctx.env`, `ctx.vars`, `ctx.assert`, `ctx.log`) is shared
// across every protocol.
//
// This fragment is concatenated INSIDE the prologue's IIFE — every
// declaration below lives in module-shared scope alongside execute.js
// and render-main.js so the runtime helpers and the render helpers
// can call each other directly.

    // ---- Protocol detection ----
    // Maps service.source / selectedProtocol to one of the four
    // protocol "shapes" the typed sandbox understands. Anything we
    // don't have a specific shape for collapses to 'rest' — REST is
    // the workbench's lingua franca and its surface is the most
    // permissive (any header / any query param is valid).
    function detectScriptProtocolShape(serviceSource, fallbackProtocol) {
        var raw = String(serviceSource || fallbackProtocol || '').toLowerCase();
        if (raw === 'grpc' || raw === 'grpc-web') return 'grpc';
        if (raw === 'mqtt') return 'mqtt';
        if (raw === 'websocket' || raw === 'ws' || raw === 'socket.io' || raw === 'socketio') return 'websocket';
        return 'rest'; // rest / graphql / odata / sse / signalr / json-rpc / unknown
    }

    // ---- Console log ----
    // Pre/post-script `ctx.log()` writes land in a small ring buffer
    // attached to the method. The Scripts tab renders it under each
    // editor so the operator gets immediate feedback without having
    // to open browser devtools. Capped to keep the tab readable.
    var SCRIPT_CONSOLE_CAP = 200;

    // Per-method consoles keyed by `service::method`. Lives in
    // module scope (lost on reload — same posture as channel state)
    // because they're transient diagnostic output, not user content.
    var _scriptConsoles = {};

    function _scriptConsoleKey(svc, method) {
        return (svc || '') + '::' + (method || '');
    }

    function getScriptConsole(svc, method) {
        var key = _scriptConsoleKey(svc, method);
        return _scriptConsoles[key] || [];
    }

    function pushScriptLog(svc, method, phase, level, args) {
        var key = _scriptConsoleKey(svc, method);
        var buf = _scriptConsoles[key] || (_scriptConsoles[key] = []);
        var parts = [];
        for (var i = 0; i < args.length; i++) {
            var v = args[i];
            if (typeof v === 'object' && v !== null) {
                try { parts.push(JSON.stringify(v)); }
                catch { parts.push(String(v)); }
            } else {
                parts.push(String(v));
            }
        }
        buf.push({
            ts: Date.now(),
            phase: phase,   // 'pre' | 'post'
            level: level,   // 'log' | 'error' | 'assert-fail'
            message: parts.join(' ')
        });
        if (buf.length > SCRIPT_CONSOLE_CAP) {
            buf.splice(0, buf.length - SCRIPT_CONSOLE_CAP);
        }
    }

    function clearScriptConsole(svc, method) {
        delete _scriptConsoles[_scriptConsoleKey(svc, method)];
    }

    // ---- Always-available API helpers ----
    function _buildScriptEnvApi(envSnapshot, onSet) {
        return {
            get: function (key) {
                return envSnapshot && Object.prototype.hasOwnProperty.call(envSnapshot, key)
                    ? envSnapshot[key]
                    : undefined;
            },
            set: function (key, value) {
                if (!key) return;
                envSnapshot[key] = value;
                if (typeof onSet === 'function') {
                    try { onSet(key, value); } catch (_) { /* best-effort */ }
                }
            },
            keys: function () { return Object.keys(envSnapshot || {}); }
        };
    }

    function _buildScriptVarsApi(capturedRef) {
        return { captured: capturedRef.captured };
    }

    function _buildScriptAssertApi() {
        function fail(message) {
            var err = new Error(message);
            err.__bowireScriptAssertFail = true;
            throw err;
        }
        return {
            ok: function (value, message) {
                if (!value) fail('assert.ok failed' + (message ? ': ' + message : ''));
            },
            equal: function (actual, expected, message) {
                // eslint-disable-next-line eqeqeq
                if (actual != expected) {
                    fail('assert.equal failed' + (message ? ': ' + message : '')
                        + ' (actual=' + JSON.stringify(actual)
                        + ', expected=' + JSON.stringify(expected) + ')');
                }
            },
            notEqual: function (actual, expected, message) {
                // eslint-disable-next-line eqeqeq
                if (actual == expected) {
                    fail('assert.notEqual failed' + (message ? ': ' + message : ''));
                }
            },
            deepEqual: function (actual, expected, message) {
                var a = '', b = '';
                try { a = JSON.stringify(actual); b = JSON.stringify(expected); }
                catch (e) { fail('assert.deepEqual could not serialise values: ' + e.message); }
                if (a !== b) {
                    fail('assert.deepEqual failed' + (message ? ': ' + message : '')
                        + ' (actual=' + a + ', expected=' + b + ')');
                }
            }
        };
    }

    // ---- Protocol-typed extension surfaces ----
    function _buildRestScriptExt(requestRef) {
        return {
            request: {
                get url() { return requestRef.url || ''; },
                set url(v) { requestRef.url = String(v == null ? '' : v); },
                get body() { return requestRef.body; },
                set body(v) { requestRef.body = v; },
                get method() { return requestRef.method || ''; },
                set method(v) { requestRef.method = String(v || '').toUpperCase(); },
                headers: {
                    get: function (k) { return requestRef.headers[k]; },
                    set: function (k, v) {
                        if (!k) return;
                        requestRef.headers[k] = v == null ? '' : String(v);
                    },
                    delete: function (k) { if (k) delete requestRef.headers[k]; },
                    has: function (k) { return Object.prototype.hasOwnProperty.call(requestRef.headers, k); },
                    keys: function () { return Object.keys(requestRef.headers); }
                },
                query: {
                    get: function (k) { return requestRef.query[k]; },
                    set: function (k, v) {
                        if (!k) return;
                        requestRef.query[k] = v == null ? '' : String(v);
                    },
                    delete: function (k) { if (k) delete requestRef.query[k]; },
                    has: function (k) { return Object.prototype.hasOwnProperty.call(requestRef.query, k); },
                    keys: function () { return Object.keys(requestRef.query); }
                }
            }
        };
    }

    function _buildGrpcScriptExt(requestRef) {
        return {
            metadata: {
                get: function (k) { return requestRef.headers[k]; },
                set: function (k, v) {
                    if (!k) return;
                    requestRef.headers[k] = v == null ? '' : String(v);
                },
                delete: function (k) { if (k) delete requestRef.headers[k]; },
                has: function (k) { return Object.prototype.hasOwnProperty.call(requestRef.headers, k); },
                keys: function () { return Object.keys(requestRef.headers); }
            },
            deadline: {
                setSeconds: function (s) {
                    var n = Number(s);
                    if (!isFinite(n) || n <= 0) return;
                    requestRef.headers['grpc-timeout'] = Math.floor(n) + 'S';
                    requestRef.deadlineSeconds = n;
                },
                getSeconds: function () { return requestRef.deadlineSeconds || 0; }
            },
            request: {
                get body() { return requestRef.body; },
                set body(v) { requestRef.body = v; }
            }
        };
    }

    function _buildMqttScriptExt(requestRef) {
        if (!requestRef.publish) {
            requestRef.publish = { qos: 0, retain: false, topic: '' };
        }
        return {
            publish: requestRef.publish,
            request: {
                get body() { return requestRef.body; },
                set body(v) { requestRef.body = v; }
            }
        };
    }

    function _buildWebSocketScriptExt(requestRef) {
        return {
            request: {
                get body() { return requestRef.body; },
                set body(v) { requestRef.body = v; }
            }
        };
    }

    function _buildScriptResponseApi(responseRef, shape) {
        var base = {
            get body() { return responseRef.body; },
            get status() { return responseRef.status; },
            get durationMs() { return responseRef.durationMs || 0; },
            json: function () {
                if (responseRef._parsedJson !== undefined) return responseRef._parsedJson;
                if (typeof responseRef.body === 'object' && responseRef.body !== null) {
                    responseRef._parsedJson = responseRef.body;
                    return responseRef.body;
                }
                try {
                    responseRef._parsedJson = JSON.parse(responseRef.body || 'null');
                } catch {
                    responseRef._parsedJson = null;
                }
                return responseRef._parsedJson;
            }
        };
        if (shape === 'rest') {
            base.headers = responseRef.headers || {};
        } else if (shape === 'grpc') {
            base.metadata = responseRef.headers || {};
        }
        return base;
    }

    // ---- Sandbox runner ----
    function runPreRequestScript(opts) {
        // opts: { source, protocolShape, service, method, request,
        //         env, captured, onEnvSet }
        if (!opts.source || !String(opts.source).trim()) return { ok: true };

        opts.request.headers = opts.request.headers || {};
        opts.request.query = opts.request.query || {};

        var envApi = _buildScriptEnvApi(opts.env || {}, opts.onEnvSet);
        var varsApi = _buildScriptVarsApi({ captured: opts.captured });
        var assertApi = _buildScriptAssertApi();
        var svc = opts.service, mth = opts.method;
        var logFn = function () {
            pushScriptLog(svc, mth, 'pre', 'log', arguments);
        };

        var ctx = {
            env: envApi,
            vars: varsApi,
            assert: assertApi,
            log: logFn,
            protocol: opts.protocolShape,
            method: mth,
            service: svc
        };

        var ext;
        switch (opts.protocolShape) {
            case 'grpc': ext = _buildGrpcScriptExt(opts.request); break;
            case 'mqtt': ext = _buildMqttScriptExt(opts.request); break;
            case 'websocket': ext = _buildWebSocketScriptExt(opts.request); break;
            default:     ext = _buildRestScriptExt(opts.request); break;
        }
        Object.assign(ctx, ext);

        try {
            var fn = new Function('ctx', '"use strict";\n' + opts.source);
            fn(ctx);
            return { ok: true };
        } catch (err) {
            var msg = (err && err.message) || String(err);
            pushScriptLog(svc, mth, 'pre',
                err && err.__bowireScriptAssertFail ? 'assert-fail' : 'error',
                [msg]);
            return { ok: false, error: msg, assertFail: !!(err && err.__bowireScriptAssertFail) };
        }
    }

    function runPostResponseScriptTyped(opts) {
        // opts: { source, protocolShape, service, method, request,
        //         response, env, captured, onEnvSet }
        if (!opts.source || !String(opts.source).trim()) return { ok: true };

        var envApi = _buildScriptEnvApi(opts.env || {}, opts.onEnvSet);
        var varsApi = _buildScriptVarsApi({ captured: opts.captured });
        var assertApi = _buildScriptAssertApi();
        var svc = opts.service, mth = opts.method;
        var logFn = function () {
            pushScriptLog(svc, mth, 'post', 'log', arguments);
        };
        var responseApi = _buildScriptResponseApi(opts.response || {}, opts.protocolShape);

        var ctx = {
            env: envApi,
            vars: varsApi,
            assert: assertApi,
            log: logFn,
            response: responseApi,
            protocol: opts.protocolShape,
            method: mth,
            service: svc
        };

        // Post-scripts read the pre-request shape as immutable record
        // — the wire request already shipped. Frozen via Object.freeze
        // so accidental mutation fails loudly in strict mode.
        var reqRef = opts.request || { headers: {}, query: {} };
        switch (opts.protocolShape) {
            case 'grpc':
                ctx.metadata = Object.freeze(Object.assign({}, reqRef.headers || {}));
                break;
            case 'mqtt':
                ctx.publish = Object.freeze(Object.assign({}, reqRef.publish || {}));
                break;
            default:
                ctx.request = Object.freeze({
                    url: reqRef.url || '',
                    method: reqRef.method || '',
                    headers: Object.assign({}, reqRef.headers || {}),
                    query: Object.assign({}, reqRef.query || {}),
                    body: reqRef.body
                });
                break;
        }

        try {
            var fn = new Function('ctx', '"use strict";\n' + opts.source);
            fn(ctx);
            return { ok: true };
        } catch (err) {
            var msg = (err && err.message) || String(err);
            pushScriptLog(svc, mth, 'post',
                err && err.__bowireScriptAssertFail ? 'assert-fail' : 'error',
                [msg]);
            return { ok: false, error: msg, assertFail: !!(err && err.__bowireScriptAssertFail) };
        }
    }

    // ---- Static lint — protocol-typed surface check ----
    // Cheap regex scan that flags use of a `ctx.*` member that the
    // current protocol's shape doesn't carry. Not a full type
    // checker, just enough to give the operator a clear hint at
    // edit time instead of a confused TypeError at run time.
    function lintScriptForShape(source, shape) {
        if (!source || !String(source).trim()) return [];
        var warnings = [];
        var WRONG = {
            rest: [
                { pattern: /\bctx\.metadata\b/, member: 'ctx.metadata', hint: 'gRPC-only — use ctx.request.headers for REST' },
                { pattern: /\bctx\.deadline\b/, member: 'ctx.deadline', hint: 'gRPC-only — REST has no deadline concept' },
                { pattern: /\bctx\.publish\b/,  member: 'ctx.publish',  hint: 'MQTT-only — REST has no publish-frame settings' }
            ],
            grpc: [
                { pattern: /\bctx\.request\.headers\b/, member: 'ctx.request.headers', hint: 'REST-only — use ctx.metadata for gRPC' },
                { pattern: /\bctx\.request\.query\b/,   member: 'ctx.request.query',   hint: 'REST-only — gRPC has no query string' },
                { pattern: /\bctx\.publish\b/,          member: 'ctx.publish',         hint: 'MQTT-only — gRPC has no publish-frame settings' }
            ],
            mqtt: [
                { pattern: /\bctx\.request\.headers\b/, member: 'ctx.request.headers', hint: 'REST-only — MQTT has no request headers' },
                { pattern: /\bctx\.request\.query\b/,   member: 'ctx.request.query',   hint: 'REST-only — MQTT has no query string' },
                { pattern: /\bctx\.metadata\b/,         member: 'ctx.metadata',        hint: 'gRPC-only — use ctx.publish for MQTT' },
                { pattern: /\bctx\.deadline\b/,         member: 'ctx.deadline',        hint: 'gRPC-only — MQTT has no deadline concept' }
            ],
            websocket: [
                { pattern: /\bctx\.metadata\b/,         member: 'ctx.metadata',        hint: 'gRPC-only — WebSocket frames are unstructured' },
                { pattern: /\bctx\.deadline\b/,         member: 'ctx.deadline',        hint: 'gRPC-only — WebSocket has no deadline concept' },
                { pattern: /\bctx\.publish\b/,          member: 'ctx.publish',         hint: 'MQTT-only — WebSocket has no publish-frame settings' },
                { pattern: /\bctx\.request\.query\b/,   member: 'ctx.request.query',   hint: 'REST-only — WebSocket connect-URL is set in the URL bar' }
            ]
        };
        var list = WRONG[shape] || [];
        for (var i = 0; i < list.length; i++) {
            if (list[i].pattern.test(source)) {
                warnings.push({ member: list[i].member, hint: list[i].hint, shape: shape });
            }
        }
        return warnings;
    }

    // ---- Test harness export ----
    // The Node-side test harness loads the fragment outside the IIFE,
    // re-using the same source text. Exporting the runtime under
    // window.__bowireScripts gives the tests a stable handle without
    // having to touch every helper individually.
    try {
        if (typeof window !== 'undefined') {
            window.__bowireScripts = {
                detectProtocolShape: detectScriptProtocolShape,
                runPreScript: runPreRequestScript,
                runPostScript: runPostResponseScriptTyped,
                lintScriptForShape: lintScriptForShape,
                getScriptConsole: getScriptConsole,
                pushScriptLog: pushScriptLog,
                clearScriptConsole: clearScriptConsole,
                SCRIPT_CONSOLE_CAP: SCRIPT_CONSOLE_CAP
            };
        }
    } catch (_) { /* window undefined in Node tests */ }
