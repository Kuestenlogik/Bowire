// @generated
// This file is a fragment of the assembled `wwwroot/bowire.js` bundle —
// MSBuild concatenates every <BowireJsFragment> in `.csproj` into a
// single runtime file. Per-file it lives inside the shared IIFE
// opened by prologue.js + closed by init.js, so it's syntactically
// incomplete on its own. The `@generated` marker tells CodeQL to skip
// parse-level checks here — the assembled bundle is what actually
// runs in the browser.
//
// #281 — Guided tour engine. Replaces the legacy `getTourSteps` /
// `startTour` / `endTour` block that previously lived in execute.js
// (which is the request-execution module — wrong neighbourhood).
//
// Engine shape:
//
//   tourStart([
//       {
//           id: 'create-first-workspace',
//           title: 'Create your first workspace',
//           body:  'A workspace is your project folder…',
//           target: '#bowire-welcome-create-btn',
//           navigate: function () { railMode = 'home'; render(); },
//           cta: { label: 'Create workspace',
//                  onClick: function () { openCreateWorkspaceDialog(); } },
//           advance: 'on-cta'                  // | 'on-event:<name>' | 'next-button'
//       },
//       …
//   ], { id: 'getting-started' });
//
// Key differences vs. the old engine:
//   • Spotlight via SVG cutout + soft halo (was: CSS box-shadow on the
//     target — broke when the target was clipped by overflow:hidden
//     ancestors).
//   • Page-navigation hook — the step describes how to route to the
//     surface its target lives on; the engine runs the hook BEFORE
//     resolving the selector so the operator never lands on 'click X'
//     when X isn't visible.
//   • CTA advance, custom-event advance, manual-next advance.
//   • Target stays clickable (pointer-events on the overlay are scoped
//     to the dim layer; the cutout punches through).
//   • Per-tour saved-once dismissal so 'Getting started' doesn't
//     re-appear after the operator finishes it.

    // ---- Engine state (module-scope so the morphdom render path
    //      doesn't clobber it; tour overlay lives outside #bowire-app). ----
    var _tourState = {
        running: false,
        tourId: null,                // for saved-once tracking
        steps: [],
        index: -1,
        rafHandle: null,             // outstanding rAF for repositioning
        resizeHandler: null,         // window.resize listener while running
        scrollHandler: null,         // capture-phase scroll listener
        eventListeners: [],          // [{ name, handler }] for on-event advance
        rerenderHandler: null,       // post-render reposition hook
        overlayEl: null,
        tooltipEl: null,
        targetEl: null,
        prevPointerEventsTarget: null,
        startedAt: 0
    };

    var TOUR_SAVED_PREFIX = 'bowire_tour_done_';

    function _tourIsDone(tourId) {
        if (!tourId) return false;
        try { return !!localStorage.getItem(TOUR_SAVED_PREFIX + tourId); }
        catch { return false; }
    }

    function _tourMarkDone(tourId) {
        if (!tourId) return;
        try { localStorage.setItem(TOUR_SAVED_PREFIX + tourId, '1'); }
        catch { /* localStorage disabled */ }
    }

    // Public reset — exposed via window.bowireResetTours so an operator
    // can re-trigger the Getting-started tour after dismissing it (e.g.
    // from a Settings affordance). Single-arg form clears one tour;
    // no-arg clears every saved-once flag.
    function tourResetSavedOnce(tourId) {
        try {
            if (tourId) {
                localStorage.removeItem(TOUR_SAVED_PREFIX + tourId);
                return;
            }
            var rm = [];
            for (var i = 0; i < localStorage.length; i++) {
                var k = localStorage.key(i);
                if (k && k.indexOf(TOUR_SAVED_PREFIX) === 0) rm.push(k);
            }
            rm.forEach(function (k) { localStorage.removeItem(k); });
        } catch { /* ignore */ }
    }

    // ---- Public API ----------------------------------------------------

    /**
     * Start a guided tour.
     *   steps  : array of step descriptors (see header comment)
     *   opts   : { id?: string,            // saved-once key (skip if already done)
     *              force?: boolean,        // re-run even if done
     *              onFinish?: function }
     */
    function tourStart(steps, opts) {
        if (!Array.isArray(steps) || steps.length === 0) return;
        opts = opts || {};

        if (_tourState.running) tourStop({ silent: true });

        if (opts.id && !opts.force && _tourIsDone(opts.id)) return;

        _tourState.running = true;
        _tourState.tourId = opts.id || null;
        _tourState.steps = steps;
        _tourState.index = 0;
        _tourState.startedAt = Date.now();
        _tourState.onFinish = typeof opts.onFinish === 'function' ? opts.onFinish : null;

        _mountTourOverlay();
        _renderTourStep();
    }

    /**
     * Stop the tour. By default marks the tour as done (so it won't
     * auto-resurface). Pass { silent: true } to NOT mark done — used
     * by the internal restart path in tourStart().
     */
    function tourStop(opts) {
        opts = opts || {};
        var wasRunning = _tourState.running;
        var tourId = _tourState.tourId;

        _tourState.running = false;
        _tourState.steps = [];
        _tourState.index = -1;

        _unmountTourOverlay();
        _detachEventAdvanceListeners();
        _detachReflowListeners();

        if (wasRunning && !opts.silent && tourId) _tourMarkDone(tourId);
        if (wasRunning && _tourState.onFinish && !opts.silent) {
            try { _tourState.onFinish(); } catch (e) { console.warn('[tour] onFinish failed', e); }
        }
        _tourState.onFinish = null;
        _tourState.tourId = null;
    }

    function tourIsRunning() { return _tourState.running; }

    // Step-descriptor convenience: dispatch a custom event that 'on-event:<name>'
    // listeners pick up. Call sites elsewhere in the bundle use
    //   tourFireEvent('workspace-created')
    // when the user completes an in-tour action.
    function tourFireEvent(name, detail) {
        if (typeof name !== 'string' || !name) return;
        var evt;
        try { evt = new CustomEvent('bowire-tour:' + name, { detail: detail || null }); }
        catch { /* IE — won't run anyway, no fallback */ return; }
        document.dispatchEvent(evt);
    }

    // ---- Step rendering ------------------------------------------------

    function _renderTourStep() {
        if (!_tourState.running) return;
        var step = _tourState.steps[_tourState.index];
        if (!step) { tourStop(); return; }

        _detachEventAdvanceListeners();

        // Page-navigation FIRST — route to the surface the target lives
        // on so the selector resolves against the right rail / drawer
        // / dialog. Wrapped in try/catch so a broken nav hook in one
        // step doesn't blow up the entire tour.
        if (typeof step.navigate === 'function') {
            try { step.navigate(); }
            catch (e) { console.warn('[tour] step.navigate failed', e); }
        }

        // The render() the navigate hook (likely) triggered is async w.r.t.
        // morphdom's commit. Defer the target-resolve to the next two frames
        // so the new DOM is in place before we measure.
        requestAnimationFrame(function () {
            requestAnimationFrame(function () {
                if (!_tourState.running) return;
                _resolveAndPaintStep(step);
                _wireAdvanceMode(step);
            });
        });
    }

    function _resolveAndPaintStep(step) {
        // Restore pointer-events on the previous step's target before
        // we touch the new one — otherwise a re-entrant render can
        // leave the OLD target with our pointerEvents:auto override
        // forever.
        _restorePrevTargetPointerEvents();

        var target = step.target ? _resolveStepTarget(step.target) : null;

        // If the target isn't there yet (slow-loading rail, async
        // dialog), retry a handful of frames before falling back to
        // a center-of-viewport placement so the tour never silently
        // stalls.
        if (step.target && !target) {
            _retryResolveStepTarget(step, 0);
            return;
        }

        _tourState.targetEl = target;
        _paintOverlay(target);
        _paintTooltip(step, target);

        // Reflow listeners only need to fire while a target step is
        // mounted — center-positioned steps don't move.
        if (target) _attachReflowListeners();
        else _detachReflowListeners();
    }

    function _retryResolveStepTarget(step, attempt) {
        // ~30 frames @ 16ms ≈ 500ms — plenty of time for any in-app
        // re-render. Past that we surface the step at viewport center
        // so the operator at least sees the body text + advance UI.
        if (attempt >= 30 || !_tourState.running) {
            _tourState.targetEl = null;
            _paintOverlay(null);
            _paintTooltip(step, null);
            _detachReflowListeners();
            return;
        }
        requestAnimationFrame(function () {
            if (!_tourState.running) return;
            var t = _resolveStepTarget(step.target);
            if (t) {
                _tourState.targetEl = t;
                _paintOverlay(t);
                _paintTooltip(step, t);
                _attachReflowListeners();
            } else {
                _retryResolveStepTarget(step, attempt + 1);
            }
        });
    }

    function _resolveStepTarget(target) {
        if (!target) return null;
        if (target instanceof Element) return target;
        try {
            // Selector form — CSS selector OR "#id". Both go through
            // querySelector so either shape works.
            return document.querySelector(target);
        } catch { return null; }
    }

    // ---- Overlay (dim layer + SVG cutout) ------------------------------

    function _mountTourOverlay() {
        if (_tourState.overlayEl) return;

        var overlay = document.createElement('div');
        overlay.className = 'bowire-tour-overlay';
        overlay.setAttribute('role', 'dialog');
        overlay.setAttribute('aria-modal', 'true');
        overlay.setAttribute('aria-label', 'Guided tour');
        overlay.id = 'bowire-tour-overlay';

        // SVG-mask cutout. The mask defines: white = visible dim,
        // black = transparent (= cutout). On every step we set the
        // black rect to the target's bounding rect so the target
        // shines through at full brightness.
        var svgNS = 'http://www.w3.org/2000/svg';
        var svg = document.createElementNS(svgNS, 'svg');
        svg.classList.add('bowire-tour-svg');
        svg.setAttribute('aria-hidden', 'true');
        // Cover the full viewport — sized via CSS.
        var defs = document.createElementNS(svgNS, 'defs');
        var mask = document.createElementNS(svgNS, 'mask');
        mask.setAttribute('id', 'bowire-tour-mask');
        var fullRect = document.createElementNS(svgNS, 'rect');
        fullRect.setAttribute('width', '100%');
        fullRect.setAttribute('height', '100%');
        fullRect.setAttribute('fill', 'white');
        mask.appendChild(fullRect);
        var cutoutRect = document.createElementNS(svgNS, 'rect');
        cutoutRect.setAttribute('rx', '8');
        cutoutRect.setAttribute('ry', '8');
        cutoutRect.setAttribute('fill', 'black');
        cutoutRect.classList.add('bowire-tour-cutout');
        mask.appendChild(cutoutRect);
        // Group for alternative-target cutouts — extra black rects appended
        // here per resolved alternative so each alt punches through the dim
        // layer alongside the primary spotlight. _paintOverlay pools the
        // child rects to avoid churn on every reflow.
        var altMaskGroup = document.createElementNS(svgNS, 'g');
        altMaskGroup.classList.add('bowire-tour-mask-alts');
        mask.appendChild(altMaskGroup);
        defs.appendChild(mask);
        svg.appendChild(defs);

        var dim = document.createElementNS(svgNS, 'rect');
        dim.setAttribute('width', '100%');
        dim.setAttribute('height', '100%');
        dim.setAttribute('fill', 'rgba(0,0,0,0.62)');
        dim.setAttribute('mask', 'url(#bowire-tour-mask)');
        svg.appendChild(dim);

        // Soft halo ring around the cutout — drawn as a separate rect
        // ABOVE the dim layer so it's not occluded. Stroke-only with
        // a glow filter for the eye-attractor effect.
        var filter = document.createElementNS(svgNS, 'filter');
        filter.setAttribute('id', 'bowire-tour-halo-filter');
        filter.setAttribute('x', '-50%');
        filter.setAttribute('y', '-50%');
        filter.setAttribute('width', '200%');
        filter.setAttribute('height', '200%');
        var blur = document.createElementNS(svgNS, 'feGaussianBlur');
        blur.setAttribute('stdDeviation', '4');
        filter.appendChild(blur);
        defs.appendChild(filter);

        var halo = document.createElementNS(svgNS, 'rect');
        halo.setAttribute('rx', '8');
        halo.setAttribute('ry', '8');
        halo.setAttribute('fill', 'none');
        halo.setAttribute('stroke-width', '2');
        halo.classList.add('bowire-tour-halo');
        svg.appendChild(halo);

        // Group for alternative-target halos — thinner / dashed strokes so
        // the eye still lands on the primary halo first but secondary spots
        // read as 'you can also click here'. Children pooled by
        // _paintOverlay alongside the alt mask rects.
        var altHaloGroup = document.createElementNS(svgNS, 'g');
        altHaloGroup.classList.add('bowire-tour-halo-alts');
        svg.appendChild(altHaloGroup);

        overlay.appendChild(svg);

        // Tooltip lives inside the overlay so its stacking context
        // sits above the dim layer without having to fight z-index
        // against everything else on the page.
        var tooltip = document.createElement('div');
        tooltip.className = 'bowire-tour-tooltip';
        tooltip.setAttribute('role', 'document');
        overlay.appendChild(tooltip);

        document.body.appendChild(overlay);

        _tourState.overlayEl = overlay;
        _tourState.tooltipEl = tooltip;

        // Click-gate: while the tour is running, the operator should
        // ONLY be able to interact with the spotlit target + the
        // tooltip (Next/Back/Skip/Close/CTA). A click off-target on
        // the dim layer would route the operator off the tour. To
        // gate this, the overlay captures pointer events (CSS:
        // pointer-events: auto). The handler below classifies each
        // click and either forwards it to the underlying target, lets
        // the tooltip's own handler fire, or swallows it + flashes
        // the spotlight so the operator's eye snaps back. Operator
        // feedback: 'only the buttons/interactions that i should be
        // able to click during the tour should be clickable/doable.
        // otherwise i get off the track.'
        function _gateClick(e) {
            if (!_tourState.running) return;
            // When a modal dialog (.bowire-confirm-overlay /
            // .bowire-prompt-overlay) is on screen, it sits at
            // z-index 10003 — ABOVE the tour overlay (10001) — and
            // captures its own clicks + focus + keyboard. The gate
            // here should yield entirely so the dialog's input
            // fields work. Operator feedback: 'ich kann im popup für
            // einen neuen workspace nichts eingeben.' The tooltip
            // (10004) still floats above the dialog for skip / next
            // / X-close affordances.
            if (document.querySelector('.bowire-confirm-overlay, .bowire-prompt-overlay')) {
                return;
            }
            // Tooltip (X close, Skip, Back, CTA, Next) — let the
            // child elements' own onclick run naturally. The handler
            // is registered with bubble phase so the tooltip's
            // descendants have already had their handlers fire by the
            // time we get here for a tooltip click — nothing to do.
            if (_tourState.tooltipEl && _tourState.tooltipEl.contains(e.target)) {
                return;
            }
            // Target rect (primary OR any alternative target) — forward
            // the click. The overlay sits on top of the page so the
            // original click landed on the overlay, not the target. We
            // temporarily disable pointer-events on the overlay so
            // elementFromPoint returns the underlying element, then
            // dispatch a synthetic click on it.
            //
            // Alternatives let the operator drive the workflow via any
            // of several equivalent affordances (e.g. Home-rail welcome
            // CTA OR the topbar workspace dropdown). The advance still
            // fires on whatever the step's `advance` says — typically
            // an 'on-event:' signal both paths emit.
            var hitTargets = _allClickableTargetsForStep();
            for (var i = 0; i < hitTargets.length; i++) {
                var t = hitTargets[i];
                var rect = t.getBoundingClientRect();
                if (e.clientX >= rect.left && e.clientX <= rect.right
                    && e.clientY >= rect.top && e.clientY <= rect.bottom) {
                    var was = overlay.style.pointerEvents;
                    overlay.style.pointerEvents = 'none';
                    var hit = document.elementFromPoint(e.clientX, e.clientY);
                    overlay.style.pointerEvents = was;
                    if (hit && typeof hit.click === 'function') {
                        try { hit.click(); }
                        catch (err) { console.warn('[tour] target click forward failed', err); }
                    }
                    return;
                }
            }
            // Off-target click — swallow it + flash the spotlight so
            // the operator's eye snaps back to the highlighted
            // affordance.
            e.preventDefault();
            e.stopPropagation();
            if (_tourState.targetEl) {
                _tourState.targetEl.classList.add('bowire-tour-target-flash');
                setTimeout(function () {
                    if (_tourState.targetEl) {
                        _tourState.targetEl.classList.remove('bowire-tour-target-flash');
                    }
                }, 420);
            }
        }
        overlay.addEventListener('click', _gateClick);
        // Also gate mousedown so a drag-start on off-target chrome
        // doesn't start a text selection or trigger any pre-click
        // handler bound on mousedown elsewhere. Mirrors the click-gate's
        // alternatives-aware hit test so the operator can mousedown on
        // any equivalent affordance without it getting swallowed.
        overlay.addEventListener('mousedown', function (e) {
            if (!_tourState.running) return;
            // Yield to active modal dialogs (see _gateClick).
            if (document.querySelector('.bowire-confirm-overlay, .bowire-prompt-overlay')) return;
            if (_tourState.tooltipEl && _tourState.tooltipEl.contains(e.target)) return;
            var hits = _allClickableTargetsForStep();
            for (var i = 0; i < hits.length; i++) {
                var r = hits[i].getBoundingClientRect();
                if (e.clientX >= r.left && e.clientX <= r.right
                    && e.clientY >= r.top && e.clientY <= r.bottom) return;
            }
            e.preventDefault();
            e.stopPropagation();
        });
    }

    // Return every element a click should pass through on the current
    // step — the primary target plus any alternative-path targets the
    // step descriptor listed. Resolved fresh each event because morphdom
    // can replace the node between renders.
    function _allClickableTargetsForStep() {
        var out = [];
        if (_tourState.targetEl) out.push(_tourState.targetEl);
        var step = _tourState.steps[_tourState.index];
        if (step && Array.isArray(step.alternatives)) {
            step.alternatives.forEach(function (alt) {
                if (!alt || !alt.target) return;
                var el = _resolveStepTarget(alt.target);
                if (el && out.indexOf(el) === -1) out.push(el);
            });
        }
        return out;
    }

    function _unmountTourOverlay() {
        if (_tourState.overlayEl) {
            _tourState.overlayEl.remove();
            _tourState.overlayEl = null;
            _tourState.tooltipEl = null;
        }
        _restorePrevTargetPointerEvents();
        _tourState.targetEl = null;
    }

    function _paintOverlay(target) {
        var overlay = _tourState.overlayEl;
        if (!overlay) return;

        var cutout = overlay.querySelector('.bowire-tour-cutout');
        var halo = overlay.querySelector('.bowire-tour-halo');
        var altMaskGroup = overlay.querySelector('.bowire-tour-mask-alts');
        var altHaloGroup = overlay.querySelector('.bowire-tour-halo-alts');
        if (!cutout || !halo) return;

        // Collect alternative-target elements that resolve right now AND
        // sit inside the viewport — alts off-screen would punch a black
        // rect into the dim layer at coords nobody can see.
        var altRects = _collectAltRectsForCurrentStep(target);

        if (!target) {
            // No target → no cutout (the whole viewport stays dimmed,
            // and the tooltip self-centers via _paintTooltip).
            cutout.setAttribute('x', '-9999');
            cutout.setAttribute('y', '-9999');
            cutout.setAttribute('width', '0');
            cutout.setAttribute('height', '0');
            halo.setAttribute('x', '-9999');
            halo.setAttribute('y', '-9999');
            halo.setAttribute('width', '0');
            halo.setAttribute('height', '0');
            _syncAltMaskAndHalo(altMaskGroup, altHaloGroup, altRects);
            return;
        }

        // Scroll the target into view if it's off-screen. The 'center'
        // block keeps the operator's eye on the same vertical anchor
        // as the tooltip lands at, which feels less jumpy than the
        // default 'start'.
        var rect = target.getBoundingClientRect();
        var offscreen = rect.bottom < 0 || rect.top > window.innerHeight
                     || rect.right < 0 || rect.left > window.innerWidth;
        if (offscreen) {
            try { target.scrollIntoView({ behavior: 'smooth', block: 'center', inline: 'center' }); }
            catch { target.scrollIntoView(); }
            // Re-read after the scroll has had a frame to settle.
            requestAnimationFrame(function () {
                if (!_tourState.running) return;
                var r2 = target.getBoundingClientRect();
                _paintCutoutFromRect(cutout, halo, r2);
                _syncAltMaskAndHalo(altMaskGroup, altHaloGroup,
                    _collectAltRectsForCurrentStep(target));
                _paintTooltipFromTarget(target);
            });
            return;
        }

        _paintCutoutFromRect(cutout, halo, rect);
        _syncAltMaskAndHalo(altMaskGroup, altHaloGroup, altRects);
        // Keep the target clickable through the dim layer. The overlay
        // has pointer-events:none on its dim svg layer (via CSS); we
        // also lift the target's stacking so any sibling that was
        // overlapping doesn't intercept the click.
        _ensureTargetClickable(target);
    }

    function _paintCutoutFromRect(cutout, halo, rect) {
        // 6px padding so the cutout reads as a frame around the
        // target instead of cutting into it.
        var pad = 6;
        var x = Math.max(0, rect.left - pad);
        var y = Math.max(0, rect.top - pad);
        var w = rect.width + pad * 2;
        var h = rect.height + pad * 2;
        cutout.setAttribute('x', String(x));
        cutout.setAttribute('y', String(y));
        cutout.setAttribute('width', String(w));
        cutout.setAttribute('height', String(h));
        halo.setAttribute('x', String(x));
        halo.setAttribute('y', String(y));
        halo.setAttribute('width', String(w));
        halo.setAttribute('height', String(h));
    }

    // Resolve every alternative-path target for the current step and
    // return its on-screen rect alongside the element (so the tooltip's
    // hover-to-flash wiring can re-find the actual DOM node). Off-screen
    // alts are dropped — punching a cutout where nothing is visible just
    // dims-then-undims a random spot. Primary target is excluded because
    // it gets the louder full halo via _paintCutoutFromRect.
    function _collectAltRectsForCurrentStep(primaryTarget) {
        var step = _tourState.steps[_tourState.index];
        if (!step || !Array.isArray(step.alternatives)) return [];
        var vw = window.innerWidth, vh = window.innerHeight;
        var out = [];
        step.alternatives.forEach(function (alt) {
            if (!alt || !alt.target) return;
            var el = _resolveStepTarget(alt.target);
            if (!el || el === primaryTarget) return;
            var r = el.getBoundingClientRect();
            // Skip zero-size / fully-offscreen alts so we don't paint a
            // cutout the operator can't see anyway.
            if (r.width <= 0 || r.height <= 0) return;
            if (r.bottom < 0 || r.top > vh || r.right < 0 || r.left > vw) return;
            out.push({ el: el, rect: r });
        });
        return out;
    }

    // Pool child <rect> elements inside the mask group (extra black
    // cutouts) and the halo group (thinner alt strokes) so alt count
    // changes across steps don't leak orphan SVG nodes.
    function _syncAltMaskAndHalo(maskGroup, haloGroup, altRects) {
        if (!maskGroup || !haloGroup) return;
        var svgNS = 'http://www.w3.org/2000/svg';
        var pad = 6;

        // Grow / shrink mask cutouts.
        while (maskGroup.childNodes.length < altRects.length) {
            var mr = document.createElementNS(svgNS, 'rect');
            mr.setAttribute('rx', '8');
            mr.setAttribute('ry', '8');
            mr.setAttribute('fill', 'black');
            mr.classList.add('bowire-tour-cutout-alt');
            maskGroup.appendChild(mr);
        }
        while (maskGroup.childNodes.length > altRects.length) {
            maskGroup.removeChild(maskGroup.lastChild);
        }

        // Grow / shrink halo rings.
        while (haloGroup.childNodes.length < altRects.length) {
            var hr = document.createElementNS(svgNS, 'rect');
            hr.setAttribute('rx', '8');
            hr.setAttribute('ry', '8');
            hr.setAttribute('fill', 'none');
            hr.setAttribute('stroke-width', '1.5');
            hr.classList.add('bowire-tour-halo-alt');
            haloGroup.appendChild(hr);
        }
        while (haloGroup.childNodes.length > altRects.length) {
            haloGroup.removeChild(haloGroup.lastChild);
        }

        // Position both layers off the same rect.
        for (var i = 0; i < altRects.length; i++) {
            var r = altRects[i].rect;
            var x = Math.max(0, r.left - pad);
            var y = Math.max(0, r.top - pad);
            var w = r.width + pad * 2;
            var h = r.height + pad * 2;
            var cm = maskGroup.childNodes[i];
            var ch = haloGroup.childNodes[i];
            cm.setAttribute('x', String(x));
            cm.setAttribute('y', String(y));
            cm.setAttribute('width', String(w));
            cm.setAttribute('height', String(h));
            ch.setAttribute('x', String(x));
            ch.setAttribute('y', String(y));
            ch.setAttribute('width', String(w));
            ch.setAttribute('height', String(h));
            // Index attribute lets the alt tooltip line wire hover-flash
            // against the right halo if we ever want to flash the ring
            // itself; for now we flash the underlying DOM element.
            ch.setAttribute('data-alt-index', String(i));
        }
    }

    function _ensureTargetClickable(target) {
        if (!target) return;
        // Stash the inline pointer-events value so we restore it on
        // teardown. Set to 'auto' to defeat any ancestor that's
        // accidentally pointer-events:none (rare but happens with the
        // dim overlay's own ancestors during transitions).
        if (_tourState.prevPointerEventsTarget && _tourState.prevPointerEventsTarget.el !== target) {
            _restorePrevTargetPointerEvents();
        }
        _tourState.prevPointerEventsTarget = {
            el: target,
            prev: target.style.pointerEvents,
            prevZ: target.style.zIndex,
            prevPos: target.style.position
        };
        target.style.pointerEvents = 'auto';
        // Lift above the overlay so the target wins hit-testing
        // against the dim svg layer (overlay z-index = 10001 via CSS).
        if (!target.style.position || target.style.position === 'static') {
            target.style.position = 'relative';
        }
        target.style.zIndex = '10003';
    }

    function _restorePrevTargetPointerEvents() {
        var p = _tourState.prevPointerEventsTarget;
        if (!p || !p.el) { _tourState.prevPointerEventsTarget = null; return; }
        try {
            p.el.style.pointerEvents = p.prev || '';
            p.el.style.zIndex = p.prevZ || '';
            p.el.style.position = p.prevPos || '';
        } catch { /* element detached — fine */ }
        _tourState.prevPointerEventsTarget = null;
    }

    // ---- Tooltip + advance UI ------------------------------------------

    function _paintTooltip(step, target) {
        var tip = _tourState.tooltipEl;
        if (!tip) return;

        // Clear + rebuild — easier than diffing.
        while (tip.firstChild) tip.removeChild(tip.firstChild);

        // Top-right (x) cancel — operator-requested escape hatch from
        // any step, sitting in the standard 'close this dialog' slot
        // so it's findable without thinking. The Skip button at the
        // footer still works the same; this just gives the operator
        // a second affordance in the place dialog-closes usually live.
        // Operator feedback: 'man sollte auch immer eine möglichkeit
        // haben die tour abzubrechen, z.B. (x)-button oben im tour
        // dialog?'.
        var closeBtn = document.createElement('button');
        closeBtn.type = 'button';
        closeBtn.className = 'bowire-tour-close';
        closeBtn.title = 'Close tour';
        closeBtn.setAttribute('aria-label', 'Close tour');
        if (typeof svgIcon === 'function') {
            closeBtn.innerHTML = svgIcon('close');
        } else {
            closeBtn.textContent = '×';
        }
        closeBtn.onclick = function () { tourStop(); };
        tip.appendChild(closeBtn);

        if (step.title) {
            var h = document.createElement('div');
            h.className = 'bowire-tour-title';
            h.textContent = step.title;
            tip.appendChild(h);
        }
        if (step.body) {
            var b = document.createElement('div');
            b.className = 'bowire-tour-body';
            // Multi-paragraph support — split on \n\n.
            var paras = String(step.body).split(/\n\n+/);
            paras.forEach(function (p) {
                var pe = document.createElement('p');
                pe.textContent = p;
                b.appendChild(pe);
            });
            tip.appendChild(b);
        }

        // Alternative-path hints — each step may name secondary
        // affordances that lead to the same advance. The spotlight stays
        // on the primary target (single eye anchor) but the operator
        // sees a small "Tip:" line for each alternative, and the
        // click-gate forwards clicks through every named element so the
        // operator can drive the workflow via whichever path they prefer.
        // Only render alternatives that actually resolve right now —
        // an alt pointing at a closed dropdown shouldn't taunt the
        // operator with a path they can't take from this surface.
        if (Array.isArray(step.alternatives) && step.alternatives.length > 0) {
            var altsResolved = step.alternatives.filter(function (alt) {
                return alt && alt.label && _resolveStepTarget(alt.target);
            });
            if (altsResolved.length > 0) {
                var altWrap = document.createElement('div');
                altWrap.className = 'bowire-tour-alts';
                altsResolved.forEach(function (alt) {
                    var line = document.createElement('div');
                    line.className = 'bowire-tour-alt';
                    var prefix = document.createElement('span');
                    prefix.className = 'bowire-tour-alt-prefix';
                    prefix.textContent = 'Tip:';
                    line.appendChild(prefix);
                    var text = document.createElement('span');
                    text.className = 'bowire-tour-alt-text';
                    text.textContent = ' ' + alt.label;
                    line.appendChild(text);
                    // Hover-link the tooltip line to the spotlit alt — the
                    // operator hovers the 'Tip:' line and the corresponding
                    // affordance flashes, so they can map the prose to the
                    // exact button on the page. Re-resolve at hover-time
                    // because morphdom can swap the underlying node between
                    // renders (see feedback_morphdom_stale_handler_pitfall).
                    line.onmouseenter = function () {
                        var el = _resolveStepTarget(alt.target);
                        if (!el) return;
                        el.classList.add('bowire-tour-target-flash');
                    };
                    line.onmouseleave = function () {
                        var el = _resolveStepTarget(alt.target);
                        if (!el) return;
                        el.classList.remove('bowire-tour-target-flash');
                    };
                    // Clicking the Tip: line itself forwards to the alt —
                    // convenient when the alt is far across the viewport
                    // and the operator already has the pointer on the
                    // tooltip. Advance still fires off the awaited event
                    // (workflow is identical to a direct click on the alt).
                    line.onclick = function (e) {
                        e.stopPropagation();
                        var el = _resolveStepTarget(alt.target);
                        if (el && typeof el.click === 'function') {
                            try { el.click(); }
                            catch (err) { console.warn('[tour] alt forward-click failed', err); }
                        }
                    };
                    altWrap.appendChild(line);
                });
                tip.appendChild(altWrap);
            }
        }

        var foot = document.createElement('div');
        foot.className = 'bowire-tour-footer';

        var counter = document.createElement('span');
        counter.className = 'bowire-tour-counter';
        counter.textContent = (_tourState.index + 1) + ' / ' + _tourState.steps.length;
        foot.appendChild(counter);

        var actions = document.createElement('div');
        actions.className = 'bowire-tour-actions';

        // Helper: build an icon-only button with a tooltip. The text
        // labels for Skip/Back/Skip-step on a 4-button row used to
        // wrap onto two lines on narrower spotlights — operator
        // feedback: 'in der tour sind die buttons unten manchmal sehr
        // gedrängt und umgebrochen. es würde auch helfen, wenn man
        // statt dem text für die buttons diesen lieber als symbol mit
        // tooltip anzeigen würde.' Primary CTA / Next / Finish stay
        // text-labelled because their wording is meaningful per-step.
        function _iconBtn(cls, iconKey, tooltip, onClick, iconStyle) {
            var b = document.createElement('button');
            b.type = 'button';
            b.className = cls + ' bowire-tour-btn-iconic';
            b.title = tooltip;
            b.setAttribute('aria-label', tooltip);
            // svgIcon is on window inside the IIFE bundle; reach in
            // safely (tour.js compiles into the same closure).
            if (typeof svgIcon === 'function') {
                b.innerHTML = svgIcon(iconKey);
                if (iconStyle) b.firstChild && b.firstChild.setAttribute
                    && b.firstChild.setAttribute('style', iconStyle);
            } else {
                b.textContent = tooltip; // SSR / failure fallback
            }
            b.onclick = onClick;
            return b;
        }

        // Skip — always present so the operator can bail anywhere.
        actions.appendChild(_iconBtn('bowire-tour-btn-skip', 'close',
            'Skip tour', function () { tourStop(); }));

        // Back — present from step 2 onwards. Chevron flipped left
        // via inline transform on the svg.
        if (_tourState.index > 0) {
            actions.appendChild(_iconBtn('bowire-tour-btn-secondary', 'chevron',
                'Back', function () { _tourState.index--; _renderTourStep(); },
                'transform: rotate(180deg)'));
        }

        var isLast = _tourState.index === _tourState.steps.length - 1;
        var advance = step.advance || 'next-button';

        if (advance === 'on-cta' && step.cta) {
            // Single primary CTA — clicking advances after running
            // the step's onClick.
            var ctaBtn = document.createElement('button');
            ctaBtn.type = 'button';
            ctaBtn.className = 'bowire-tour-btn-primary';
            ctaBtn.textContent = step.cta.label || (isLast ? 'Finish' : 'Next');
            ctaBtn.onclick = function () {
                if (step.cta && typeof step.cta.onClick === 'function') {
                    try { step.cta.onClick(); }
                    catch (e) { console.warn('[tour] cta.onClick failed', e); }
                }
                _advanceOrFinish();
            };
            actions.appendChild(ctaBtn);
        } else if (advance && advance.indexOf('on-event:') === 0) {
            // Waiting for an external signal. If the step defines a
            // CTA with an onClick (e.g. 'Create workspace…'), expose
            // it as a real clickable primary button so the operator
            // can drive the workflow from the tour itself — the
            // previous render used a passive italic span that LOOKED
            // like a link but had no handler. Operator feedback:
            // '"Create workspace…" button link aus der tour heraus
            // geht auch nicht. warum?'.
            if (step.cta && typeof step.cta.onClick === 'function') {
                var ctaBtnE = document.createElement('button');
                ctaBtnE.type = 'button';
                ctaBtnE.className = 'bowire-tour-btn-primary';
                ctaBtnE.textContent = step.cta.label || 'Continue';
                ctaBtnE.onclick = function () {
                    try { step.cta.onClick(); }
                    catch (e) { console.warn('[tour] cta.onClick failed', e); }
                    // No _advanceOrFinish here — the tour advances
                    // when the awaited event fires (wired in
                    // _wireAdvanceMode), so the CTA only kicks off
                    // the workflow.
                };
                actions.appendChild(ctaBtnE);
                actions.appendChild(_iconBtn('bowire-tour-btn-secondary', 'chevron',
                    'Skip this step', function () { _advanceOrFinish(); }));
            } else if (step.cta && step.cta.label) {
                // No onClick — purely declarative 'Waiting for …' hint.
                var waiting = document.createElement('span');
                waiting.className = 'bowire-tour-waiting';
                waiting.textContent = step.cta.label;
                actions.appendChild(waiting);
            } else {
                var waiting2 = document.createElement('span');
                waiting2.className = 'bowire-tour-waiting';
                waiting2.textContent = 'Waiting…';
                actions.appendChild(waiting2);
            }
        } else {
            // 'next-button' / default — manual advance.
            var nextBtn = document.createElement('button');
            nextBtn.type = 'button';
            nextBtn.className = 'bowire-tour-btn-primary';
            nextBtn.textContent = isLast ? 'Finish' : 'Next';
            nextBtn.onclick = function () { _advanceOrFinish(); };
            actions.appendChild(nextBtn);
        }
        foot.appendChild(actions);
        tip.appendChild(foot);

        _paintTooltipFromTarget(target);
    }

    function _paintTooltipFromTarget(target) {
        var tip = _tourState.tooltipEl;
        if (!tip) return;

        // Reset any prior anchor classes so the arrow re-orients.
        tip.classList.remove('bowire-tour-tip-top', 'bowire-tour-tip-bottom',
                             'bowire-tour-tip-left', 'bowire-tour-tip-right',
                             'bowire-tour-tip-center');

        if (!target) {
            tip.classList.add('bowire-tour-tip-center');
            tip.style.left = '50%';
            tip.style.top = '50%';
            tip.style.transform = 'translate(-50%, -50%)';
            return;
        }
        tip.style.transform = '';

        var rect = target.getBoundingClientRect();
        var tipRect = tip.getBoundingClientRect();
        var margin = 18; // gap between target and tooltip (visual breathing room)
        var vw = window.innerWidth;
        var vh = window.innerHeight;

        // Pick the side with the most room. Preference order:
        //   bottom > right > top > left
        // because bottom-anchored callouts feel most natural for top-
        // anchored CTAs (which is what most workbench targets are).
        var spaceBottom = vh - rect.bottom;
        var spaceTop    = rect.top;
        var spaceRight  = vw - rect.right;
        var spaceLeft   = rect.left;

        var placement;
        if (spaceBottom >= tipRect.height + margin) placement = 'bottom';
        else if (spaceRight >= tipRect.width + margin) placement = 'right';
        else if (spaceTop >= tipRect.height + margin) placement = 'top';
        else if (spaceLeft >= tipRect.width + margin) placement = 'left';
        else placement = 'bottom'; // fall through — clamp below

        var left, top;
        if (placement === 'bottom') {
            top = rect.bottom + margin;
            left = rect.left + (rect.width / 2) - (tipRect.width / 2);
            tip.classList.add('bowire-tour-tip-bottom');
        } else if (placement === 'top') {
            top = rect.top - margin - tipRect.height;
            left = rect.left + (rect.width / 2) - (tipRect.width / 2);
            tip.classList.add('bowire-tour-tip-top');
        } else if (placement === 'right') {
            top = rect.top + (rect.height / 2) - (tipRect.height / 2);
            left = rect.right + margin;
            tip.classList.add('bowire-tour-tip-right');
        } else { // left
            top = rect.top + (rect.height / 2) - (tipRect.height / 2);
            left = rect.left - margin - tipRect.width;
            tip.classList.add('bowire-tour-tip-left');
        }

        // Clamp into the viewport with a small inset so the tooltip
        // never butts up against the edge.
        var inset = 12;
        if (left + tipRect.width > vw - inset) left = vw - tipRect.width - inset;
        if (left < inset) left = inset;
        if (top + tipRect.height > vh - inset) top = vh - tipRect.height - inset;
        if (top < inset) top = inset;

        tip.style.left = left + 'px';
        tip.style.top = top + 'px';

        // Arrow position — anchored on the cross-axis center of the
        // target so the callout reads as 'this thing'. Custom property
        // consumed by the ::before pseudo-element in CSS.
        if (placement === 'bottom' || placement === 'top') {
            var arrowX = (rect.left + rect.width / 2) - left;
            arrowX = Math.max(16, Math.min(tipRect.width - 16, arrowX));
            tip.style.setProperty('--bowire-tour-arrow-x', arrowX + 'px');
        } else {
            var arrowY = (rect.top + rect.height / 2) - top;
            arrowY = Math.max(16, Math.min(tipRect.height - 16, arrowY));
            tip.style.setProperty('--bowire-tour-arrow-y', arrowY + 'px');
        }
    }

    // ---- Advance modes -------------------------------------------------

    function _wireAdvanceMode(step) {
        if (!step) return;
        var advance = step.advance || 'next-button';
        if (advance.indexOf('on-event:') === 0) {
            var evtName = advance.slice('on-event:'.length);
            var handler = function () { _advanceOrFinish(); };
            document.addEventListener('bowire-tour:' + evtName, handler);
            _tourState.eventListeners.push({ name: 'bowire-tour:' + evtName, handler: handler });
        }
    }

    function _detachEventAdvanceListeners() {
        var list = _tourState.eventListeners || [];
        list.forEach(function (e) {
            try { document.removeEventListener(e.name, e.handler); } catch { /* ignore */ }
        });
        _tourState.eventListeners = [];
    }

    function _advanceOrFinish() {
        if (!_tourState.running) return;
        _tourState.index++;
        if (_tourState.index >= _tourState.steps.length) {
            tourStop();
            return;
        }
        _renderTourStep();
    }

    // ---- Reflow listeners ----------------------------------------------

    function _attachReflowListeners() {
        if (_tourState.resizeHandler) return;
        var reflow = function () { _scheduleReflow(); };
        window.addEventListener('resize', reflow);
        document.addEventListener('scroll', reflow, true);
        _tourState.resizeHandler = reflow;
        _tourState.scrollHandler = reflow;
        // morphdom commits land WITHOUT a resize / scroll event, so we
        // also hook into the global render() via a post-commit
        // listener exposed below. init.js's render() fires
        // 'bowire-rendered' on document after every commit (we add
        // that fire-site as part of this patch).
        var rerender = function () { _scheduleReflow(); };
        document.addEventListener('bowire-rendered', rerender);
        _tourState.rerenderHandler = rerender;
    }

    function _detachReflowListeners() {
        if (_tourState.resizeHandler) {
            window.removeEventListener('resize', _tourState.resizeHandler);
            _tourState.resizeHandler = null;
        }
        if (_tourState.scrollHandler) {
            document.removeEventListener('scroll', _tourState.scrollHandler, true);
            _tourState.scrollHandler = null;
        }
        if (_tourState.rerenderHandler) {
            document.removeEventListener('bowire-rendered', _tourState.rerenderHandler);
            _tourState.rerenderHandler = null;
        }
        if (_tourState.rafHandle) {
            cancelAnimationFrame(_tourState.rafHandle);
            _tourState.rafHandle = null;
        }
    }

    function _scheduleReflow() {
        if (!_tourState.running) return;
        if (_tourState.rafHandle) return;
        _tourState.rafHandle = requestAnimationFrame(function () {
            _tourState.rafHandle = null;
            if (!_tourState.running) return;
            var step = _tourState.steps[_tourState.index];
            if (!step) return;
            // Re-resolve the target in case morphdom replaced the node
            // (we attach our pointer-events / z-index inline-style to
            // the live element, so a node swap orphans our overrides).
            var fresh = step.target ? _resolveStepTarget(step.target) : null;
            if (fresh && fresh !== _tourState.targetEl) {
                _restorePrevTargetPointerEvents();
                _tourState.targetEl = fresh;
            }
            _paintOverlay(_tourState.targetEl);
            _paintTooltipFromTarget(_tourState.targetEl);
        });
    }

    // ---- Built-in tours ------------------------------------------------

    // Reusable workspace-create fragment — extracted from
    // _gettingStartedSteps() so per-rail prerequisite tours can run the
    // same four-step walk-through (welcome CTA → name → template →
    // submit) without copy-pasting. Operator: 'try to reuse tours (not
    // create a "create workspace tour" for every rail as a copy).'
    //
    // Step 1 lists the topbar workspace chip as an alternative entry
    // point so the operator can drive the dialog from either the Home
    // welcome card OR the chip dropdown's "+ New workspace" item — both
    // surfaces end up firing the same ws-dialog-open event that
    // advances the tour. Operator: 'sometimes there are alternative
    // paths, e.g. for the workspace you can use the dropdown, too.'
    function _createWorkspaceSteps() {
        return [
            {
                id: 'create-first-workspace',
                title: 'Create a workspace',
                body: 'A workspace is your project folder. It holds the URLs you discover, the environments + secrets you reference, and the collections / recordings / benchmarks you build.\n\nMost operators name them after the project ("Petstore Staging", "Internal CMS").',
                target: '#bowire-welcome-create-btn',
                alternatives: [
                    {
                        target: '#bowire-workspace-chip',
                        label: 'You can also open the workspace chip in the topbar and pick "+ New workspace".'
                    }
                ],
                navigate: function () {
                    if (typeof railMode !== 'undefined') {
                        railMode = 'home';
                        try { localStorage.setItem('bowire_rail_mode', 'home'); } catch { /* ignore */ }
                    }
                    if (typeof render === 'function') render();
                },
                cta: {
                    label: 'Create workspace…',
                    onClick: function () {
                        if (typeof openCreateWorkspaceDialog === 'function') {
                            openCreateWorkspaceDialog(function (ws) {
                                if (ws && typeof activeWorkspaceId !== 'undefined') {
                                    activeWorkspaceId = ws.id;
                                }
                                if (typeof render === 'function') render();
                                tourFireEvent('workspace-created');
                            });
                            // Once the dialog is in the DOM, advance the
                            // tour to the in-dialog walkthrough.
                            setTimeout(function () {
                                if (document.querySelector('.bowire-ws-create-dialog')) {
                                    tourFireEvent('ws-dialog-open');
                                }
                            }, 50);
                        }
                    }
                },
                advance: 'on-event:ws-dialog-open'
            },
            {
                // In-dialog walkthrough A: name the workspace.
                // Operator feedback: 'in der tour um einen workspace
                // anzulegen, da fehlt im bzw. nach schritt 2/6, dass er
                // den popup dialog auch highlighted nach dem klick auf
                // new workspace und dann durch den popup dialog führt
                // (eingabe name des workspaces, auswahl des templates)'.
                id: 'ws-dialog-name',
                title: 'Name your workspace',
                body: 'Type a name. Most operators name after the project ("Petstore Staging", "Internal CMS").\n\nClick Next once you have a name.',
                target: '#bowire-ws-create-name',
                advance: 'next-button'
            },
            {
                // In-dialog walkthrough B: template selection.
                id: 'ws-dialog-template',
                title: 'Start from scratch or a template',
                body: 'A template seeds the workspace with example URLs, environments, and collections so you can explore immediately. Try the REST or gRPC template — or stay on "Empty" to start clean.\n\nClick Next once you pick.',
                target: '#bowire-ws-create-templates',
                advance: 'next-button'
            },
            {
                // In-dialog walkthrough C: hit Create. Advances on the
                // workspace-created event the dialog\'s commit() fires
                // once the workspace is persisted.
                id: 'ws-dialog-submit',
                title: 'Create it',
                body: 'Click Create. Bowire will set up the workspace, seed any chosen template, and switch the workbench to it.',
                target: '#bowire-ws-create-submit',
                advance: 'on-event:workspace-created'
            }
        ];
    }

    // Standalone entry point for the create-workspace fragment. Exposed
    // via window.bowireStartCreateWorkspaceTour and called from the
    // shared workspace-prereq empty card (renderWorkspacePrereqEmpty in
    // helpers.js) so the operator can opt into a short tour explaining
    // why a workspace is required + how to create one.
    function tourStartCreateWorkspace(opts) {
        opts = opts || {};
        tourStart(_createWorkspaceSteps(), {
            id: 'create-workspace-fragment',
            force: !!opts.force,
            onFinish: opts.onFinish || null
        });
    }

    // Getting started — walks a brand-new operator from 'I just launched
    // bowire' to 'I see a response in the console'. Composes
    // _createWorkspaceSteps() in the middle so the workspace-create
    // affordance stays in sync across every entry point that teaches it.
    // Selectors target stable DOM ids (preferred) or class hooks that
    // exist regardless of which protocol the operator points Bowire at.
    function _gettingStartedSteps() {
        var welcomeStep = {
            id: 'welcome',
            title: 'Welcome to Bowire',
            body: 'A multi-protocol API workbench — gRPC, REST, GraphQL, WebSocket, SSE, MQTT, all in one place.\n\nThis quick tour walks you from a blank workbench to your first response in five steps. You can skip anytime; we won\'t bring it up again.',
            target: null,
            advance: 'next-button'
        };
        var laterSteps = [
            {
                id: 'add-url',
                title: 'Step 2 — Point at your API',
                body: 'Open the Workspaces rail and add the URL of the API you want to call. Bowire works with any URL that exposes a discovery surface — OpenAPI / Swagger, gRPC reflection, GraphQL introspection — or a schema file you upload.\n\nGood test URLs to try: petstore3.swagger.io/api/v3/openapi.json, countries.trevorblades.com.',
                target: '[data-rail-mode-id="workspaces"]',
                navigate: function () {
                    if (typeof railMode !== 'undefined') {
                        railMode = 'workspaces';
                        try { localStorage.setItem('bowire_rail_mode', 'workspaces'); } catch { /* ignore */ }
                    }
                    if (typeof activeWorkspaceId !== 'undefined'
                        && typeof workspacesSelectedId !== 'undefined') {
                        workspacesSelectedId = activeWorkspaceId;
                    }
                    if (typeof render === 'function') render();
                },
                advance: 'next-button'
            },
            {
                id: 'discover',
                title: 'Step 3 — Discover services',
                body: 'Switch to the Discover rail. Once Bowire fetches your URL, it lists every service + method it found — grouped by protocol, searchable, filterable.\n\nClick a method to open it in a new tab.',
                target: '[data-rail-mode-id="discover"]',
                navigate: function () {
                    if (typeof railMode !== 'undefined') {
                        railMode = 'discover';
                        try { localStorage.setItem('bowire_rail_mode', 'discover'); } catch { /* ignore */ }
                    }
                    if (typeof sidebarView !== 'undefined') sidebarView = 'services';
                    if (typeof render === 'function') render();
                },
                advance: 'next-button'
            },
            {
                id: 'execute',
                title: 'Step 4 — Send the request',
                body: 'Once you have a method open, fill in the request form (or paste JSON) and hit Execute. Ctrl+Enter from anywhere in the request pane fires it too.\n\nThe response lands underneath — JSON, status, timing, headers — and the call is captured in your history so you can replay it later.',
                target: '.bowire-execute-btn',
                advance: 'next-button'
            },
            {
                id: 'wrap-up',
                title: 'You\'re set',
                body: 'That\'s the core loop: workspace → URL → discover → invoke. From here you can record a session and turn it into a mock, build collections, write tests, or wire envs + secrets.\n\nPress F1 anywhere for in-app docs. Ctrl+/ shows every keyboard shortcut. Have fun.',
                target: null,
                advance: 'next-button'
            }
        ];
        return [welcomeStep].concat(_createWorkspaceSteps()).concat(laterSteps);
    }

    function tourStartGettingStarted(opts) {
        opts = opts || {};
        tourStart(_gettingStartedSteps(), {
            id: 'getting-started',
            force: !!opts.force,
            onFinish: opts.onFinish || null
        });
    }

    // ---- #303 — Per-rail empty-state secondary tours ------------------
    //
    // Three short tours, opt-in from the rail's empty card. Each one
    // is force-runnable (the empty card's CTA passes force:true so the
    // operator can re-trigger after dismissing) but still saved-once
    // so the CTA stops feeling chatty after the first run.

    // Shared helper — jump to a rail mode and persist the choice so a
    // mid-tour render() doesn't snap back. Mirrors the inline pattern
    // from _gettingStartedSteps but factored because the per-rail tours
    // do this on most steps.
    function _tourGoToRail(mode) {
        if (typeof railMode !== 'undefined') {
            railMode = mode;
            try { localStorage.setItem('bowire_rail_mode', mode); } catch { /* ignore */ }
        }
        if (typeof render === 'function') render();
    }

    // 'Build a mock' — Recordings rail empty.
    //
    // The path is: pick (or capture) a recording → "Use as mock" →
    // mock host appears on the Mocks rail with its URL. We can't
    // guarantee a recording exists, so the tour stays narrative on the
    // first step and switches to live targets once we're on the
    // Recordings rail.
    function _buildMockSteps() {
        return [
            {
                id: 'mock-intro',
                title: 'Build a mock from a recording',
                body: 'Mocks let you replay a captured session as a local HTTP server — handy for offline development, demos, or wiring an integration test against a frozen response set.\n\nYou need a recording first. If you don\'t have one yet, capture a few calls from Discover; the tour points you there.',
                target: null,
                advance: 'next-button'
            },
            {
                id: 'mock-go-recordings',
                title: 'Step 1 — Open the Recordings rail',
                body: 'Switch to the Recordings rail. If you already have a saved recording it shows in the sidebar; otherwise the empty card walks you through capturing one from Discover.',
                target: '[data-rail-mode-id="recordings"]',
                navigate: function () { _tourGoToRail('recordings'); },
                advance: 'next-button'
            },
            {
                id: 'mock-use-as-mock',
                title: 'Step 2 — Use as mock',
                body: 'Pick a recording, then hit "Use as mock" in its detail toolbar. Bowire spins up a local MockServer on a free port and copies the URL to your clipboard.',
                target: '#bowire-main-recordings',
                cta: {
                    label: 'Waiting for mock to start…'
                },
                advance: 'on-event:mock-started'
            },
            {
                id: 'mock-switch-to-mocks',
                title: 'Step 3 — Inspect the running mock',
                body: 'The Intercept rail\'s "Mock servers" sub-tab lists every mock host you\'ve started. Pick one to see its URL, copy it again, open the live request log, or stop the server.\n\nFire requests against the mock\'s port from your other tools and watch them stream in.',
                target: '[data-rail-mode-id="intercept"]',
                navigate: function () {
                    try { localStorage.setItem('bowire_intercept_sub_tab', 'mock-servers'); } catch { /* ignore */ }
                    _tourGoToRail('intercept');
                },
                advance: 'next-button'
            },
            {
                id: 'mock-wrap-up',
                title: 'That\'s the mock loop',
                body: 'Recording → Use as mock → invoke against the mock port. From here you can chain it: benchmark the mock, share its URL with the team, or stop it from the Intercept rail\'s Mock servers sub-tab when you\'re done.',
                target: null,
                advance: 'next-button'
            }
        ];
    }

    function tourStartBuildMock(opts) {
        opts = opts || {};
        tourStart(_buildMockSteps(), {
            id: 'build-a-mock',
            force: !!opts.force,
            onFinish: opts.onFinish || null
        });
    }

    // 'Set up environments' — Workspaces → Envs overview empty.
    //
    // The empty Envs overview is reached via the workspace tree, so
    // step 1 routes there and pins the spotlight on the "+ New
    // environment" button. Step 2 waits for the create-event the
    // openCreateEnvironmentDialog flow now fires; step 3 nudges the
    // operator toward {{var}} references in a request.
    function _setupEnvironmentsSteps() {
        return [
            {
                id: 'env-intro',
                title: 'Set up environments',
                body: 'Environments scope variables, auth tokens, and secrets per deployment stage — staging vs. prod vs. local — so you can re-point an entire workspace at a different backend by toggling the active env.',
                target: null,
                advance: 'next-button'
            },
            {
                id: 'env-new',
                title: 'Step 1 — Create an environment',
                body: 'Hit "+ New environment" on the workspace\'s envs overview. Give it a stage name like "staging" or "prod". The new env shows up in the workspace tree and in the topbar env dropdown.',
                target: '#bowire-workspace-env-new-btn',
                advance: 'on-event:environment-created'
            },
            {
                id: 'env-add-vars',
                title: 'Step 2 — Add variables',
                body: 'Open the env editor (click the env name in the tree). Each row is a key=value pair — type a host, a token, a tenant id. Values are stored locally; mark a row as a secret to keep it out of exports.\n\nYou can paste a .env file in too — Bowire parses KEY=value lines.',
                target: null,
                advance: 'next-button'
            },
            {
                id: 'env-reference',
                title: 'Step 3 — Reference vars in a request',
                body: 'Anywhere a string field accepts input (URLs, headers, request body) you can write {{varName}} and Bowire substitutes the active env\'s value at send-time. Flip the active env from the topbar dropdown and the same request hits a different backend.',
                target: null,
                advance: 'next-button'
            },
            {
                id: 'env-wrap-up',
                title: 'You\'re set',
                body: 'That\'s envs: create → add vars → reference. Use the topbar env dropdown to switch active env on the fly; the workspace tree shows the env count next to each workspace so you can see the spread at a glance.',
                target: null,
                advance: 'next-button'
            }
        ];
    }

    function tourStartSetupEnvironments(opts) {
        opts = opts || {};
        tourStart(_setupEnvironmentsSteps(), {
            id: 'set-up-environments',
            force: !!opts.force,
            onFinish: opts.onFinish || null
        });
    }

    // 'Run a benchmark' — Benchmarks rail empty.
    //
    // The natural path is: pick a benchmark source (method, collection,
    // or recording) → set N + concurrency → Run → read p95 / p99 in the
    // results panel. We surface "New benchmark" as the first concrete
    // action so the operator gets a spec to fiddle with.
    function _runBenchmarkSteps() {
        return [
            {
                id: 'bench-intro',
                title: 'Benchmark a method',
                body: 'A benchmark repeats N calls at K concurrency and reports latency percentiles (p50 / p95 / p99) plus the status distribution.\n\nThree shapes: single method (one unary call), collection (replay every item), or recording (replay every step). This tour walks the single-method shape.',
                target: null,
                advance: 'next-button'
            },
            {
                id: 'bench-go-rail',
                title: 'Step 1 — Open the Benchmarks rail',
                body: 'Switch to Benchmarks. The sidebar lists saved specs; the main pane shows the configurator for the selected one.',
                target: '[data-rail-mode-id="benchmarks"]',
                navigate: function () { _tourGoToRail('benchmarks'); },
                advance: 'next-button'
            },
            {
                id: 'bench-new',
                title: 'Step 2 — New benchmark',
                body: 'Hit "New benchmark" on the empty card to create a fresh spec, then pick a method from the target dropdown. Or kick the tour off from Discover with the "Benchmark" button on any method.',
                target: '#bowire-bench-new-btn',
                advance: 'next-button'
            },
            {
                id: 'bench-configure',
                title: 'Step 3 — Set N + concurrency',
                body: 'Two knobs do most of the work: total iterations (N) and concurrent workers (VUs). Start small — 100 / 5 finishes in a few seconds and is enough to spot obvious regressions.\n\nFor sustained-load testing, switch the phase from iteration-bounded to duration-bounded (run for 60s @ 10 VUs) and read the steady-state RPS.',
                target: null,
                advance: 'next-button'
            },
            {
                id: 'bench-run',
                title: 'Step 4 — Run it',
                body: 'Click Run. The progress bar ticks up as iterations land; cancellation is one click away if something\'s wrong.\n\nWhen it finishes, the result panel below the configurator shows p50 / p95 / p99 latency, status-code distribution, and a sparkline of per-iteration durations. Re-run with tweaks; the last N runs stay in history so you can A/B them.',
                target: null,
                advance: 'on-event:benchmark-run-complete'
            },
            {
                id: 'bench-wrap-up',
                title: 'You\'re benchmarking',
                body: 'That\'s the loop: pick a target → set N + concurrency → run → read percentiles. Benchmark history sticks around per spec so you can see if today\'s change made things faster.',
                target: null,
                advance: 'next-button'
            }
        ];
    }

    function tourStartRunBenchmark(opts) {
        opts = opts || {};
        tourStart(_runBenchmarkSteps(), {
            id: 'run-a-benchmark',
            force: !!opts.force,
            onFinish: opts.onFinish || null
        });
    }

    // 'Capture a recording' — Recordings rail empty.
    //
    // The path is: open Recordings → Start recording → switch to Discover
    // and invoke methods → stop → see steps populate the recording
    // detail. We anchor step 1 on the rail-strip glyph (always present)
    // and the rest narrate the loop since the empty-card's primary CTA
    // (Start recording) already exists and lands the operator in
    // Discover with capture armed.
    function _captureRecordingSteps() {
        return [
            {
                id: 'rec-intro',
                title: 'Capture a recording',
                body: 'A recording is an ordered sequence of live calls Bowire captures while you drive the API. You can replay it later, spin it up as a mock host, or run it as a benchmark.\n\nThis tour walks the capture loop: arm → invoke → stop → save.',
                target: null,
                advance: 'next-button'
            },
            {
                id: 'rec-go-rail',
                title: 'Step 1 — Open the Recordings rail',
                body: 'Switch to the Recordings rail. The sidebar lists saved recordings; the main pane shows the selected one or — when empty — the Start CTA you\'ll use next.',
                target: '[data-rail-mode-id="recordings"]',
                navigate: function () { _tourGoToRail('recordings'); },
                advance: 'next-button'
            },
            {
                id: 'rec-start',
                title: 'Step 2 — Start recording',
                body: 'Hit "Start recording" on the empty card. Bowire arms the capture pipeline and jumps you into Discover so the next thing you click can be recorded. A red dot in the topbar shows recording is live.',
                target: '#bowire-main-recordings',
                advance: 'next-button'
            },
            {
                id: 'rec-invoke',
                title: 'Step 3 — Invoke a few methods',
                body: 'From Discover, pick a method, fill the request, hit Execute. Each Execute is appended to the recording as one step. Chain a few calls together — that\'s your captured session.',
                target: null,
                advance: 'next-button'
            },
            {
                id: 'rec-wrap-up',
                title: 'Step 4 — Stop and reuse',
                body: 'Click the red dot (or hit "Stop recording" on the Recordings rail) when you\'re done. The captured session lands in the sidebar with its steps.\n\nFrom the detail toolbar you can rename it, replay it, "Use as mock" to spin up a fake host on the captured responses, or feed it to a benchmark.',
                target: null,
                advance: 'next-button'
            }
        ];
    }

    function tourStartCaptureRecording(opts) {
        opts = opts || {};
        tourStart(_captureRecordingSteps(), {
            id: 'capture-a-recording',
            force: !!opts.force,
            onFinish: opts.onFinish || null
        });
    }

    // 'Build a collection' — Collections rail empty.
    //
    // Collections group saved requests so they can be replayed as a
    // set (Postman-style runner). The path is: New collection → add
    // requests (manually, or from Discover / Compose via Save) → run.
    function _buildCollectionSteps() {
        return [
            {
                id: 'col-intro',
                title: 'Build a collection',
                body: 'A collection is a folder of saved requests you can replay as a set — think Postman runner. Useful for smoke tests, demo scripts, or a personal "things I run every Monday" stash.\n\nThis tour walks the build loop: create → add requests → run.',
                target: null,
                advance: 'next-button'
            },
            {
                id: 'col-go-rail',
                title: 'Step 1 — Open Compose',
                body: 'The Compose rail hosts the canonical Collections + Presets surface (the standalone Collections rail was retired in v2.1). Switch to Compose; the side panel on the right lists every collection in the active workspace.',
                target: '[data-rail-mode-id="compose"]',
                navigate: function () { _tourGoToRail('compose'); },
                advance: 'next-button'
            },
            {
                id: 'col-new',
                title: 'Step 2 — New collection',
                body: 'Hit the + icon in the Library sidebar toolbar. A fresh, empty collection lands in the list with the focus on its name — type a label like "petstore-smoke" or "auth-flows".\n\nOr import a Postman collection / OpenAPI spec via the Workspaces rail to seed one from existing material.',
                target: '.bowire-compose-side-section-collections',
                advance: 'next-button'
            },
            {
                id: 'col-add-items',
                title: 'Step 3 — Add requests',
                body: 'Two ways to fill a collection: from Discover, right-click any method → "Save to collection"; or from Compose, the request builder\'s "Save" button lets you pick the target collection. Items show up in order in the detail pane.',
                target: null,
                advance: 'next-button'
            },
            {
                id: 'col-wrap-up',
                title: 'Step 4 — Run as a set',
                body: 'When the collection has items, the detail pane\'s Run button replays them top-to-bottom and reports pass/fail per item. Hit it on every code change to catch regressions; export it as a .json to share with the team.',
                target: null,
                advance: 'next-button'
            }
        ];
    }

    function tourStartBuildCollection(opts) {
        opts = opts || {};
        tourStart(_buildCollectionSteps(), {
            id: 'build-a-collection',
            force: !!opts.force,
            onFinish: opts.onFinish || null
        });
    }

    // 'Build a flow' — Flows rail empty.
    //
    // Flows chain multiple requests + pass response data from one
    // step into the next. The path is: New flow → drop a request node
    // → add a second, reference {{step1.response.body.id}} → Run.
    function _buildFlowSteps() {
        return [
            {
                id: 'flow-intro',
                title: 'Build a flow',
                body: 'Flows chain API calls together: the response of one step becomes the input of the next. Use them for login → fetch → mutate sequences, smoke pipelines that need response-derived ids, or any "I always run these three in a row" workflow.',
                target: null,
                advance: 'next-button'
            },
            {
                id: 'flow-go-rail',
                title: 'Step 1 — Open the Flows rail',
                body: 'Switch to the Flows rail. The sidebar lists saved flows; the canvas in the main pane is where you wire the nodes once a flow is selected.',
                target: '[data-rail-mode-id="flows"]',
                navigate: function () { _tourGoToRail('flows'); },
                advance: 'next-button'
            },
            {
                id: 'flow-new',
                title: 'Step 2 — New flow',
                body: 'Hit "New flow" on the empty card. The canvas opens with a single start node. From the node palette (left edge of the canvas) drag a Request node onto it — that\'s your first call.',
                target: '#bowire-flow-canvas-empty',
                advance: 'next-button'
            },
            {
                id: 'flow-chain',
                title: 'Step 3 — Chain steps',
                body: 'Add a second Request node and wire the start node\'s output into it. Inside the second request\'s URL or body, reference the first step\'s response with {{step1.response.body.<field>}}. Bowire substitutes the value at run-time.\n\nCondition, Loop, Delay, and Variable nodes let you branch / iterate / wait / capture intermediate values.',
                target: null,
                advance: 'next-button'
            },
            {
                id: 'flow-wrap-up',
                title: 'Step 4 — Run + export',
                body: 'The Run button in the canvas header executes the flow top-to-bottom and shows per-step results. Export as .bwf to share, or "Export as collection" to lift the request nodes into a Collection that runs in CI.',
                target: null,
                advance: 'next-button'
            }
        ];
    }

    function tourStartBuildFlow(opts) {
        opts = opts || {};
        tourStart(_buildFlowSteps(), {
            id: 'build-a-flow',
            force: !!opts.force,
            onFinish: opts.onFinish || null
        });
    }

    // 'Compose a request' — Compose rail empty.
    //
    // The simplest of the per-rail tours — Compose is just the
    // request builder. Path: New request → type URL → pick method →
    // Execute.
    function _composeRequestSteps() {
        return [
            {
                id: 'compose-intro',
                title: 'Compose a request',
                body: 'Compose is the freeform request builder — type a URL, pick a method, fire. Useful when you don\'t want to (or can\'t) discover a schema first, when you\'re probing an unknown endpoint, or when you need a one-off call outside any collection.',
                target: null,
                advance: 'next-button'
            },
            {
                id: 'compose-go-rail',
                title: 'Step 1 — Open the Compose rail',
                body: 'Switch to the Compose rail. Each request opens as its own tab in the strip across the top — Ctrl+L from anywhere opens a fresh one.',
                target: '[data-rail-mode-id="compose"]',
                navigate: function () { _tourGoToRail('compose'); },
                advance: 'next-button'
            },
            {
                id: 'compose-new',
                title: 'Step 2 — New request',
                body: 'Hit "New request" on the empty card. A fresh request tab opens with the cursor on the URL field. Type any URL — Bowire infers the protocol (REST / GraphQL / WebSocket / SSE / MQTT) from the scheme.',
                target: '#bowire-main-compose',
                advance: 'next-button'
            },
            {
                id: 'compose-fill',
                title: 'Step 3 — Method + body',
                body: 'Pick the HTTP method from the dropdown, switch to the Body tab if you need a payload (JSON / form / raw — Bowire renders the right editor). Headers, params, auth, env-variable references all sit in their own tabs alongside Body.',
                target: null,
                advance: 'next-button'
            },
            {
                id: 'compose-wrap-up',
                title: 'Step 4 — Execute',
                body: 'Hit Execute (Ctrl+Enter from anywhere in the request pane). The response lands below with status, timing, headers, and a syntax-highlighted body. Save the request into a Collection from the same toolbar for replay later.',
                target: '.bowire-execute-btn',
                advance: 'next-button'
            }
        ];
    }

    function tourStartComposeRequest(opts) {
        opts = opts || {};
        tourStart(_composeRequestSteps(), {
            id: 'compose-a-request',
            force: !!opts.force,
            onFinish: opts.onFinish || null
        });
    }

    // 'Capture traffic' — Traffic rail empty.
    //
    // The empty state is reached when no flows have arrived yet —
    // either embedded (UseBowireInterceptor not wired) or standalone
    // (no sidecar driving traffic). Tour walks both paths.
    function _captureTrafficSteps() {
        return [
            {
                id: 'traffic-intro',
                title: 'Capture traffic',
                body: 'The Intercept rail\'s Captured sub-tab shows live HTTP flows intercepted by Bowire — every request that hits the host, with timing, status, request + response bodies. Useful when you\'re debugging a misbehaving client or learning how an app actually talks to its backend.',
                target: null,
                advance: 'next-button'
            },
            {
                id: 'traffic-go-rail',
                title: 'Step 1 — Open the Intercept rail',
                body: 'Switch to the Intercept rail. The Captured sub-tab streams flows in real time; the main pane shows the selected flow\'s request + response detail.',
                target: '[data-rail-mode-id="intercept"]',
                navigate: function () {
                    try { localStorage.setItem('bowire_intercept_sub_tab', 'captured'); } catch { /* ignore */ }
                    _tourGoToRail('intercept');
                },
                advance: 'next-button'
            },
            {
                id: 'traffic-source',
                title: 'Step 2 — Point a traffic source at Bowire',
                body: 'Two deployment shapes:\n\n• Embedded — Bowire is mounted via MapBowire() in your ASP.NET host. Add app.UseBowireInterceptor() to the pipeline. Every request through that host shows up here.\n\n• Standalone — run "bowire proxy" or "bowire interceptor" in a terminal as a sidecar and point your client at its URL. Bowire intercepts and forwards while capturing.',
                target: null,
                advance: 'next-button'
            },
            {
                id: 'traffic-invoke',
                title: 'Step 3 — Drive some traffic',
                body: 'Hit the wired-up endpoint from your client (browser, curl, your app). Each flow lands in the sidebar as it happens — status code coloured by class (2xx green / 4xx amber / 5xx red), method + path on each row.',
                target: null,
                advance: 'next-button'
            },
            {
                id: 'traffic-wrap-up',
                title: 'Step 4 — Reuse the capture',
                body: 'Pick a row to inspect the request + response in the main pane. "Send to recording" lifts the flow into a Bowire recording for replay / fuzzing; "Mock this route" generates a mock rule that intercepts the next matching call. The flows pane is a ring buffer — older entries roll off as new ones arrive.',
                target: null,
                advance: 'next-button'
            }
        ];
    }

    function tourStartCaptureTraffic(opts) {
        opts = opts || {};
        tourStart(_captureTrafficSteps(), {
            id: 'capture-traffic',
            force: !!opts.force,
            onFinish: opts.onFinish || null
        });
    }

    // 'Scan for risks' — Security rail empty.
    //
    // The Security rail's empty state expects discovered endpoints
    // (via Discover) before the threat-model can rank them. Tour
    // walks: get endpoints into Discover → switch to Security → pick
    // tier (heuristic / AI-assisted) → Run → read ranked surface.
    function _securityScanSteps() {
        return [
            {
                id: 'sec-intro',
                title: 'Scan for security risks',
                body: 'The Security rail runs threat-modelling over the API surface Bowire discovered: ranks endpoints by attack-surface risk, surfaces unauth\'d mutations, mass-assignment shapes, weak auth signals.\n\nHeuristic mode is on by default — sub-millisecond rules engine, no AI required. Flip the tier if you have an AI configured for semantic scoring on top.',
                target: null,
                advance: 'next-button'
            },
            {
                id: 'sec-need-endpoints',
                title: 'Step 1 — Get endpoints into Discover',
                body: 'The threat-model reads from the same service tree Discover shows. You need at least one URL added (or a schema uploaded) so there are endpoints to rank. If Discover is empty, do that first — the empty-card on Security walks you there with "Open Discover".',
                target: '[data-rail-mode-id="discover"]',
                advance: 'next-button'
            },
            {
                id: 'sec-go-rail',
                title: 'Step 2 — Open the Security rail',
                body: 'Switch back to Security. With endpoints present, the threat-model surface shows the tier toggle (Heuristic / AI-assisted) and a Run button.',
                target: '[data-rail-mode-id="security"]',
                navigate: function () { _tourGoToRail('security'); },
                advance: 'next-button'
            },
            {
                id: 'sec-run',
                title: 'Step 3 — Run the scan',
                body: 'Pick a tier — start with Heuristic, it\'s instant and gives you a baseline. Hit Run. The endpoints get ranked by risk score with one-line rationale per row: "POST mutates state, no auth required", "DELETE on path with id parameter", &c.',
                target: null,
                advance: 'next-button'
            },
            {
                id: 'sec-wrap-up',
                title: 'You\'re scanning',
                body: 'Click any ranked row to drill into the rule hits + remediation hint. Use the per-row "Generate test" button to spin up a security regression test against that endpoint.\n\nThe right-side Security drawer keeps the panel reachable from any rail so a scan-finding can stay open while you investigate in Discover or Compose.',
                target: null,
                advance: 'next-button'
            }
        ];
    }

    function tourStartSecurityScan(opts) {
        opts = opts || {};
        tourStart(_securityScanSteps(), {
            id: 'scan-for-risks',
            force: !!opts.force,
            onFinish: opts.onFinish || null
        });
    }

    // ---- Window exposure ----------------------------------------------
    // Other fragments + external scripts reach the engine through
    // these globals so they don't have to live inside the IIFE.
    if (typeof window !== 'undefined') {
        window.bowireStartTour = tourStart;
        window.bowireStopTour = tourStop;
        window.bowireTourIsRunning = tourIsRunning;
        window.bowireFireTourEvent = tourFireEvent;
        window.bowireStartGettingStartedTour = tourStartGettingStarted;
        // Workspace-prereq tour — fired from the no-workspace redirect
        // when an operator clicks a workspace-dependent rail (Recordings,
        // Mocks, Collections, Flows, Benchmarks, Compose). Runs the
        // reusable create-workspace fragment so the operator gets the
        // same dialog walk-through as in Getting Started, without us
        // copying the steps per rail.
        window.bowireStartCreateWorkspaceTour = tourStartCreateWorkspace;
        // #303 — Per-rail secondary tours, fired from empty-card CTAs.
        window.bowireStartBuildMockTour = tourStartBuildMock;
        window.bowireStartSetupEnvironmentsTour = tourStartSetupEnvironments;
        window.bowireStartRunBenchmarkTour = tourStartRunBenchmark;
        // Per-rail welcome tours for every remaining rail that has a
        // welcome / empty-state card — Recordings, Collections, Flows,
        // Compose, Traffic, Security. Operator feedback: every rail with
        // a usable first action should let the new user opt into a short
        // tour of it from the same place the primary CTA sits.
        window.bowireStartCaptureRecordingTour = tourStartCaptureRecording;
        window.bowireStartBuildCollectionTour = tourStartBuildCollection;
        window.bowireStartBuildFlowTour = tourStartBuildFlow;
        window.bowireStartComposeRequestTour = tourStartComposeRequest;
        window.bowireStartCaptureTrafficTour = tourStartCaptureTraffic;
        window.bowireStartSecurityScanTour = tourStartSecurityScan;
        window.bowireResetTours = tourResetSavedOnce;
    }
