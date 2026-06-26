    // ---- #145 — Deprecation + migration of ${name} syntax ----
    //
    // Bowire keeps two interpolation syntaxes resolving identically:
    //   ${name}    — the original Bash-style placeholder (escape: $${name})
    //   {{name}}   — the canonical multi-source resolver (escape: {{{{name}}}})
    //
    // Phase 1 (soft deprecation):
    //   Scan the active workspace's stored data once at boot, and if
    //   any string field still uses ${...} surface a one-time toast
    //   nudging the operator toward the migration tool. A workspace-
    //   scoped snooze marker keeps the toast from re-appearing on
    //   every page load.
    //
    // Phase 2 (auto-migrate on demand):
    //   `migrateLegacyVars()` walks every string-typed field the
    //   resolver might consume and rewrites ${name} → {{name}} in
    //   place, with the special rewrites the ticket calls out:
    //     ${response.path}        → {{prev.path}}
    //     ${response}             → {{prev}}
    //     ${now|nowMs|timestamp}  → {{runtime.now|...}}
    //     ${uuid|random}          → {{runtime.uuid|random}}
    //     ${now+N} / ${now-N}     → {{runtime.now+N}}/{{runtime.now-N}}
    //     $${name} (escape)       → {{{{name}}}} (quadruple-brace escape)
    //   Migration is idempotent: every rewrite acts on ${...} only,
    //   leaving existing {{...}} untouched, so re-running the migration
    //   on an already-migrated workspace produces zero diff.
    //
    // Both the toast and the Data settings panel call into the same
    // migrate function; the toast offers a "Migrate now" primary
    // button, settings exposes it as a stand-alone "Migrate ${} →
    // {{}}" action.

    var LEGACY_VARS_SNOOZE_KEY = 'bowire_vars_dollar_snoozed';

    // Match ${...} that is NOT preceded by a literal $ (the $${...}
    // escape, which substituteVars treats as a literal ${...}). The
    // lookbehind keeps escaped occurrences from triggering the toast.
    // Mirrors the resolver's regex (history-env.js:506) but inverts
    // the escape branch so escapes are skipped instead of substituted.
    var LEGACY_VARS_DETECTION_PATTERN = /(?<!\$)\$\{[^}]+\}/;

    function _scanString(s) {
        if (typeof s !== 'string' || !s) return false;
        return LEGACY_VARS_DETECTION_PATTERN.test(s);
    }

    function _scanStringBag(bag) {
        // Headers / metadata / params — plain string-valued maps.
        if (!bag || typeof bag !== 'object') return false;
        for (var k in bag) {
            if (Object.prototype.hasOwnProperty.call(bag, k)) {
                if (_scanString(bag[k])) return true;
            }
        }
        return false;
    }

    function _scanRecording(rec) {
        if (!rec) return false;
        if (!Array.isArray(rec.steps)) return false;
        for (var i = 0; i < rec.steps.length; i++) {
            var step = rec.steps[i];
            if (!step) continue;
            if (_scanString(step.body)) return true;
            if (Array.isArray(step.messages)) {
                for (var m = 0; m < step.messages.length; m++) {
                    if (_scanString(step.messages[m])) return true;
                }
            }
            if (_scanStringBag(step.metadata)) return true;
            if (_scanString(step.serverUrl)) return true;
        }
        return false;
    }

    function _scanCollection(col) {
        if (!col || !Array.isArray(col.items)) return false;
        for (var i = 0; i < col.items.length; i++) {
            var item = col.items[i];
            if (!item) continue;
            if (_scanString(item.body)) return true;
            if (_scanString(item.serverUrl)) return true;
            if (_scanStringBag(item.metadata)) return true;
            if (_scanStringBag(item.headers)) return true;
            if (_scanStringBag(item.params)) return true;
        }
        return false;
    }

    function _scanFreeform(req) {
        if (!req) return false;
        if (_scanString(req.body)) return true;
        if (_scanString(req.serverUrl)) return true;
        if (_scanStringBag(req.metadata)) return true;
        return false;
    }

    function _scanFlow(flow) {
        if (!flow || !Array.isArray(flow.nodes)) return false;
        for (var i = 0; i < flow.nodes.length; i++) {
            var node = flow.nodes[i];
            if (!node || !node.config) continue;
            for (var k in node.config) {
                if (Object.prototype.hasOwnProperty.call(node.config, k)) {
                    if (_scanString(node.config[k])) return true;
                }
            }
        }
        return false;
    }

    function _scanEnvironments() {
        // env.vars is an object map { key: value }, not an array of
        // {key,value} records — earlier iterations of this file
        // checked Array.isArray and never matched. Walk the object's
        // string values directly.
        var envs = (typeof getEnvironments === 'function') ? getEnvironments() : [];
        for (var i = 0; i < envs.length; i++) {
            var env = envs[i];
            if (!env || !env.vars || typeof env.vars !== 'object') continue;
            for (var k in env.vars) {
                if (Object.prototype.hasOwnProperty.call(env.vars, k)
                    && _scanString(env.vars[k])) return true;
            }
        }
        // Cross-workspace globals — the merged resolver folds these in
        // alongside env vars, so legacy syntax living in a global is
        // just as much a migration target as one living in an env.
        if (typeof getGlobalVars === 'function') {
            var globals = getGlobalVars();
            if (globals && typeof globals === 'object') {
                for (var gk in globals) {
                    if (Object.prototype.hasOwnProperty.call(globals, gk)
                        && _scanString(globals[gk])) return true;
                }
            }
        }
        return false;
    }

    function workspaceHasLegacyVars() {
        // Walks every string-typed field the resolver might consume.
        // Bails out on the first hit — we only need a yes/no answer
        // for the toast trigger, not a count. The migration tool
        // does the full walk + reports per-source totals.
        try {
            if (Array.isArray(recordingsList)) {
                for (var i = 0; i < recordingsList.length; i++) {
                    if (_scanRecording(recordingsList[i])) return true;
                }
            }
            if (Array.isArray(collectionsList)) {
                for (var c = 0; c < collectionsList.length; c++) {
                    if (_scanCollection(collectionsList[c])) return true;
                }
            }
            if (_scanFreeform(freeformRequest)) return true;
            if (typeof flowsList !== 'undefined' && Array.isArray(flowsList)) {
                for (var f = 0; f < flowsList.length; f++) {
                    if (_scanFlow(flowsList[f])) return true;
                }
            }
            if (_scanEnvironments()) return true;
        } catch (e) {
            // Detection is best-effort — never break boot over it.
            console.warn('[vars-deprecation] scan failed', e);
            return false;
        }
        return false;
    }

    function isLegacyVarsToastSnoozed() {
        try {
            return localStorage.getItem(wsKey(LEGACY_VARS_SNOOZE_KEY)) === '1';
        } catch {
            return false;
        }
    }

    function snoozeLegacyVarsToast() {
        try { localStorage.setItem(wsKey(LEGACY_VARS_SNOOZE_KEY), '1'); }
        catch { /* quota — let the toast reappear next time */ }
    }

    // ----------------------------------------------------------------
    // Phase 2 — migration helpers
    // ----------------------------------------------------------------

    // Names that resolveSystemVar handles bare in ${X}. Each becomes
    // {{runtime.X}} so the post-migration string round-trips through
    // the curly resolver's runtime.* branch (history-env.js:408).
    // ${now+N} / ${now-N} aren't keyed by an exact name — they hit
    // the /^now([+-])(\d+)$/ branch — but the substring 'now' triggers
    // the same rewrite, so the offset comes along for free.
    var _RUNTIME_NAMES = ['now', 'nowMs', 'timestamp', 'uuid', 'random'];

    function _isRuntimeName(name) {
        // Bare ${now} / ${uuid} / ... → {{runtime.now}} / ...
        for (var i = 0; i < _RUNTIME_NAMES.length; i++) {
            if (name === _RUNTIME_NAMES[i]) return true;
        }
        // ${now+3600} / ${now-60} — system-var arithmetic.
        return /^now[+-]\d+$/.test(name);
    }

    /**
     * Rewrite a single string from ${name} syntax to {{name}} syntax.
     *
     * Order matters:
     *   1. $${name}  → sentinel — Bowire's literal-escape form. Must
     *      capture the inner before the bare ${name} pass eats it.
     *   2. ${name}   → {{name}} (with the response/system rewrites
     *      from the ticket's mapping table applied per-name).
     *   3. sentinel  → {{{{name}}}} — Postman / curly-resolver's
     *      quadruple-brace escape, so the migrated string still emits
     *      a literal {{name}} where the original emitted a literal
     *      ${name}.
     *
     * Strings without `${` are returned untouched (object identity
     * preserved) so the caller can detect "changed?" with a !== check.
     */
    function migrateString(s) {
        if (typeof s !== 'string' || s.indexOf('${') === -1) return s;

        // PUA sentinels for the escape pass, same trick the curly
        // resolver uses for {{{{X}}}}. U+E000 / U+E001 can't collide
        // with real source text.
        var SENT_OPEN = '', SENT_CLOSE = '';

        // Pass 1: capture $${name} escapes into sentinels so the next
        // pass treats them as already-rewritten.
        var out = s.replace(/\$\$\{([^}]+)\}/g, function (_m, name) {
            return SENT_OPEN + name + SENT_CLOSE;
        });

        // Pass 2: rewrite bare ${name} → {{name}} with per-name
        // remapping (response → prev, system vars → runtime.*).
        out = out.replace(/\$\{([^}]+)\}/g, function (_m, raw) {
            var name = raw.trim();
            // ${response} / ${response.X} → {{prev}} / {{prev.X}}
            if (name === 'response') return '{{prev}}';
            if (name.indexOf('response.') === 0) {
                return '{{prev.' + name.substring('response.'.length) + '}}';
            }
            // ${now} / ${nowMs} / ${timestamp} / ${uuid} / ${random}
            // / ${now+N} / ${now-N} → {{runtime.X}}
            if (_isRuntimeName(name)) return '{{runtime.' + name + '}}';
            // Everything else passes through name-for-name. env.X /
            // captured.X / ai.X / secret.X / step<N>.X all resolve
            // identically through the curly dispatcher, so the prefix
            // stays as-is — only the brackets change.
            return '{{' + name + '}}';
        });

        // Pass 3: swap sentinels back to {{{{NAME}}}}.
        if (out.indexOf(SENT_OPEN) !== -1) {
            out = out.split(SENT_OPEN).join('{{{{')
                     .split(SENT_CLOSE).join('}}}}');
        }
        return out;
    }

    function _migrateStringBag(bag) {
        // Returns count of fields rewritten. Mutates in place.
        if (!bag || typeof bag !== 'object') return 0;
        var n = 0;
        for (var k in bag) {
            if (!Object.prototype.hasOwnProperty.call(bag, k)) continue;
            var v = bag[k];
            if (typeof v !== 'string') continue;
            var migrated = migrateString(v);
            if (migrated !== v) { bag[k] = migrated; n++; }
        }
        return n;
    }

    function _migrateRecording(rec) {
        if (!rec || !Array.isArray(rec.steps)) return 0;
        var n = 0;
        for (var i = 0; i < rec.steps.length; i++) {
            var step = rec.steps[i];
            if (!step) continue;
            if (typeof step.body === 'string') {
                var b = migrateString(step.body);
                if (b !== step.body) { step.body = b; n++; }
            }
            if (Array.isArray(step.messages)) {
                for (var m = 0; m < step.messages.length; m++) {
                    if (typeof step.messages[m] === 'string') {
                        var msg = migrateString(step.messages[m]);
                        if (msg !== step.messages[m]) { step.messages[m] = msg; n++; }
                    }
                }
            }
            n += _migrateStringBag(step.metadata);
            if (typeof step.serverUrl === 'string') {
                var su = migrateString(step.serverUrl);
                if (su !== step.serverUrl) { step.serverUrl = su; n++; }
            }
        }
        return n;
    }

    function _migrateCollection(col) {
        if (!col || !Array.isArray(col.items)) return 0;
        var n = 0;
        for (var i = 0; i < col.items.length; i++) {
            var item = col.items[i];
            if (!item) continue;
            if (typeof item.body === 'string') {
                var b = migrateString(item.body);
                if (b !== item.body) { item.body = b; n++; }
            }
            if (typeof item.serverUrl === 'string') {
                var su = migrateString(item.serverUrl);
                if (su !== item.serverUrl) { item.serverUrl = su; n++; }
            }
            n += _migrateStringBag(item.metadata);
            n += _migrateStringBag(item.headers);
            n += _migrateStringBag(item.params);
        }
        return n;
    }

    function _migrateFreeform(req) {
        if (!req) return 0;
        var n = 0;
        if (typeof req.body === 'string') {
            var b = migrateString(req.body);
            if (b !== req.body) { req.body = b; n++; }
        }
        if (typeof req.serverUrl === 'string') {
            var su = migrateString(req.serverUrl);
            if (su !== req.serverUrl) { req.serverUrl = su; n++; }
        }
        n += _migrateStringBag(req.metadata);
        return n;
    }

    function _migrateFlow(flow) {
        if (!flow || !Array.isArray(flow.nodes)) return 0;
        var n = 0;
        for (var i = 0; i < flow.nodes.length; i++) {
            var node = flow.nodes[i];
            if (!node || !node.config) continue;
            n += _migrateStringBag(node.config);
        }
        return n;
    }

    /**
     * Walk every string-typed field in the active workspace's stored
     * data, rewrite ${...} → {{...}} in place, then persist via the
     * existing per-store save hooks (so disk sync + save-pill markers
     * fire exactly as they would for a normal edit).
     *
     * Returns { recordings, collections, freeform, flows, environments,
     *           globals, total }.
     *
     * Idempotent: re-running on already-migrated data returns zeros
     * because migrateString is a no-op on strings without `${`.
     */
    function migrateLegacyVars() {
        var counts = {
            recordings: 0, collections: 0, freeform: 0,
            flows: 0, environments: 0, globals: 0, total: 0
        };

        try {
            // Recordings
            if (Array.isArray(recordingsList)) {
                for (var i = 0; i < recordingsList.length; i++) {
                    counts.recordings += _migrateRecording(recordingsList[i]);
                }
                if (counts.recordings > 0 && typeof persistRecordings === 'function') {
                    persistRecordings();
                }
            }

            // Collections
            if (Array.isArray(collectionsList)) {
                for (var c = 0; c < collectionsList.length; c++) {
                    counts.collections += _migrateCollection(collectionsList[c]);
                }
                if (counts.collections > 0 && typeof persistCollections === 'function') {
                    persistCollections();
                }
            }

            // Freeform — single in-memory object. The render path
            // owns persistence (compose-rail persistDesignTabs runs
            // off render), so a render() after the rewrite is enough
            // to land the change in localStorage.
            if (typeof freeformRequest !== 'undefined') {
                counts.freeform = _migrateFreeform(freeformRequest);
            }

            // Flows
            if (typeof flowsList !== 'undefined' && Array.isArray(flowsList)) {
                for (var f = 0; f < flowsList.length; f++) {
                    counts.flows += _migrateFlow(flowsList[f]);
                }
                if (counts.flows > 0 && typeof persistFlows === 'function') {
                    persistFlows();
                }
            }

            // Environments — workspace-scoped env list. env.vars is
            // a { key: value } object map, walked the same way as
            // any string-bag.
            if (typeof getEnvironments === 'function'
                && typeof saveEnvironments === 'function') {
                var envs = getEnvironments();
                var envsDirty = 0;
                for (var e = 0; e < envs.length; e++) {
                    if (envs[e] && envs[e].vars) {
                        envsDirty += _migrateStringBag(envs[e].vars);
                    }
                }
                if (envsDirty > 0) {
                    counts.environments = envsDirty;
                    saveEnvironments(envs);
                }
            }

            // Cross-workspace globals. getGlobalVars returns a clone
            // — mutate the clone, then saveGlobalVars writes back.
            if (typeof getGlobalVars === 'function'
                && typeof saveGlobalVars === 'function') {
                var globals = getGlobalVars();
                var gDirty = _migrateStringBag(globals);
                if (gDirty > 0) {
                    counts.globals = gDirty;
                    saveGlobalVars(globals);
                }
            }
        } catch (e) {
            console.error('[vars-deprecation] migration failed', e);
        }

        counts.total = counts.recordings + counts.collections
            + counts.freeform + counts.flows
            + counts.environments + counts.globals;

        // Re-render so freeform changes + any open editor reflect
        // the rewrite. Snooze the toast unconditionally — even a
        // zero-count migration means the user has acknowledged it.
        try { snoozeLegacyVarsToast(); } catch (_) {}
        try { if (typeof render === 'function') render(); } catch (_) {}

        return counts;
    }

    function _runMigrationFromUi(opts) {
        var counts = migrateLegacyVars();
        if (typeof toast !== 'function') return counts;
        if (counts.total === 0) {
            toast('No legacy ${name} placeholders found.', 'info');
            return counts;
        }
        var parts = [];
        if (counts.recordings) parts.push(counts.recordings + ' recording field' + (counts.recordings === 1 ? '' : 's'));
        if (counts.collections) parts.push(counts.collections + ' collection field' + (counts.collections === 1 ? '' : 's'));
        if (counts.freeform) parts.push(counts.freeform + ' freeform field' + (counts.freeform === 1 ? '' : 's'));
        if (counts.flows) parts.push(counts.flows + ' flow field' + (counts.flows === 1 ? '' : 's'));
        if (counts.environments) parts.push(counts.environments + ' env var' + (counts.environments === 1 ? '' : 's'));
        if (counts.globals) parts.push(counts.globals + ' global var' + (counts.globals === 1 ? '' : 's'));
        toast('Migrated ' + counts.total + ' placeholder' + (counts.total === 1 ? '' : 's') + ' to {{name}}: ' + parts.join(', ') + '.', 'success');
        return counts;
    }

    function showLegacyVarsToastIfNeeded() {
        if (isLegacyVarsToastSnoozed()) return;
        if (!workspaceHasLegacyVars()) return;
        if (typeof toast !== 'function') return;

        // Sticky (duration: 0) — the user picks 'Migrate', 'Snooze',
        // or 'Dismiss'. Auto-fading a deprecation nudge would defeat
        // the purpose.
        var t = toast(
            'This workspace uses the legacy ${name} syntax. The canonical syntax is {{name}} (#145).',
            'info',
            { duration: 0 }
        );
        if (!t) return;

        // Insert action buttons BEFORE the auto-added close (×).
        var closeBtn = t.querySelector('.bowire-toast-close');

        var migrateBtn = el('button', {
            className: 'bowire-toast-undo',
            textContent: 'Migrate now',
            onClick: function (e) {
                e.stopPropagation();
                try { _runMigrationFromUi(); } catch (err) {
                    console.error('[vars-deprecation] migrate-now failed', err);
                }
                t.classList.add('bowire-toast-out');
                setTimeout(function () { t.remove(); }, 200);
            }
        });

        var snoozeBtn = el('button', {
            className: 'bowire-toast-undo',
            textContent: 'Snooze for this workspace',
            onClick: function (e) {
                e.stopPropagation();
                snoozeLegacyVarsToast();
                t.classList.add('bowire-toast-out');
                setTimeout(function () { t.remove(); }, 200);
            }
        });

        if (closeBtn) {
            t.insertBefore(migrateBtn, closeBtn);
            t.insertBefore(snoozeBtn, closeBtn);
        } else {
            t.appendChild(migrateBtn);
            t.appendChild(snoozeBtn);
        }
    }
