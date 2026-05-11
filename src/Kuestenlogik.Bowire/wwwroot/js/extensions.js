// Copyright 2026 Küstenlogik · Apache-2.0
// ----------------------------------------------------------------------
// Frame-semantics extension framework — JS side (Phase 3).
//
// Implements the contract pinned in
// docs/architecture/frame-semantics-framework.md, "Extension framework"
// section. Built-in widgets (widgets/map.js) AND third-party bundles
// loaded later via /api/ui/extensions/{id}/{bundle} both call
// window.BowireExtensions.register({...}); the routing logic in
// mountWidgetsForMethod(...) picks viewers based on the semantic
// annotations returned by /api/semantics/effective.
//
// The v1.0 ctx surface is deliberately tight (frames$, theme, viewport,
// host). Anything beyond it is a v1.1+ additive concern; widgets that
// reach for a missing field should fall back gracefully.
// ----------------------------------------------------------------------

    /**
     * Internal registry of extensions, keyed by their primary `kind`. A
     * kind can have multiple registrations — the first one to register
     * is the default; later ones are still callable but won't auto-mount
     * unless the user explicitly picks them.
     */
    var bowireExtensions = {
        byKind: {},          // kind → array of registrations, insertion order
        byId: {},            // id → registration
        builtInIds: {}       // id → true; built-ins win tie-breaks
    };

    /**
     * Tag a registration as built-in. extensions.js runs inside the
     * core IIFE so any register() call from the concat'd bundle counts
     * as built-in. Third-party bundles loaded dynamically later don't
     * see this flag and default to "third-party".
     */
    function bowireMarkBuiltIn(id) {
        if (id) bowireExtensions.builtInIds[id] = true;
    }

    /**
     * Trivial semver compatibility check for the `bowireApi: '1.x'`
     * shape. The contract is intentionally narrow — only `Major.x` or
     * an exact `Major.Minor` string is supported in v1.3. Anything
     * unparseable returns true (we trust the extension author over a
     * brittle parser).
     */
    var BOWIRE_API_MAJOR = 1;
    function bowireIsApiCompatible(range) {
        if (typeof range !== 'string') return true;
        var m = range.match(/^(\d+)\.(x|\d+)/);
        if (!m) return true;
        return parseInt(m[1], 10) === BOWIRE_API_MAJOR;
    }

    /**
     * Validate the registration shape — fast-fail with a console
     * warning if a required field is missing rather than letting the
     * extension half-mount and produce confusing failures downstream.
     */
    function bowireValidateRegistration(reg) {
        if (!reg || typeof reg !== 'object') {
            throw new Error('BowireExtensions.register: payload must be an object');
        }
        if (typeof reg.id !== 'string' || !reg.id) {
            throw new Error('BowireExtensions.register: missing or empty id');
        }
        if (typeof reg.kind !== 'string' || !reg.kind) {
            throw new Error('BowireExtensions.register[' + reg.id + ']: missing kind');
        }
        if (!reg.viewer && !reg.editor) {
            throw new Error('BowireExtensions.register[' + reg.id + ']: must define viewer or editor');
        }
        if (reg.viewer && typeof reg.viewer.mount !== 'function') {
            throw new Error('BowireExtensions.register[' + reg.id + ']: viewer.mount must be a function');
        }
        if (reg.editor && typeof reg.editor.mount !== 'function') {
            throw new Error('BowireExtensions.register[' + reg.id + ']: editor.mount must be a function');
        }
    }

    /**
     * Register an extension. Idempotent in the soft sense — re-registering
     * the same `id` replaces the previous record, which lets a hot-reload
     * scenario work without losing state. Conflict on `kind` is allowed
     * (multiple extensions can target the same kind); the default
     * resolution rule for v1.3 is "built-in wins, otherwise first-to-
     * register wins". Phase 4 will add a per-binding override picker.
     */
    function bowireRegisterExtension(reg) {
        bowireValidateRegistration(reg);

        if (!bowireIsApiCompatible(reg.bowireApi)) {
            console.warn(
                '[bowire] skipping extension ' + reg.id
                + ' — declares bowireApi=' + reg.bowireApi
                + ' but workbench is v' + BOWIRE_API_MAJOR + '.x');
            return;
        }

        // Replace by-id (hot-reload). Drop the old kind binding first
        // so we don't leave a dangling pointer in byKind.
        if (bowireExtensions.byId[reg.id]) {
            var old = bowireExtensions.byId[reg.id];
            var bucket = bowireExtensions.byKind[old.kind] || [];
            for (var i = 0; i < bucket.length; i++) {
                if (bucket[i].id === old.id) { bucket.splice(i, 1); break; }
            }
        }

        bowireExtensions.byId[reg.id] = reg;
        if (!bowireExtensions.byKind[reg.kind]) {
            bowireExtensions.byKind[reg.kind] = [];
        }
        bowireExtensions.byKind[reg.kind].push(reg);
    }

    /**
     * Pick the preferred extension for a kind. Built-ins win against
     * third-party registrations; among extensions of the same class,
     * registration order is the tie-breaker. Phase 4 adds a user
     * override stored in bowire.schema-hints.json under a `viewers`
     * companion field — this function will consult it then.
     */
    function bowirePreferredExtension(kind) {
        var bucket = bowireExtensions.byKind[kind];
        if (!bucket || bucket.length === 0) return null;
        if (bucket.length === 1) return bucket[0];

        // Built-ins win.
        for (var i = 0; i < bucket.length; i++) {
            if (bowireExtensions.builtInIds[bucket[i].id]) return bucket[i];
        }
        return bucket[0];
    }

    /**
     * Public registration entry point. Made available on `window` so
     * dynamically-imported third-party bundles can reach it without
     * being inside the workbench IIFE. Built-in bundles call the
     * internal `bowireRegisterExtension` directly so the `builtInIds`
     * flag can be set.
     */
    window.BowireExtensions = {
        register: function (reg) {
            bowireRegisterExtension(reg);
        },
        // Inspection surface used by /tests + by hot-reload code.
        _internal: {
            byId: function (id) { return bowireExtensions.byId[id] || null; },
            byKind: function (kind) { return (bowireExtensions.byKind[kind] || []).slice(); }
        }
    };

    // ---------------------------------------------------------------
    // Pairing checks
    // ---------------------------------------------------------------

    /**
     * Parent path of a JSONPath expression. `$.position.lat` →
     * `$.position`; `$.lat` → `$`; non-rooted paths fall back to the
     * empty string so degenerate inputs don't crash the matcher.
     */
    function bowireParentPath(jsonPath) {
        if (typeof jsonPath !== 'string') return '';
        var idx = jsonPath.lastIndexOf('.');
        return idx > 0 ? jsonPath.substring(0, idx) : (jsonPath || '');
    }

    /**
     * Group annotations by parent path. Used by the pairing matcher to
     * find "all annotations at $.position", "all annotations at $.geo",
     * etc. — the framework's "same-parent" pairing scope means a
     * latitude + longitude paired widget mounts only when both live
     * under the same parent object.
     */
    function bowireGroupByParent(annotations) {
        var groups = {};
        for (var i = 0; i < annotations.length; i++) {
            var ann = annotations[i];
            var parent = bowireParentPath(ann.jsonPath);
            (groups[parent] = groups[parent] || []).push(ann);
        }
        return groups;
    }

    /**
     * Find pairing matches for the given extension against a flat
     * annotation list. Returns an array of `{ parentPath, kinds: { kind:
     * jsonPath } }` records — one per group that satisfies the pairing.
     * Returns the empty array when no group satisfies, in which case
     * the caller should render the "missing companion" hint.
     */
    function bowireFindPairingMatches(extension, annotations) {
        if (!extension.pairing || !extension.pairing.required) {
            // Single-kind extension with no pairing — every annotation
            // that matches the extension's kind triggers a mount on its
            // own parent group.
            var matches = [];
            for (var i = 0; i < annotations.length; i++) {
                if (annotations[i].semantic === extension.kind) {
                    matches.push({
                        parentPath: bowireParentPath(annotations[i].jsonPath),
                        kinds: makeKindMap([extension.kind], [annotations[i].jsonPath])
                    });
                }
            }
            return matches;
        }

        var required = extension.pairing.required;
        var scope = extension.pairing.scope || 'same-parent';

        if (scope === 'any') {
            // Each kind found anywhere counts — collect one jsonPath
            // per kind and emit a single match if all kinds are covered.
            var byKindAny = {};
            for (var k = 0; k < annotations.length; k++) {
                var a = annotations[k];
                if (required.indexOf(a.semantic) >= 0 && !byKindAny[a.semantic]) {
                    byKindAny[a.semantic] = a.jsonPath;
                }
            }
            for (var r = 0; r < required.length; r++) {
                if (!byKindAny[required[r]]) return [];
            }
            return [{ parentPath: '$', kinds: byKindAny }];
        }

        // Default scope: same-parent (treat same-object as the same for v1.3
        // — same-object is a strict subset; the ADR distinguishes them but
        // the only difference is array-element grouping which Phase 4 will
        // tighten).
        var groups = bowireGroupByParent(annotations);
        var pairMatches = [];
        for (var parent in groups) {
            if (!Object.prototype.hasOwnProperty.call(groups, parent)) continue;
            var byKind = {};
            var group = groups[parent];
            for (var g = 0; g < group.length; g++) {
                if (required.indexOf(group[g].semantic) >= 0) {
                    byKind[group[g].semantic] = group[g].jsonPath;
                }
            }
            var covered = true;
            for (var rr = 0; rr < required.length; rr++) {
                if (!byKind[required[rr]]) { covered = false; break; }
            }
            if (covered) {
                pairMatches.push({ parentPath: parent, kinds: byKind });
            }
        }
        return pairMatches;
    }

    function makeKindMap(kinds, paths) {
        var m = {};
        for (var i = 0; i < kinds.length; i++) m[kinds[i]] = paths[i];
        return m;
    }

    /**
     * Same as findPairingMatches but for the half-satisfied case — used
     * to render the "latitude marked, longitude needed" hint. Returns the
     * list of (parentPath, presentKinds, missingKinds) records.
     */
    function bowireFindPairingHints(extension, annotations) {
        if (!extension.pairing || !extension.pairing.required) return [];
        var required = extension.pairing.required;
        var groups = bowireGroupByParent(annotations);
        var hints = [];
        for (var parent in groups) {
            if (!Object.prototype.hasOwnProperty.call(groups, parent)) continue;
            var seen = {};
            var group = groups[parent];
            for (var g = 0; g < group.length; g++) {
                if (required.indexOf(group[g].semantic) >= 0) {
                    seen[group[g].semantic] = true;
                }
            }
            var present = [];
            var missing = [];
            for (var i = 0; i < required.length; i++) {
                if (seen[required[i]]) present.push(required[i]);
                else missing.push(required[i]);
            }
            if (present.length > 0 && missing.length > 0) {
                hints.push({ parentPath: parent, present: present, missing: missing });
            }
        }
        return hints;
    }

    // ---------------------------------------------------------------
    // ctx factory — the v1.0 surface frozen in the ADR.
    // ---------------------------------------------------------------

    /**
     * Construct an "async iterable of frame events" view of the live SSE
     * stream. The implementation pulls from a callback that the host
     * wires to whichever stream is currently active (sseSource for unary
     * server-streaming, duplexSseSource for channel-style methods). When
     * no stream is active, the iterable just doesn't yield — the widget
     * sits idle until frames start flowing.
     */
    function bowireMakeFramesAsyncIterable() {
        var buffered = [];
        var resolveNext = null;
        var closed = false;

        function push(frame) {
            if (closed) return;
            if (resolveNext) {
                var r = resolveNext;
                resolveNext = null;
                r({ value: frame, done: false });
            } else {
                buffered.push(frame);
            }
        }

        function close() {
            if (closed) return;
            closed = true;
            if (resolveNext) {
                var r = resolveNext;
                resolveNext = null;
                r({ value: undefined, done: true });
            }
        }

        var iter = {
            next: function () {
                if (buffered.length > 0) {
                    return Promise.resolve({ value: buffered.shift(), done: false });
                }
                if (closed) {
                    return Promise.resolve({ value: undefined, done: true });
                }
                return new Promise(function (resolve) { resolveNext = resolve; });
            },
            return: function () { close(); return Promise.resolve({ value: undefined, done: true }); }
        };
        iter[Symbol.asyncIterator] = function () { return iter; };
        return { iterator: iter, push: push, close: close };
    }

    /**
     * Build a viewer ctx for a specific (extension, pairing-match,
     * stream-subscription) tuple. Each mount gets its own ctx so
     * unsubscribe / cleanup happens on the right scope when the widget
     * is unmounted.
     */
    function bowireMakeViewerCtx(opts) {
        var framesPipe = bowireMakeFramesAsyncIterable();

        // Subscribe to stream-message events fired by execute.js / api.js.
        // The handler is installed once per ctx; the widget receives
        // every frame-shaped event the workbench produces, plus the
        // pairing's resolved JSON paths as `interpretations`.
        var streamHandler = function (evt) {
            var detail = evt && evt.detail;
            if (!detail) return;
            framesPipe.push(detail);
        };
        document.addEventListener('bowire:stream-message', streamHandler);

        // Viewport: a minimal resize-observer-backed object. on('resize')
        // delivers width + height; the widget bursts out a re-layout on
        // demand. The contract pins on(event, cb) → unsubscribe, so we
        // return the un-listen lambda directly.
        var viewportListeners = [];
        function fireViewport(event, payload) {
            for (var i = 0; i < viewportListeners.length; i++) {
                var l = viewportListeners[i];
                if (l.event === event) {
                    try { l.cb(payload); } catch (e) { console.error('[bowire-ext]', e); }
                }
            }
        }
        var ro = null;
        if (typeof ResizeObserver === 'function' && opts.container) {
            ro = new ResizeObserver(function (entries) {
                if (!entries || entries.length === 0) return;
                var r = entries[0].contentRect;
                fireViewport('resize', { width: r.width, height: r.height });
            });
            ro.observe(opts.container);
        }

        return {
            ctx: {
                frames$: framesPipe.iterator,
                interpretations: opts.kinds || {},
                discriminator: opts.discriminator || '*',
                theme: bowireExtensionTheme(),
                viewport: {
                    get width() { return opts.container ? opts.container.clientWidth : 0; },
                    get height() { return opts.container ? opts.container.clientHeight : 0; },
                    on: function (event, cb) {
                        viewportListeners.push({ event: event, cb: cb });
                        return function () {
                            for (var i = 0; i < viewportListeners.length; i++) {
                                if (viewportListeners[i].cb === cb) {
                                    viewportListeners.splice(i, 1);
                                    return;
                                }
                            }
                        };
                    }
                },
                host: {
                    subscribeSse: function (url) {
                        // Returns a same-shape async-iterable backed by an
                        // EventSource. Auto-closes on cleanup.
                        var pipe = bowireMakeFramesAsyncIterable();
                        var es = new EventSource(url);
                        es.onmessage = function (e) {
                            try { pipe.push(JSON.parse(e.data)); }
                            catch { pipe.push(e.data); }
                        };
                        es.onerror = function () { pipe.close(); };
                        var closeFn = function () { try { es.close(); } catch {} pipe.close(); };
                        pipe.iterator.return = function () { closeFn(); return Promise.resolve({ done: true }); };
                        return pipe.iterator;
                    },
                    fetch: function (url, init) {
                        // Same-origin fetch through the workbench — no
                        // additional auth wiring in v1.0 (the host
                        // already attaches cookies on same-origin).
                        return fetch(url, init);
                    }
                }
            },
            cleanup: function () {
                document.removeEventListener('bowire:stream-message', streamHandler);
                if (ro) { try { ro.disconnect(); } catch {} }
                framesPipe.close();
            }
        };
    }

    /**
     * Extract the active theme info for ctx.theme. Mirrors the
     * data-theme attribute the rest of the workbench reads.
     */
    function bowireExtensionTheme() {
        var mode = document.documentElement.getAttribute('data-theme') === 'light' ? 'light' : 'dark';
        return {
            mode: mode,
            accent: mode === 'light' ? '#4f46e5' : '#6366f1',
            font: getComputedStyle(document.body).fontFamily || 'system-ui, sans-serif'
        };
    }

    // ---------------------------------------------------------------
    // Mount decision
    // ---------------------------------------------------------------

    /**
     * Cache for /api/semantics/effective responses. Keyed by
     * `${service}::${method}` — short-lived, cleared whenever the user
     * navigates to a new method tab. The framework re-fetches when the
     * cache misses; refetching during streaming is intentionally OFF —
     * the resolver is stable per binding by ADR design.
     */
    var bowireEffectiveCache = {};

    function bowireEffectiveCacheKey(serviceId, methodId) {
        return serviceId + '::' + methodId;
    }

    function bowireFetchEffective(serviceId, methodId) {
        var key = bowireEffectiveCacheKey(serviceId, methodId);
        if (bowireEffectiveCache[key]) return Promise.resolve(bowireEffectiveCache[key]);
        var url = config.prefix + '/api/semantics/effective?service='
            + encodeURIComponent(serviceId)
            + '&method=' + encodeURIComponent(methodId);
        return fetch(url, { headers: { 'Accept': 'application/json' } })
            .then(function (r) { return r.ok ? r.json() : { annotations: [] }; })
            .then(function (data) {
                bowireEffectiveCache[key] = data;
                return data;
            })
            .catch(function () { return { annotations: [] }; });
    }

    /**
     * Forget cached effective-schema entries — Phase-4's right-click
     * persistence path will call this when a user-supplied annotation
     * changes the resolution. v1.3 has no UI for that yet but the
     * test code uses it.
     */
    function bowireClearEffectiveCache() {
        bowireEffectiveCache = {};
    }

    /**
     * Public entry point used by render-main.js when the response pane
     * for a (service, method) tab is being mounted. Fetches the
     * effective annotations, walks every registered extension, and
     * mounts those whose pairing requirements are satisfied as tabs in
     * `paneContainer`. Returns a cleanup function the caller invokes
     * before re-mounting (tab switch, method change).
     */
    function bowireMountWidgetsForMethod(serviceId, methodId, paneContainer) {
        var cleanups = [];
        function unmountAll() {
            for (var i = 0; i < cleanups.length; i++) {
                try { cleanups[i](); } catch (e) { console.error('[bowire-ext]', e); }
            }
            cleanups = [];
        }

        bowireFetchEffective(serviceId, methodId).then(function (data) {
            var annotations = (data && data.annotations) || [];
            if (annotations.length === 0) return;

            // Build a unique list of kinds present in the response.
            var kindsSeen = {};
            for (var i = 0; i < annotations.length; i++) {
                kindsSeen[annotations[i].semantic] = true;
            }

            // For each registered extension whose primary kind shows up
            // (or whose pairing.required[0] shows up), find pairing
            // matches and mount one tab per match.
            for (var id in bowireExtensions.byId) {
                if (!Object.prototype.hasOwnProperty.call(bowireExtensions.byId, id)) continue;
                var ext = bowireExtensions.byId[id];

                // Skip third-party extensions when a built-in claims
                // the same kind (built-ins-win tie-break rule).
                var preferred = bowirePreferredExtension(ext.kind);
                if (preferred && preferred.id !== ext.id) continue;

                if (!kindsSeen[ext.kind]
                    && !(ext.pairing
                         && ext.pairing.required
                         && ext.pairing.required.some(function (k) { return kindsSeen[k]; }))) {
                    continue;
                }

                var matches = bowireFindPairingMatches(ext, annotations);
                if (matches.length === 0) {
                    // No pairing — render the hint(s).
                    var hints = bowireFindPairingHints(ext, annotations);
                    if (hints.length > 0 && ext.viewer) {
                        var hintEl = bowireRenderPairingHint(ext, hints);
                        paneContainer.appendChild(hintEl);
                        cleanups.push(function () {
                            if (hintEl.parentNode) hintEl.parentNode.removeChild(hintEl);
                        });
                    }
                    continue;
                }

                for (var m = 0; m < matches.length; m++) {
                    var match = matches[m];
                    if (!ext.viewer) continue;
                    var slot = document.createElement('div');
                    slot.className = 'bowire-ext-mount bowire-ext-mount-' + ext.kind.replace(/\./g, '-');
                    slot.dataset.extensionId = ext.id;
                    slot.dataset.parentPath = match.parentPath;
                    paneContainer.appendChild(slot);

                    var ctxBundle = bowireMakeViewerCtx({
                        container: slot,
                        kinds: match.kinds,
                        discriminator: '*'
                    });

                    var unmount;
                    try {
                        unmount = ext.viewer.mount(slot, ctxBundle.ctx);
                    } catch (e) {
                        console.error('[bowire-ext] viewer.mount threw for ' + ext.id, e);
                        unmount = null;
                    }

                    cleanups.push((function (slot, ctxBundle, unmount) {
                        return function () {
                            try { if (typeof unmount === 'function') unmount(); }
                            catch (e) { console.error('[bowire-ext]', e); }
                            ctxBundle.cleanup();
                            if (slot.parentNode) slot.parentNode.removeChild(slot);
                        };
                    })(slot, ctxBundle, unmount));
                }
            }
        });

        return unmountAll;
    }

    function bowireRenderPairingHint(ext, hints) {
        var div = document.createElement('div');
        div.className = 'bowire-ext-pairing-hint';
        var label = ext.viewer && ext.viewer.label ? ext.viewer.label : ext.id;
        var lines = [];
        for (var i = 0; i < hints.length; i++) {
            var h = hints[i];
            lines.push(label + ': '
                + h.present.join(', ') + ' marked at ' + h.parentPath
                + ', ' + h.missing.join(', ') + ' needed.');
        }
        div.textContent = lines.join(' ');
        return div;
    }

    /**
     * Bridge from execute.js / api.js / protocols.js — they call this
     * whenever a stream frame arrives so the framework can dispatch
     * the frame to mounted viewers. Implementation is a simple custom
     * event on document; each viewer's ctx.frames$ async iterator picks
     * it up via the document-level listener installed in
     * bowireMakeViewerCtx.
     */
    function bowireDispatchStreamMessage(detail) {
        try {
            document.dispatchEvent(new CustomEvent('bowire:stream-message', { detail: detail }));
        } catch (e) {
            console.error('[bowire-ext] dispatchStreamMessage', e);
        }
    }

    // Public-from-the-IIFE handles used by other fragments — kept on
    // the closure-local namespace so the existing window contract stays
    // minimal (just `BowireExtensions`).
    window.__bowireExtFramework = {
        markBuiltIn: bowireMarkBuiltIn,
        register: bowireRegisterExtension,
        mountWidgetsForMethod: bowireMountWidgetsForMethod,
        dispatchStreamMessage: bowireDispatchStreamMessage,
        clearEffectiveCache: bowireClearEffectiveCache,
        // Test seams
        _findPairingMatches: bowireFindPairingMatches,
        _findPairingHints: bowireFindPairingHints,
        _parentPath: bowireParentPath
    };
