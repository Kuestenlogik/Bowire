    // ---- #145 Phase 1 — Soft deprecation of ${name} syntax ----
    //
    // Bowire keeps two interpolation syntaxes resolving identically:
    //   ${name}    — the original Bash-style placeholder (escape: $${name})
    //   {{name}}   — the canonical multi-source resolver (escape: {{{{name}}}})
    //
    // Phase 1 is purely cosmetic: scan the active workspace's stored
    // data once at boot, and if any string field still uses ${...}
    // surface a one-time toast nudging the operator toward Settings
    // → Migration (which lands in Phase 2). A workspace-scoped snooze
    // marker keeps the toast from re-appearing on every page load.
    //
    // No data is rewritten here — Phase 1 is detection-only. The
    // migration tool (Phase 2) is the place to mutate.

    var LEGACY_VARS_SNOOZE_KEY = 'bowire_vars_dollar_snoozed';

    // Match ${...} that is NOT preceded by a literal $ (the $${...}
    // escape, which substituteVars treats as a literal ${...}). The
    // lookbehind keeps escaped occurrences from triggering the toast.
    // Mirrors the resolver's regex (history-env.js:474) but inverts
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
        // getEnvironments() returns the workspace-visible subset, so
        // the scan stays scoped to what this workspace will actually
        // resolve. Envs shared with other workspaces aren't surfaced
        // through this workspace's toast.
        var envs = (typeof getEnvironments === 'function') ? getEnvironments() : [];
        for (var i = 0; i < envs.length; i++) {
            var env = envs[i];
            if (!env || !Array.isArray(env.vars)) continue;
            for (var v = 0; v < env.vars.length; v++) {
                var kv = env.vars[v];
                if (kv && _scanString(kv.value)) return true;
            }
        }
        return false;
    }

    function workspaceHasLegacyVars() {
        // Walks every string-typed field the resolver might consume.
        // Bails out on the first hit — we only need a yes/no answer
        // for the toast trigger, not a count. The migration tool
        // (Phase 2) does the full walk + reports per-source totals.
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

    function showLegacyVarsToastIfNeeded() {
        if (isLegacyVarsToastSnoozed()) return;
        if (!workspaceHasLegacyVars()) return;
        if (typeof toast !== 'function') return;

        // Sticky (duration: 0) — the user picks 'Snooze' or 'Dismiss'.
        // Auto-fading a deprecation nudge would defeat the purpose.
        var t = toast(
            'This workspace uses the legacy ${name} syntax. The canonical syntax is {{name}}. A migration tool lands in Phase 2 (#145).',
            'info',
            { duration: 0 }
        );
        if (!t) return;

        // Insert the Snooze button BEFORE the auto-added close (×).
        var closeBtn = t.querySelector('.bowire-toast-close');
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
        if (closeBtn) t.insertBefore(snoozeBtn, closeBtn);
        else t.appendChild(snoozeBtn);
    }
