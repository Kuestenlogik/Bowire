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
// The v1.0 ctx surface is deliberately tight (frames$, selection$,
// theme, viewport, host). Anything beyond it is a v1.1+ additive
// concern; widgets that reach for a missing field should fall back
// gracefully.
//
// selection$ — added in Phase 3.1 — is a snapshot stream (not a
// delta stream) of `{ selectedFrameIds }` events bridged from the
// `bowire:frames-selection-changed` custom event the workbench
// dispatches when the user clicks frames in the Streaming-Frames pane.
// "Snapshot" means a viewer doesn't have to accumulate state to know
// what's selected — every yielded value is the complete current
// selection. A widget that joins mid-stream gets the current snapshot
// on its first `await` and stays in sync from there.
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
     *
     * Phase 3.2 adds the per-block `selectionMode` capability — a
     * `'single' | 'multi'` flag declared at registration time. Unknown
     * values don't silently coerce: the offending block is dropped
     * from the registration with a `console.warn`, so a typo'd
     * `selectionMode: 'multiple'` produces a loud failure instead of a
     * widget that quietly never sees the selection stream's deltas.
     * Default is `'single'` when omitted — safe for new widget authors
     * who haven't thought about multi-select yet.
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
        // Phase 3.2 — validate + normalise selectionMode per block. The
        // field is per-block (viewer vs editor) so a viewer can declare
        // 'multi' while the editor sticks to 'single' (a coordinate
        // editor only cares about one (lat, lon) pair).
        if (reg.viewer && !bowireNormaliseSelectionMode(reg, 'viewer')) {
            delete reg.viewer;
        }
        if (reg.editor && !bowireNormaliseSelectionMode(reg, 'editor')) {
            delete reg.editor;
        }
        if (!reg.viewer && !reg.editor) {
            throw new Error('BowireExtensions.register[' + reg.id + ']: all blocks rejected (invalid selectionMode)');
        }
    }

    /**
     * Phase 3.2 — coerce `block.selectionMode` to a known value or
     * reject the block. Returns `true` when the block is keep-able,
     * `false` when the caller should drop it. Missing → default
     * `'single'`. Anything other than `'single'` / `'multi'` produces
     * a `console.warn` and drops the block; the caller deletes the
     * field so the rest of the registry stays consistent.
     */
    function bowireNormaliseSelectionMode(reg, blockName) {
        var block = reg[blockName];
        if (!block) return false;
        if (block.selectionMode === undefined || block.selectionMode === null) {
            block.selectionMode = 'single';
            return true;
        }
        if (block.selectionMode === 'single' || block.selectionMode === 'multi') {
            return true;
        }
        console.warn(
            '[bowire] extension ' + reg.id + '.' + blockName
            + ': unknown selectionMode "' + String(block.selectionMode)
            + '" — must be \'single\' or \'multi\'. Block ignored.');
        return false;
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
     * Module-local mirror of the most recently dispatched selection
     * snapshot. The Streaming-Frames pane writes to it via
     * `bowireRecordSelectionSnapshot`; new ctxs read it on
     * construction so a widget that mounts AFTER the user has already
     * made a selection (e.g. switched tabs) sees the current state on
     * its first `await ctx.selection$.next()`. Empty by default — no
     * frames selected.
     */
    var bowireCurrentSelection = { selectedFrameIds: [] };

    /**
     * Called by render-main.js whenever the workbench's selected-frame
     * set changes. Updates the module-local mirror and dispatches the
     * `bowire:frames-selection-changed` custom event so every active
     * ctx's selection$ iterator yields the new snapshot. The contract
     * is "one event = one full N-snapshot"; callers MUST NOT dispatch
     * one event per delta entry.
     */
    function bowireRecordSelectionSnapshot(selectedFrameIds) {
        var ids = Array.isArray(selectedFrameIds) ? selectedFrameIds.slice() : [];
        bowireCurrentSelection = { selectedFrameIds: ids };
        try {
            document.dispatchEvent(new CustomEvent('bowire:frames-selection-changed', {
                detail: bowireCurrentSelection
            }));
        } catch (e) {
            console.error('[bowire-ext] dispatchSelection', e);
        }
    }

    /**
     * Phase 3.2 — apply a widget's declared `selectionMode` to one
     * incoming snapshot. Pure function: no side effects beyond the
     * `state` ledger the caller hands in (one ledger per ctx so two
     * widgets on the same method stay independent).
     *
     * `'multi'` → pass-through, the widget gets the full snapshot.
     * `'single'` → deliver `[lastSelected]`, where lastSelected is:
     *   - the LAST id newly added vs the previous snapshot (delta-add
     *     case — handles Shift-click extending from `a` to `c` by
     *     picking `c`, and select-all by picking the last array entry);
     *   - the previous `lastSelected` if it survives the new snapshot
     *     (deselect of some other id — the widget shouldn't jump);
     *   - the last entry of the new snapshot as a fallback (the prior
     *     lastSelected was deselected but others remain).
     * Empty snapshots stay empty in both modes — a widget needs to be
     * able to render "nothing selected".
     */
    function bowireApplySelectionMode(currentIds, mode, state) {
        if (mode === 'multi') {
            state.prev = currentIds.slice();
            // lastSelected still tracked for parity with mode flips, even
            // though it's unused in multi mode.
            state.lastSelected = currentIds.length === 0
                ? null
                : currentIds[currentIds.length - 1];
            return currentIds;
        }
        if (currentIds.length === 0) {
            state.prev = [];
            state.lastSelected = null;
            return [];
        }
        var prev = state.prev || [];
        var prevSet = {};
        for (var i = 0; i < prev.length; i++) prevSet[prev[i]] = true;
        var newest = null;
        for (var j = 0; j < currentIds.length; j++) {
            if (!prevSet[currentIds[j]]) newest = currentIds[j];
        }
        var pick;
        if (newest !== null) {
            pick = newest;
        } else if (state.lastSelected !== null
                   && currentIds.indexOf(state.lastSelected) >= 0) {
            pick = state.lastSelected;
        } else {
            pick = currentIds[currentIds.length - 1];
        }
        state.prev = currentIds.slice();
        state.lastSelected = pick;
        return [pick];
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

        // Selection stream — snapshot semantics. The pipe yields the
        // current selection immediately (priming the first `await`)
        // and then one snapshot per dispatched event.
        //
        // Phase 3.2 — the snapshot is filtered through `bowireApplySelectionMode`
        // at push time so each ctx applies its declared `selectionMode`
        // independently. State for the filter (the previous snapshot +
        // the running lastSelected pointer) lives on the closure below
        // — per-ctx, never shared, so two widgets on the same method
        // with different selectionModes don't cross wires.
        var selectionPipe = bowireMakeFramesAsyncIterable();
        var selectionFilterState = { prev: [], lastSelected: null };
        var selectionMode = opts.selectionMode === 'multi' ? 'multi' : 'single';
        function pushSelection(snapshot) {
            var raw = Array.isArray(snapshot && snapshot.selectedFrameIds)
                ? snapshot.selectedFrameIds.slice()
                : [];
            var delivered = bowireApplySelectionMode(raw, selectionMode, selectionFilterState);
            selectionPipe.push({ selectedFrameIds: delivered });
        }
        // Prime — the framework's spec is "first awaited value IS the
        // current selection". We run it through the same filter as
        // subsequent events so a single-mode widget that mounts AFTER
        // a multi-select sees `[lastSelected]` and not the full set.
        pushSelection(bowireCurrentSelection);
        var selectionHandler = function (evt) {
            var detail = evt && evt.detail;
            if (!detail) return;
            pushSelection(detail);
        };
        document.addEventListener('bowire:frames-selection-changed', selectionHandler);

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
                selection$: selectionPipe.iterator,
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
                document.removeEventListener('bowire:frames-selection-changed', selectionHandler);
                if (ro) { try { ro.disconnect(); } catch {} }
                framesPipe.close();
                selectionPipe.close();
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
     * persistence path calls this when a user-supplied annotation
     * changes the resolution. The semantics-menu fragment dispatches
     * `bowire:semantics-changed` after a successful write; the listener
     * installed at the bottom of this fragment clears the cache and
     * lets render-main.js's next call re-fetch the effective schema.
     */
    function bowireClearEffectiveCache() {
        bowireEffectiveCache = {};
    }

    /**
     * Expose the cached effective-schema entries for the Phase-4
     * companion-field suggestion. Returns the annotations array for
     * the (service, method) pair, or null when the cache has nothing
     * for that pair. Read-only — callers must not mutate the returned
     * list.
     */
    function bowireEffectiveCacheFor(serviceId, methodId) {
        var key = bowireEffectiveCacheKey(serviceId, methodId);
        var entry = bowireEffectiveCache[key];
        return entry && Array.isArray(entry.annotations) ? entry.annotations : null;
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

            // Phase 3-R — placeholder cards for any annotation kind
            // that has NO extension registered. We render one card per
            // distinct package suggestion so a method that lights up
            // both `coordinate.latitude` and `coordinate.longitude`
            // doesn't double-render the MapLibre placeholder. The
            // suggestion table only ships well-known coordinate kinds
            // today; future kinds (image.bytes, audio.bytes) plug in
            // additively without changing the routing code.
            var placeholdersEmitted = {};
            for (var kind in kindsSeen) {
                if (!Object.prototype.hasOwnProperty.call(kindsSeen, kind)) continue;
                var suggestion = bowirePackageSuggestions[kind];
                if (!suggestion) continue;
                // Skip when the kind is already covered by a registered
                // extension — either directly (the umbrella kind has a
                // viewer) or as a `pairing.required` companion.
                if (bowireExtensions.byKind[kind] && bowireExtensions.byKind[kind].length > 0) {
                    continue;
                }
                var covered = false;
                for (var rid in bowireExtensions.byId) {
                    if (!Object.prototype.hasOwnProperty.call(bowireExtensions.byId, rid)) continue;
                    var rext = bowireExtensions.byId[rid];
                    if (rext.pairing && rext.pairing.required
                        && rext.pairing.required.indexOf(kind) >= 0) {
                        covered = true; break;
                    }
                }
                if (covered) continue;
                if (placeholdersEmitted[suggestion]) continue;
                placeholdersEmitted[suggestion] = true;

                var placeholder = bowireRenderPlaceholder(kind, suggestion);
                paneContainer.appendChild(placeholder);
                cleanups.push((function (el) {
                    return function () {
                        if (el.parentNode) el.parentNode.removeChild(el);
                    };
                })(placeholder));
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
                        discriminator: '*',
                        // Phase 3.2 — propagate the viewer block's
                        // declared selectionMode into the ctx so the
                        // selection-stream filter knows whether to
                        // truncate to [lastSelected]. Default 'single'
                        // is enforced at registration time.
                        selectionMode: ext.viewer && ext.viewer.selectionMode
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

    // ---------------------------------------------------------------
    // Phase 3-R — external-extension bootstrap + placeholder-tab
    //
    // Built-in widgets used to be concat'd into core's bowire.js. After
    // Phase 3-R every viewer/editor (including MapLibre) ships as a
    // separate NuGet package — its JS bundle lives as an
    // EmbeddedResource on the extension assembly and is served at
    // `/api/ui/extensions/{id}/{bundle}` by the core asset endpoint.
    // The workbench fetches `/api/ui/extensions` at boot and
    // dynamic-loads each declared bundle via `<script src=…>` + an
    // optional `<link rel="stylesheet">`. Same-origin only — bundles
    // are served by Bowire itself, never an external CDN.
    //
    // The placeholder-tab path runs INSIDE mountWidgetsForMethod: when
    // an annotation references a `kind` for which no extension has
    // registered, the framework mounts a grey-text "Install
    // Kuestenlogik.Bowire.Extension.X" placeholder card so the user
    // discovers extensions organically when their payload's data shape
    // suggests one.
    // ---------------------------------------------------------------

    /**
     * Map from semantic-kind → suggested package id. Used to render
     * helpful "Install Kuestenlogik.Bowire.Extension.MapLibre" messages
     * when an annotation exists but no extension has registered against
     * the kind. Generic across kinds — the kind name is the lookup key,
     * the package id is the recommendation text.
     */
    var bowirePackageSuggestions = {
        // Direct umbrella-kind hits — when an annotation explicitly
        // carries `coordinate.wgs84` (rather than the per-axis latitude
        // / longitude companions the auto-detector produces).
        'coordinate.wgs84': 'Kuestenlogik.Bowire.Extension.MapLibre',
        // Companion-kind hits — the auto-detector writes
        // `coordinate.latitude` + `coordinate.longitude` annotations;
        // the umbrella kind never lands in the store. Suggest the map
        // extension as soon as EITHER companion shows up so the user
        // discovers the package on day one.
        'coordinate.latitude': 'Kuestenlogik.Bowire.Extension.MapLibre',
        'coordinate.longitude': 'Kuestenlogik.Bowire.Extension.MapLibre'
    };

    /**
     * Pull the listing of server-side-declared extensions and load each
     * one's JS bundle + optional stylesheet. Idempotent — calling twice
     * is safe; already-loaded ids are skipped. Returns a promise that
     * resolves when every declared bundle has either loaded or failed
     * (the framework never blocks on a single misbehaving extension).
     */
    var bowireExternalsLoaded = null;
    function bowireLoadExternalExtensions() {
        if (bowireExternalsLoaded) return bowireExternalsLoaded;
        var url = config.prefix + '/api/ui/extensions';
        bowireExternalsLoaded = fetch(url, { headers: { 'Accept': 'application/json' } })
            .then(function (r) { return r.ok ? r.json() : { extensions: [] }; })
            .then(function (data) {
                var list = (data && data.extensions) || [];
                var loads = [];
                for (var i = 0; i < list.length; i++) {
                    loads.push(bowireLoadOneExtension(list[i]));
                }
                return Promise.allSettled(loads);
            })
            .catch(function (e) {
                console.warn('[bowire-ext] failed to enumerate /api/ui/extensions', e);
                return [];
            });
        return bowireExternalsLoaded;
    }

    /**
     * Inject the stylesheet + script tags for one extension descriptor.
     * Both URLs are same-origin (the asset endpoint serves the embedded
     * resource out of the extension's assembly). Failure of one tag
     * doesn't fail the whole bootstrap — the warning sticks in the
     * console and the placeholder-tab path renders for the missing kind.
     */
    function bowireLoadOneExtension(desc) {
        if (!desc || typeof desc.id !== 'string' || !desc.id) {
            return Promise.resolve();
        }
        // Already-registered? Skip — built-ins (if any future fragment
        // re-registers under the same id) win the tie-break, and a
        // second script tag would be wasted bandwidth.
        if (bowireExtensions.byId[desc.id]) return Promise.resolve();

        var stylesPromise = Promise.resolve();
        if (desc.stylesUrl) {
            var cssId = 'bowire-ext-css-' + desc.id.replace(/[^a-z0-9]/gi, '-');
            if (!document.getElementById(cssId)) {
                var link = document.createElement('link');
                link.id = cssId;
                link.rel = 'stylesheet';
                link.href = desc.stylesUrl;
                document.head.appendChild(link);
            }
        }
        if (!desc.bundleUrl) return stylesPromise;

        return stylesPromise.then(function () {
            return new Promise(function (resolve) {
                var script = document.createElement('script');
                script.src = desc.bundleUrl;
                script.async = false; // preserve register-order
                script.onload = function () { resolve(); };
                script.onerror = function () {
                    console.warn('[bowire-ext] failed to load bundle for ' + desc.id
                        + ' from ' + desc.bundleUrl);
                    resolve();
                };
                document.head.appendChild(script);
            });
        });
    }

    /**
     * Render a placeholder card when the workbench sees an annotation
     * kind for which no extension is registered. Generic across kinds —
     * the message names the kind so the user knows what data the
     * suggested package would render. Includes a small copy-to-clipboard
     * affordance next to the package id; Bowire core can't install
     * packages from the workbench, but a one-click clipboard write keeps
     * the friction low.
     */
    function bowireRenderPlaceholder(kind, suggestion) {
        var card = document.createElement('div');
        card.className = 'bowire-ext-placeholder';
        card.dataset.kind = kind;
        card.setAttribute('role', 'note');
        // Inline styles so the card renders against any theme without
        // depending on bowire.css being loaded yet. Mirrors the same
        // independent-rendering rule the map widget itself follows.
        card.style.padding = '14px 16px';
        card.style.margin = '8px 0';
        card.style.borderRadius = '6px';
        card.style.font = '13px system-ui, sans-serif';
        card.style.color = 'var(--bowire-fg-muted, #6b7280)';
        card.style.background = 'var(--bowire-bg-elevated, rgba(120, 120, 120, 0.06))';
        card.style.border = '1px dashed var(--bowire-border-muted, rgba(120, 120, 120, 0.25))';

        var msg = document.createElement('span');
        msg.textContent = 'Install ';
        card.appendChild(msg);

        var code = document.createElement('code');
        code.textContent = suggestion;
        code.style.padding = '2px 6px';
        code.style.borderRadius = '4px';
        code.style.background = 'var(--bowire-bg-code, rgba(0, 0, 0, 0.08))';
        code.style.fontFamily = 'ui-monospace, SFMono-Regular, Menlo, monospace';
        card.appendChild(code);

        var copyBtn = document.createElement('button');
        copyBtn.type = 'button';
        copyBtn.textContent = 'Copy';
        copyBtn.title = 'Copy package id to clipboard';
        copyBtn.style.marginLeft = '8px';
        copyBtn.style.padding = '2px 8px';
        copyBtn.style.font = 'inherit';
        copyBtn.style.cursor = 'pointer';
        copyBtn.addEventListener('click', function () {
            try {
                if (navigator.clipboard && typeof navigator.clipboard.writeText === 'function') {
                    navigator.clipboard.writeText(suggestion).then(function () {
                        copyBtn.textContent = 'Copied!';
                        setTimeout(function () { copyBtn.textContent = 'Copy'; }, 1200);
                    });
                }
            } catch { /* clipboard API unavailable — ignore */ }
        });
        card.appendChild(copyBtn);

        var tail = document.createElement('span');
        tail.textContent = ' to render `' + kind + '` annotations on a map.';
        card.appendChild(tail);

        return card;
    }

    // Public-from-the-IIFE handles used by other fragments — kept on
    // the closure-local namespace so the existing window contract stays
    // minimal (just `BowireExtensions`).
    window.__bowireExtFramework = {
        markBuiltIn: bowireMarkBuiltIn,
        register: bowireRegisterExtension,
        mountWidgetsForMethod: bowireMountWidgetsForMethod,
        dispatchStreamMessage: bowireDispatchStreamMessage,
        recordSelectionSnapshot: bowireRecordSelectionSnapshot,
        clearEffectiveCache: bowireClearEffectiveCache,
        // Phase 4 — read-only accessor for the cached effective-schema
        // entries so the semantics-menu's companion-field suggester
        // can find already-marked siblings without re-fetching.
        effectiveCacheFor: bowireEffectiveCacheFor,
        fetchEffective: bowireFetchEffective,
        // Phase 3.1 — expose the preferred-extension lookup so the
        // workbench (render-main.js) can ask "which built-in viewer
        // handles coordinate.wgs84?" without having to walk byId itself.
        preferredExtension: bowirePreferredExtension,
        findPairingMatches: bowireFindPairingMatches,
        makeViewerCtx: bowireMakeViewerCtx,
        // Read-only accessor for the current selection snapshot.
        currentSelection: function () { return bowireCurrentSelection; },
        // Phase 3-R — kicks off the external-bundle bootstrap. Init
        // calls this once after services are loaded so every declared
        // extension's JS lands before the first mountWidgetsForMethod.
        loadExternalExtensions: bowireLoadExternalExtensions,
        // Phase 3-R — placeholder-card factory exposed for tests +
        // future widget overrides. Returns a detached DOM node.
        renderPlaceholder: bowireRenderPlaceholder,
        // Phase 3-R — kind → package-id suggestion table. Read-only
        // for callers; framework consults it in mountWidgetsForMethod
        // when an unregistered kind shows up in the annotations.
        packageSuggestionFor: function (kind) {
            return bowirePackageSuggestions[kind] || null;
        },
        // Test seams
        _findPairingMatches: bowireFindPairingMatches,
        _findPairingHints: bowireFindPairingHints,
        _parentPath: bowireParentPath,
        // Phase 3.2 — pure-function test seam for the selection-mode
        // filter. Callers pass their own ledger so each invocation
        // simulates a single ctx's per-event state machine.
        _applySelectionMode: bowireApplySelectionMode
    };

    // Phase 4 — listen for the semantics-changed event the menu
    // dispatches after a successful write. Clear the effective-schema
    // cache so the next mountWidgetsForMethod / fetchEffective sees the
    // new resolution. Listening here (in extensions.js, not the menu
    // fragment) keeps the cache-internals private to the framework.
    document.addEventListener('bowire:semantics-changed', function () {
        bowireClearEffectiveCache();
    });

    // Phase 4 — every stream message also updates the workbench-local
    // discriminator + path catalogue so the scope picker can offer
    // "all message types in this method" without enumerating the
    // entire schema universe. The catalogue is a write-only sink from
    // here — the semantics-menu reads it through
    // `window.__bowireSemanticsCatalogue`.
    document.addEventListener('bowire:stream-message', function (evt) {
        var d = evt && evt.detail;
        if (!d || !d.service || !d.method) return;
        if (window.__bowireSemanticsMenu) {
            try {
                window.__bowireSemanticsMenu.recordSeenDiscriminator(
                    d.service, d.method, d.discriminator);
            } catch (e) { /* swallow */ }
        }
    });
