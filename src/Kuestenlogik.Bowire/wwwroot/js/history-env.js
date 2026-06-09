    // ---- History ----
    function getHistory() {
        try {
            return JSON.parse(localStorage.getItem(HISTORY_KEY) || '[]');
        } catch {
            return [];
        }
    }

    // Apply search-text + status-bucket filters on top of an already
    // method-filtered history list. Search is case-insensitive substring
    // match across service / method / body / status. Status bucket is
    // "all" (everything), "ok" (status looks successful) or "error" (any
    // non-OK status name or HTTP 4xx/5xx).
    function filterHistoryEntries(entries) {
        var query = (historySearchQuery || '').trim().toLowerCase();
        var bucket = historyStatusFilter;

        if (!query && bucket === 'all') return entries;

        var result = [];
        for (var i = 0; i < entries.length; i++) {
            var h = entries[i];
            if (bucket !== 'all') {
                var isOk = isHistoryEntryOk(h);
                if (bucket === 'ok' && !isOk) continue;
                if (bucket === 'error' && isOk) continue;
            }
            if (query) {
                var hay = ((h.service || '') + ' ' + (h.method || '') + ' ' +
                           (h.body || '') + ' ' + (h.status || '')).toLowerCase();
                if (hay.indexOf(query) === -1) continue;
            }
            result.push(h);
        }
        return result;
    }

    function isHistoryEntryOk(h) {
        if (!h || !h.status) return true;
        var s = String(h.status);
        if (s === 'OK' || s === 'Connected' || s === 'Completed') return true;
        // Numeric HTTP status: 1xx-3xx are OK, 4xx/5xx are not
        var num = parseInt(s, 10);
        if (!isNaN(num)) return num < 400;
        // gRPC status names other than OK are errors
        return false;
    }

    function addHistory(entry) {
        const history = getHistory();
        history.unshift({
            ...entry,
            timestamp: Date.now()
        });
        if (history.length > MAX_HISTORY) history.length = MAX_HISTORY;
        localStorage.setItem(HISTORY_KEY, JSON.stringify(history));
    }

    function clearHistory() {
        localStorage.removeItem(HISTORY_KEY);
        render();
    }

    function restoreHistory(entries) {
        try { localStorage.setItem(HISTORY_KEY, JSON.stringify(entries)); } catch {}
        render();
    }

    // ---- Favorites ----
    function getFavorites() {
        try {
            return JSON.parse(localStorage.getItem(FAVORITES_KEY) || '[]');
        } catch {
            return [];
        }
    }

    function isFavorite(service, method) {
        return getFavorites().some(function (f) { return f.service === service && f.method === method; });
    }

    function toggleFavorite(service, method) {
        var favs = getFavorites();
        var idx = favs.findIndex(function (f) { return f.service === service && f.method === method; });
        if (idx >= 0) {
            favs.splice(idx, 1);
        } else {
            favs.push({ service: service, method: method });
        }
        localStorage.setItem(FAVORITES_KEY, JSON.stringify(favs));
        render();
    }

    // Move a favorite from position `fromIdx` to `toIdx` in the
    // persisted array. Supports drag-and-drop reordering in the
    // favorites sidebar view.
    function reorderFavorite(fromIdx, toIdx) {
        var favs = getFavorites();
        if (fromIdx < 0 || fromIdx >= favs.length) return;
        if (toIdx < 0 || toIdx >= favs.length) return;
        var moved = favs.splice(fromIdx, 1)[0];
        favs.splice(toIdx, 0, moved);
        localStorage.setItem(FAVORITES_KEY, JSON.stringify(favs));
        render();
    }

    // ---- Environments + Variables (Postman-style) ----
    // Storage shape:
    //   bowire_environments → [{ id, name, vars: { key: value, ... } }, ...]
    //   bowire_global_vars  → { key: value, ... }
    //   bowire_active_env   → id (or '' for none)

    function getEnvironments() {
        try {
            var raw = localStorage.getItem(ENVIRONMENTS_KEY);
            var list = raw ? JSON.parse(raw) : [];
            return Array.isArray(list) ? list : [];
        } catch { return []; }
    }

    function saveEnvironments(envs) {
        localStorage.setItem(ENVIRONMENTS_KEY, JSON.stringify(envs));
        scheduleDiskSync();
    }

    function getGlobalVars() {
        try {
            return JSON.parse(localStorage.getItem(GLOBAL_VARS_KEY) || '{}') || {};
        } catch { return {}; }
    }

    function saveGlobalVars(vars) {
        localStorage.setItem(GLOBAL_VARS_KEY, JSON.stringify(vars));
        scheduleDiskSync();
    }

    function getActiveEnvId() {
        return localStorage.getItem(ACTIVE_ENV_KEY) || '';
    }

    function setActiveEnvId(id) {
        if (id) localStorage.setItem(ACTIVE_ENV_KEY, id);
        else localStorage.removeItem(ACTIVE_ENV_KEY);
        scheduleDiskSync();
    }

    // ---- Disk Sync (debounced PUT to ~/.bowire/environments.json) ----
    var diskSyncTimer = null;
    var diskSyncSuspended = false; // true while loading from disk to avoid sync loops

    function scheduleDiskSync() {
        if (diskSyncSuspended) return;
        if (diskSyncTimer) clearTimeout(diskSyncTimer);
        diskSyncTimer = setTimeout(function () {
            diskSyncTimer = null;
            var payload = JSON.stringify({
                globals: getGlobalVars(),
                environments: getEnvironments(),
                activeEnvId: getActiveEnvId()
            });
            fetch(config.prefix + '/api/environments', {
                method: 'PUT',
                headers: { 'Content-Type': 'application/json' },
                body: payload
            }).catch(function () { /* server may not support yet — silent */ });
        }, 400);
    }

    async function loadEnvironmentsFromDisk() {
        try {
            var resp = await fetch(config.prefix + '/api/environments');
            if (!resp.ok) return;
            var data = await resp.json();
            if (!data || typeof data !== 'object') return;

            // Server is the source of truth on startup. Suspend sync to avoid
            // immediately writing the loaded data right back to disk.
            diskSyncSuspended = true;
            try {
                if (data.environments && Array.isArray(data.environments)) {
                    localStorage.setItem(ENVIRONMENTS_KEY, JSON.stringify(data.environments));
                }
                if (data.globals && typeof data.globals === 'object') {
                    localStorage.setItem(GLOBAL_VARS_KEY, JSON.stringify(data.globals));
                }
                if (typeof data.activeEnvId === 'string') {
                    if (data.activeEnvId) localStorage.setItem(ACTIVE_ENV_KEY, data.activeEnvId);
                    else localStorage.removeItem(ACTIVE_ENV_KEY);
                }
            } finally {
                diskSyncSuspended = false;
            }
        } catch { /* offline / endpoint missing — keep localStorage as-is */ }
    }

    async function clearAllEnvironments() {
        diskSyncSuspended = true;
        try {
            localStorage.removeItem(ENVIRONMENTS_KEY);
            localStorage.removeItem(GLOBAL_VARS_KEY);
            localStorage.removeItem(ACTIVE_ENV_KEY);
            try {
                await fetch(config.prefix + '/api/environments', { method: 'DELETE' });
            } catch { /* offline */ }
        } finally {
            diskSyncSuspended = false;
        }
    }

    function getActiveEnv() {
        var id = getActiveEnvId();
        if (!id) return null;
        return getEnvironments().find(function (e) { return e.id === id; }) || null;
    }

    /** Merged variable map: global vars first, active env overrides. */
    function getMergedVars() {
        var merged = Object.assign({}, getGlobalVars());
        var env = getActiveEnv();
        if (env && env.vars) Object.assign(merged, env.vars);
        return merged;
    }

    /**
     * Built-in system variables that don't need to be defined anywhere — they
     * resolve at substitution time. Useful for JWT claims (iat/exp), correlation
     * IDs, and any time-bound or random values in test requests.
     *
     *   ${now}        — current Unix timestamp in seconds
     *   ${now+N}      — current Unix timestamp + N seconds  (e.g. ${now+3600})
     *   ${now-N}      — current Unix timestamp - N seconds
     *   ${nowMs}      — current Unix timestamp in milliseconds
     *   ${timestamp}  — current ISO 8601 timestamp (e.g. 2026-04-06T10:00:00Z)
     *   ${uuid}       — random RFC 4122 v4 UUID
     *   ${random}     — random integer in [0, 2^32)
     */
    // Cryptographically strong 32-bit unsigned int via Web Crypto.
    // Falls back to Math.random only on the rare ancient browser
    // without crypto.getRandomValues (IE10-, pre-2013 webview).
    function secureRandomU32() {
        if (typeof crypto !== 'undefined' && typeof crypto.getRandomValues === 'function') {
            var buf = new Uint32Array(1);
            crypto.getRandomValues(buf);
            return buf[0];
        }
        return Math.floor(Math.random() * 0x100000000);
    }

    function resolveSystemVar(key) {
        if (key === 'now') return String(Math.floor(Date.now() / 1000));
        if (key === 'nowMs') return String(Date.now());
        if (key === 'timestamp') return new Date().toISOString();
        if (key === 'uuid') {
            if (typeof crypto !== 'undefined' && typeof crypto.randomUUID === 'function') {
                return crypto.randomUUID();
            }
            // Fallback v4 generator using crypto.getRandomValues for the
            // 16 hex nibbles per UUID — full entropy, no Math.random.
            return 'xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx'.replace(/[xy]/g, function (c) {
                var r = secureRandomU32() & 0xf;
                return (c === 'x' ? r : (r & 0x3 | 0x8)).toString(16);
            });
        }
        if (key === 'random') return String(secureRandomU32());
        // ${now+N} / ${now-N}
        var m = /^now([+-])(\d+)$/.exec(key);
        if (m) {
            var base = Math.floor(Date.now() / 1000);
            var offset = parseInt(m[2], 10);
            return String(m[1] === '+' ? base + offset : base - offset);
        }
        return null;
    }

    /**
     * Substitute ${name} placeholders in a string using the merged variable map.
     * Unknown placeholders are left as-is so the user notices the typo.
     * Escape with $${name} to emit a literal "${name}".
     * System variables (now, uuid, etc.) take precedence over user-defined vars
     * with the same name.
     */
    function substituteVars(input) {
        if (typeof input !== 'string') return input;
        // Two syntaxes resolve through the same dispatch table:
        // - ${name}        (Bowire's original Bash-style placeholder)
        // - {{source.path}} (Postman / #125 multi-source resolver)
        // Each placeholder rewrites to its resolved string; unknown
        // refs are left intact so the operator sees the typo.
        var hasDollar = input.indexOf('${') !== -1;
        var hasCurly = input.indexOf('{{') !== -1;
        if (!hasDollar && !hasCurly) return input;
        var vars = getMergedVars();

        function resolveKey(key, match) {
            // ${response.path} / {{prev.path}} / {{response.path}} — read from last response.
            if (key === 'response' || key.indexOf('response.') === 0
                || key === 'prev' || key.indexOf('prev.') === 0) {
                var prefix = key.indexOf('prev') === 0 ? 'prev' : 'response';
                var path = key === prefix ? '' : key.substring(prefix.length + 1);
                var resolved = resolveResponseVar(path);
                return resolved === null ? match : resolved;
            }
            // {{runtime.X}} — wrapper around the existing system vars.
            // Lets the operator type runtime.now / runtime.uuid /
            // runtime.timestamp explicitly while ${now} / ${uuid}
            // still works bare.
            if (key.indexOf('runtime.') === 0) {
                var sys = resolveSystemVar(key.substring('runtime.'.length));
                return sys === null ? match : sys;
            }
            // {{env.NAME}} — explicit env-var prefix. {{NAME}} (no
            // prefix) also resolves through env, matching the
            // Postman default behaviour.
            if (key.indexOf('env.') === 0) {
                var envKey = key.substring('env.'.length);
                return Object.prototype.hasOwnProperty.call(vars, envKey)
                    ? String(vars[envKey]) : match;
            }
            // {{step<N>.path}} — read response.path from recording step N.
            var stepMatch = /^step(\d+)\.(.+)$/.exec(key);
            if (stepMatch) {
                var stepIdx = parseInt(stepMatch[1], 10);
                var stepPath = stepMatch[2];
                var stepResolved = resolveStepVar(stepIdx, stepPath);
                return stepResolved === null ? match : stepResolved;
            }
            // {{secret.NAME}} — placeholder for the OS keyring read
            // (Phase 2). For now resolves to a masked literal so the
            // operator's template doesn't leak the bare placeholder
            // when copied.
            if (key.indexOf('secret.') === 0) {
                return '****';
            }
            // {{ai.NAME}} — placeholder for the AI-suggested-value
            // path (Phase 2). For now leaves the placeholder intact
            // so the operator sees it's unresolved.
            if (key.indexOf('ai.') === 0) {
                return match;
            }
            // System var (bare ${now} / ${uuid}).
            var sysBare = resolveSystemVar(key);
            if (sysBare !== null) return sysBare;
            // Per-flow-run vars shadow env vars.
            if (typeof flowVars !== 'undefined' && flowVars
                && Object.prototype.hasOwnProperty.call(flowVars, key)) {
                return String(flowVars[key]);
            }
            // Bare env var lookup.
            return Object.prototype.hasOwnProperty.call(vars, key)
                ? String(vars[key]) : match;
        }

        var out = input;
        if (hasDollar) {
            out = out.replace(/\$(\$?)\{([^}]+)\}/g, function (match, esc, name) {
                if (esc) return '${' + name + '}'; // $${x} → literal ${x}
                return resolveKey(name.trim(), match);
            });
        }
        if (hasCurly) {
            // {{name}} — match the inner content greedily but stop at
            // the first '}}'; double-curly so it doesn't collide with
            // single-brace JSON literals.
            out = out.replace(/\{\{([^{}]+)\}\}/g, function (match, name) {
                return resolveKey(name.trim(), match);
            });
        }
        return out;
    }

    // #125 Phase 1 — step<N>.path resolution. Looks up the Nth step
    // in the *currently selected* recording and applies the same
    // path walk resolveResponseVar uses. Returns null on miss.
    function resolveStepVar(stepIdx, path) {
        var rec = null;
        if (typeof recordingsList !== 'undefined' && recordingManagerSelectedId) {
            rec = recordingsList.find(function (r) { return r.id === recordingManagerSelectedId; });
        }
        if (!rec || !rec.steps || stepIdx < 1 || stepIdx > rec.steps.length) return null;
        var step = rec.steps[stepIdx - 1];
        if (!step || step.response == null) return null;
        // Reuse resolveResponseVar's body-walk by temporarily
        // pointing lastResponse* at this step. resolveResponseVar
        // is module-scoped and reads the module's
        // lastResponseJson / lastResponseText — so swap, resolve,
        // restore.
        var prevJson = typeof lastResponseJson !== 'undefined' ? lastResponseJson : undefined;
        var prevText = typeof lastResponseText !== 'undefined' ? lastResponseText : undefined;
        try {
            // step.response may be a JSON string OR an already-parsed
            // object depending on the recorder path that wrote it.
            var asObj = typeof step.response === 'string'
                ? (function () { try { return JSON.parse(step.response); } catch { return null; } })()
                : step.response;
            if (typeof lastResponseJson !== 'undefined') lastResponseJson = asObj;
            if (typeof lastResponseText !== 'undefined') lastResponseText = (typeof step.response === 'string') ? step.response : JSON.stringify(step.response);
            return resolveResponseVar(path);
        } finally {
            if (typeof lastResponseJson !== 'undefined') lastResponseJson = prevJson;
            if (typeof lastResponseText !== 'undefined') lastResponseText = prevText;
        }
    }

    function substituteMetadata(meta) {
        if (!meta) return meta;
        var out = {};
        for (var k in meta) {
            if (Object.prototype.hasOwnProperty.call(meta, k)) {
                out[k] = substituteVars(meta[k]);
            }
        }
        return out;
    }

    function substituteMessages(messages) {
        if (!Array.isArray(messages)) return messages;
        return messages.map(substituteVars);
    }

