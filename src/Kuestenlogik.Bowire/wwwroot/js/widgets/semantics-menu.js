// Copyright 2026 Küstenlogik · Apache-2.0
// ----------------------------------------------------------------------
// Phase 4 — manual user override UI for the frame-semantics framework.
//
// Attaches a right-click (and long-press) handler to a response-tree
// leaf and renders a popup menu that issues writes to the Phase-4 HTTP
// endpoints (POST/DELETE /api/semantics/annotation). The menu offers:
//
//   • Accept (auto/plugin) → promotes the existing detection to a
//     user-tier annotation so the choice survives.
//   • Reinterpret as ▸ submenu of every BuiltInSemanticTag value plus
//     a free-text "Custom kind…" input.
//   • Suppress — writes the explicit `none` tag.
//   • Persist for ▸ session / user / project — the sticky-default for
//     the rest of the session is stored under
//     `bowire_semantics_persist_default` in localStorage.
//   • Scope ▸ three radio-style options matching the ADR's "current
//     discriminator only" (default), "this method, all message types
//     where this path exists", and "this method, all matching path
//     names by leaf name."
//
// Closes on Escape, click-outside, or successful action. Successful
// writes dispatch `bowire:semantics-changed` with `{ service, method }`
// so the workbench can clear /api/semantics/effective and re-render.
//
// Companion-field suggestion: after a coordinate.latitude /
// coordinate.longitude / coordinate.ecef.x / image.bytes / audio.bytes
// write, the menu opens a follow-up toast offering to mark a sibling
// numeric field as the natural companion. The sibling enumerator
// walks the response-tree DOM once and never blocks the menu close.
// ----------------------------------------------------------------------

    /**
     * Built-in semantic tags surfaced in the "Reinterpret as" submenu.
     * Mirror of `BuiltInSemanticTags` on the C# side (keep the two in
     * lockstep — see frame-semantics-framework.md ADR). Listed in the
     * order users will scan them, family-grouped.
     */
    var bowireBuiltInSemanticTags = [
        'coordinate.latitude',
        'coordinate.longitude',
        'coordinate.ecef.x',
        'coordinate.ecef.y',
        'coordinate.ecef.z',
        'image.bytes',
        'image.mime-type',
        'audio.bytes',
        'audio.sample-rate',
        'timeseries.timestamp',
        'timeseries.value',
        'table.row-array'
    ];

    /**
     * Companion-field suggestions per semantic. After a user marks a
     * field as `coordinate.latitude`, the menu walks sibling fields and
     * proposes one to mark as `coordinate.longitude` (and symmetrically
     * for ecef triple, image bytes ↔ mime, audio bytes ↔ sample-rate).
     * Each entry: { source kind: [ list of companion kinds to suggest ] }
     */
    var bowireSemanticCompanions = {
        'coordinate.latitude': ['coordinate.longitude'],
        'coordinate.longitude': ['coordinate.latitude'],
        'coordinate.ecef.x': ['coordinate.ecef.y', 'coordinate.ecef.z'],
        'coordinate.ecef.y': ['coordinate.ecef.x', 'coordinate.ecef.z'],
        'coordinate.ecef.z': ['coordinate.ecef.x', 'coordinate.ecef.y'],
        'image.bytes': ['image.mime-type'],
        'audio.bytes': ['audio.sample-rate']
    };

    /**
     * Persistence-default key in localStorage. Allowed values mirror
     * the JSON `scope` field on the wire — keep these strings in sync
     * with the C# AnnotationTier parser.
     */
    var BOWIRE_PERSIST_DEFAULT_KEY = 'bowire_semantics_persist_default';
    var BOWIRE_PERSIST_DEFAULT_ALLOWED = { session: 1, user: 1, project: 1 };

    function bowireSemanticsPersistDefault() {
        try {
            var v = localStorage.getItem(BOWIRE_PERSIST_DEFAULT_KEY);
            if (v && BOWIRE_PERSIST_DEFAULT_ALLOWED[v]) return v;
        } catch { /* swallow — locked-down storage just falls back to session */ }
        return 'session';
    }

    function bowireSetSemanticsPersistDefault(value) {
        if (!BOWIRE_PERSIST_DEFAULT_ALLOWED[value]) return;
        try { localStorage.setItem(BOWIRE_PERSIST_DEFAULT_KEY, value); }
        catch { /* swallow */ }
    }

    /**
     * Track the active popup so a second right-click closes the
     * previous menu (instead of stacking N menus on top of each other).
     * Single global pointer is fine — only one menu can be open at a
     * time per UX design.
     */
    var bowireActiveSemanticsMenu = null;

    /**
     * Long-press timeout in ms for mobile-friendly access to the menu.
     * Below 500ms the gesture conflicts with native scroll/select.
     */
    var BOWIRE_LONG_PRESS_MS = 600;

    /**
     * Public entry point — attach the right-click + long-press handlers
     * to a response-tree DOM node. Opts shape:
     *   { service, method, discriminator, jsonPath, currentTag,
     *     currentSource, parentLeafContext }
     * `parentLeafContext` is an optional root element for sibling-field
     * enumeration; defaults to the closest `.bowire-json-tree`.
     *
     * Returns a `disposer` function the caller invokes to remove
     * listeners (re-renders, tab switches).
     */
    function bowireMountSemanticsContextMenu(treeNode, opts) {
        if (!treeNode || !opts) return function () { };
        var longPressTimer = null;
        function onContextMenu(e) {
            e.preventDefault();
            e.stopPropagation();
            bowireOpenSemanticsMenu(e.clientX, e.clientY, treeNode, opts);
        }
        function onTouchStart(e) {
            if (!e.touches || e.touches.length !== 1) return;
            var t = e.touches[0];
            longPressTimer = setTimeout(function () {
                longPressTimer = null;
                bowireOpenSemanticsMenu(t.clientX, t.clientY, treeNode, opts);
            }, BOWIRE_LONG_PRESS_MS);
        }
        function onTouchEnd() {
            if (longPressTimer) { clearTimeout(longPressTimer); longPressTimer = null; }
        }
        treeNode.addEventListener('contextmenu', onContextMenu);
        treeNode.addEventListener('touchstart', onTouchStart, { passive: true });
        treeNode.addEventListener('touchend', onTouchEnd);
        treeNode.addEventListener('touchmove', onTouchEnd);
        treeNode.addEventListener('touchcancel', onTouchEnd);
        return function () {
            treeNode.removeEventListener('contextmenu', onContextMenu);
            treeNode.removeEventListener('touchstart', onTouchStart);
            treeNode.removeEventListener('touchend', onTouchEnd);
            treeNode.removeEventListener('touchmove', onTouchEnd);
            treeNode.removeEventListener('touchcancel', onTouchEnd);
        };
    }

    /**
     * Render and position the popup. The menu attaches at the document
     * body with `position: fixed` and (x, y) translated to keep it on
     * screen — DOM-tree append rather than container-nesting so the
     * menu stays clickable even when the response pane has its own
     * `overflow: hidden`. The viewport-clamp math is pure pixel-space:
     * compute the bounding rect after first render, shift left / up by
     * the overflow so the menu's right/bottom edge stays inside the
     * viewport.
     */
    function bowireOpenSemanticsMenu(x, y, treeNode, opts) {
        if (bowireActiveSemanticsMenu) {
            bowireCloseSemanticsMenu();
        }
        var menu = document.createElement('div');
        menu.className = 'bowire-semantics-menu';
        menu.setAttribute('role', 'menu');
        menu.tabIndex = -1;
        menu.style.position = 'fixed';
        menu.style.left = x + 'px';
        menu.style.top = y + 'px';
        menu.style.zIndex = '10000';

        bowirePopulateSemanticsMenu(menu, treeNode, opts);
        document.body.appendChild(menu);

        // Clamp into the viewport after layout — same shift the browser
        // applies to its own native menus. Reading offsetWidth /
        // offsetHeight after appendChild forces a layout once; the menu
        // is small so the cost is negligible.
        var vw = window.innerWidth, vh = window.innerHeight;
        var rect = menu.getBoundingClientRect();
        if (rect.right > vw) {
            menu.style.left = Math.max(8, vw - rect.width - 8) + 'px';
        }
        if (rect.bottom > vh) {
            menu.style.top = Math.max(8, vh - rect.height - 8) + 'px';
        }

        function onKeyDown(e) {
            if (e.key === 'Escape') {
                e.preventDefault();
                bowireCloseSemanticsMenu();
            }
        }
        function onDocClick(e) {
            if (!menu.contains(e.target)) bowireCloseSemanticsMenu();
        }
        document.addEventListener('keydown', onKeyDown);
        // The mousedown variant runs before the menu's own click handler
        // installs the "close on action" hook, so we listen on click to
        // give the menu's own buttons a chance to fire first.
        setTimeout(function () { document.addEventListener('click', onDocClick); }, 0);

        bowireActiveSemanticsMenu = {
            element: menu,
            close: function () {
                document.removeEventListener('keydown', onKeyDown);
                document.removeEventListener('click', onDocClick);
                if (menu.parentNode) menu.parentNode.removeChild(menu);
            }
        };
        menu.focus();
    }

    function bowireCloseSemanticsMenu() {
        if (!bowireActiveSemanticsMenu) return;
        var m = bowireActiveSemanticsMenu;
        bowireActiveSemanticsMenu = null;
        m.close();
    }

    /**
     * Build the menu's inner DOM. Pulled out so the structure can be
     * unit-tested without spinning up an actual right-click event.
     */
    function bowirePopulateSemanticsMenu(menu, treeNode, opts) {
        var headerText = bowireSemanticsHeaderText(opts);
        var header = document.createElement('div');
        header.className = 'bowire-semantics-menu-header';
        header.textContent = headerText;
        menu.appendChild(header);

        // Accept — only meaningful when something below the user tier
        // already claims the path (auto/plugin). For an unannotated
        // field there's nothing to "accept" — the user has to pick a
        // kind via Reinterpret instead.
        if (opts.currentTag
            && opts.currentTag !== 'none'
            && (opts.currentSource === 'auto' || opts.currentSource === 'plugin')) {
            menu.appendChild(bowireMenuItem('Accept', function () {
                bowireWriteAnnotation(opts, opts.currentTag).then(function () {
                    bowireMaybeSuggestCompanion(opts, opts.currentTag, treeNode);
                });
            }));
        }

        // Reinterpret as ▸ submenu (built-ins + Custom).
        var reinterpret = bowireSubmenuItem('Reinterpret as ▸');
        for (var i = 0; i < bowireBuiltInSemanticTags.length; i++) {
            (function (kind) {
                reinterpret.submenu.appendChild(bowireMenuItem(kind, function () {
                    bowireWriteAnnotation(opts, kind).then(function () {
                        bowireMaybeSuggestCompanion(opts, kind, treeNode);
                    });
                }));
            })(bowireBuiltInSemanticTags[i]);
        }
        var divider = document.createElement('div');
        divider.className = 'bowire-semantics-menu-divider';
        reinterpret.submenu.appendChild(divider);
        reinterpret.submenu.appendChild(bowireCustomKindRow(opts));
        menu.appendChild(reinterpret.element);

        // Suppress — writes the explicit 'none' tag at the chosen tier.
        menu.appendChild(bowireMenuItem('Suppress', function () {
            bowireWriteAnnotation(opts, 'none');
        }));

        menu.appendChild(bowireDivider());

        // Fuzz this field ▸ — Tier-2 security-testing entry-point.
        // Submenu lists the four built-in payload categories the
        // /api/security/fuzz endpoint accepts (sqli / xss / pathtrav /
        // cmdinj). Choosing one POSTs to the endpoint with the current
        // request body + the right-clicked field's JSONPath, then
        // opens the result panel below.
        var fuzz = bowireSubmenuItem('Fuzz this field ▸');
        var fuzzCategories = [
            ['sqli', 'SQL injection'],
            ['xss', 'Cross-site scripting'],
            ['pathtrav', 'Path traversal'],
            ['cmdinj', 'Command injection'],
        ];
        for (var fi = 0; fi < fuzzCategories.length; fi++) {
            (function (cat, label) {
                fuzz.submenu.appendChild(bowireMenuItem(label, function () {
                    bowireCloseSemanticsMenu();
                    bowireRunFuzzAgainstField(opts, cat);
                }));
            })(fuzzCategories[fi][0], fuzzCategories[fi][1]);
        }
        menu.appendChild(fuzz.element);

        menu.appendChild(bowireDivider());

        // Persist for ▸ submenu. The label echoes the current sticky
        // default so the user sees what "Enter" would do if there
        // were a hot-key path.
        var persistDefault = bowireSemanticsPersistDefault();
        var persistLabel = 'Persist for ▸ (' + persistDefault + ')';
        var persist = bowireSubmenuItem(persistLabel);
        ['session', 'user', 'project'].forEach(function (tier) {
            var label = tier.charAt(0).toUpperCase() + tier.slice(1)
                + (tier === persistDefault ? '  ✓' : '');
            persist.submenu.appendChild(bowireMenuItem(label, function () {
                bowireSetSemanticsPersistDefault(tier);
                // Re-issue the current effective semantic (or the
                // visible tag if no edit happened yet) into the chosen
                // tier. Skip the re-write if there's no semantic to
                // commit — the tier choice is sticky on its own.
                var sem = opts.currentTag || null;
                if (sem && sem !== 'none') {
                    bowireWriteAnnotationWithScope(opts, sem, tier);
                }
            }));
        });
        menu.appendChild(persist.element);

        // Scope ▸ submenu — three radio-style options. The choice
        // sticks on the opts object for the lifetime of this menu;
        // subsequent click on Accept / Reinterpret / Suppress reads
        // the value back.
        if (opts.scope === undefined) opts.scope = 'this-discriminator';
        var scope = bowireSubmenuItem('Scope ▸');
        var scopeOptions = [
            ['this-discriminator', 'Just ' + bowireFormatDiscriminator(opts) + ' in this method'],
            ['this-method-where-path-exists', 'All message types in this method where this path exists'],
            ['this-method-all-matching-path-names', 'All message types in this method, all matching path names']
        ];
        for (var s = 0; s < scopeOptions.length; s++) {
            (function (value, label) {
                var item = bowireMenuItem(
                    (opts.scope === value ? '● ' : '○ ') + label,
                    function () {
                        opts.scope = value;
                        // Refresh the menu so the radio dots update.
                        var newMenu = document.createElement('div');
                        newMenu.className = menu.className;
                        bowirePopulateSemanticsMenu(newMenu, treeNode, opts);
                        menu.replaceChildren.apply(menu, Array.prototype.slice.call(newMenu.childNodes));
                    });
                scope.submenu.appendChild(item);
            })(scopeOptions[s][0], scopeOptions[s][1]);
        }
        menu.appendChild(scope.element);
    }

    function bowireSemanticsHeaderText(opts) {
        var src = opts.currentSource || 'none';
        if (!opts.currentTag || opts.currentTag === 'none') {
            return opts.jsonPath + ' — (no semantic detected)';
        }
        var sourceLabel;
        switch (src) {
            case 'auto': sourceLabel = 'Auto-detected'; break;
            case 'plugin': sourceLabel = 'Plugin'; break;
            case 'user': sourceLabel = 'User'; break;
            default: sourceLabel = 'Detected';
        }
        return opts.jsonPath + ' — ' + sourceLabel + ': ' + opts.currentTag;
    }

    function bowireFormatDiscriminator(opts) {
        var d = opts.discriminator;
        if (!d || d === '*') return 'this method';
        return '`' + d + '`';
    }

    function bowireMenuItem(label, onSelect) {
        var item = document.createElement('button');
        item.type = 'button';
        item.className = 'bowire-semantics-menu-item';
        item.setAttribute('role', 'menuitem');
        item.textContent = label;
        item.addEventListener('click', function (e) {
            e.preventDefault();
            e.stopPropagation();
            try { onSelect(); } catch (err) { console.error('[bowire-semantics] menu action threw', err); }
            bowireCloseSemanticsMenu();
        });
        return item;
    }

    function bowireSubmenuItem(label) {
        var wrap = document.createElement('div');
        wrap.className = 'bowire-semantics-menu-submenu';
        var trigger = document.createElement('button');
        trigger.type = 'button';
        trigger.className = 'bowire-semantics-menu-item bowire-semantics-menu-submenu-trigger';
        trigger.textContent = label;
        wrap.appendChild(trigger);
        var submenu = document.createElement('div');
        submenu.className = 'bowire-semantics-menu-submenu-panel';
        wrap.appendChild(submenu);
        return { element: wrap, submenu: submenu };
    }

    function bowireDivider() {
        var d = document.createElement('div');
        d.className = 'bowire-semantics-menu-divider';
        return d;
    }

    function bowireCustomKindRow(opts) {
        var row = document.createElement('div');
        row.className = 'bowire-semantics-menu-custom-row';
        var input = document.createElement('input');
        input.type = 'text';
        input.className = 'bowire-semantics-menu-custom-input';
        input.placeholder = 'Custom kind…';
        input.addEventListener('keydown', function (e) {
            if (e.key === 'Enter') {
                e.preventDefault();
                e.stopPropagation();
                var v = (input.value || '').trim();
                if (v) {
                    bowireWriteAnnotation(opts, v);
                }
            }
        });
        // Click-inside-input must not bubble up to the document-level
        // "close on outside click" listener.
        input.addEventListener('click', function (e) { e.stopPropagation(); });
        row.appendChild(input);
        return row;
    }

    /**
     * Issue the write to the C# endpoint. Picks the tier from the
     * persistence-default localStorage key. Returns a Promise so the
     * caller can chain the companion-field suggestion after a
     * successful write.
     */
    function bowireWriteAnnotation(opts, semantic) {
        var tier = bowireSemanticsPersistDefault();
        return bowireWriteAnnotationWithScope(opts, semantic, tier);
    }

    function bowireWriteAnnotationWithScope(opts, semantic, tier) {
        var keys = bowireExpandScopeKeys(opts);
        var writes = [];
        for (var i = 0; i < keys.length; i++) {
            writes.push(bowirePostAnnotation({
                service: opts.service,
                method: opts.method,
                messageType: keys[i].messageType,
                jsonPath: keys[i].jsonPath,
                semantic: semantic,
                scope: tier
            }));
        }
        return Promise.all(writes).then(function () {
            bowireDispatchSemanticsChanged(opts.service, opts.method);
            try { toast('Semantic ' + semantic + ' saved (' + tier + ')', 'success'); }
            catch { /* toast may not exist in test contexts */ }
        }).catch(function (err) {
            console.error('[bowire-semantics] write failed', err);
            try { toast('Failed to save semantic annotation', 'error'); }
            catch { /* swallow */ }
        });
    }

    /**
     * Translate `opts.scope` into the (messageType, jsonPath) pairs the
     * write should land on. "this-method-where-path-exists" and
     * "this-method-all-matching-path-names" are bounded to discriminator
     * values the workbench has already seen on this method — the API
     * never enumerates the entire schema universe, so the call set stays
     * proportional to the number of discriminator values the user has
     * actually streamed through (typically 1–5, not quadratic).
     */
    function bowireExpandScopeKeys(opts) {
        var current = {
            messageType: opts.discriminator || '*',
            jsonPath: opts.jsonPath
        };
        if (!opts.scope || opts.scope === 'this-discriminator') return [current];

        var seen = bowireKnownDiscriminatorsForMethod(opts.service, opts.method);
        // Always include the active discriminator even if the
        // workbench's seen-set didn't capture it yet.
        if (seen.indexOf(current.messageType) < 0) seen.push(current.messageType);
        if (seen.length === 0) return [current];

        if (opts.scope === 'this-method-where-path-exists') {
            var out = [];
            for (var i = 0; i < seen.length; i++) {
                out.push({ messageType: seen[i], jsonPath: opts.jsonPath });
            }
            return out;
        }
        if (opts.scope === 'this-method-all-matching-path-names') {
            // Match by leaf name only — $.position.lat and $.entity.lat
            // both share leaf 'lat'. The workbench-side path catalogue
            // can be richer later; for v1.3 we only have the active
            // path catalogued, so the leaf-match degenerates to "same
            // leaf across every discriminator value." Phase 5 will
            // extend the catalogue as recordings carry more shapes.
            var leaf = bowireSemanticsLeafName(opts.jsonPath);
            var paths = bowireKnownPathsForMethodMatchingLeaf(opts.service, opts.method, leaf);
            if (paths.length === 0) paths = [opts.jsonPath];
            var resultSet = {};
            var result = [];
            for (var d = 0; d < seen.length; d++) {
                for (var p = 0; p < paths.length; p++) {
                    var key = seen[d] + '::' + paths[p];
                    if (resultSet[key]) continue;
                    resultSet[key] = 1;
                    result.push({ messageType: seen[d], jsonPath: paths[p] });
                }
            }
            return result;
        }
        return [current];
    }

    function bowireSemanticsLeafName(jsonPath) {
        if (typeof jsonPath !== 'string') return '';
        var idx = jsonPath.lastIndexOf('.');
        return idx >= 0 ? jsonPath.substring(idx + 1) : jsonPath;
    }

    /**
     * Lightweight, workbench-local discriminator catalogue. The
     * extensions-framework records every (service, method,
     * discriminator) tuple it has seen via
     * `bowireRecordSeenDiscriminator(...)` (called from the SSE
     * frame-handler in extensions.js). This function looks the
     * recorded set up — empty array when none seen, in which case the
     * scope expander degrades to single-key writes.
     */
    function bowireKnownDiscriminatorsForMethod(service, method) {
        if (!window.__bowireSemanticsCatalogue) return [];
        var entry = window.__bowireSemanticsCatalogue.discriminators[service + '::' + method];
        if (!entry) return [];
        return Object.keys(entry);
    }

    function bowireKnownPathsForMethodMatchingLeaf(service, method, leaf) {
        if (!window.__bowireSemanticsCatalogue) return [];
        var entry = window.__bowireSemanticsCatalogue.paths[service + '::' + method];
        if (!entry) return [];
        var out = [];
        for (var p in entry) {
            if (!Object.prototype.hasOwnProperty.call(entry, p)) continue;
            if (bowireSemanticsLeafName(p) === leaf) out.push(p);
        }
        return out;
    }

    function bowirePostAnnotation(body) {
        var url = config.prefix + '/api/semantics/annotation';
        return fetch(url, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json', 'Accept': 'application/json' },
            body: JSON.stringify(body)
        }).then(function (r) {
            if (!r.ok) throw new Error('HTTP ' + r.status);
            return r.json();
        });
    }

    function bowireDispatchSemanticsChanged(service, method) {
        try {
            document.dispatchEvent(new CustomEvent('bowire:semantics-changed', {
                detail: { service: service, method: method }
            }));
        } catch (e) {
            console.error('[bowire-semantics] dispatch', e);
        }
    }

    // ---------------------------------------------------------------
    // Companion-field suggestion
    // ---------------------------------------------------------------

    /**
     * After a successful write, look for sibling numeric fields that
     * would make a natural companion (longitude for a freshly-marked
     * latitude, mime-type for image bytes, etc.). Renders the
     * suggestion as a one-off inline toast carrying the candidate
     * field-buttons — clicking any candidate fires the matching
     * companion write under the same tier as the original.
     */
    function bowireMaybeSuggestCompanion(opts, sourceKind, treeNode) {
        var companions = bowireSemanticCompanions[sourceKind];
        if (!companions || companions.length === 0) return;

        // Skip when a companion is already annotated at the same
        // parent path — no point asking when the framework already
        // has the pair.
        var existing = bowireExistingSiblingAnnotations(opts, companions);
        if (existing.allCovered) return;

        var candidates = bowireFindSiblingNumericFields(treeNode, opts.jsonPath);
        if (candidates.length === 0) return;

        // Top-2 candidates per the ADR — keep the toast tight.
        var top2 = candidates.slice(0, 2);
        bowireRenderCompanionToast(opts, sourceKind, existing.missing, top2);
    }

    /**
     * Walk the response-tree DOM rooted at `treeNode` to find sibling
     * leaf fields under the same parent JSONPath. Looks for the
     * `data-json-path` attributes that `renderJsonTree` emits.
     */
    function bowireFindSiblingNumericFields(treeNode, sourceJsonPath) {
        var root = treeNode.closest ? treeNode.closest('.bowire-json-tree') : null;
        if (!root) return [];
        var parent = bowireSemanticsParentPath(sourceJsonPath);
        var picks = root.querySelectorAll('[data-json-path]');
        var seen = {};
        var out = [];
        for (var i = 0; i < picks.length; i++) {
            var raw = picks[i].getAttribute('data-json-path') || '';
            if (!raw) continue;
            if (raw === sourceJsonPath) continue;
            if (bowireSemanticsParentPath(raw) !== parent) continue;
            if (seen[raw]) continue;
            seen[raw] = 1;
            // Only suggest leaves that look numeric / value-shaped. The
            // tree emits class names like bowire-json-number /
            // bowire-json-string on the value span — sniff for those.
            if (picks[i].classList && picks[i].classList.contains('bowire-json-tree-label')) continue;
            out.push({
                jsonPath: raw,
                label: bowireSemanticsLeafName(raw)
            });
        }
        return out;
    }

    function bowireSemanticsParentPath(jsonPath) {
        if (typeof jsonPath !== 'string') return '';
        var idx = jsonPath.lastIndexOf('.');
        return idx > 0 ? jsonPath.substring(0, idx) : (jsonPath || '');
    }

    /**
     * Determine which of the candidate companions are already
     * annotated at the same parent path — uses the cached effective
     * schema the workbench loaded for this (service, method). The
     * extensions framework exposes the cache via
     * `__bowireExtFramework`; we re-use it rather than re-fetching.
     */
    function bowireExistingSiblingAnnotations(opts, companions) {
        var cache = (window.__bowireExtFramework
            && typeof window.__bowireExtFramework.effectiveCacheFor === 'function')
            ? window.__bowireExtFramework.effectiveCacheFor(opts.service, opts.method)
            : null;
        if (!cache) return { allCovered: false, missing: companions.slice() };
        var parent = bowireSemanticsParentPath(opts.jsonPath);
        var covered = {};
        for (var i = 0; i < cache.length; i++) {
            var ann = cache[i];
            if (bowireSemanticsParentPath(ann.jsonPath) !== parent) continue;
            if (companions.indexOf(ann.semantic) >= 0) covered[ann.semantic] = 1;
        }
        var missing = [];
        for (var j = 0; j < companions.length; j++) {
            if (!covered[companions[j]]) missing.push(companions[j]);
        }
        return { allCovered: missing.length === 0, missing: missing };
    }

    function bowireRenderCompanionToast(opts, sourceKind, missingKinds, candidates) {
        // Use the regular toast() helper from helpers.js. We append
        // custom action buttons after the message text.
        if (typeof toast !== 'function') return;
        var missingLabel = missingKinds.join(' / ');
        var t = toast('Pair with a ' + missingLabel + '?', 'info', { duration: 8000 });
        if (!t || !t.appendChild) return;
        var actions = document.createElement('div');
        actions.className = 'bowire-semantics-companion-actions';
        for (var i = 0; i < candidates.length; i++) {
            (function (cand) {
                var btn = document.createElement('button');
                btn.type = 'button';
                btn.className = 'bowire-toast-undo';
                btn.textContent = cand.label;
                btn.addEventListener('click', function () {
                    // For multi-companion (ecef → y+z), tie the click to
                    // the first missing kind; the user can repeat for
                    // the second from another right-click.
                    var kind = missingKinds[0];
                    bowireWriteAnnotation({
                        service: opts.service,
                        method: opts.method,
                        discriminator: opts.discriminator,
                        jsonPath: cand.jsonPath,
                        currentTag: null,
                        currentSource: 'none',
                        scope: 'this-discriminator'
                    }, kind);
                });
                actions.appendChild(btn);
            })(candidates[i]);
        }
        var none = document.createElement('button');
        none.type = 'button';
        none.className = 'bowire-toast-undo';
        none.textContent = 'None';
        actions.appendChild(none);
        t.appendChild(actions);
    }

    // ---------------------------------------------------------------
    // Workbench-side discriminator + path catalogue
    // ---------------------------------------------------------------

    /**
     * Module-local catalogue of (service, method, discriminator) and
     * (service, method, jsonPath) tuples the workbench has seen.
     * Populated by extensions.js's frame dispatcher. Used by the
     * scope picker to bound "all message types" / "all matching path
     * names" without forcing a quadratic call set.
     */
    window.__bowireSemanticsCatalogue = window.__bowireSemanticsCatalogue || {
        discriminators: {}, // "service::method" → { discValue: 1 }
        paths: {}           // "service::method" → { jsonPath: 1 }
    };

    function bowireRecordSeenDiscriminator(service, method, discriminator) {
        if (!service || !method) return;
        var k = service + '::' + method;
        var cat = window.__bowireSemanticsCatalogue;
        if (!cat.discriminators[k]) cat.discriminators[k] = {};
        var v = discriminator || '*';
        cat.discriminators[k][v] = 1;
    }

    function bowireRecordSeenJsonPath(service, method, jsonPath) {
        if (!service || !method || !jsonPath) return;
        var k = service + '::' + method;
        var cat = window.__bowireSemanticsCatalogue;
        if (!cat.paths[k]) cat.paths[k] = {};
        cat.paths[k][jsonPath] = 1;
    }

    // ---------------------------------------------------------------
    // Response-tree decorator — Phase 4 (D)
    // ---------------------------------------------------------------

    /**
     * Walk a freshly-rendered response tree, annotate each leaf with
     * a `bowire-semantics-badge` showing its current semantic kind +
     * source tier, and attach the right-click handler. Called
     * synchronously after `renderJsonTree(...)` lands in the DOM —
     * `output` is the container holding the tree.
     *
     * The function is idempotent: calling it twice on the same node
     * removes any previous badge spans before re-rendering. That
     * matters because render-main.js's morphdom diff can leave a
     * partially-decorated tree behind during fast re-renders.
     */
    function bowireDecorateResponseTreeForSemantics(output, service, method) {
        if (!output || !service || !method) return;
        // Clear any stale badges from a prior decoration pass.
        var stale = output.querySelectorAll('.bowire-semantics-badge');
        for (var s = 0; s < stale.length; s++) {
            if (stale[s].parentNode) stale[s].parentNode.removeChild(stale[s]);
        }
        // Cached effective schema if the extensions-framework already
        // loaded it; otherwise fetch fresh.
        var loader;
        if (window.__bowireExtFramework
            && typeof window.__bowireExtFramework.effectiveCacheFor === 'function'
            && window.__bowireExtFramework.effectiveCacheFor(service, method)) {
            loader = Promise.resolve({
                annotations: window.__bowireExtFramework.effectiveCacheFor(service, method)
            });
        } else if (window.__bowireExtFramework
            && typeof window.__bowireExtFramework.fetchEffective === 'function') {
            loader = window.__bowireExtFramework.fetchEffective(service, method);
        } else {
            loader = Promise.resolve({ annotations: [] });
        }

        loader.then(function (data) {
            var annotations = (data && data.annotations) || [];
            // Path convention bridge: annotations from
            // /api/semantics/effective use the JSONPath convention
            // ("$.lat", "$.position.lat") while renderJsonTree's
            // data-json-path attributes use the chain-variable
            // convention ("lat", "position.lat") because that's what
            // ${response.X} expects. Normalize the annotation side by
            // stripping the leading "$." so the lookup matches.
            var byPath = {};
            for (var i = 0; i < annotations.length; i++) {
                var key = annotations[i].jsonPath || '';
                if (key.indexOf('$.') === 0) key = key.slice(2);
                else if (key === '$') key = '';
                byPath[key] = annotations[i];
            }
            // Each label span carries data-json-path; that's our hook.
            var labels = output.querySelectorAll('.bowire-json-tree-label[data-json-path]');
            for (var j = 0; j < labels.length; j++) {
                var path = labels[j].getAttribute('data-json-path') || '';
                if (!path) continue;
                // Catalogue the seen path so the scope picker can offer
                // "all message types with this path" later.
                bowireRecordSeenJsonPath(service, method, path);
                var ann = byPath[path];
                var currentTag = ann ? ann.semantic : null;
                var currentSource = ann ? ann.source : 'none';
                // Append the badge after the label span.
                var badge = bowireMakeBadge(currentTag, currentSource);
                if (badge) {
                    labels[j].parentNode.insertBefore(
                        badge, labels[j].nextSibling);
                }
                // Right-click + click-on-badge → menu.
                var opts = {
                    service: service,
                    method: method,
                    discriminator: ann ? ann.messageType : '*',
                    jsonPath: path,
                    currentTag: currentTag,
                    currentSource: currentSource
                };
                bowireMountSemanticsContextMenu(labels[j], opts);
                if (badge) {
                    (function (anchor, badgeEl, mountOpts) {
                        badgeEl.addEventListener('click', function (e) {
                            e.preventDefault();
                            e.stopPropagation();
                            bowireOpenSemanticsMenu(e.clientX, e.clientY, anchor, mountOpts);
                        });
                    })(labels[j], badge, opts);
                }
            }
        }).catch(function (err) {
            console.error('[bowire-semantics] decorate failed', err);
        });
    }

    /**
     * Build the badge span for an annotation. Returns `null` for a
     * "no semantic detected" path — the leaf is still right-clickable
     * for the user to MARK it, but we don't litter unannotated leaves
     * with empty badges.
     */
    function bowireMakeBadge(currentTag, currentSource) {
        if (!currentTag || currentTag === 'none') return null;
        var badge = document.createElement('span');
        badge.className = 'bowire-semantics-badge bowire-semantics-badge-'
            + (currentSource || 'auto');
        badge.title = 'Click to refine — ' + currentTag + ' (' + currentSource + ')';
        badge.textContent = ' ' + currentTag + ' (' + currentSource + ')';
        return badge;
    }

    // ---------------------------------------------------------------
    // Fuzz this field — Tier-2 right-click integration.
    // Grabs the live workbench request shape (server URL, http verb /
    // path, current body, auth metadata) and POSTs to the
    // /api/security/fuzz endpoint; renders the per-payload result rows
    // in a floating panel anchored to the response pane.
    // ---------------------------------------------------------------

    function bowireRunFuzzAgainstField(opts, category) {
        if (typeof window.toast !== 'function' && typeof toast === 'undefined') {
            // Worst case: no toast helper available — fall through and
            // surface errors via console / panel header.
        }
        var notify = (typeof toast === 'function') ? toast : function (msg, _kind) { console.warn(msg); };

        // Resolve the live request body. The workbench keeps the
        // current request payload(s) in `requestMessages` (see
        // execute.js); the editor's textarea value mirrors it. For
        // unary methods the body is requestMessages[0]; streaming
        // methods are not fuzz-eligible in v1 (one body per probe).
        var body = '{}';
        try {
            if (typeof requestMessages !== 'undefined'
                && requestMessages
                && requestMessages.length > 0) {
                body = requestMessages[0] || '{}';
            } else {
                // Fallback: peek the visible editor textarea.
                var ed = document.querySelector('.bowire-editor, .bowire-message-editor');
                if (ed && ed.value) body = ed.value;
            }
        } catch { /* keep body = '{}' */ }

        // Validate the field actually exists in the body before we
        // ship the request — fuzzing a path that resolves to nothing
        // is just network noise. Use the same JSONPath subset the
        // backend FuzzExecutor walks.
        var parsed;
        try { parsed = JSON.parse(body); }
        catch {
            notify('Cannot fuzz: request body is not valid JSON. Edit the body, then retry.', 'error');
            return;
        }
        if (!bowireFuzzFieldExists(parsed, opts.jsonPath)) {
            notify('Cannot fuzz: field ' + opts.jsonPath + ' not found in the current request body. Right-click happened on a response field?', 'error');
            return;
        }

        // Resolve target URL — prefer the active workbench server URL,
        // fall through to the embedded host. serverUrls[0] is the
        // common case (one configured target); embedded mode resolves
        // via window.location.
        var target = '';
        try {
            if (typeof serverUrls !== 'undefined' && serverUrls && serverUrls.length > 0) {
                target = serverUrls[0];
            } else if (typeof getPrimaryServerUrl === 'function') {
                target = getPrimaryServerUrl();
            }
        } catch { /* fall through */ }
        if (!target) {
            // Last resort: same-origin loopback. Most embedded-mode
            // workbenches are pointed at their own host.
            target = window.location.origin;
        }

        // Method shape — Bowire's request envelope carries httpVerb /
        // httpPath on the protocol-resolved metadata for REST / OData
        // / GraphQL. Default to POST + the recorded path when present.
        var httpVerb = (typeof selectedMethod !== 'undefined' && selectedMethod && selectedMethod.httpMethod)
            ? selectedMethod.httpMethod : 'POST';
        var httpPath = (typeof selectedMethod !== 'undefined' && selectedMethod && selectedMethod.httpPath)
            ? selectedMethod.httpPath : '/';

        var panel = bowireOpenFuzzPanel(opts, category);
        bowireRenderFuzzPanelHeader(panel, opts, category, 'sending probes…');

        var prefix = (window.__BOWIRE_CONFIG__ && window.__BOWIRE_CONFIG__.prefix) || '';
        fetch(prefix + '/api/security/fuzz', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({
                target: target,
                httpVerb: httpVerb,
                httpPath: httpPath,
                body: body,
                field: opts.jsonPath,
                category: category,
                timeoutSeconds: 30
            })
        }).then(function (resp) {
            if (!resp.ok) {
                return resp.text().then(function (txt) { throw new Error(txt || ('HTTP ' + resp.status)); });
            }
            return resp.json();
        }).then(function (result) {
            bowireRenderFuzzPanelResults(panel, opts, category, target, result);
        }).catch(function (err) {
            bowireRenderFuzzPanelError(panel, err.message || String(err));
        });
    }

    /** Verify the JSONPath resolves in the given parsed body. */
    function bowireFuzzFieldExists(root, path) {
        if (!path || path === '$') return true;
        var trimmed = path;
        if (trimmed.indexOf('$.') === 0) trimmed = trimmed.substring(2);
        else if (trimmed.charAt(0) === '$') trimmed = trimmed.substring(1);
        var segments = trimmed.split('.').filter(Boolean);
        var cur = root;
        for (var i = 0; i < segments.length; i++) {
            if (cur === null || typeof cur !== 'object') return false;
            if (!Object.prototype.hasOwnProperty.call(cur, segments[i])) return false;
            cur = cur[segments[i]];
        }
        return true;
    }

    /** Open (or recycle) the fuzz-result panel anchored to the document. */
    function bowireOpenFuzzPanel(_opts, _category) {
        var existing = document.getElementById('bowire-fuzz-panel');
        if (existing) existing.remove();
        var panel = document.createElement('div');
        panel.id = 'bowire-fuzz-panel';
        panel.className = 'bowire-fuzz-panel';
        panel.setAttribute('role', 'dialog');
        panel.setAttribute('aria-label', 'Fuzz results');
        document.body.appendChild(panel);

        // Closer button — Esc + click-outside aren't wired here
        // because the panel is non-modal (operator should be able to
        // scroll the workbench while results render).
        var close = document.createElement('button');
        close.type = 'button';
        close.className = 'bowire-fuzz-panel-close';
        close.setAttribute('aria-label', 'Close fuzz panel');
        close.textContent = '×';
        close.addEventListener('click', function () { panel.remove(); });
        panel.appendChild(close);

        return panel;
    }

    function bowireRenderFuzzPanelHeader(panel, opts, category, statusLine) {
        var header = panel.querySelector('.bowire-fuzz-panel-header')
            || document.createElement('div');
        header.className = 'bowire-fuzz-panel-header';
        header.textContent = 'Fuzz ' + opts.jsonPath + ' · ' + category + ' — ' + statusLine;
        if (!header.parentNode) panel.appendChild(header);
    }

    function bowireRenderFuzzPanelError(panel, message) {
        var body = panel.querySelector('.bowire-fuzz-panel-body')
            || document.createElement('div');
        body.className = 'bowire-fuzz-panel-body';
        body.textContent = 'Error: ' + message;
        if (!body.parentNode) panel.appendChild(body);
    }

    function bowireRenderFuzzPanelResults(panel, opts, category, target, result) {
        var header = panel.querySelector('.bowire-fuzz-panel-header');
        var rows = result.rows || [];
        var vulnCount = rows.filter(function (r) { return r.outcome === 'Vulnerable'; }).length;
        if (header) {
            header.textContent = 'Fuzz ' + opts.jsonPath + ' · ' + category
                + ' — ' + rows.length + ' payload(s), ' + vulnCount + ' suspicious';
        }

        var body = panel.querySelector('.bowire-fuzz-panel-body')
            || document.createElement('div');
        body.className = 'bowire-fuzz-panel-body';
        body.replaceChildren();
        if (!body.parentNode) panel.appendChild(body);

        var targetLine = document.createElement('div');
        targetLine.className = 'bowire-fuzz-panel-target';
        targetLine.textContent = target;
        body.appendChild(targetLine);

        if (result.baselineStatus !== undefined && result.baselineStatus !== null) {
            var baseline = document.createElement('div');
            baseline.className = 'bowire-fuzz-panel-baseline';
            baseline.textContent = 'baseline: status=' + result.baselineStatus
                + ' body=' + (result.baselineBodySize || 0) + 'B latency=' + (result.baselineLatencyMs || 0) + 'ms';
            body.appendChild(baseline);
        }

        for (var i = 0; i < rows.length; i++) {
            var r = rows[i];
            var row = document.createElement('div');
            row.className = 'bowire-fuzz-panel-row bowire-fuzz-row-' + (r.outcome || 'safe').toLowerCase();
            var marker = document.createElement('span');
            marker.className = 'bowire-fuzz-panel-row-marker';
            marker.textContent = r.outcome === 'Vulnerable' ? '[VULN]'
                              : r.outcome === 'Error' ? '[err]'
                              : '[ok]';
            row.appendChild(marker);
            var payloadEl = document.createElement('code');
            payloadEl.className = 'bowire-fuzz-panel-row-payload';
            payloadEl.textContent = r.payload;
            row.appendChild(payloadEl);
            var detail = document.createElement('span');
            detail.className = 'bowire-fuzz-panel-row-detail';
            detail.textContent = r.detail || '';
            row.appendChild(detail);
            body.appendChild(row);
        }

        var note = document.createElement('div');
        note.className = 'bowire-fuzz-panel-note';
        note.textContent = 'Heuristics fire on response shape, not confirmation. Verify each finding by hand before reporting.';
        body.appendChild(note);
    }

    // ---------------------------------------------------------------
    // Public surface
    // ---------------------------------------------------------------

    window.__bowireSemanticsMenu = {
        mount: bowireMountSemanticsContextMenu,
        open: bowireOpenSemanticsMenu,
        close: bowireCloseSemanticsMenu,
        decorate: bowireDecorateResponseTreeForSemantics,
        recordSeenDiscriminator: bowireRecordSeenDiscriminator,
        recordSeenJsonPath: bowireRecordSeenJsonPath,
        // Test seams — pure helpers that don't touch DOM.
        _persistDefault: bowireSemanticsPersistDefault,
        _setPersistDefault: bowireSetSemanticsPersistDefault,
        _parentPath: bowireSemanticsParentPath,
        _leafName: bowireSemanticsLeafName,
        _expandScopeKeys: bowireExpandScopeKeys,
        _builtInTags: bowireBuiltInSemanticTags,
        _companions: bowireSemanticCompanions
    };
