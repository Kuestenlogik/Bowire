// script-sandbox.js — where pre-request and post-response scripts run (#1062).
//
// A script is a string, and the workbench's Content-Security-Policy refuses
// to evaluate strings in the page ('unsafe-eval'), which is right: an
// injected one would run with everything the page holds. So a script runs in
// QuickJS compiled to WebAssembly (vendored by scripts/vendor/quickjs.mjs),
// in a heap of its own: no DOM, no storage, no network, no reach into the
// page. The policy only has to allow compiling that one module
// ('wasm-unsafe-eval').
//
// What runs inside is scripts.js itself, fetched as text, entered through
// its runScriptJson — the same file and the same entry the server runs in
// Jint for `bowire test`. Every run gets a fresh runtime with a deadline and
// a memory ceiling (config.scriptLimits, from Bowire:Scripting:*); the
// result comes back as JSON and is written onto the caller's objects, so the
// callers see what they saw when the runtime ran in the page.
//
// Loading is asynchronous, running is not: a caller awaits
// ensureScriptSandbox() once — a handshake, the load resolves or reports why
// it failed — and then runs scripts synchronously.
//
// This fragment is concatenated INSIDE the prologue's IIFE, after scripts.js.

    var _scriptSandbox = null;        // { qjs, runtime } once loaded
    var _scriptSandboxLoad = null;    // the load in flight, shared by every caller
    var _scriptSandboxError = null;   // why the last load failed
    var _scriptSandboxFiles = null;   // { wasm, runtime } once fetched, for a reload
    // QuickJS's own stack ceiling. Kept well below what the browser's engine
    // allows the WebAssembly call stack, so a runaway recursion is QuickJS's
    // "stack overflow", which the script sees, and not the host's.
    var SCRIPT_STACK_BYTES = 256 * 1024;

    function _scriptSandboxUrl(name) {
        return config.prefix + '/api/ui/script-sandbox/' + name
            + '?v=' + encodeURIComponent(config.version || '');
    }

    function _loadQuickJsScript() {
        return new Promise(function (resolve, reject) {
            if (window.BowireQuickJS) { resolve(); return; }
            var el = document.createElement('script');
            el.src = _scriptSandboxUrl('quickjs.js');
            el.onload = function () { resolve(); };
            el.onerror = function () { reject(new Error('quickjs.js did not load')); };  // i18n-exempt: the reason inside scripts.sandbox.loadFailed, names a file
            document.head.appendChild(el);
        });
    }

    function _fetchOk(name, read) {
        return fetch(_scriptSandboxUrl(name)).then(function (resp) {
            if (!resp.ok) throw new Error(name + ': HTTP ' + resp.status);  // i18n-exempt: the reason inside scripts.sandbox.loadFailed, names a file and a status
            return read(resp);
        });
    }

    // Resolves to the sandbox, or to null with _scriptSandboxError set; it
    // never rejects, so a caller can await it and then simply run. A failed
    // load is tried again on the next call.
    function ensureScriptSandbox() {
        if (_scriptSandbox) return Promise.resolve(_scriptSandbox);
        if (_scriptSandboxLoad) return _scriptSandboxLoad;
        _scriptSandboxLoad = _loadQuickJsScript()
            .then(function () {
                if (_scriptSandboxFiles) return _scriptSandboxFiles;
                return Promise.all([
                    _fetchOk('quickjs.wasm', function (r) { return r.arrayBuffer(); }),
                    _fetchOk('scripts.js', function (r) { return r.text(); })
                ]).then(function (files) {
                    // scripts.js is a fragment of the bundle: it expects the
                    // bundle's `t` (only its lint calls it) and exports
                    // through `window`. Jint gets the same two lines.
                    _scriptSandboxFiles = {
                        wasm: files[0],
                        runtime: 'var window = {};\nfunction t(key) { return key; }\n' + files[1]
                    };
                    return _scriptSandboxFiles;
                });
            })
            .then(function (files) {
                return window.BowireQuickJS.load(new Uint8Array(files.wasm)).then(function (qjs) {
                    _scriptSandbox = { qjs: qjs, runtime: files.runtime };
                    _scriptSandboxError = null;
                    return _scriptSandbox;
                });
            })
            .catch(function (err) {
                _scriptSandboxError = (err && err.message) || String(err);
                return null;
            })
            .then(function (sandbox) {
                _scriptSandboxLoad = null;
                return sandbox;
            });
        return _scriptSandboxLoad;
    }

    function _scriptLimits() {
        var l = (config && config.scriptLimits) || {};
        return {
            timeoutMs: l.timeoutMs > 0 ? l.timeoutMs : 5000,
            memoryBytes: l.memoryBytes > 0 ? l.memoryBytes : 64 * 1024 * 1024
        };
    }

    // An error that left the runtime: a limit QuickJS enforces, or a fault
    // of the runtime itself. A script's own throw never gets here —
    // scripts.js catches it and reports it as the script's failure.
    function _sandboxFailure(err, limits) {
        var message = (err && typeof err === 'object' && err.message) || String(err);
        if (message === 'interrupted') {
            return t('scripts.stopped.timeout', { seconds: String(limits.timeoutMs / 1000) });
        }
        if (message === 'out of memory') {
            return t('scripts.stopped.memory', { mb: String(Math.round(limits.memoryBytes / (1024 * 1024))) });
        }
        if (message === 'stack overflow') return t('scripts.stopped.stack');
        return message;
    }

    // What the script left behind, onto the caller's objects: the request
    // (pre-request only — a post-response script reads a request that has
    // gone), environment writes through onEnvSet, the captured bag in place
    // (#132 sessions hold a reference to it), and this run's log lines.
    function _applySandboxResult(phase, opts, out) {
        if (phase === 'pre' && opts.request && out.request) {
            Object.keys(opts.request).forEach(function (k) { delete opts.request[k]; });
            Object.assign(opts.request, out.request);
        }
        var env = opts.env || {};
        Object.keys(out.env || {}).forEach(function (k) {
            if (env[k] === out.env[k]) return;
            env[k] = out.env[k];
            if (typeof opts.onEnvSet === 'function') {
                try { opts.onEnvSet(k, out.env[k]); } catch (_) { /* persistence is best-effort */ }
            }
        });
        if (opts.captured && out.captured) {
            Object.keys(opts.captured).forEach(function (k) { delete opts.captured[k]; });
            Object.assign(opts.captured, out.captured);
        }
        (out.log || []).forEach(function (e) {
            pushScriptLog(opts.service, opts.method, phase, e.level, [e.message]);
        });
    }

    // opts as scripts.js's runPreRequestScript / runPostResponseScriptTyped
    // take them; the answer is theirs too: { ok, error, assertFail }.
    function runSandboxedScript(phase, opts) {
        if (!opts.source || !String(opts.source).trim()) return { ok: true };
        var svc = opts.service, mth = opts.method;
        function failed(message) {
            pushScriptLog(svc, mth, phase, 'error', [message]);
            return { ok: false, error: message, assertFail: false };
        }
        if (!_scriptSandbox) {
            return failed(t('scripts.sandbox.loadFailed', { reason: _scriptSandboxError || t('scripts.sandbox.notLoaded') }));
        }

        var limits = _scriptLimits();
        var rt = _scriptSandbox.qjs.newRuntime();
        rt.setMemoryLimit(limits.memoryBytes);
        rt.setMaxStackSize(SCRIPT_STACK_BYTES);
        rt.setInterruptHandler(window.BowireQuickJS.shouldInterruptAfterDeadline(Date.now() + limits.timeoutMs));
        var ctx = rt.newContext();
        try {
            var input = JSON.stringify({
                phase: phase,
                source: String(opts.source),
                shape: opts.protocolShape || 'rest',
                service: svc || '',
                method: mth || '',
                request: opts.request || { headers: {}, query: {} },
                response: opts.response,
                env: opts.env || {},
                captured: opts.captured || {}
            });
            var res;
            try {
                res = ctx.evalCode(
                    _scriptSandbox.runtime + '\n;window.__bowireScripts.runJson(' + JSON.stringify(input) + ');',
                    'scripts.js');
            } catch (hostErr) {
                // The host engine's own limit, reached inside the interpreter
                // (its call stack); the instance is not to be trusted after.
                _scriptSandbox = null;
                return failed(hostErr instanceof RangeError ? t('scripts.stopped.stack')
                    : ((hostErr && hostErr.message) || String(hostErr)));
            }
            if (res.error) {
                var err = ctx.dump(res.error);
                res.error.dispose();
                return failed(_sandboxFailure(err, limits));
            }
            var out = JSON.parse(ctx.dump(res.value));
            res.value.dispose();
            _applySandboxResult(phase, opts, out);
            return { ok: !!out.ok, error: out.error || undefined, assertFail: !!out.assertFail };
        } finally {
            try {
                ctx.dispose();
                rt.dispose();
            } catch (_) {
                // QuickJS can abort while freeing a runtime a script left in a
                // bad state (unbounded recursion does it), and an aborted
                // WebAssembly instance stays dead. The run's answer is already
                // read; drop the instance, and the next ensureScriptSandbox()
                // builds a fresh one from the bytes already fetched.
                _scriptSandbox = null;
            }
        }
    }

    function runSandboxedPreScript(opts) { return runSandboxedScript('pre', opts); }
    function runSandboxedPostScript(opts) { return runSandboxedScript('post', opts); }
