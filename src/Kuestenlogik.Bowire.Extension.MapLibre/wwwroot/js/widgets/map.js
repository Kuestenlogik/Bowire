// Copyright 2026 Küstenlogik · Apache-2.0
// ----------------------------------------------------------------------
// Map widget — first viewer/editor registered against the
// `coordinate.wgs84` semantic kind. Uses MapLibre GL JS for rendering;
// the bundle is vendored under wwwroot/maplibre/ and served from
// /api/ui/extensions/kuestenlogik.maplibre/maplibre-gl.js so the workbench
// never reaches an external CDN.
//
// Pairing: requires `coordinate.latitude` + `coordinate.longitude` at
// the same parent path. A field with only one of the two surfaces a
// "latitude marked, longitude needed" hint instead of mounting.
// ----------------------------------------------------------------------

    /**
     * Track whether the MapLibre script tag has been injected. Multiple
     * map widgets mounted in the same session share one MapLibre lib;
     * the first widget pays the load cost, every other widget reuses
     * `window.maplibregl`.
     */
    var bowireMapLibreLoading = null;

    /**
     * Capture the bundle's own URL once at module-load time so the
     * MapLibre vendor files (served by the SAME extension-asset
     * endpoint) can be addressed without knowing whether Bowire is
     * mounted at `/` or under a `/bowire` prefix. The core IIFE's
     * `config` symbol that the in-core widget used to read isn't
     * visible from external extensions — they load as separate
     * scripts outside the core's closure. document.currentScript.src
     * is the only stable signal an external bundle has about where
     * it lives.
     */
    var bowireMapBundleUrl = (function () {
        try {
            if (document.currentScript && document.currentScript.src) {
                return document.currentScript.src.replace(/\/[^/]+$/, '');
            }
        } catch { /* ignore */ }
        return '/api/ui/extensions/kuestenlogik.maplibre';
    })();

    function bowireLoadMapLibre() {
        if (window.maplibregl) return Promise.resolve(window.maplibregl);
        if (bowireMapLibreLoading) return bowireMapLibreLoading;

        var baseUrl = bowireMapBundleUrl;

        // Inject the stylesheet first so it parses while the script
        // downloads. Idempotent — re-mounting the widget on a second
        // method tab doesn't double-inject.
        if (!document.getElementById('bowire-maplibre-css')) {
            var link = document.createElement('link');
            link.id = 'bowire-maplibre-css';
            link.rel = 'stylesheet';
            link.href = baseUrl + '/maplibre-gl.css';
            document.head.appendChild(link);
        }

        bowireMapLibreLoading = new Promise(function (resolve, reject) {
            var script = document.createElement('script');
            script.src = baseUrl + '/maplibre-gl.js';
            script.async = true;
            script.onload = function () {
                if (window.maplibregl) resolve(window.maplibregl);
                else reject(new Error('maplibre-gl loaded but window.maplibregl is undefined'));
            };
            script.onerror = function () {
                bowireMapLibreLoading = null;
                reject(new Error('Failed to load maplibre-gl from ' + script.src));
            };
            document.head.appendChild(script);
        });
        return bowireMapLibreLoading;
    }

    /**
     * Blank-background style for the offline-default case. MapLibre still
     * renders pins on top of a solid background even when no tile source
     * is configured — exactly the behaviour the ADR's "Offline mode"
     * section pins.
     *
     * Phase 3-R — no-network lockdown. The style intentionally OMITS
     * `glyphs` AND `sprite` URLs. MapLibre only requests glyph PBFs
     * when a `symbol` layer with `text-field` is mounted; the workbench
     * widget renders entirely through `circle` layers (per-discriminator
     * colour, multi-select restyle) so no glyphs are needed, and no
     * glyph fetch can leak to an external host. Same story for sprite
     * atlases — no `symbol` layer references a sprite icon, so the
     * `sprite` URL stays absent. A regex-over-bundle test pins the
     * absence; any future style tweak that re-introduces a labelled
     * symbol layer must also explicitly opt into a glyph source (and
     * own the offline-mode consequences).
     */
    function bowireMapBlankStyle(themeMode) {
        var bg = themeMode === 'light' ? '#eef2f8' : '#1a1d2b';
        return {
            version: 8,
            sources: {},
            layers: [{
                id: 'background',
                type: 'background',
                paint: { 'background-color': bg }
            }]
        };
    }

    /**
     * Resolve `Bowire:MapTileUrl` from the host config. The host
     * surfaces it as a top-level config key when set; absence means
     * "offline blank style" (the ADR's default).
     */
    function bowireMapTileUrl() {
        // Allow the host to pre-bake the URL into __BOWIRE_CONFIG__ via
        // a future field; for v1.3, an env var or appsettings entry
        // could surface it, but the bowire-options binding for it is
        // out of Phase 3 scope. The widget reads window.__BOWIRE_CONFIG__
        // defensively in case a future host injection lands.
        var cfg = window.__BOWIRE_CONFIG__;
        return (cfg && cfg.mapTileUrl) || null;
    }

    /**
     * Walk a JSON value down a (limited) JSONPath expression. The path
     * shapes the extension framework emits look like `$.position.lat`
     * or `$.items[0].lat` — enough to round-trip the auto-detector
     * output without pulling a full JSONPath library into the bundle.
     */
    function bowireResolveJsonPath(root, path) {
        if (!path || path === '$') return root;
        if (path.indexOf('$') === 0) path = path.substring(1);
        if (path.indexOf('.') === 0) path = path.substring(1);

        // Tokenise on dots and bracket indices.
        var tokens = [];
        var i = 0;
        while (i < path.length) {
            if (path[i] === '[') {
                var end = path.indexOf(']', i);
                if (end < 0) return undefined;
                tokens.push(path.substring(i + 1, end));
                i = end + 1;
                if (path[i] === '.') i++;
            } else {
                var nextDot = path.indexOf('.', i);
                var nextBracket = path.indexOf('[', i);
                var stop;
                if (nextDot < 0 && nextBracket < 0) stop = path.length;
                else if (nextDot < 0) stop = nextBracket;
                else if (nextBracket < 0) stop = nextDot;
                else stop = Math.min(nextDot, nextBracket);
                tokens.push(path.substring(i, stop));
                i = stop;
                if (path[i] === '.') i++;
            }
        }

        var cur = root;
        for (var t = 0; t < tokens.length; t++) {
            if (cur == null) return undefined;
            var key = tokens[t];
            if (/^\d+$/.test(key) && Array.isArray(cur)) {
                cur = cur[parseInt(key, 10)];
            } else if (typeof cur === 'object') {
                cur = cur[key];
            } else {
                return undefined;
            }
        }
        return cur;
    }

    /**
     * Per-discriminator colour palette. Layers in the map widget are
     * coloured by the frame's `discriminator` value (e.g. EntityStatePdu
     * blue, FirePdu red) — same scheme the ADR sketches under "Layer
     * behaviour for mixed discriminators". For v1.3 we cycle a small
     * palette deterministically; explicit colour overrides land later.
     */
    var BOWIRE_MAP_PALETTE = [
        '#4f46e5', '#dc2626', '#16a34a', '#d97706',
        '#0891b2', '#c026d3', '#7c3aed', '#65a30d'
    ];
    function bowireMapDiscriminatorColor(discriminator, store) {
        var key = discriminator || '*';
        if (!(key in store)) {
            store[key] = BOWIRE_MAP_PALETTE[Object.keys(store).length % BOWIRE_MAP_PALETTE.length];
        }
        return store[key];
    }

    /**
     * Build the viewer mount function. Hoisted so register() below stays
     * readable.
     */
    async function bowireMapViewerMount(container, ctx) {
        // Reserve a square aspect even before MapLibre kicks in so the
        // layout doesn't flicker when the script lands. Inline styles
        // (rather than CSS classes) keep the widget independent of
        // bowire.css — extensions are supposed to render against any
        // theme.
        container.style.minHeight = '320px';
        container.style.width = '100%';
        container.style.position = 'relative';

        var pinsByDiscriminator = {};
        var paletteStore = {};
        var disposed = false;
        var pinSeq = 0;

        var maplibregl;
        try {
            maplibregl = await bowireLoadMapLibre();
        } catch (e) {
            // Offline + no vendored bundle? Render a fallback "no map"
            // surface so the widget doesn't leave the tab blank.
            console.warn('[bowire-map] failed to load MapLibre, falling back to text:', e);
            var notice = document.createElement('div');
            notice.className = 'bowire-map-fallback';
            notice.style.padding = '12px';
            notice.style.font = '13px system-ui, sans-serif';
            notice.textContent = 'Map widget unavailable: MapLibre bundle did not load.';
            container.appendChild(notice);
            return function () { if (notice.parentNode) notice.parentNode.removeChild(notice); };
        }
        if (disposed) return function () {};

        var tileUrl = bowireMapTileUrl();
        var style;
        if (tileUrl) {
            // User has explicitly opted into an external tile source by
            // configuring Bowire:MapTileUrl. That's the only path where
            // the widget reaches an external host; offline-default has
            // no `sources`, no `glyphs`, no `sprite` (see
            // bowireMapBlankStyle for the lockdown rationale). When a
            // tile URL IS configured, we still omit `glyphs` / `sprite`
            // because the workbench's pin-rendering doesn't use them —
            // adding either would silently widen the egress surface.
            style = {
                version: 8,
                sources: {
                    'bowire-tiles': {
                        type: 'raster',
                        tiles: [tileUrl],
                        tileSize: 256,
                        attribution: ''
                    }
                },
                layers: [{
                    id: 'bowire-tiles-layer',
                    type: 'raster',
                    source: 'bowire-tiles'
                }]
            };
        } else {
            style = bowireMapBlankStyle(ctx.theme && ctx.theme.mode);
        }

        var map = new maplibregl.Map({
            container: container,
            style: style,
            center: [0, 0],
            zoom: 1,
            attributionControl: false
        });
        map.addControl(new maplibregl.NavigationControl({ showCompass: false }), 'top-right');

        // Lazy-attach a "points" source on first frame so the empty
        // state has nothing on the map until data flows.
        var pointsSource = {
            type: 'FeatureCollection',
            features: []
        };

        await new Promise(function (resolve) {
            map.on('load', resolve);
        });
        if (disposed) {
            try { map.remove(); } catch {}
            return function () {};
        }

        map.addSource('bowire-points', { type: 'geojson', data: pointsSource });
        // Data-driven styling: the `selected` feature property switches
        // the radius + stroke colour without rebuilding the layer.
        // Selection sync (Phase 3.1) flips `selected` on per-feature
        // through a single `setData(...)` call — no layer thrashing on
        // every Ctrl-click. See `applySelectionRestyle` below.
        map.addLayer({
            id: 'bowire-points-layer',
            type: 'circle',
            source: 'bowire-points',
            paint: {
                'circle-radius': [
                    'match', ['get', 'selected'],
                    'yes', 9,
                    /* default */ 6
                ],
                'circle-color': ['get', 'color'],
                'circle-stroke-width': [
                    'match', ['get', 'selected'],
                    'yes', 2.5,
                    /* default */ 1.5
                ],
                'circle-stroke-color': [
                    'match', ['get', 'selected'],
                    'yes', (ctx.theme && ctx.theme.accent) || '#4f46e5',
                    /* default */ '#ffffff'
                ],
                'circle-opacity': [
                    'match', ['get', 'selected'],
                    'yes', 1.0,
                    'no-but-others-are', 0.55,
                    /* default */ 1.0
                ]
            }
        });

        var bounds = null;
        function maybeFit() {
            if (!bounds || !bounds.isFinite || !bounds.isFinite()) return;
            try { map.fitBounds(bounds, { padding: 30, maxZoom: 12, duration: 0 }); }
            catch {}
        }

        // Pull (lat, lon) out of one frame using the resolved kind →
        // path mapping from ctx.interpretations.
        function extractCoord(frame) {
            var kinds = ctx.interpretations || {};
            var latPath = kinds['coordinate.latitude'];
            var lonPath = kinds['coordinate.longitude'];
            if (!latPath || !lonPath) return null;

            // Frames coming through the SSE stream wrap the payload in
            // a `data` field. Try both shapes so the widget works for
            // raw frames AND for wrapped envelopes.
            var roots = [];
            if (frame && frame.data !== undefined) roots.push(frame.data);
            if (frame && frame.frame !== undefined) roots.push(frame.frame);
            roots.push(frame);

            for (var i = 0; i < roots.length; i++) {
                var root = roots[i];
                if (root && typeof root === 'string') {
                    try { root = JSON.parse(root); } catch { continue; }
                }
                var lat = bowireResolveJsonPath(root, latPath);
                var lon = bowireResolveJsonPath(root, lonPath);
                lat = typeof lat === 'number' ? lat : parseFloat(lat);
                lon = typeof lon === 'number' ? lon : parseFloat(lon);
                if (isFinite(lat) && isFinite(lon)
                    && lat >= -90 && lat <= 90
                    && lon >= -180 && lon <= 180) {
                    return { lat: lat, lon: lon };
                }
            }
            return null;
        }

        // Phase 3.1 — selected-frame tracking. We hold the snapshot
        // separately from the feature collection so a re-styled feature
        // can be flagged in O(1) on each selection event. The
        // userMovedCamera flag suppresses auto-fit once the user has
        // panned/zoomed — same instinct as the existing 5-pin auto-fit
        // cutoff, just extended for the selection-driven camera moves.
        var selectedFrameIds = new Set();
        var userMovedCamera = false;
        map.on('dragstart', function () { userMovedCamera = true; });
        map.on('zoomstart', function (e) {
            // MapLibre fires zoomstart for programmatic flyTo / fitBounds
            // too; only count it as user-driven when there's no
            // originalEvent attached (originalEvent is set on
            // mousewheel/touch).
            if (e && e.originalEvent) userMovedCamera = true;
        });

        function addPin(frame) {
            var coord = extractCoord(frame);
            if (!coord) return;
            var discriminator = (frame && frame.discriminator) || '*';
            var color = bowireMapDiscriminatorColor(discriminator, paletteStore);
            var frameId = (frame && frame.id) || null;

            var anySelected = selectedFrameIds.size > 0;
            var isSelected = frameId != null && selectedFrameIds.has(frameId);
            // `selected` is a tristate so the layer's match expression
            // can switch radius/opacity in one place:
            //   'yes'                — this pin is selected
            //   'no-but-others-are'  — selection is non-empty but this pin isn't in it
            //   absent / 'no'        — no selection at all (normal)
            var selectedTag = isSelected
                ? 'yes'
                : (anySelected ? 'no-but-others-are' : 'no');

            pointsSource.features.push({
                type: 'Feature',
                id: ++pinSeq,
                geometry: { type: 'Point', coordinates: [coord.lon, coord.lat] },
                properties: {
                    color: color,
                    discriminator: discriminator,
                    frameId: frameId,
                    selected: selectedTag
                }
            });

            // Trim pin count to avoid unbounded memory in long streams.
            // The cap mirrors what render-main.js does for streamMessages.
            var CAP = 5000;
            if (pointsSource.features.length > CAP) {
                pointsSource.features.splice(0, pointsSource.features.length - CAP);
            }

            var src = map.getSource('bowire-points');
            if (src) src.setData(pointsSource);

            if (!bounds) {
                bounds = new maplibregl.LngLatBounds(
                    [coord.lon, coord.lat], [coord.lon, coord.lat]);
            } else {
                bounds.extend([coord.lon, coord.lat]);
            }
            // Auto-fit on the first few pins, then leave navigation to
            // the user. Avoids the jitter of a re-fit on every frame.
            // Once the user has moved the camera (drag/zoom) OR a
            // selection has driven the camera, skip auto-fit so we
            // don't fight the user.
            if (pointsSource.features.length <= 5 && !userMovedCamera) maybeFit();
        }

        /**
         * Re-flag every feature's `selected` property and push the
         * collection back to the source in a single `setData(...)`
         * call. MapLibre's data-driven `match` expression on the
         * paint properties resolves the new value on the next
         * frame — no layer rebuilds, no per-pin DOM churn.
         */
        function applySelectionRestyle() {
            var anySelected = selectedFrameIds.size > 0;
            for (var i = 0; i < pointsSource.features.length; i++) {
                var f = pointsSource.features[i];
                var fid = f.properties && f.properties.frameId;
                var isSelected = fid != null && selectedFrameIds.has(fid);
                f.properties.selected = isSelected
                    ? 'yes'
                    : (anySelected ? 'no-but-others-are' : 'no');
            }
            var src = map.getSource('bowire-points');
            if (src) src.setData(pointsSource);
        }

        /**
         * Camera rule per the Phase 3.1 spec:
         *   0 selected → no change (preserve user pan/zoom)
         *   1 selected → flyTo({ center, zoom: 14 }) for that frame's
         *                first coord pair
         *   N selected → fitBounds(...) of every selected frame's
         *                coord pairs, ~40px padding
         */
        function applySelectionCamera() {
            var ids = selectedFrameIds;
            if (ids.size === 0) return;

            // Walk the feature collection (cheaper than re-resolving
            // JSONPaths on the raw frames) and collect coords for any
            // feature whose frameId is in the selected set.
            var coords = [];
            for (var i = 0; i < pointsSource.features.length; i++) {
                var f = pointsSource.features[i];
                var fid = f.properties && f.properties.frameId;
                if (fid != null && ids.has(fid)) {
                    coords.push(f.geometry.coordinates);
                }
            }
            if (coords.length === 0) return;

            // Mark the upcoming camera move as programmatic so the
            // dragstart/zoomstart heuristic above doesn't latch
            // `userMovedCamera` and disable auto-fit forever after.
            if (coords.length === 1) {
                try { map.flyTo({ center: coords[0], zoom: 14, duration: 350 }); } catch {}
            } else {
                var b = new maplibregl.LngLatBounds(coords[0], coords[0]);
                for (var j = 1; j < coords.length; j++) b.extend(coords[j]);
                try { map.fitBounds(b, { padding: 40, duration: 350, maxZoom: 14 }); } catch {}
            }
        }

        // Stream loop — pull from the framework's async iterable.
        (async function consume() {
            try {
                for await (var frame of ctx.frames$) {
                    if (disposed) return;
                    addPin(frame);
                }
            } catch (e) {
                if (!disposed) console.error('[bowire-map] stream loop ended:', e);
            }
        })();

        // Selection loop — pulls full snapshots and re-flags pins +
        // moves the camera per the rule above. The first iteration
        // primes from `ctx.selection$`'s buffered current snapshot,
        // so a widget mounted AFTER the user already made a selection
        // syncs without an extra event.
        if (ctx.selection$) {
            (async function consumeSelection() {
                try {
                    for await (var snap of ctx.selection$) {
                        if (disposed) return;
                        var ids = (snap && Array.isArray(snap.selectedFrameIds))
                            ? snap.selectedFrameIds : [];
                        selectedFrameIds = new Set(ids);
                        applySelectionRestyle();
                        applySelectionCamera();
                    }
                } catch (e) {
                    if (!disposed) console.error('[bowire-map] selection loop ended:', e);
                }
            })();
        }

        return function unmount() {
            disposed = true;
            try { map.remove(); } catch {}
        };
    }

    /**
     * v1.3 editor — minimum-viable "click-to-set-pin" surface. The full
     * drag-and-edit Phase-4 surface goes through the right-click menu;
     * this stub is enough for the round-trip story (user picks a
     * coordinate on the map, request form's lat/lon fields update).
     */
    async function bowireMapEditorMount(container, ctx) {
        container.style.minHeight = '280px';
        container.style.width = '100%';
        container.style.position = 'relative';

        var maplibregl;
        try { maplibregl = await bowireLoadMapLibre(); }
        catch {
            container.textContent = 'Map editor unavailable: MapLibre bundle did not load.';
            return function () {};
        }

        var style = bowireMapBlankStyle(ctx.theme && ctx.theme.mode);
        var map = new maplibregl.Map({
            container: container,
            style: style,
            center: [0, 0],
            zoom: 1,
            attributionControl: false
        });

        var marker = null;
        map.on('click', function (e) {
            if (!marker) {
                marker = new maplibregl.Marker({ draggable: true }).setLngLat(e.lngLat).addTo(map);
                marker.on('dragend', function () {
                    var p = marker.getLngLat();
                    if (typeof ctx.onChange === 'function') {
                        ctx.onChange({
                            'coordinate.latitude': p.lat,
                            'coordinate.longitude': p.lng
                        });
                    }
                });
            } else {
                marker.setLngLat(e.lngLat);
            }
            if (typeof ctx.onChange === 'function') {
                ctx.onChange({
                    'coordinate.latitude': e.lngLat.lat,
                    'coordinate.longitude': e.lngLat.lng
                });
            }
        });

        return function unmount() {
            try { map.remove(); } catch {}
        };
    }

    // ---------------------------------------------------------------
    // Register against the framework. Calls the internal register so
    // the framework can flag this as a built-in (built-ins win
    // tie-breaks against same-kind third-party extensions).
    // ---------------------------------------------------------------
    (function () {
        var framework = window.__bowireExtFramework;
        if (!framework) {
            // extensions.js hasn't loaded yet — should never happen given
            // the fragment order in the csproj, but degrade gracefully.
            console.warn('[bowire-map] extension framework not present; skipping registration');
            return;
        }
        framework.register({
            id: 'kuestenlogik.maplibre',
            bowireApi: '1.x',
            kind: 'coordinate.wgs84',
            pairing: {
                required: ['coordinate.latitude', 'coordinate.longitude'],
                scope: 'same-parent'
            },
            viewer: {
                label: 'Map',
                icon: 'map-pin',
                // Phase 3.2 — the map naturally renders >1 selected
                // pin (the existing Phase 3.1 camera + restyle logic
                // already fitBounds(...)es N coords), so the viewer
                // opts into multi-select snapshot delivery. Without
                // this flag the framework would truncate every
                // snapshot to [lastSelected].
                selectionMode: 'multi',
                mount: bowireMapViewerMount
            },
            editor: {
                label: 'Pick on map',
                // The coordinate editor only ever cares about a single
                // (lat, lon) pair, so leave it on the safe default.
                selectionMode: 'single',
                mount: bowireMapEditorMount
            }
        });
        framework.markBuiltIn('kuestenlogik.maplibre');
    })();
