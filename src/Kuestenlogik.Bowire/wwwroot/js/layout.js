// Copyright 2026 Küstenlogik · Apache-2.0
// ----------------------------------------------------------------------
// Split-pane layout primitive — Phase 3.1.
//
// Tabs are the workbench's only layout primitive today (Request /
// Response / Streaming-Frames all share one pane via tab switching).
// The map widget needs to live ALONGSIDE the streaming-frames list so
// the user can multi-select frames AND see the map react in real time.
// That requires a split-pane primitive.
//
// This module is deliberately framework-free: vanilla JS, two slots,
// one draggable divider, ratio persisted to localStorage. The map
// widget is the first consumer; future kinds (image, chart, audio) can
// reuse the same primitive without extending the widget contract — the
// layout decision lives in the workbench, not in the widget itself.
//
// Mobile fallback (viewport width < 720px) stacks vertically regardless
// of the orientation passed in — the workbench's screen real estate at
// phone widths can't accommodate side-by-side maps + lists, so the
// primitive falls back to a vertical stack so the user still gets both
// views, just one above the other.
// ----------------------------------------------------------------------

    /**
     * Minimum slot size in pixels. Both slots clamp at this size so the
     * map widget always has enough real estate to render its tile grid
     * and the streaming-frames list keeps its visible columns. The
     * divider-drag handler enforces the clamp on each move.
     */
    var BOWIRE_SPLIT_MIN_SLOT_PX = 240;

    /**
     * Viewport-width threshold below which split-panes collapse to a
     * vertical stack. 720px matches the tablet/phone breakpoint the
     * rest of the workbench uses (bowire.css media queries).
     */
    var BOWIRE_SPLIT_MOBILE_BREAKPOINT_PX = 720;

    /**
     * Track every live split-pane instance so a window-resize that
     * crosses the mobile breakpoint can flip the orientation on each
     * one without the consumer having to wire its own listener. We
     * weak-reference the container instead of holding the instance
     * itself so detached containers get garbage-collected naturally.
     */
    var bowireSplitPaneInstances = [];

    /**
     * Read the persisted ratio for a storage key, defaulting to the
     * caller-supplied `initialRatio` when nothing is stored or the
     * value is unparseable. Clamped to [0.1, 0.9] so a corrupted entry
     * can't collapse one slot to zero.
     */
    function bowireSplitPaneLoadRatio(storageKey, initialRatio) {
        if (!storageKey) return clampRatio(initialRatio);
        try {
            var raw = localStorage.getItem(storageKey);
            if (raw == null) return clampRatio(initialRatio);
            var n = parseFloat(raw);
            if (!isFinite(n)) return clampRatio(initialRatio);
            return clampRatio(n);
        } catch {
            return clampRatio(initialRatio);
        }
    }

    function bowireSplitPaneSaveRatio(storageKey, ratio) {
        if (!storageKey) return;
        try { localStorage.setItem(storageKey, String(clampRatio(ratio))); } catch {}
    }

    function clampRatio(r) {
        if (typeof r !== 'number' || !isFinite(r)) return 0.5;
        if (r < 0.1) return 0.1;
        if (r > 0.9) return 0.9;
        return r;
    }

    /**
     * Build a split-pane inside `container`. Returns a handle exposing:
     *   - firstSlot       — HTMLElement; populate with anything.
     *   - secondSlot      — HTMLElement; same.
     *   - setRatio(r)     — force a ratio update (e.g. layout toggle).
     *   - dispose()       — detach window listeners, mark instance dead.
     *
     * The container itself is repurposed: any existing children are
     * removed and replaced with the three split-pane nodes (slot,
     * divider, slot). Callers that need to preserve other DOM should
     * pass in an empty wrapper.
     *
     * `orientation` is the requested layout for normal-width viewports.
     * On viewports below 720px (BOWIRE_SPLIT_MOBILE_BREAKPOINT_PX) the
     * pane stacks vertically regardless of the requested value — the
     * orientation lives in `effectiveOrientation` and is re-evaluated
     * on every window-resize.
     */
    function bowireCreateSplitPane(container, opts) {
        opts = opts || {};
        var requestedOrientation = (opts.orientation === 'vertical') ? 'vertical' : 'horizontal';
        var storageKey = opts.storageKey || null;
        var ratio = bowireSplitPaneLoadRatio(storageKey, typeof opts.initialRatio === 'number' ? opts.initialRatio : 0.5);

        // Clear any existing contents so the wrapper becomes ours alone.
        while (container.firstChild) container.removeChild(container.firstChild);
        container.classList.add('bowire-split-pane');

        var firstSlot = document.createElement('div');
        firstSlot.className = 'bowire-split-pane-slot bowire-split-pane-first';
        var divider = document.createElement('div');
        divider.className = 'bowire-split-pane-divider';
        divider.setAttribute('role', 'separator');
        divider.setAttribute('aria-orientation', 'vertical');
        divider.tabIndex = 0;
        var secondSlot = document.createElement('div');
        secondSlot.className = 'bowire-split-pane-slot bowire-split-pane-second';

        container.appendChild(firstSlot);
        container.appendChild(divider);
        container.appendChild(secondSlot);

        // Inline styles intentionally over a CSS rule so the layout
        // primitive carries its own contract — consumers can drop the
        // pane into any host without first wiring up CSS. The split
        // direction switches between row (horizontal) and column
        // (vertical) flex layouts based on the effective orientation.
        container.style.display = 'flex';
        container.style.position = container.style.position || 'relative';
        container.style.overflow = 'hidden';
        firstSlot.style.overflow = 'auto';
        firstSlot.style.minWidth = '0';
        firstSlot.style.minHeight = '0';
        secondSlot.style.overflow = 'auto';
        secondSlot.style.minWidth = '0';
        secondSlot.style.minHeight = '0';

        function effectiveOrientation() {
            try {
                if (window.innerWidth < BOWIRE_SPLIT_MOBILE_BREAKPOINT_PX) return 'vertical';
            } catch {}
            return requestedOrientation;
        }

        function applyOrientation() {
            var eff = effectiveOrientation();
            container.classList.toggle('bowire-split-pane-horizontal', eff === 'horizontal');
            container.classList.toggle('bowire-split-pane-vertical', eff === 'vertical');
            container.style.flexDirection = eff === 'horizontal' ? 'row' : 'column';
            divider.style.cursor = eff === 'horizontal' ? 'ew-resize' : 'ns-resize';
            divider.style.flex = '0 0 auto';
            divider.style.background = 'var(--bowire-border, rgba(127,127,127,0.3))';
            if (eff === 'horizontal') {
                divider.style.width = '6px';
                divider.style.height = 'auto';
                divider.style.alignSelf = 'stretch';
                divider.setAttribute('aria-orientation', 'vertical');
            } else {
                divider.style.height = '6px';
                divider.style.width = 'auto';
                divider.style.alignSelf = 'stretch';
                divider.setAttribute('aria-orientation', 'horizontal');
            }
            applyRatio();
        }

        function applyRatio() {
            var eff = effectiveOrientation();
            var pct = (ratio * 100).toFixed(2) + '%';
            var counter = ((1 - ratio) * 100).toFixed(2) + '%';
            if (eff === 'horizontal') {
                firstSlot.style.flex = '0 0 ' + pct;
                firstSlot.style.width = pct;
                firstSlot.style.height = '';
                secondSlot.style.flex = '1 1 ' + counter;
                secondSlot.style.width = '';
                secondSlot.style.height = '';
            } else {
                firstSlot.style.flex = '0 0 ' + pct;
                firstSlot.style.height = pct;
                firstSlot.style.width = '';
                secondSlot.style.flex = '1 1 ' + counter;
                secondSlot.style.height = '';
                secondSlot.style.width = '';
            }
        }

        // ---- Divider drag ----
        // The drag handler attaches mousemove + mouseup to `document`
        // for the duration of a single drag, then detaches both on
        // mouseup. This is the same pattern the streaming-list splitter
        // uses and avoids the leak that would happen if we kept the
        // listeners attached for the lifetime of the pane: every call
        // to `createSplitPane` would otherwise leave its mousemove
        // listener bound forever, even after the container was torn
        // down by a re-render.
        function onDividerDown(ev) {
            ev.preventDefault();
            // mousedown is most common, but the divider is also keyboard-
            // focusable for accessibility; arrow-keys arrive through a
            // separate handler below.
            var eff = effectiveOrientation();
            var rect = container.getBoundingClientRect();
            var total = eff === 'horizontal' ? rect.width : rect.height;
            var origin = eff === 'horizontal' ? rect.left : rect.top;
            if (total < 2 * BOWIRE_SPLIT_MIN_SLOT_PX) {
                // The container itself is too small to honour the minimums
                // on both sides. Skip the drag — anything we'd do would
                // violate the clamp the moment we touched it.
                return;
            }
            var minRatio = BOWIRE_SPLIT_MIN_SLOT_PX / total;
            var maxRatio = 1 - minRatio;
            document.body.style.cursor = divider.style.cursor;
            document.body.style.userSelect = 'none';

            function onMove(e) {
                var pos = eff === 'horizontal' ? e.clientX : e.clientY;
                var rel = (pos - origin) / total;
                if (rel < minRatio) rel = minRatio;
                if (rel > maxRatio) rel = maxRatio;
                ratio = rel;
                applyRatio();
            }
            function onUp() {
                document.removeEventListener('mousemove', onMove);
                document.removeEventListener('mouseup', onUp);
                document.body.style.cursor = '';
                document.body.style.userSelect = '';
                bowireSplitPaneSaveRatio(storageKey, ratio);
            }
            document.addEventListener('mousemove', onMove);
            document.addEventListener('mouseup', onUp);
        }
        divider.addEventListener('mousedown', onDividerDown);

        // Keyboard nudge: arrow keys move the divider by ~2.5% on the
        // active axis. Mirrors what most native split-pane widgets do
        // and means a user without a mouse can still resize.
        divider.addEventListener('keydown', function (e) {
            var eff = effectiveOrientation();
            var step = 0.025;
            var delta = 0;
            if (eff === 'horizontal' && (e.key === 'ArrowLeft' || e.key === 'ArrowRight')) {
                delta = e.key === 'ArrowLeft' ? -step : step;
            } else if (eff === 'vertical' && (e.key === 'ArrowUp' || e.key === 'ArrowDown')) {
                delta = e.key === 'ArrowUp' ? -step : step;
            }
            if (delta !== 0) {
                e.preventDefault();
                ratio = clampRatio(ratio + delta);
                applyRatio();
                bowireSplitPaneSaveRatio(storageKey, ratio);
            }
        });

        // Re-evaluate orientation when the viewport crosses the mobile
        // breakpoint. We register one listener per instance and remove
        // it on dispose() so a render() that tears down and rebuilds
        // the pane doesn't leak listeners.
        function onWindowResize() {
            applyOrientation();
        }
        try { window.addEventListener('resize', onWindowResize); } catch {}

        var instance = {
            container: container,
            firstSlot: firstSlot,
            secondSlot: secondSlot,
            divider: divider,
            getRatio: function () { return ratio; },
            setRatio: function (r) {
                ratio = clampRatio(r);
                applyRatio();
                bowireSplitPaneSaveRatio(storageKey, ratio);
            },
            dispose: function () {
                try { window.removeEventListener('resize', onWindowResize); } catch {}
                divider.removeEventListener('mousedown', onDividerDown);
                var idx = bowireSplitPaneInstances.indexOf(instance);
                if (idx >= 0) bowireSplitPaneInstances.splice(idx, 1);
            }
        };
        bowireSplitPaneInstances.push(instance);

        applyOrientation();
        return instance;
    }

    // ---- Per-widget layout-mode persistence ----
    //
    // Each (service, method, widgetId) tuple gets one persisted layout
    // record under `bowire_widget_layout:${serviceId}:${methodId}:${widgetId}`.
    // The shape is forward-compatible: `mode` is one of
    //   'tab'                 — render the widget as a tab in the response pane (default for non-map kinds)
    //   'split-horizontal'    — split-pane, divider on the X axis (default for coordinate.wgs84)
    //   'split-vertical'      — split-pane, divider on the Y axis (future, accepted but no toggle ships v1.3.1)
    //   'floating'            — pop-out window (future v1.x; the persisted shape reserves the slot)
    // and `ratio` is optional (used when `mode` starts with `split-`).
    //
    // v1.3.1 ships only `tab` and `split-horizontal` from the toggle UI;
    // the other two are read-tolerant so a future version can extend the
    // toggle without a migration step.

    function bowireWidgetLayoutKey(serviceId, methodId, widgetId) {
        return 'bowire_widget_layout:' + (serviceId || '') + ':' + (methodId || '') + ':' + (widgetId || '');
    }

    /**
     * Default layout per `kind`. The map widget defaults to a horizontal
     * split (list-on-left, map-on-right) so the streaming-frames pane
     * stays visible. Every other kind currently falls back to a tab.
     */
    function bowireDefaultLayoutForKind(kind) {
        if (kind === 'coordinate.wgs84') {
            return { mode: 'split-horizontal', ratio: 0.5 };
        }
        return { mode: 'tab' };
    }

    function bowireLoadWidgetLayout(serviceId, methodId, widgetId, kind) {
        var key = bowireWidgetLayoutKey(serviceId, methodId, widgetId);
        var fallback = bowireDefaultLayoutForKind(kind);
        try {
            var raw = localStorage.getItem(key);
            if (!raw) return fallback;
            var parsed = JSON.parse(raw);
            if (!parsed || typeof parsed !== 'object') return fallback;
            var mode = parsed.mode;
            if (mode !== 'tab'
                && mode !== 'split-horizontal'
                && mode !== 'split-vertical'
                && mode !== 'floating') {
                return fallback;
            }
            return {
                mode: mode,
                ratio: typeof parsed.ratio === 'number' ? parsed.ratio : fallback.ratio
            };
        } catch {
            return fallback;
        }
    }

    function bowireSaveWidgetLayout(serviceId, methodId, widgetId, layout) {
        var key = bowireWidgetLayoutKey(serviceId, methodId, widgetId);
        try {
            localStorage.setItem(key, JSON.stringify({
                mode: layout.mode,
                ratio: typeof layout.ratio === 'number' ? layout.ratio : undefined
            }));
        } catch {}
    }

    /**
     * Cycle the layout mode for a widget that supports more than the
     * single 'tab' default. v1.3.1 only ships the (tab ↔ split-horizontal)
     * toggle for the map widget; other kinds skip the toggle entirely
     * (the UI button hides). The cycle list is centralised here so a
     * future kind that grows split-vertical support just appends to
     * the array.
     */
    function bowireCycleLayoutMode(currentMode, kind) {
        var cycle;
        if (kind === 'coordinate.wgs84') {
            cycle = ['tab', 'split-horizontal'];
        } else {
            cycle = ['tab'];
        }
        if (cycle.length <= 1) return cycle[0];
        var idx = cycle.indexOf(currentMode);
        var next = cycle[(idx + 1) % cycle.length];
        return next;
    }

    /**
     * Public handles exposed via a closure-local namespace so the rest
     * of the workbench (render-main.js, the extension framework) can
     * call into the primitive without polluting `window`.
     */
    window.__bowireLayout = {
        createSplitPane: bowireCreateSplitPane,
        loadWidgetLayout: bowireLoadWidgetLayout,
        saveWidgetLayout: bowireSaveWidgetLayout,
        defaultLayoutForKind: bowireDefaultLayoutForKind,
        cycleLayoutMode: bowireCycleLayoutMode,
        // Test seams
        _minSlotPx: BOWIRE_SPLIT_MIN_SLOT_PX,
        _mobileBreakpointPx: BOWIRE_SPLIT_MOBILE_BREAKPOINT_PX
    };
