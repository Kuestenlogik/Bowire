    // ---- History ----
    function getHistory() {
        try {
            return JSON.parse(localStorage.getItem(wsKey(HISTORY_KEY)) || '[]');
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
        localStorage.setItem(wsKey(HISTORY_KEY), JSON.stringify(history));
    }

    function clearHistory() {
        localStorage.removeItem(wsKey(HISTORY_KEY));
        render();
    }

    function restoreHistory(entries) {
        try { localStorage.setItem(wsKey(HISTORY_KEY), JSON.stringify(entries)); } catch {}
        render();
    }

    // ---- Favorites ----
    function getFavorites() {
        try {
            return JSON.parse(localStorage.getItem(wsKey(FAVORITES_KEY)) || '[]');
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
        localStorage.setItem(wsKey(FAVORITES_KEY), JSON.stringify(favs));
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
        localStorage.setItem(wsKey(FAVORITES_KEY), JSON.stringify(favs));
        render();
    }

    // ---- Environments + Variables (Postman-style) ----
    // #146 Storage shape (post-migration):
    //   bowire_environments_shared → [{ id, name, vars: {...} }, ...] (GLOBAL, all workspaces see same list)
    //   bowire_global_vars_shared  → { key: value, ... } (GLOBAL, always in scope)
    //   bowire_ws_<id>_active_env  → id of the workspace's active env
    //   workspaces[*].includedEnvironmentIds → string[] — which shared envs are visible to this workspace
    //
    // Self-contained workspaces (#A refactor): envs are owned by their
    // workspace, stored under `wsKey(ENVIRONMENTS_KEY)` (i.e.
    // `bowire_ws_<id>_environments`). The old global SHARED_ENVS_KEY /
    // inclusion-list model is gone — each workspace exports cleanly
    // (Git-versionable as a single project folder), and there's no
    // cross-workspace shared mutation surface that two workspaces
    // could trample. Globals (`bowire_global_vars_shared`) stay
    // cross-workspace as the explicit shared layer for genuinely
    // ambient values.
    //
    // Migration from the shared-env model runs at boot in prologue.js
    // (_migrateSelfContainedEnvs). For each workspace, the migration
    // copies its included envs out of the shared store into the
    // workspace's own store. The shared store stays behind for
    // rollback, but no live code reads it.
    const SHARED_GLOBAL_VARS_KEY = 'bowire_global_vars_shared';

    // Returns this workspace's envs. Replaces the prior
    // getAllSharedEnvironments / inclusion-filter pair — there's no
    // longer a meaningful distinction between "every available env"
    // and "envs visible to this workspace" because the workspace IS
    // the store.
    function getEnvironments() {
        try {
            var raw = localStorage.getItem(wsKey(ENVIRONMENTS_KEY));
            var list = raw ? JSON.parse(raw) : [];
            return Array.isArray(list) ? list : [];
        } catch { return []; }
    }

    // Back-compat alias for callers still asking for "all shared envs"
    // — same workspace-scoped list now. Kept so the surrounding code
    // doesn't have to be touched in lockstep with the storage flip;
    // a follow-up sweep can collapse callers to getEnvironments.
    function getAllSharedEnvironments() { return getEnvironments(); }

    // Writes the workspace's env list. markSaved drives the save-pill
    // + disk sync hooks the same way the prior shared-store path did.
    function saveEnvironments(envs) {
        try {
            localStorage.setItem(wsKey(ENVIRONMENTS_KEY), JSON.stringify(envs));
            markSaved('environments');
        } catch (e) { markSaveFailed('environments', e); }
        scheduleDiskSync();
    }

    // Reads a non-active workspace's envs (sidebar tree leaves under
    // collapsed-but-not-active workspaces). Mirrors readWorkspaceUrls
    // — directly hits the wsKeyFor-prefixed key without switching
    // active state.
    function readWorkspaceEnvironments(workspaceId) {
        if (!workspaceId) return [];
        if (workspaceId === activeWorkspaceId) return getEnvironments();
        try {
            var raw = localStorage.getItem(wsKeyFor(workspaceId, ENVIRONMENTS_KEY));
            var list = raw ? JSON.parse(raw) : [];
            return Array.isArray(list) ? list : [];
        } catch { return []; }
    }

    // Inclusion-list helpers retired with the self-contained model.
    // Stubs kept so any straggling caller doesn't throw; a follow-up
    // sweep removes the remaining call sites.
    function setEnvIncludedInWorkspace(_envId, _included) { /* no-op */ }
    function isEnvIncludedInWorkspace(_envId) { return true; }

    function getGlobalVars() {
        try {
            return JSON.parse(localStorage.getItem(SHARED_GLOBAL_VARS_KEY) || '{}') || {};
        } catch { return {}; }
    }

    function saveGlobalVars(vars) {
        try { localStorage.setItem(SHARED_GLOBAL_VARS_KEY, JSON.stringify(vars)); }
        catch { /* ignore */ }
        scheduleDiskSync();
    }

    function getActiveEnvId() {
        return localStorage.getItem(wsKey(ACTIVE_ENV_KEY)) || '';
    }

    function setActiveEnvId(id) {
        if (id) localStorage.setItem(wsKey(ACTIVE_ENV_KEY), id);
        else localStorage.removeItem(wsKey(ACTIVE_ENV_KEY));
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
                    localStorage.setItem(wsKey(ENVIRONMENTS_KEY), JSON.stringify(data.environments));
                }
                if (data.globals && typeof data.globals === 'object') {
                    localStorage.setItem(wsKey(GLOBAL_VARS_KEY), JSON.stringify(data.globals));
                }
                if (typeof data.activeEnvId === 'string') {
                    if (data.activeEnvId) localStorage.setItem(wsKey(ACTIVE_ENV_KEY), data.activeEnvId);
                    else localStorage.removeItem(wsKey(ACTIVE_ENV_KEY));
                }
            } finally {
                diskSyncSuspended = false;
            }
        } catch { /* offline / endpoint missing — keep localStorage as-is */ }
    }

    async function clearAllEnvironments() {
        diskSyncSuspended = true;
        try {
            localStorage.removeItem(wsKey(ENVIRONMENTS_KEY));
            localStorage.removeItem(wsKey(GLOBAL_VARS_KEY));
            localStorage.removeItem(wsKey(ACTIVE_ENV_KEY));
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

    /**
     * Merged variable map. Layers, lowest-precedence first:
     *   1. cross-workspace globals (getGlobalVars)
     *   2. workspace defaults (activeWorkspace().vars) — workspace-level
     *      vars that every Environment in this workspace inherits
     *   3. active environment overrides (env.vars)
     * Same precedence shape GitHub Actions uses for env / repo / org
     * variables — the closer you are to the call site, the more
     * specific the override.
     */
    function getMergedVars() {
        var merged = Object.assign({}, getGlobalVars());
        var ws = (typeof activeWorkspace === 'function') ? activeWorkspace() : null;
        if (ws && ws.vars) Object.assign(merged, ws.vars);
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
    function substituteVars(input, _resolvingFromCaller) {
        if (typeof input !== 'string') return input;
        // Two syntaxes resolve through the same dispatch table:
        // - ${name}        (Bowire's original Bash-style placeholder)
        // - {{source.path}} (Postman / #125 multi-source resolver,
        //                    canonical going forward — supports the
        //                    full source-prefix vocabulary + the
        //                    {{{{name}}}} quadruple-brace escape)
        // Existing recordings / collections that use ${name} keep
        // working unchanged; ${name} stays alongside as legacy. New
        // surfaces prefer {{name}}. A future bump deprecates the
        // dollar form once every shipped template/example is on the
        // curly syntax.
        // Each placeholder rewrites to its resolved string; unknown
        // refs are left intact so the operator sees the typo.
        var hasDollar = input.indexOf('${') !== -1;
        var hasCurly = input.indexOf('{{') !== -1;
        if (!hasDollar && !hasCurly) return input;
        var vars = getMergedVars();
        // #125 Phase 2 — cycle detection. A var that references
        // itself transitively (A: {{B}}, B: {{A}}) would otherwise
        // recurse infinitely. We pass the call stack through
        // resolveKey via the third arg; each env-var resolution
        // pushes its key on entry and pops on exit. A repeat push
        // short-circuits with an inline error marker so the operator
        // sees the cycle exactly where it lives instead of the
        // browser tab freezing.
        // Inherit the caller's resolving set when this is a recursive
        // call from env-var expansion; otherwise start fresh.
        var resolving = _resolvingFromCaller || new Set();

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
            // Postman default behaviour. Env-var values can
            // themselves contain {{...}} so we recurse via
            // substituteVars — the cycle guard above prevents
            // infinite loops.
            if (key.indexOf('env.') === 0) {
                var envKey = key.substring('env.'.length);
                if (!Object.prototype.hasOwnProperty.call(vars, envKey)) return match;
                if (resolving.has(envKey)) return '<<cycle:' + envKey + '>>';
                resolving.add(envKey);
                try {
                    return substituteVars(String(vars[envKey]), resolving);
                } finally {
                    resolving.delete(envKey);
                }
            }
            // {{step<N>.path}} — read response.path from recording step N.
            var stepMatch = /^step(\d+)\.(.+)$/.exec(key);
            if (stepMatch) {
                var stepIdx = parseInt(stepMatch[1], 10);
                var stepPath = stepMatch[2];
                var stepResolved = resolveStepVar(stepIdx, stepPath);
                return stepResolved === null ? match : stepResolved;
            }
            // {{secret.NAME}} — workspace-scoped in-memory secret.
            // Phase 4 stores secrets only in the JS session (cleared
            // on reload); Phase 5 will wrap an OS keyring. The
            // resolver returns the REAL value to the request
            // pipeline so the upstream actually gets the bearer
            // token / API key. UI chips render masked, and
            // recording / export sanitisers replace the resolved
            // value with '***' before it leaves the workbench.
            if (key.indexOf('secret.') === 0) {
                var secretName = key.substring('secret.'.length);
                var secretValue = (typeof resolveSecret === 'function')
                    ? resolveSecret(secretName) : null;
                // Unset secrets leave the placeholder intact so the
                // operator sees the typo / missing config instead of
                // a silent empty substitution.
                return secretValue === null ? match : secretValue;
            }
            // {{ai.NAME}} — session-cached AI-suggested value. The
            // synchronous resolver only reads from the cache —
            // prefetchAiVars(template) is the async path that
            // populates the cache before a send. Missing entries
            // leave the placeholder so the operator sees that the
            // ai-prefetch step hasn't run yet (or returned no
            // value), instead of a silent empty substitution.
            if (key.indexOf('ai.') === 0) {
                var aiName = key.substring('ai.'.length);
                var aiValue = (typeof resolveAiVar === 'function')
                    ? resolveAiVar(aiName) : null;
                return aiValue === null ? match : aiValue;
            }
            // System var (bare ${now} / ${uuid}).
            var sysBare = resolveSystemVar(key);
            if (sysBare !== null) return sysBare;
            // Per-flow-run vars shadow env vars.
            if (typeof flowVars !== 'undefined' && flowVars
                && Object.prototype.hasOwnProperty.call(flowVars, key)) {
                return String(flowVars[key]);
            }
            // Bare env var lookup. Same recursive expansion + cycle
            // guard as the explicit {{env.X}} branch so a chain like
            // A → B → A surfaces as <<cycle:A>> instead of freezing.
            if (Object.prototype.hasOwnProperty.call(vars, key)) {
                if (resolving.has(key)) return '<<cycle:' + key + '>>';
                resolving.add(key);
                try { return substituteVars(String(vars[key]), resolving); }
                finally { resolving.delete(key); }
            }
            return match;
        }

        var out = input;
        if (hasDollar) {
            out = out.replace(/\$(\$?)\{([^}]+)\}/g, function (match, esc, name) {
                if (esc) return '${' + name + '}'; // $${x} → literal ${x}
                return resolveKey(name.trim(), match);
            });
        }
        if (hasCurly) {
            // Three-pass curly substitution:
            //   1) Detect {{{{NAME}}}} → quadruple-brace escape.
            //      Postman has no escape at all; we copy the doubling
            //      pattern from ${name}'s $${name} so an author can
            //      emit a literal {{NAME}} in output even when {{NAME}}
            //      would otherwise resolve. Park the inner text under
            //      a sentinel so the next pass leaves it alone.
            //   2) Resolve {{NAME}} normally (env/prev/runtime/step/…).
            //   3) Swap sentinels back to literal {{NAME}}.
            // Sentinel uses U+E000 / U+E001 from the Private-Use Area
            // so it can't collide with any real source text.
            var SENT_OPEN = '', SENT_CLOSE = '';  // U+E000 / U+E001 PUA sentinels
            out = out.replace(/\{\{\{\{([^{}]+)\}\}\}\}/g, function (_m, name) {
                return SENT_OPEN + name + SENT_CLOSE;
            });
            out = out.replace(/\{\{([^{}]+)\}\}/g, function (match, name) {
                return resolveKey(name.trim(), match);
            });
            if (out.indexOf(SENT_OPEN) !== -1) {
                out = out.split(SENT_OPEN).join('{{').split(SENT_CLOSE).join('}}');
            }
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

