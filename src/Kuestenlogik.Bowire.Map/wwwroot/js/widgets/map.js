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
     * #117 — the translator, reached through the extension contract.
     *
     * This bundle loads as its own <script>, outside the workbench IIFE,
     * so the core's `t` is not in scope here; calling it bare threw
     * ReferenceError on the first map mount. `BowireExtensions.t` is the
     * published way in, and it is looked up per call rather than captured
     * once, for two reasons: the bundle can be served before the core has
     * finished installing the handle, and setLocale swaps the catalogue
     * behind it — a captured reference would still work, an old copy of
     * the resolved text would not.
     *
     * Falling back to the key keeps an older core (one that predates the
     * handle) serving a newer bundle: the widget renders with key names
     * showing, which is ugly and obvious, rather than not rendering.
     */
    function t(key, params) {
        var api = window.BowireExtensions;
        if (api && typeof api.t === 'function') return api.t(key, params);
        return key;
    }

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
     * milsymbol — MIL-STD-2525 / APP-6 symbol renderer (MIT), vendored
     * under wwwroot/milsymbol/ and served by the same extension-asset
     * endpoint as MapLibre. Shared across every map widget in the
     * session the same way `window.maplibregl` is; the UMD bundle
     * publishes itself as `window.ms`.
     *
     * Loaded alongside MapLibre rather than before it, and never
     * awaited by the mount: a pin does not need the symbol library to
     * be a pin. Until the script lands — or forever, if it does not —
     * the symbol layer paints the four built-in affinity shapes, and
     * the first frame after the load repaints with the standard's own
     * symbols.
     */
    var bowireMilSymbolLoading = null;

    function bowireLoadMilSymbol() {
        if (window.ms && typeof window.ms.Symbol === 'function') {
            return Promise.resolve(window.ms);
        }
        if (bowireMilSymbolLoading) return bowireMilSymbolLoading;

        bowireMilSymbolLoading = new Promise(function (resolve, reject) {
            var script = document.createElement('script');
            script.src = bowireMapBundleUrl + '/milsymbol.js';
            script.async = true;
            script.onload = function () {
                if (window.ms && typeof window.ms.Symbol === 'function') resolve(window.ms);
                else reject(new Error('milsymbol loaded but window.ms is undefined'));
            };
            script.onerror = function () {
                bowireMilSymbolLoading = null;
                reject(new Error('Failed to load milsymbol from ' + script.src));
            };
            document.head.appendChild(script);
        });
        return bowireMilSymbolLoading;
    }

    /**
     * mil-sym-ts — the MIL-STD-2525D / APP-6D multipoint renderer
     * (Apache-2.0), vendored gzipped under wwwroot/mil-sym-ts/ and
     * served by the same extension-asset endpoint, which inflates it
     * for a client that does not take gzip. It draws what milsymbol
     * does not: the tactical graphics a frame carries as a geometry
     * rather than a point — boundaries, phase lines, areas, axes of
     * advance, corridors, range fans, ellipses. The UMD bundle publishes
     * itself as `window.C5Ren`.
     *
     * Seven megabytes inflated, so unlike milsymbol it is not fetched
     * on mount: the first frame that carries a multipoint geometry asks
     * for it. Until it lands — or forever, if it does not — a graphic
     * is drawn as its bare geometry in the affinity colour, the same
     * kind of fallback the pins have in their four shapes.
     */
    var bowireMilSymTsLoading = null;

    function bowireLoadMilSymTs() {
        if (window.C5Ren && window.C5Ren.WebRenderer) {
            return Promise.resolve(window.C5Ren);
        }
        if (bowireMilSymTsLoading) return bowireMilSymTsLoading;

        bowireMilSymTsLoading = new Promise(function (resolve, reject) {
            var script = document.createElement('script');
            script.src = bowireMapBundleUrl + '/mil-sym-ts.js';
            script.async = true;
            script.onload = function () {
                if (window.C5Ren && window.C5Ren.WebRenderer) resolve(window.C5Ren);
                else reject(new Error('mil-sym-ts loaded but window.C5Ren is undefined'));
            };
            script.onerror = function () {
                bowireMilSymTsLoading = null;
                reject(new Error('Failed to load mil-sym-ts from ' + script.src));
            };
            document.head.appendChild(script);
        });
        return bowireMilSymTsLoading;
    }

    /**
     * The 2525C → 2525D crosswalk for tactical graphics — the
     * fifteen-letter code's category and function id to the 2525D
     * entity — built from Esri's joint-military-symbology-xml legacy
     * table (Apache-2.0) and served next to mil-sym-ts. Fetched once
     * per session, the first time a graphic arrives with a C code;
     * shared by every widget through `window.bowireMil2525CGraphics`.
     */
    var bowireMil2525CGraphicsLoading = null;

    function bowireLoadMil2525CGraphics() {
        if (window.bowireMil2525CGraphics) return Promise.resolve(window.bowireMil2525CGraphics);
        if (bowireMil2525CGraphicsLoading) return bowireMil2525CGraphicsLoading;
        bowireMil2525CGraphicsLoading = window.fetch(bowireMapBundleUrl + '/2525c-graphics.json')
            .then(function (res) {
                if (!res.ok) throw new Error('2525c-graphics.json: HTTP ' + res.status);
                return res.json();
            })
            .then(function (json) {
                var table = json && json.map;
                if (!table || typeof table !== 'object') throw new Error('2525c-graphics.json has no map');
                window.bowireMil2525CGraphics = table;
                return table;
            }, function (e) {
                bowireMil2525CGraphicsLoading = null;
                throw e;
            });
        return bowireMil2525CGraphicsLoading;
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
     * surfaces it as a top-level config key when set; absence falls
     * through to the demotiles default (see bowireMapBasemapSpec).
     */
    function bowireMapTileUrl() {
        var cfg = window.__BOWIRE_CONFIG__;
        return (cfg && cfg.mapTileUrl) || null;
    }

    /**
     * Resolve the basemap to render under the pins. Three modes:
     *
     *   1. Custom URL — `config.mapBasemap` is a raster tile URL with
     *      {z}/{x}/{y} placeholders, or a style.json URL. Picks the
     *      shape automatically by inspecting the URL's tail.
     *   2. Named alias — `config.mapBasemap` is "osm" / "satellite" /
     *      "demotiles" / "none". "osm" hits openstreetmap.org;
     *      "satellite" hits ESRI's free World Imagery service (no API
     *      key, attribution required); "demotiles" hits MapLibre's
     *      free demo vector dataset (the recommended free-tier
     *      fallback); "none" reverts to the pre-v0.3 blank-style
     *      behaviour for true offline installs.
     *   3. Default — demotiles. Works in any browser that can reach
     *      MapLibre's CDN without an API key. Custom URL is still the
     *      preferred answer for internal-mapserver setups, configured
     *      via host appsettings (Bowire:Map:Basemap key) or per-user
     *      via the Settings dialog (planned).
     *
     * Returns either { kind: 'styleUrl', url } / { kind: 'raster', url }
     * / { kind: 'blank' } so the caller can build the MapLibre style
     * object without re-parsing the URL.
     */
    function bowireMapBasemapSpec() {
        var cfg = window.__BOWIRE_CONFIG__ || {};
        // Legacy mapTileUrl still honoured as an explicit raster source.
        if (cfg.mapTileUrl) {
            return { kind: 'raster', url: cfg.mapTileUrl, attribution: '' };
        }

        var basemap = cfg.mapBasemap;
        if (basemap === 'none') return { kind: 'blank' };
        if (basemap === 'osm') {
            return {
                kind: 'raster',
                url: 'https://tile.openstreetmap.org/{z}/{x}/{y}.png',
                attribution: '© <a href="https://www.openstreetmap.org/copyright" target="_blank" rel="noopener">OpenStreetMap</a>'
            };
        }
        if (basemap === 'satellite') {
            // ESRI's World Imagery service — free, no API key required,
            // attribution-required, and one of the highest-resolution
            // free satellite mosaics. Used widely by Leaflet / MapLibre
            // tutorials as the "just works" satellite default.
            return {
                kind: 'raster',
                url: 'https://server.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer/tile/{z}/{y}/{x}',
                attribution: 'Tiles © <a href="https://www.esri.com" target="_blank" rel="noopener">Esri</a> &mdash; Source: Esri, Maxar, Earthstar Geographics, and the GIS User Community'
            };
        }
        if (basemap === 'demotiles' || !basemap) {
            return {
                kind: 'styleUrl',
                url: 'https://demotiles.maplibre.org/style.json'
            };
        }
        // Heuristic: a string ending in .json is treated as a full
        // style URL; anything else as a raster tile URL.
        if (/\.json(\?.*)?$/i.test(basemap)) {
            return { kind: 'styleUrl', url: basemap };
        }
        return { kind: 'raster', url: basemap, attribution: '' };
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
        for (var ti = 0; ti < tokens.length; ti++) {
            if (cur == null) return undefined;
            var key = tokens[ti];
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
     *
     * Note: when a frame carries a MIL-2525C SIDC the symbol layer
     * (below) overrides the per-discriminator colour with the
     * affinity-coloured tactical icon (Friend cyan square / Hostile red
     * diamond / Neutral green square / Unknown yellow circle). The
     * discriminator palette stays in place as a fallback for frames
     * that lack a SIDC — including the WGS84 detector's bread-and-butter
     * non-military shapes (GPS traces, weather buoys, AIS without
     * symbol code).
     */
    var BOWIRE_MAP_PALETTE = [
        '#4f46e5', '#dc2626', '#16a34a', '#d97706',
        '#0891b2', '#c026d3', '#7c3aed', '#65a30d'
    ];
    // Separator for the composite track key. A control character, not a
    // punctuation mark, because every half is a free-form string: a JSON
    // path carries dots and brackets, a discriminator carries whatever
    // the protocol calls its message type, and a track id carries
    // whatever the producer put in the field. Any printable delimiter is
    // one that could also occur inside a half and merge two tracks into
    // one.
    var BOWIRE_TRACK_KEY_SEP = String.fromCharCode(31);

    function bowireMapDiscriminatorColor(discriminator, store) {
        var key = discriminator || '*';
        if (!(key in store)) {
            store[key] = BOWIRE_MAP_PALETTE[Object.keys(store).length % BOWIRE_MAP_PALETTE.length];
        }
        return store[key];
    }

    /**
     * MIL-2525C / APP-6 affinity icons. Rendered as inline SVG and
     * uploaded to the MapLibre sprite atlas via `map.addImage` on load.
     * Four shapes covering the standard-identity space — the same four
     * NATO planners draw on paper maps:
     *
     *   - Friend    → cyan-blue rectangle (sea-surface vessel framing
     *                 in MIL-STD-2525C Appendix A is a rectangle for
     *                 friend, with the affinity carried by the cyan
     *                 fill colour).
     *   - Hostile   → red diamond (rotated square per the affinity
     *                 chart; reads as "threat" at a glance against
     *                 satellite imagery).
     *   - Neutral   → green square (NATO neutral colour, square outline
     *                 for sea-surface objects).
     *   - Unknown   → yellow filled circle with question mark; reserved
     *                 for affinity P/U/A/J/K/etc. when no clean shape
     *                 mapping applies.
     *
     * White stroke and a deliberately high-contrast palette so the
     * icons stay legible on both OSM and the ESRI satellite basemap.
     * 32x32 viewBox uploads at MapLibre's pixel-ratio-aware default
     * resolution — same approach Mapbox's own MIL-2525 demos use.
     */
    var BOWIRE_MAP_AFFINITY_ICONS = {
        friend:
            '<svg xmlns="http://www.w3.org/2000/svg" width="32" height="32" viewBox="0 0 32 32">' +
              '<rect x="5" y="10" width="22" height="13" rx="1" ry="1"' +
              ' fill="#22d3ee" stroke="#ffffff" stroke-width="2"/>' +
            '</svg>',
        hostile:
            '<svg xmlns="http://www.w3.org/2000/svg" width="32" height="32" viewBox="0 0 32 32">' +
              '<polygon points="16,3 29,16 16,29 3,16"' +
              ' fill="#dc2626" stroke="#ffffff" stroke-width="2"/>' +
            '</svg>',
        neutral:
            '<svg xmlns="http://www.w3.org/2000/svg" width="32" height="32" viewBox="0 0 32 32">' +
              '<rect x="6" y="6" width="20" height="20"' +
              ' fill="#16a34a" stroke="#ffffff" stroke-width="2"/>' +
            '</svg>',
        unknown:
            '<svg xmlns="http://www.w3.org/2000/svg" width="32" height="32" viewBox="0 0 32 32">' +
              '<circle cx="16" cy="16" r="11"' +
              ' fill="#facc15" stroke="#ffffff" stroke-width="2"/>' +
              '<text x="16" y="22" font-family="Arial, Helvetica, sans-serif"' +
              ' font-size="16" font-weight="bold" fill="#1c1917"' +
              ' text-anchor="middle">?</text>' +
            '</svg>'
    };

    /**
     * The four affinity colours as the icons above paint them, for the
     * graphics that fall back to their bare geometry: a line in the
     * colour says whose it is the way the shape does for a pin.
     */
    var BOWIRE_MAP_AFFINITY_COLORS = {
        friend: '#22d3ee',
        hostile: '#dc2626',
        neutral: '#16a34a',
        unknown: '#facc15'
    };

    /**
     * Load an SVG string into the MapLibre sprite atlas under `name`.
     * MapLibre's `addImage` accepts HTMLImageElement, so we route the
     * SVG through a data URL → Image() → addImage path. The promise
     * resolves once the image has been registered (or rejects if the
     * SVG fails to decode).
     */
    function bowireRegisterMapIcon(map, name, svg, opts) {
        var width = (opts && opts.width) || 32;
        var height = (opts && opts.height) || 32;
        // Sprites rendered at 2x declare it, so MapLibre draws them at
        // their intended size and hi-DPI screens get the extra pixels.
        var addOpts = (opts && opts.pixelRatio) ? { pixelRatio: opts.pixelRatio } : undefined;
        return new Promise(function (resolve, reject) {
            // Bail early if the image is already present (re-mount path).
            if (map.hasImage(name)) { resolve(); return; }
            var img = new Image(width, height);
            img.onload = function () {
                try {
                    if (!map.hasImage(name)) map.addImage(name, img, addOpts);
                    resolve();
                } catch (e) { reject(e); }
            };
            img.onerror = function () {
                reject(new Error('Failed to decode SVG for icon ' + name));
            };
            // Inline data URL avoids hitting the network and stays
            // immune to CORS issues with cross-origin sprite hosts.
            img.src = 'data:image/svg+xml;charset=utf-8,'
                    + encodeURIComponent(svg);
        });
    }

    /**
     * MIL-2525C SIDC pattern. 15 characters, first character is a
     * coding-scheme letter, second character is the standard identity
     * (the bit we actually use for affinity colouring), the remaining
     * 13 are battle-dimension/function-id/etc. Standard identity uses
     * the 14-letter set listed in MIL-STD-2525C Appendix A; the regex
     * accepts both the live affinities (F/H/N/U/...) and the wildcard
     * `-`/`*` fillers seeded examples and real coalition feeds use.
     *
     * Position 4 is the status — present / anticipated, plus the
     * 2525C additions (fully capable, damaged, destroyed, full to
     * capacity) — and positions 5–10 are the function id, which can be
     * any letter. An earlier form of this pattern checked the status
     * set one position late, against the function id's first letter:
     * `SFGPU…` passed by luck, a cruiser (`SFSPCLCC…`) did not, and a
     * pin whose own code fell through picked up the nearest
     * neighbour's colour from the scan of the whole frame.
     */
    var BOWIRE_SIDC_RE = /^[A-Z][PUAFNSHGWMDLJK\-\*][A-Z\-\*][PACDXF\-\*][A-Z\-\*]{11}$/;

    /**
     * MIL-2525D / APP-6D SIDC pattern. All digits: 20 for the base
     * code, 30 with the optional country-code and originator block
     * appended. Positions 1–2 are the version (`10` for 2525D, `11`
     * for 2525E / APP-6E), which is what tells it apart from any other
     * twenty-digit number that happens to sit next to a coordinate.
     */
    var BOWIRE_SIDC_D_RE = /^1[0-9]\d{18}(\d{10})?$/;

    /** Either standard's SIDC shape. */
    function bowireIsSidc(value) {
        return BOWIRE_SIDC_RE.test(value) || BOWIRE_SIDC_D_RE.test(value);
    }

    /**
     * Recursive depth-first scan for the first string in `value` that
     * matches the SIDC pattern. Used as the per-entity lookup once
     * `findSidcForPair` has narrowed `value` down to the entity root —
     * the standard TacticalAPI shape buries the SIDC at
     * `symbol.symbolIdentifier.content.stringIdentifier`, but the scan
     * stays schema-agnostic so symbol catalogues that nest the
     * identifier differently still light up.
     */
    function bowireFindSidcInValue(value) {
        if (value == null) return null;
        if (typeof value === 'string') {
            return bowireIsSidc(value) ? value : null;
        }
        if (Array.isArray(value)) {
            for (var i = 0; i < value.length; i++) {
                var f = bowireFindSidcInValue(value[i]);
                if (f) return f;
            }
            return null;
        }
        if (typeof value === 'object') {
            // The two ten-digit halves a 2525D code travels as in
            // TacticalAPI's NumericIdentifier are one code, not two
            // numbers that happen to sit together.
            var numeric = bowireNumericSidc(value);
            if (numeric) return numeric;
            for (var k in value) {
                if (Object.prototype.hasOwnProperty.call(value, k)) {
                    var f2 = bowireFindSidcInValue(value[k]);
                    if (f2) return f2;
                }
            }
        }
        return null;
    }

    /**
     * Strip the trailing path segment (object key or array index) off
     * a JSONPath-ish string. Used by `findSidcForPair` to walk upward
     * from the lat/lon parent toward the entity root.
     */
    function bowireDropLastSegment(path) {
        return path.replace(/\.[^.\[\]]+$|\[\d+\]$/, '');
    }

    /**
     * Longest common prefix of two JSONPath strings, computed in
     * structural units (segments separated by `.` or `[N]`). For
     * `$.situationObjects[0].symbol.location.content.point.geoPoint.latitudeCoordinate`
     * + the same path with `longitudeCoordinate` at the tail, this
     * returns `$.situationObjects[0].symbol.location.content.point.geoPoint`
     * — i.e. the lat/lon parent (`geoPoint`).
     */
    function bowireCommonPathPrefix(a, b) {
        var min = Math.min(a.length, b.length);
        var lastSep = 0;
        for (var i = 0; i < min; i++) {
            if (a[i] !== b[i]) break;
            if (a[i] === '.' || a[i] === '[' || a[i] === ']') lastSep = i;
        }
        if (lastSep === 0) {
            // No structural separator matched — bail to the leading
            // root marker. Caller resolves '$' as the whole root which
            // still gives DFS scan a chance to find the SIDC.
            return '$';
        }
        return a.substring(0, lastSep);
    }

    /**
     * Per-pair SIDC lookup. Walks UP from the lat/lon common parent,
     * level-by-level, doing a depth-first SIDC scan at each level.
     * Stops at the first ancestor that yields a SIDC — that's the
     * entity root for this coord pair. Walking UP rather than
     * scanning the whole frame matters for multi-pairing responses
     * (e.g. `situationObjects[N]`): a frame-wide DFS would always
     * return entity[0]'s SIDC regardless of which entity the coord
     * belonged to.
     */
    function bowireFindSidcForPair(parsedRoot, latPath, lonPath) {
        if (parsedRoot == null) return null;
        var common = bowireCommonPathPrefix(latPath, lonPath);
        var probe = common;
        var safety = 16;
        while (safety-- > 0 && probe && probe !== '') {
            var node = bowireResolveJsonPath(parsedRoot, probe);
            if (node != null) {
                var hit = bowireFindSidcInValue(node);
                if (hit) return hit;
            }
            if (probe === '$') break;
            var next = bowireDropLastSegment(probe);
            if (next === probe) break;
            probe = next || '$';
        }
        return null;
    }

    /**
     * MIL-2525C standard-identity character (position 2 of the SIDC) →
     * affinity bucket. Buckets line up with the four icons the
     * symbol layer paints:
     *
     *   F (Friend), A (AssumedFriend), M (ExerciseFriend),
     *   W (ExerciseAssumedFriend)                              → friend
     *   H (Hostile), S (Suspect), L (ExerciseHostile)          → hostile
     *   N (Neutral), D (ExerciseNeutral)                       → neutral
     *   P (Pending), U (Unknown), G (ExercisePending),
     *   J (Joker), K (Faker), other/blank                      → unknown
     *
     * A 2525D code carries the same identity as one digit at
     * position 4 (context — reality / exercise / simulation — sits
     * at position 3 and does not change the colour):
     *
     *   2 (AssumedFriend), 3 (Friend)                          → friend
     *   5 (Suspect), 6 (Hostile)                               → hostile
     *   4 (Neutral)                                            → neutral
     *   0 (Pending), 1 (Unknown), other                        → unknown
     */
    function bowireSidcAffinity(sidc) {
        if (!sidc || sidc.length < 2) return 'unknown';
        if (BOWIRE_SIDC_D_RE.test(sidc)) {
            var d = sidc.charAt(3);
            if (d === '2' || d === '3') return 'friend';
            if (d === '5' || d === '6') return 'hostile';
            if (d === '4') return 'neutral';
            return 'unknown';
        }
        var c = sidc.charAt(1).toUpperCase();
        if (c === 'F' || c === 'A' || c === 'M' || c === 'W') return 'friend';
        if (c === 'H' || c === 'S' || c === 'L') return 'hostile';
        if (c === 'N' || c === 'D') return 'neutral';
        return 'unknown';
    }

    /**
     * Colours for the panels that float over the map — the trajectory
     * toggle, the tracks legend, the playback bar. They sit on
     * whatever the basemap is (satellite imagery, OSM, the blank
     * offline style) and follow the workbench theme the host hands in
     * as `ctx.theme.mode`. MapLibre paints a control group white; the
     * legend used to be a white card on a dark workbench, and the
     * playback bar a dark strip on a light one.
     *
     * Inline for the same reason as everything else in this widget:
     * an extension renders against any host and cannot rely on
     * bowire.css. `colorScheme` is what makes the native checkbox,
     * select and text input inside the panels take the same side.
     */
    function bowireMapOverlayPalette(theme) {
        var light = theme && theme.mode === 'light';
        return light
            ? {
                scheme: 'light',
                bg: 'rgba(255,255,255,0.9)',
                fg: '#1c1f26',
                border: 'rgba(0,0,0,0.12)',
                controlBg: 'rgba(0,0,0,0.04)',
                controlBorder: 'rgba(0,0,0,0.22)'
            }
            : {
                scheme: 'dark',
                bg: 'rgba(20,22,30,0.86)',
                fg: '#e8eaf0',
                border: 'rgba(255,255,255,0.14)',
                controlBg: 'rgba(255,255,255,0.08)',
                controlBorder: 'rgba(255,255,255,0.25)'
            };
    }

    /** Paint one floating panel with the palette. */
    function bowireMapThemePanel(elm, palette) {
        elm.style.background = palette.bg;
        elm.style.color = palette.fg;
        elm.style.colorScheme = palette.scheme;
        elm.style.backdropFilter = 'blur(2px)';
    }

    /** Paint a native select / text input inside a panel. */
    function bowireMapThemeField(elm, palette) {
        elm.style.background = palette.controlBg;
        elm.style.color = 'inherit';
        elm.style.border = '1px solid ' + palette.controlBorder;
        elm.style.borderRadius = '3px';
        elm.style.padding = '2px 4px';
    }

    /**
     * Sprite name a pin's SIDC resolves to. The symbol layer asks for
     * this name first and falls back to the affinity shape while it is
     * not (or never) registered — see the `coalesce` in the layer's
     * `icon-image`.
     */
    function bowireSidcIconName(sidc) {
        return 'bowire-sidc-' + sidc;
    }

    /**
     * Draw one SIDC with milsymbol. Returns `{ svg, width, height }` in
     * CSS pixels at 2x, or null when the library cannot make sense of
     * the code (milsymbol reports that through `isValid`, not by
     * throwing — but a throw is treated the same way).
     *
     * "Make sense of" means the frame: affiliation and battle
     * dimension known. A function id milsymbol has no icon for still
     * gets its frame — the sample's `SFGPUCT` is one, and an empty
     * friendly ground frame says more than the fallback rectangle
     * would. The plain `isValid()` would refuse it, because it also
     * demands the icon.
     *
     * milsymbol takes both standards from the string alone — fifteen
     * letters are 2525C, twenty digits are 2525D — so the widget does
     * not translate between them. `size` is the L-frame height; 40 at
     * pixelRatio 2 paints at the same 20 px the affinity shapes use.
     */
    function bowireRenderSidcSymbol(ms, sidc) {
        try {
            var sym = new ms.Symbol(sidc, { size: 40 });
            if (typeof sym.isValid === 'function') {
                var v = sym.isValid(true);
                if (v && typeof v === 'object') {
                    if (!v.affiliation || v.affiliation === 'undefined') return null;
                    if (v.dimensionUnknown || !v.drawInstructions) return null;
                } else if (!v) {
                    return null;
                }
            }
            var size = sym.getSize();
            if (!size || !(size.width > 0) || !(size.height > 0)) return null;
            return {
                svg: sym.asSVG(),
                width: Math.ceil(size.width),
                height: Math.ceil(size.height)
            };
        } catch (e) {
            return null;
        }
    }

    // ------------------------------------------------------------------
    // Tactical graphics — the multipoint symbols.
    //
    // A pin is a point, and a point is the one shape a symbol renderer
    // can draw from the code alone. The rest of what MIL-STD-2525 calls
    // a tactical graphic is drawn from its geometry: a boundary follows
    // its vertices, an axis of advance takes a path and a width point, a
    // range fan is a vertex with two ranges and two azimuths. The WGS84
    // detector sees none of that — it pairs lat and lon per object and
    // hands the widget one coordinate per vertex — so the widget has to
    // put the vertices back together before it can ask the renderer for
    // the graphic. That is what the grouping below does, from the paths
    // the coordinates arrived under.
    // ------------------------------------------------------------------

    /** Last object key of a JSONPath — `polygon` for `$.a[3].location.polygon`. */
    function bowireLastSegmentName(path) {
        var m = /(?:^|\.)([^.\[\]]+)(?:\[\d+\])*$/.exec(path || '');
        return m ? m[1] : '';
    }

    /**
     * The twenty-digit form of a 2525D code that arrived as the two
     * ten-digit halves TacticalAPI's `NumericIdentifier` carries —
     * `{ firstTenDigits, secondTenDigits }`, as numbers or, the way
     * protobuf's JSON mapping writes an int64, as strings. Either half
     * can have lost a leading zero on the way through an integer, so
     * both are padded back to ten. Null when the shape is not that.
     */
    function bowireNumericSidc(value) {
        if (!value || typeof value !== 'object' || Array.isArray(value)) return null;
        var a = value.firstTenDigits, b = value.secondTenDigits;
        if (a == null || b == null) return null;
        a = String(a); b = String(b);
        if (!/^\d{1,10}$/.test(a) || !/^\d{1,10}$/.test(b)) return null;
        while (a.length < 10) a = '0' + a;
        while (b.length < 10) b = '0' + b;
        var sidc = a + b;
        return BOWIRE_SIDC_D_RE.test(sidc) ? sidc : null;
    }

    /**
     * A 2525C tactical graphic's code as the 2525D code mil-sym-ts can
     * draw, or null when the table has no row for it (or the code is
     * not a tactical graphic — scheme letter `G`).
     *
     * The table carries what only a table can: the function id's
     * entity. The rest translates letter by letter — the affiliation to
     * context + identity (an exercise affiliation is the exercise
     * context with the live identity), the status to present or
     * planned, the echelon letter at position 12 to the two-digit
     * amplifier. Modifiers are left at zero; a 2525C graphic has none
     * in its code.
     */
    var BOWIRE_2525C_IDENTITY = {
        P: '00', U: '01', A: '02', F: '03', N: '04', S: '05', H: '06',
        G: '10', W: '12', M: '13', D: '14', L: '16', J: '15', K: '16'
    };
    var BOWIRE_2525C_ECHELON = {
        A: '11', B: '12', C: '13', D: '14', E: '15', F: '16', G: '17', H: '18',
        I: '21', J: '22', K: '23', L: '24', M: '25', N: '26'
    };

    function bowireSidcCToD(sidc, table) {
        if (!table || typeof sidc !== 'string' || sidc.length < 10) return null;
        if (sidc.charAt(0).toUpperCase() !== 'G') return null;
        var upper = sidc.toUpperCase().replace(/\*/g, '-');
        var entity = table[upper.charAt(2) + upper.substring(4, 10)];
        if (!entity) return null;
        var contextIdentity = BOWIRE_2525C_IDENTITY[upper.charAt(1)] || '01';
        var status = upper.charAt(3) === 'A' ? '1' : '0';
        var echelon = (upper.length >= 12 && BOWIRE_2525C_ECHELON[upper.charAt(11)]) || '00';
        return '10' + contextIdentity + '25' + status + '0' + echelon + entity + '0000';
    }

    /**
     * Walk UP from `startPath` towards the root, level by level, until
     * an ancestor's subtree yields a SIDC. Returns the code, the node it
     * was found under and that node's path — the entity root — or null.
     * The per-pair lookup the pins use is this walk started at the
     * coordinate's parent; a graphic starts it at its geometry.
     */
    function bowireFindEntityUpwards(parsedRoot, startPath) {
        if (parsedRoot == null) return null;
        var probe = startPath || '$';
        var safety = 16;
        while (safety-- > 0 && probe) {
            var node = bowireResolveJsonPath(parsedRoot, probe);
            if (node != null) {
                var hit = bowireFindSidcInValue(node);
                if (hit) return { sidc: hit, node: node, path: probe };
            }
            if (probe === '$') break;
            var next = bowireDropLastSegment(probe);
            if (next === probe) break;
            probe = next || '$';
        }
        return null;
    }

    /**
     * The designation a graphic is labelled with — the `T` modifier the
     * standard prints as "PL HANSE" or "AA BUCHE" from the bare name.
     * Read off the entity root: a `name` that is a string, or a
     * `{ content }` wrapper the way TacticalAPI's data properties nest
     * it. Empty when the entity has neither.
     */
    function bowireGraphicDesignation(entityNode) {
        if (!entityNode || typeof entityNode !== 'object') return '';
        var name = entityNode.name;
        if (name && typeof name === 'object' && typeof name.content === 'string') return name.content;
        if (typeof name === 'string') return name;
        return '';
    }

    /** Great-circle distance in metres — enough for a radius or a width. */
    function bowireGeoDistanceMetres(lat1, lon1, lat2, lon2) {
        var toRad = Math.PI / 180;
        var dLat = (lat2 - lat1) * toRad, dLon = (lon2 - lon1) * toRad;
        var a = Math.sin(dLat / 2) * Math.sin(dLat / 2)
            + Math.cos(lat1 * toRad) * Math.cos(lat2 * toRad) * Math.sin(dLon / 2) * Math.sin(dLon / 2);
        return 6371000 * 2 * Math.atan2(Math.sqrt(a), Math.sqrt(1 - a));
    }

    /** Initial bearing from point 1 to point 2, degrees clockwise from north. */
    function bowireGeoBearingDegrees(lat1, lon1, lat2, lon2) {
        var toRad = Math.PI / 180;
        var y = Math.sin((lon2 - lon1) * toRad) * Math.cos(lat2 * toRad);
        var x = Math.cos(lat1 * toRad) * Math.sin(lat2 * toRad)
            - Math.sin(lat1 * toRad) * Math.cos(lat2 * toRad) * Math.cos((lon2 - lon1) * toRad);
        return ((Math.atan2(y, x) * 180 / Math.PI) + 360) % 360;
    }

    /** First numeric value on `node` whose key matches `re`, or null. */
    function bowireNumericField(node, re) {
        if (!node || typeof node !== 'object') return null;
        for (var k in node) {
            if (!Object.prototype.hasOwnProperty.call(node, k) || !re.test(k)) continue;
            var v = node[k];
            if (typeof v === 'string') v = parseFloat(v);
            if (typeof v === 'number' && isFinite(v)) return v;
        }
        return null;
    }

    /**
     * Sort the coordinates of one frame into pins and graphics.
     *
     * A coordinate's parent path says how it sits in the frame. Under
     * an array index — `…polygon.points[2]` — it is a vertex, and every
     * vertex of the same array is one geometry: a line, an area, an
     * axis, a corridor. Under a key — `…ellipse.centerPoint`,
     * `…fan.vertexPoint`, `…point.geoPoint` — it is a named point, and
     * the geometry is the object holding it; an ellipse is three named
     * points, a fan one point with ranges and azimuths beside it, and a
     * plain point is a pin.
     *
     * A geometry is only a graphic when an entity above it carries a
     * symbol code: that is what says the vertices are a tactical
     * graphic rather than a GPS trace, whose points stay pins and a
     * trajectory. With a code but fewer than two vertices, or a named
     * point that is neither ellipse nor fan, the coordinate is a pin as
     * before — the pin path is unchanged for everything it handled.
     *
     * The key names are TacticalAPI's, read as patterns: `points`,
     * `width`, `minimumRangeDimension`, `orientationAngle`,
     * `centerPoint`. Another catalogue that nests differently gets its
     * areas and lines from the array rule alone, which needs no names.
     */
    function bowireSplitGraphics(coords) {
        var pins = [];
        var graphics = [];
        var arrays = new Map();  // arrayPath -> { coords: [ { idx, coord } ] }
        var named = new Map();   // geometryPath -> { points: { key: coord } }

        for (var i = 0; i < coords.length; i++) {
            var coord = coords[i];
            var parent = coord.parentPath || '';
            var m = /^(.*)\[(\d+)\]$/.exec(parent);
            if (m) {
                var group = arrays.get(m[1]);
                if (!group) { group = { coords: [] }; arrays.set(m[1], group); }
                group.coords.push({ idx: parseInt(m[2], 10), coord: coord });
            } else {
                var geomPath = bowireDropLastSegment(parent);
                var ng = named.get(geomPath);
                if (!ng) { ng = { points: {}, order: [] }; named.set(geomPath, ng); }
                var key = bowireLastSegmentName(parent);
                ng.points[key] = coord;
                ng.order.push(key);
            }
        }

        arrays.forEach(function (group, arrayPath) {
            group.coords.sort(function (a, b) { return a.idx - b.idx; });
            var list = group.coords.map(function (e) { return e.coord; });
            var geomPath = bowireDropLastSegment(arrayPath);
            var entity = list.length >= 2 ? bowireFindEntityUpwards(list[0].root, geomPath) : null;
            if (!entity) {
                for (var p = 0; p < list.length; p++) pins.push(list[p]);
                return;
            }
            var geomNode = bowireResolveJsonPath(list[0].root, geomPath);
            var kind = bowireLastSegmentName(geomPath);
            var graphic = {
                key: arrayPath,
                geomPath: geomPath,
                kind: kind,
                sidc: entity.sidc,
                affinity: bowireSidcAffinity(entity.sidc),
                designation: bowireGraphicDesignation(entity.node),
                points: list.map(function (c) { return [c.lat, c.lon]; }),
                first: list[0],
                vertexPaths: list.map(function (c) { return c.parentPath; }),
                closed: /polygon|area/i.test(kind),
                modifiers: {}
            };
            // A corridor is its centreline and a width in metres — the
            // standard's AM modifier.
            var width = bowireNumericField(geomNode, /^width$|corridor.*width|width.*m(etre|eter)s?$/i);
            if (width != null && width > 0) graphic.modifiers.AM_DISTANCE = String(Math.round(width));
            graphics.push(graphic);
        });

        named.forEach(function (ng, geomPath) {
            var keys = ng.order;
            var first = ng.points[keys[0]];
            var geomNode = bowireResolveJsonPath(first.root, geomPath);
            var kind = bowireLastSegmentName(geomPath);
            var entity = null;

            // Ellipse: a centre and one point on each axis. Radii are
            // the distances to the axis points, the rotation is the
            // bearing of the first axis, turned into the standard's
            // convention (degrees counter-clockwise from east).
            var centreKey = null;
            for (var k = 0; k < keys.length; k++) {
                if (/cent(er|re)/i.test(keys[k])) { centreKey = keys[k]; break; }
            }
            if (keys.length >= 3 && centreKey && /ellipse/i.test(kind)) {
                entity = bowireFindEntityUpwards(first.root, geomPath);
            }
            if (entity) {
                var centre = ng.points[centreKey];
                var axes = keys.filter(function (kk) { return kk !== centreKey; })
                    .map(function (kk) { return ng.points[kk]; });
                var r1 = bowireGeoDistanceMetres(centre.lat, centre.lon, axes[0].lat, axes[0].lon);
                var r2 = bowireGeoDistanceMetres(centre.lat, centre.lon, axes[1].lat, axes[1].lon);
                var majorPoint = r1 >= r2 ? axes[0] : axes[1];
                var bearing = bowireGeoBearingDegrees(centre.lat, centre.lon, majorPoint.lat, majorPoint.lon);
                var rotation = ((90 - bearing) % 360 + 360) % 360;
                graphics.push({
                    key: geomPath,
                    geomPath: geomPath,
                    kind: kind,
                    sidc: entity.sidc,
                    affinity: bowireSidcAffinity(entity.sidc),
                    designation: bowireGraphicDesignation(entity.node),
                    points: [[centre.lat, centre.lon]],
                    first: centre,
                    vertexPaths: keys.map(function (kk) { return ng.points[kk].parentPath; }),
                    closed: false,
                    modifiers: {
                        AM_DISTANCE: Math.round(Math.max(r1, r2)) + ',' + Math.round(Math.min(r1, r2)),
                        AN_AZIMUTH: String(Math.round(rotation))
                    }
                });
                return;
            }

            // Fan: one vertex, a minimum and maximum range in metres, the
            // azimuth of the left edge and the width of the sector — the
            // standard wants the two ranges as AM and both edges as AN.
            var minRange = bowireNumericField(geomNode, /min(imum)?.*range/i);
            var maxRange = bowireNumericField(geomNode, /max(imum)?.*range/i);
            var leftAz = bowireNumericField(geomNode, /orientation|left.*azimuth/i);
            var sector = bowireNumericField(geomNode, /sector.*(size|angle|width)/i);
            var rightAz = bowireNumericField(geomNode, /right.*azimuth/i);
            if (keys.length === 1 && maxRange != null && leftAz != null && (sector != null || rightAz != null)) {
                entity = bowireFindEntityUpwards(first.root, geomPath);
            }
            if (entity) {
                if (rightAz == null) rightAz = (leftAz + sector) % 360;
                graphics.push({
                    key: geomPath,
                    geomPath: geomPath,
                    kind: kind,
                    sidc: entity.sidc,
                    affinity: bowireSidcAffinity(entity.sidc),
                    designation: bowireGraphicDesignation(entity.node),
                    points: [[first.lat, first.lon]],
                    first: first,
                    vertexPaths: [first.parentPath],
                    closed: false,
                    modifiers: {
                        AM_DISTANCE: Math.round(Math.max(0, minRange || 0)) + ',' + Math.round(maxRange),
                        AN_AZIMUTH: Math.round(leftAz) + ',' + Math.round(rightAz)
                    }
                });
                return;
            }

            for (var q = 0; q < keys.length; q++) pins.push(ng.points[keys[q]]);
        });

        return { pins: pins, graphics: graphics };
    }

    /**
     * Ask mil-sym-ts for one graphic at the current view. Multipoint
     * symbols cannot be drawn the same at every scale — an arrowhead is
     * so many pixels wide, not so many metres — so the renderer takes
     * the viewport and the caller draws again when it moves.
     *
     * Returns the GeoJSON features to draw, or null when the library
     * declined: a code it does not know, too few points for the draw
     * rule, a 2525C string it has no tables for. The caller falls back
     * to the bare geometry then. Labels that are only a caption with
     * nothing after the colon ("Min Alt: ") are dropped here; the
     * renderer emits them for the optional amplifiers a corridor has
     * slots for, whether or not the data filled them.
     */
    function bowireRenderGraphic(c5, graphic, view) {
        var WebRenderer = c5.WebRenderer;
        var sidc = graphic.renderSidc || graphic.sidc;
        var controlPoints = graphic.points.map(function (p) { return p[1] + ',' + p[0]; }).join(' ');
        var modifiers = new Map();
        if (graphic.designation) modifiers.set('T_UNIQUE_DESIGNATION_1', graphic.designation);
        for (var k in graphic.modifiers) {
            if (Object.prototype.hasOwnProperty.call(graphic.modifiers, k)) modifiers.set(k, graphic.modifiers[k]);
        }
        var attributes = new Map();
        attributes.set('LINEWIDTH', '2');
        attributes.set('HIDEOPTIONALLABELS', 'true');
        var out;
        try {
            out = WebRenderer.RenderSymbol2D(
                'bowire-' + graphic.key, graphic.designation || '', '',
                sidc, controlPoints,
                view.width, view.height, view.bbox,
                modifiers, attributes, WebRenderer.OUTPUT_FORMAT_GEOJSON);
        } catch (e) {
            return null;
        }
        var parsed;
        try { parsed = typeof out === 'string' ? JSON.parse(out) : out; } catch { return null; }
        if (!parsed || !Array.isArray(parsed.features)) return null;
        var features = [];
        for (var i = 0; i < parsed.features.length; i++) {
            var f = parsed.features[i];
            if (!f || !f.geometry || !f.geometry.coordinates || f.geometry.coordinates.length === 0) continue;
            var props = f.properties || {};
            if (f.geometry.type === 'Point') {
                var label = String(props.label == null ? '' : props.label);
                if (!label.trim() || /^[^:]+:\s*$/.test(label)) continue;
            }
            features.push(f);
        }
        return features;
    }

    /**
     * The labels come out of the renderer as points with a text and a
     * font; the map cannot set type without a glyph server, which the
     * offline lockdown forbids. So each distinct label is drawn once on
     * a canvas — the outline the renderer asks for behind the text — and
     * registered as a sprite, the same route the symbols take. Returns
     * `{ data, width, height }` at 2x, or null when the page has no
     * canvas to draw on.
     */
    function bowireDrawLabelSprite(props) {
        var text = String(props.label);
        var pt = parseFloat(String(props.fontSize || '12pt')) || 12;
        var px = /px$/.test(String(props.fontSize)) ? pt : pt * 4 / 3;
        var weight = props.fontWeight || 'bold';
        var family = props.fontFamily || 'Arial, sans-serif';
        var outline = props.labelOutlineWidth != null ? Number(props.labelOutlineWidth) : 3;
        var ratio = 2;
        var canvas = document.createElement('canvas');
        var ctx2d = canvas.getContext('2d');
        if (!ctx2d) return null;
        ctx2d.font = weight + ' ' + (px * ratio) + 'px ' + family;
        var metrics = ctx2d.measureText(text);
        var pad = outline * ratio + 2;
        var width = Math.ceil(metrics.width + pad * 2);
        var height = Math.ceil(px * ratio * 1.3 + pad * 2);
        canvas.width = width;
        canvas.height = height;
        ctx2d = canvas.getContext('2d');
        ctx2d.font = weight + ' ' + (px * ratio) + 'px ' + family;
        ctx2d.textBaseline = 'middle';
        ctx2d.textAlign = 'center';
        ctx2d.lineJoin = 'round';
        if (outline > 0) {
            ctx2d.lineWidth = outline * ratio;
            ctx2d.strokeStyle = props.labelOutlineColor || '#ffffff';
            ctx2d.strokeText(text, width / 2, height / 2);
        }
        ctx2d.fillStyle = props.fontColor || '#000000';
        ctx2d.fillText(text, width / 2, height / 2);
        return { data: ctx2d.getImageData(0, 0, width, height), width: width, height: height };
    }

    /**
     * Sprite name for a label — one per distinct text and paint, so a
     * phase line that says "PL HANSE" at both ends draws one sprite.
     */
    function bowireLabelSpriteName(props) {
        return 'bowire-lbl-' + [
            props.label, props.fontColor, props.fontSize, props.fontWeight, props.labelOutlineColor
        ].join('|');
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
        // Fill the available height (not just minHeight) so the canvas
        // grows when the widget pane changes geometry — maximize toggle,
        // split-pane drag. Without an explicit height rule the slot
        // would size to its content (== the canvas at mount-time
        // dimensions) and never grow.
        container.style.height = '100%';
        container.style.flex = '1 1 auto';

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
            notice.textContent = t('map.unavailable');
            container.appendChild(notice);
            return function () { if (notice.parentNode) notice.parentNode.removeChild(notice); };
        }
        if (disposed) return function () {};

        // Symbol sprites, one per distinct SIDC seen on this map.
        // 'waiting' until milsymbol has loaded, 'pending' while the SVG
        // decodes, 'ok' once MapLibre has it, 'failed' when milsymbol
        // or the decode rejected the code — the pin then keeps its
        // affinity shape and the code is not retried on every frame.
        var sidcIcons = new Map();
        var milSymbol = null;
        bowireLoadMilSymbol().then(function (ms) {
            if (disposed) return;
            milSymbol = ms;
            // Pins that arrived before the library did.
            sidcIcons.forEach(function (state, sidc) {
                if (state === 'waiting') ensureSidcIcon(sidc);
            });
        }, function (e) {
            console.warn('[bowire-map] milsymbol unavailable, pins keep the affinity shapes:', e);
        });

        var overlay = bowireMapOverlayPalette(ctx.theme);

        var basemap = bowireMapBasemapSpec();
        var style;
        if (basemap.kind === 'styleUrl') {
            // MapLibre accepts a style URL directly — that's the path
            // for demotiles and other style.json-shaped basemaps. The
            // map constructor fetches it and wires glyphs/sprite if
            // the style declares them.
            style = basemap.url;
        } else if (basemap.kind === 'raster') {
            // Self-built raster style for a single tile URL. Custom
            // mapserver setups and the OSM alias both land here.
            // glyphs/sprite stay omitted because the workbench's
            // pin-rendering doesn't use them — adding either would
            // silently widen the egress surface.
            style = {
                version: 8,
                sources: {
                    'bowire-tiles': {
                        type: 'raster',
                        tiles: [basemap.url],
                        tileSize: 256,
                        attribution: basemap.attribution || ''
                    }
                },
                layers: [{
                    id: 'bowire-tiles-layer',
                    type: 'raster',
                    source: 'bowire-tiles'
                }]
            };
        } else {
            // `mapBasemap: 'none'` — true offline fallback.
            style = bowireMapBlankStyle(ctx.theme && ctx.theme.mode);
        }

        var map = new maplibregl.Map({
            container: container,
            style: style,
            center: [0, 0],
            zoom: 1,
            // Show attribution when a basemap is in play — OSM's tile
            // usage policy requires it, and demotiles/MapLibre style.json
            // wires it for free. Pure-blank fallback has no attribution
            // to render.
            attributionControl: basemap.kind !== 'blank' ? { compact: true } : false
        });
        map.addControl(new maplibregl.NavigationControl({ showCompass: false }), 'top-right');

        // Bowire's ResizeObserver fires whenever the widget container
        // changes geometry — split-pane drag, maximize toggle (CSS
        // position-fixed to fill the viewport), window resize.
        // MapLibre's own ResizeObserver in v3+ usually catches the
        // same event, but position-fixed transitions trip it on some
        // browsers — calling map.resize() explicitly is cheap and
        // makes the maximize-to-fullscreen story robust.
        if (ctx.viewport && typeof ctx.viewport.on === 'function') {
            ctx.viewport.on('resize', function () {
                try { map.resize(); } catch (e) { /* map disposed */ }
            });
        }

        // Lazy-attach a "points" source on first frame so the empty
        // state has nothing on the map until data flows.
        var pointsSource = {
            type: 'FeatureCollection',
            features: []
        };

        // #238 — trajectory geometry. The line features are DERIVED from
        // pointsSource on every rebuild rather than accumulated
        // alongside it. Two things fall out of that and both matter:
        // the FIFO cap below already trims pins, so the trajectory
        // inherits the same 5000-vertex ceiling without a second
        // budget to keep in step; and a pin and its segment can never
        // disagree about position, selection or colour, because there
        // is only one copy of that state.
        //
        // The cost is an O(pins) pass per frame. addPin already
        // serialises the whole collection through setData on every
        // frame, so this is the same order of work — and it is skipped
        // outright while the toggle is off, which is the default.
        var linesSource = {
            type: 'FeatureCollection',
            features: []
        };

        // ctx.prefs is a v1.1 additive. A widget mounted by an older
        // host (or a third-party one) must still mount, so fall back to
        // an in-memory stub: the toggle then works for the session and
        // simply does not survive a reload.
        var mapPrefs = (ctx && ctx.prefs) || {
            get: function (key, fallback) { return fallback; },
            set: function () { return false; }
        };
        // `.method` is additive on top of `prefs`, which is itself
        // additive on the v1.0 contract, so a host can have one and not
        // the other. Resolved into its own binding rather than patched
        // onto `ctx.prefs`: that object belongs to the host, is shared
        // with every other widget on the page, and a widget writing a
        // field onto it would be deciding for all of them. Falling back
        // to the widget-wide store keeps the call sites unconditional —
        // a per-method setting then behaves as a per-workspace one,
        // which is a narrower promise than intended but never a crash.
        var mapMethodPrefs = mapPrefs.method || mapPrefs;
        var showTrajectory = mapPrefs.get('trajectory', false) === true;

        await new Promise(function (resolve) {
            map.on('load', resolve);
        });
        if (disposed) {
            try { map.remove(); } catch {}
            return function () {};
        }

        // Register the MIL-2525C affinity icons in the sprite atlas
        // before the symbol layer references them. addLayer is forgiving
        // when an icon-image points at a not-yet-registered name, but
        // the first frame the consumer loop hands us would silently
        // render the layer's `icon-image` fallback ('unknown') for
        // every pin until the icons land — racy and ugly. Await the
        // four registrations up front and the symbol layer always has
        // its sprites ready when addPin fires.
        try {
            await Promise.all([
                bowireRegisterMapIcon(map, 'bowire-affinity-friend',
                    BOWIRE_MAP_AFFINITY_ICONS.friend),
                bowireRegisterMapIcon(map, 'bowire-affinity-hostile',
                    BOWIRE_MAP_AFFINITY_ICONS.hostile),
                bowireRegisterMapIcon(map, 'bowire-affinity-neutral',
                    BOWIRE_MAP_AFFINITY_ICONS.neutral),
                bowireRegisterMapIcon(map, 'bowire-affinity-unknown',
                    BOWIRE_MAP_AFFINITY_ICONS.unknown)
            ]);
        } catch (e) {
            // SVG decode failure leaves the layer falling back to its
            // 'unknown' default for every pin — degraded but not broken.
            console.warn('[bowire-map] failed to register affinity icons:', e);
        }
        if (disposed) {
            try { map.remove(); } catch {}
            return function () {};
        }

        // Tactical graphics — the multipoint symbols, drawn by
        // mil-sym-ts from the geometry a frame carries. Three sources:
        // the fills, the strokes and the labels, all rebuilt from the
        // renderer's output whenever the graphics change or the camera
        // stops moving. Registered BEFORE the trajectory and pin layers
        // so an area's fill never covers a pin.
        var graphicsFillSource = { type: 'FeatureCollection', features: [] };
        var graphicsLineSource = { type: 'FeatureCollection', features: [] };
        var graphicsLabelSource = { type: 'FeatureCollection', features: [] };
        map.addSource('bowire-graphics-fill', { type: 'geojson', data: graphicsFillSource });
        map.addSource('bowire-graphics-lines', { type: 'geojson', data: graphicsLineSource });
        map.addSource('bowire-graphics-labels', { type: 'geojson', data: graphicsLabelSource });
        map.addLayer({
            id: 'bowire-graphics-fill-layer',
            type: 'fill',
            source: 'bowire-graphics-fill',
            paint: {
                'fill-color': ['get', 'color'],
                'fill-opacity': ['get', 'opacity']
            }
        });
        // A light casing under every stroke. The standard draws a
        // friendly graphic black, which vanishes on the dark blank style
        // and on satellite imagery; the casing is what a paper overlay's
        // white ground did for the same line.
        map.addLayer({
            id: 'bowire-graphics-casing-layer',
            type: 'line',
            source: 'bowire-graphics-lines',
            layout: { 'line-cap': 'round', 'line-join': 'round' },
            paint: {
                // The accent under a graphic the JSON viewer is hovering,
                // the same signal the pins give with their halo.
                'line-color': [
                    'match', ['get', 'highlighted'],
                    'yes', (ctx.theme && ctx.theme.accent) || '#4f46e5',
                    /* default */ 'rgba(255,255,255,0.75)'
                ],
                'line-width': ['+', ['get', 'width'], 3],
                'line-opacity': ['get', 'opacity']
            }
        });
        // Solid and dashed strokes are two layers because MapLibre's
        // line-dasharray is not data-driven; the renderer marks a
        // planned graphic dashed and the feature carries the flag.
        map.addLayer({
            id: 'bowire-graphics-lines-layer',
            type: 'line',
            source: 'bowire-graphics-lines',
            filter: ['!=', ['get', 'dashed'], 'yes'],
            layout: { 'line-cap': 'round', 'line-join': 'round' },
            paint: {
                'line-color': ['get', 'color'],
                'line-width': ['get', 'width'],
                'line-opacity': ['get', 'opacity']
            }
        });
        map.addLayer({
            id: 'bowire-graphics-dashed-layer',
            type: 'line',
            source: 'bowire-graphics-lines',
            filter: ['==', ['get', 'dashed'], 'yes'],
            layout: { 'line-cap': 'butt', 'line-join': 'round' },
            paint: {
                'line-color': ['get', 'color'],
                'line-width': ['get', 'width'],
                'line-opacity': ['get', 'opacity'],
                'line-dasharray': [2, 2]
            }
        });
        // Labels are sprites, not text: the offline lockdown allows no
        // glyph server, so each label is drawn on a canvas once and
        // placed as an icon at the point and angle the renderer chose
        // for this view. Alignment and offset come from the renderer
        // too; both are recomputed with the graphic on every move.
        map.addLayer({
            id: 'bowire-graphics-labels-layer',
            type: 'symbol',
            source: 'bowire-graphics-labels',
            layout: {
                'icon-image': ['get', 'sprite'],
                'icon-anchor': ['get', 'anchor'],
                'icon-offset': ['get', 'offset'],
                'icon-rotate': ['get', 'angle'],
                'icon-rotation-alignment': 'viewport',
                'icon-pitch-alignment': 'viewport',
                'icon-allow-overlap': true,
                'icon-ignore-placement': true
            },
            paint: {
                'icon-opacity': ['get', 'opacity']
            }
        });

        // Graphics by geometry path — the polygon, the line, the fan.
        // Each key holds the versions the stream has shown, oldest first,
        // stamped with the frame ordinal they arrived on: a snapshot feed
        // sends the same boundary every two seconds, and that is one
        // version, not a thousand — a new one is kept only when the
        // shape, code or name changed. The cursor picks the version in
        // force at the moment it shows, the way it picks the pins; live,
        // the latest wins. They are still not tracks: no trajectory, no
        // legend row — a control measure is planned, not observed.
        var graphics = new Map();       // key -> [{ ordinal, graphic, signature, cache }]
        var GRAPHIC_VERSION_CAP = 64;
        var graphicErrors = new Map();  // key -> points signature the renderer refused
        var milSymTs = null;
        var milSymTsFailed = false;
        // The 2525C → 2525D crosswalk, fetched on the first C-coded graphic.
        var crosswalk = window.bowireMil2525CGraphics || null;
        var crosswalkFailed = false;
        var labelSprites = new Set();
        var graphicsRenderTimer = null;

        function graphicSignature(g) {
            return JSON.stringify([g.sidc, g.designation, g.points, g.modifiers]);
        }

        /**
         * The version of a graphic the map shows right now: the latest
         * while live, otherwise the last one that had arrived by the
         * cursor's frame — and none at all for a graphic the stream had
         * not yet shown at that moment.
         */
        function graphicAtCursor(versions) {
            if (!versions || versions.length === 0) return null;
            if (cursorOrdinal === null) return versions[versions.length - 1];
            for (var i = versions.length - 1; i >= 0; i--) {
                if (versions[i].ordinal <= cursorOrdinal) return versions[i];
            }
            return null;
        }

        function noteGraphics(list, frame, frameOrdinal) {
            var frameId = (frame && frame.id) || null;
            var discriminator = (frame && frame.discriminator) || '*';
            for (var i = 0; i < list.length; i++) {
                var g = list[i];
                g.frameId = frameId;
                g.discriminator = discriminator;
                g.highlighted = false;
                var versions = graphics.get(g.key);
                if (!versions) { versions = []; graphics.set(g.key, versions); }
                var signature = graphicSignature(g);
                var last = versions.length > 0 ? versions[versions.length - 1] : null;
                if (last && last.signature === signature) {
                    // Same graphic again: the frame it belongs to moves
                    // on, the version does not. Highlight state rides
                    // on the version, so it survives a re-send too.
                    last.graphic.frameId = frameId;
                    g = last.graphic;
                } else {
                    if (last) g.highlighted = last.graphic.highlighted;
                    versions.push({ ordinal: frameOrdinal, graphic: g, signature: signature, cache: null });
                    if (versions.length > GRAPHIC_VERSION_CAP) versions.splice(0, versions.length - GRAPHIC_VERSION_CAP);
                }
                for (var p = 0; p < g.points.length; p++) {
                    var pt = g.points[p];
                    if (!bounds) bounds = new maplibregl.LngLatBounds([pt[1], pt[0]], [pt[1], pt[0]]);
                    else bounds.extend([pt[1], pt[0]]);
                }
                // A fan or an ellipse is one point and a reach in metres;
                // the auto-fit has to see the reach, or the graphic sits
                // half off the edge of a view fitted to its centre.
                if (g.points.length === 1 && g.modifiers.AM_DISTANCE) {
                    var reach = 0;
                    String(g.modifiers.AM_DISTANCE).split(',').forEach(function (v) {
                        var n = parseFloat(v);
                        if (isFinite(n) && n > reach) reach = n;
                    });
                    if (reach > 0) {
                        var lat = g.points[0][0], lon = g.points[0][1];
                        var dLat = reach / 111320;
                        var dLon = reach / (111320 * Math.max(0.1, Math.cos(lat * Math.PI / 180)));
                        bounds.extend([lon - dLon, lat - dLat]);
                        bounds.extend([lon + dLon, lat + dLat]);
                    }
                }
            }
            if (!crosswalk && !crosswalkFailed && list.some(function (g) { return BOWIRE_SIDC_RE.test(g.sidc); })) {
                bowireLoadMil2525CGraphics().then(function (table) {
                    if (disposed) return;
                    crosswalk = table;
                    renderGraphics();
                }, function (e) {
                    crosswalkFailed = true;
                    console.warn('[bowire-map] 2525C crosswalk unavailable, C-coded graphics keep their bare geometry:', e);
                });
            }
            if (list.length > 0 && !milSymTs && !milSymTsFailed) {
                // The first graphic asks for the library; the pins never
                // pay for it.
                bowireLoadMilSymTs().then(function (c5) {
                    if (disposed) return;
                    milSymTs = c5;
                    renderGraphics();
                }, function (e) {
                    milSymTsFailed = true;
                    console.warn('[bowire-map] mil-sym-ts unavailable, graphics keep their bare geometry:', e);
                });
            }
        }

        function scheduleGraphicsRender() {
            if (graphicsRenderTimer !== null || graphics.size === 0) return;
            graphicsRenderTimer = setTimeout(function () {
                graphicsRenderTimer = null;
                if (!disposed) renderGraphics();
            }, 40);
        }
        map.on('moveend', scheduleGraphicsRender);
        map.on('resize', scheduleGraphicsRender);

        function ensureLabelSprite(props) {
            var name = bowireLabelSpriteName(props);
            if (labelSprites.has(name) && map.hasImage(name)) return name;
            var drawn = bowireDrawLabelSprite(props);
            if (!drawn) return null;
            try {
                if (map.hasImage(name)) map.removeImage(name);
                map.addImage(name, drawn.data, { pixelRatio: 2 });
            } catch (e) {
                return null;
            }
            labelSprites.add(name);
            return name;
        }

        function graphicSelectedTag(g) {
            var anySelected = selectedFrameIds.size > 0;
            var isSelected = g.frameId != null && selectedFrameIds.has(g.frameId);
            return isSelected ? 'yes' : (anySelected ? 'no-but-others-are' : 'no');
        }

        /**
         * The bare geometry, for a graphic the renderer cannot or has
         * not yet drawn: its vertices as a line in the affinity colour,
         * closed for an area. The same kind of fallback the pins have in
         * their four shapes — visibly a placeholder, never nothing.
         */
        function fallbackGraphicFeatures(g, selected) {
            var color = BOWIRE_MAP_AFFINITY_COLORS[g.affinity] || BOWIRE_MAP_AFFINITY_COLORS.unknown;
            var coords = g.points.map(function (p) { return [p[1], p[0]]; });
            if (coords.length < 2) return { lines: [], fills: [], labels: [] };
            if (g.closed && coords.length >= 3) coords.push(coords[0]);
            return {
                lines: [{
                    type: 'Feature',
                    geometry: { type: 'LineString', coordinates: coords },
                    properties: graphicLineProps(g, selected, color, 2, false, 1)
                }],
                fills: [],
                labels: []
            };
        }

        function graphicLineProps(g, selected, color, width, dashed, opacity) {
            return {
                color: color,
                width: (selected === 'yes' || g.highlighted) ? width + 2 : width,
                dashed: dashed ? 'yes' : 'no',
                opacity: selected === 'no-but-others-are' && !g.highlighted ? opacity * 0.45 : opacity,
                highlighted: g.highlighted ? 'yes' : 'no',
                graphicKey: g.key,
                sidc: g.sidc,
                frameId: g.frameId,
                parentPath: (g.first && g.first.parentPath) || '',
                latPath: (g.first && g.first.latPath) || '',
                lonPath: (g.first && g.first.lonPath) || ''
            };
        }

        /**
         * Draw every graphic for the current view and hand the three
         * collections to their sources. Called when graphics arrive,
         * when the library lands, when the selection changes and when
         * the camera comes to rest — the renderer's output is for one
         * viewport, and an arrowhead drawn for the last one is the wrong
         * size for this one.
         */
        function renderGraphics() {
            var fillSrc = map.getSource('bowire-graphics-fill');
            var lineSrc = map.getSource('bowire-graphics-lines');
            var labelSrc = map.getSource('bowire-graphics-labels');
            if (!fillSrc || !lineSrc || !labelSrc) return;

            var fills = [], lines = [], labels = [];
            var view = null;
            if (milSymTs) {
                var container = map.getContainer();
                var b = map.getBounds();
                view = {
                    width: Math.max(1, Math.round(container.clientWidth || 1)),
                    height: Math.max(1, Math.round(container.clientHeight || 1)),
                    bbox: [b.getWest(), b.getSouth(), b.getEast(), b.getNorth()].join(',')
                };
            }

            var viewKey = view ? view.width + '|' + view.height + '|' + view.bbox : null;
            graphics.forEach(function (versions) {
                var version = graphicAtCursor(versions);
                if (!version) return;
                var g = version.graphic;
                var selected = graphicSelectedTag(g);
                // A 2525C code is drawn through the crosswalk; until the
                // table is here, or for a code it does not know, the
                // graphic stays bare — never handed to a renderer that
                // has no tables for it.
                g.renderSidc = BOWIRE_SIDC_RE.test(g.sidc) ? bowireSidcCToD(g.sidc, crosswalk) : g.sidc;
                var signature = g.points.length + '|' + (g.renderSidc || '');
                var drawn = null;
                if (view && g.renderSidc && graphicErrors.get(g.key) !== signature) {
                    // The renderer's answer is for one view; while the
                    // view holds — a hover, a selection, a re-sent frame —
                    // the answer holds too, and only the paint changes.
                    if (version.cache && version.cache.viewKey === viewKey) {
                        drawn = version.cache.features;
                    } else {
                        drawn = bowireRenderGraphic(milSymTs, g, view);
                        version.cache = drawn ? { viewKey: viewKey, features: drawn } : null;
                    }
                    if (!drawn) graphicErrors.set(g.key, signature);
                }
                if (!drawn) {
                    var fb = fallbackGraphicFeatures(g, selected);
                    lines.push.apply(lines, fb.lines);
                    return;
                }
                for (var i = 0; i < drawn.length; i++) {
                    var f = drawn[i];
                    var p = f.properties || {};
                    var type = f.geometry.type;
                    if (type === 'Point') {
                        var sprite = ensureLabelSprite(p);
                        if (!sprite) continue;
                        var align = p.labelAlign === 'left' ? 'left' : (p.labelAlign === 'right' ? 'right' : 'center');
                        labels.push({
                            type: 'Feature',
                            geometry: f.geometry,
                            properties: {
                                sprite: sprite,
                                anchor: align,
                                offset: [Number(p.anchorOffsetX) || 0, Number(p.anchorOffsetY) || 0],
                                angle: Number(p.angle) || 0,
                                opacity: selected === 'no-but-others-are' ? 0.45 : 1,
                                graphicKey: g.key
                            }
                        });
                        continue;
                    }
                    var strokeColor = p.strokeColor || '#000000';
                    var strokeWidth = Number(p.strokeWidth) || 2;
                    var dashed = Array.isArray(p.strokeDasharray) && p.strokeDasharray.length > 0;
                    var lineOpacity = p.lineOpacity != null ? Number(p.lineOpacity) : 1;
                    if (type === 'Polygon' || type === 'MultiPolygon') {
                        if (p.fillColor) {
                            fills.push({
                                type: 'Feature',
                                geometry: f.geometry,
                                properties: {
                                    color: p.fillColor,
                                    opacity: (p.fillOpacity != null ? Number(p.fillOpacity) : 0.25)
                                        * (selected === 'no-but-others-are' ? 0.45 : 1),
                                    graphicKey: g.key,
                                    frameId: g.frameId,
                                    parentPath: (g.first && g.first.parentPath) || '',
                                    latPath: (g.first && g.first.latPath) || '',
                                    lonPath: (g.first && g.first.lonPath) || ''
                                }
                            });
                        }
                        // The outline rides in the line source so it gets
                        // the casing and the dash treatment like any stroke.
                        var rings = type === 'Polygon' ? [f.geometry.coordinates] : f.geometry.coordinates;
                        for (var r = 0; r < rings.length; r++) {
                            lines.push({
                                type: 'Feature',
                                geometry: { type: 'MultiLineString', coordinates: rings[r] },
                                properties: graphicLineProps(g, selected, strokeColor, strokeWidth, dashed, lineOpacity)
                            });
                        }
                        continue;
                    }
                    if (type === 'LineString' || type === 'MultiLineString') {
                        lines.push({
                            type: 'Feature',
                            geometry: f.geometry,
                            properties: graphicLineProps(g, selected, strokeColor, strokeWidth, dashed, lineOpacity)
                        });
                    }
                }
            });

            graphicsFillSource.features = fills;
            graphicsLineSource.features = lines;
            graphicsLabelSource.features = labels;
            fillSrc.setData(graphicsFillSource);
            lineSrc.setData(graphicsLineSource);
            labelSrc.setData(graphicsLabelSource);
        }

        // Trajectory line source + layer. Registered BEFORE the two
        // point layers so it paints underneath them: MapLibre stacks in
        // insertion order, and the pins have to stay on top to remain
        // legible and clickable for selection.
        map.addSource('bowire-lines', { type: 'geojson', data: linesSource });
        map.addLayer({
            id: 'bowire-lines-layer',
            type: 'line',
            source: 'bowire-lines',
            layout: {
                'line-cap': 'round',
                'line-join': 'round',
                // Toggled rather than added/removed on demand: keeping
                // the layer registered means the toggle never has to
                // re-derive its position in the stack, and a hidden
                // layer with an empty source costs nothing to draw.
                visibility: showTrajectory ? 'visible' : 'none'
            },
            paint: {
                // Per-discriminator colour, carried on the feature so
                // one layer covers every track — same approach the pin
                // layers take with affinity and selection.
                'line-color': ['get', 'color'],
                'line-width': [
                    'match', ['get', 'selected'],
                    'yes', 4,
                    /* default */ 2
                ],
                // Unselected tracks sit back so a selected path reads
                // as the foreground one, mirroring how the pin layer
                // dims with 'no-but-others-are'.
                'line-opacity': [
                    'match', ['get', 'selected'],
                    'yes', 0.95,
                    'no-but-others-are', 0.35,
                    /* default */ 0.6
                ]
            }
        });

        map.addSource('bowire-points', { type: 'geojson', data: pointsSource });

        // Selection halo — a circle layer sitting UNDER the symbol
        // layer that paints an accent-coloured ring around any pin
        // with selected === 'yes'. Pulled out of the old combined
        // circle layer so the affinity icon on top stays unobscured
        // by the halo, and so unselected pins are visually
        // affinity-driven (no leftover circle artefact). circle-opacity
        // is gated on the selection state so the halo doesn't show
        // around unselected pins at all.
        map.addLayer({
            id: 'bowire-points-halo',
            type: 'circle',
            source: 'bowire-points',
            paint: {
                // Halo grows for hover-highlight so the operator's
                // eye snaps to it from across the canvas; selected
                // pins keep the smaller ring so they don't fight
                // the hover signal.
                'circle-radius': [
                    'match', ['get', 'highlighted'],
                    'yes', 18,
                    /* default */ 14
                ],
                'circle-color': 'rgba(0,0,0,0)',
                'circle-stroke-width': [
                    'match', ['get', 'highlighted'],
                    'yes', 4,
                    /* default */ 3
                ],
                'circle-stroke-color': (ctx.theme && ctx.theme.accent) || '#4f46e5',
                'circle-opacity': 1,
                // Both `selected === 'yes'` and `highlighted === 'yes'`
                // light the halo. Highlight wins on tie (same colour
                // anyway). Resolved through a single `case` so a
                // pin selected AND hover-highlighted reads as the
                // brighter hover state.
                'circle-stroke-opacity': [
                    'case',
                    ['==', ['get', 'highlighted'], 'yes'], 1.0,
                    ['==', ['get', 'selected'], 'yes'], 0.95,
                    0.0
                ]
            }
        });

        // Tactical symbology layer. The icon-image expression picks
        // one of the four registered sprites (friend / hostile /
        // neutral / unknown) based on the feature's `affinity`
        // property, which extractCoords derived from the SIDC's
        // standard-identity character (MIL-2525C Appendix A position
        // 2). icon-size + icon-opacity carry the selection state so a
        // selection event doesn't need a layer rebuild — same
        // single-setData re-render path Phase 3.1's circle layer used.
        map.addLayer({
            id: 'bowire-points-layer',
            type: 'symbol',
            source: 'bowire-points',
            layout: {
                // The pin's own MIL-2525 symbol when its sprite is
                // registered, the affinity shape until then. `image`
                // resolves against the images the style has right now,
                // so a sprite that lands later (milsymbol still loading,
                // SVG still decoding) is picked up by the next setData
                // without a layer rebuild.
                'icon-image': [
                    'coalesce',
                    ['image', ['concat', 'bowire-sidc-', ['get', 'sidc']]],
                    ['image', [
                        'match', ['get', 'affinity'],
                        'friend', 'bowire-affinity-friend',
                        'hostile', 'bowire-affinity-hostile',
                        'neutral', 'bowire-affinity-neutral',
                        /* default */ 'bowire-affinity-unknown'
                    ]]
                ],
                'icon-size': [
                    'match', ['get', 'selected'],
                    'yes', 1.2,
                    /* default */ 0.85
                ],
                'icon-allow-overlap': true,
                'icon-ignore-placement': true
            },
            paint: {
                'icon-opacity': [
                    'match', ['get', 'selected'],
                    'yes', 1.0,
                    'no-but-others-are', 0.55,
                    /* default */ 1.0
                ]
            }
        });

        var bounds = null;
        function maybeFit() {
            // MapLibre's LngLatBounds doesn't expose isFinite() — that's a
            // Mapbox-GL-JS API. An earlier guard `!bounds.isFinite || !bounds.isFinite()`
            // here read as `!undefined || …` which silently early-returned
            // every call, so fitBounds was NEVER invoked and the map stayed
            // centred at (0, 0) zoom 1 regardless of how many pins landed.
            // The bounds object IS validated by fitBounds itself; the
            // wrapping try/catch + an explicit !bounds short-circuit is
            // enough.
            if (!bounds) return;
            try { map.fitBounds(bounds, { padding: 30, maxZoom: 12, duration: 0 }); }
            catch { /* fitBounds rejects only on degenerate input — leave camera alone */ }
        }

        // Resolve ctx.interpretations into the list of (latPath, lonPath)
        // pairs we need to extract from every incoming frame. Two
        // shapes ship from the framework today:
        //   - Object (legacy / single-pairing): one kindMap with
        //     coordinate.latitude + coordinate.longitude
        //   - Array  (multi-pairing, Phase 3.2+): one entry per sibling
        //     pairing the framework folded into this mount, e.g. when
        //     the response has `situationObjects[N]` and the WGS84
        //     detector wrote N annotations. extensions.js does the fold
        //     when viewer.selectionMode === 'multi'.
        var interpretationPairs = (function () {
            var kinds = ctx.interpretations || {};
            var rawList = Array.isArray(kinds) ? kinds : [kinds];
            var pairs = [];
            for (var i = 0; i < rawList.length; i++) {
                var k = rawList[i] || {};
                var lat = k['coordinate.latitude'];
                var lon = k['coordinate.longitude'];
                if (lat && lon) pairs.push({ lat: lat, lon: lon });
            }
            return pairs;
        })();

        // Walk a frame through every interpretation pair, returning
        // every valid (lat, lon) we find. Returns [] when the frame
        // carries no coordinates the widget can plot. Each pair is
        // tried against the unwrapped frame roots (the SSE envelope
        // wraps the payload in `data`/`frame`, so we try both shapes).
        function extractCoords(frame) {
            if (interpretationPairs.length === 0) return [];

            var roots = [];
            if (frame && frame.data !== undefined) roots.push(frame.data);
            if (frame && frame.frame !== undefined) roots.push(frame.frame);
            roots.push(frame);

            var parsedRoots = [];
            for (var r = 0; r < roots.length; r++) {
                var root = roots[r];
                if (root && typeof root === 'string') {
                    try { root = JSON.parse(root); } catch { continue; }
                }
                if (root !== undefined && root !== null) parsedRoots.push(root);
            }

            var out = [];
            for (var p = 0; p < interpretationPairs.length; p++) {
                var pair = interpretationPairs[p];
                for (var i = 0; i < parsedRoots.length; i++) {
                    var lat = bowireResolveJsonPath(parsedRoots[i], pair.lat);
                    var lon = bowireResolveJsonPath(parsedRoots[i], pair.lon);
                    lat = typeof lat === 'number' ? lat : parseFloat(lat);
                    lon = typeof lon === 'number' ? lon : parseFloat(lon);
                    if (isFinite(lat) && isFinite(lon)
                        && lat >= -90 && lat <= 90
                        && lon >= -180 && lon <= 180) {
                        // Per-pair SIDC lookup. Walks UP from the lat/lon
                        // common parent until the first ancestor whose
                        // subtree contains a SIDC-shaped string — the
                        // entity root for THIS coord pair. Non-tactical
                        // frames (GPS traces, AIS without symbol code,
                        // weather buoys) return null → affinity defaults
                        // to 'unknown', which still renders as a yellow
                        // circle icon so the pin remains visible.
                        var sidc = bowireFindSidcForPair(
                            parsedRoots[i], pair.lat, pair.lon);
                        // Common path prefix of (lat, lon) — used by the
                        // JSON↔map hover-sync to look up a pin from a
                        // path that the JSON viewer's mouseenter
                        // handler resolved. Stable across re-renders;
                        // doesn't change unless the response schema
                        // does. Single source of truth for the
                        // `parent` of a coord pair.
                        var parentPath = bowireCommonPathPrefix(
                            pair.lat, pair.lon);
                        out.push({
                            lat: lat, lon: lon,
                            // #240 — the root this pair actually resolved
                            // against. A track-id path is resolved here
                            // too, and re-deriving which of the envelope
                            // shapes matched would be a second guess at a
                            // question already answered.
                            root: parsedRoots[i],
                            sidc: sidc,
                            affinity: bowireSidcAffinity(sidc),
                            latPath: pair.lat,
                            lonPath: pair.lon,
                            parentPath: parentPath
                        });
                        break; // one root match per pair is enough
                    }
                }
            }
            return out;
        }

        // Back-compat shim — single-coord extraction still has internal
        // callers (selection-driven camera moves, hover/pin testing).
        // Returns the FIRST extracted coord or null, preserving the
        // pre-aggregation contract for those callsites.
        function extractCoord(frame) {
            var coords = extractCoords(frame);
            return coords.length > 0 ? coords[0] : null;
        }

        // Phase 3.1 — selected-frame tracking. We hold the snapshot
        // separately from the feature collection so a re-styled feature
        // can be flagged in O(1) on each selection event. The
        // userMovedCamera flag suppresses auto-fit once the user has
        // panned/zoomed — same instinct as the existing 5-pin auto-fit
        // cutoff, just extended for the selection-driven camera moves.
        var selectedFrameIds = new Set();
        var framesSeen = 0;
        var userMovedCamera = false;
        map.on('dragstart', function () { userMovedCamera = true; });
        map.on('zoomstart', function (e) {
            // MapLibre fires zoomstart for programmatic flyTo / fitBounds
            // too; only count it as user-driven when there's no
            // originalEvent attached (originalEvent is set on
            // mousewheel/touch).
            if (e && e.originalEvent) userMovedCamera = true;
        });

        /**
         * Make sure the sprite for `sidc` exists, or is on its way.
         * Called for every pin that carries a code; the Map makes the
         * repeat calls free. The repaint after registration is what
         * turns the affinity shape into the symbol for pins already on
         * the map: `coalesce` skipped the sprite while it was missing,
         * so no tile ever asked for it, and MapLibre only re-lays-out
         * tiles for images they asked for. The setData is the ask.
         */
        function ensureSidcIcon(sidc) {
            if (!sidc) return;
            var state = sidcIcons.get(sidc);
            if (state && state !== 'waiting') return;
            if (!milSymbol) { sidcIcons.set(sidc, 'waiting'); return; }
            var name = bowireSidcIconName(sidc);
            if (map.hasImage(name)) { sidcIcons.set(sidc, 'ok'); return; }
            var drawn = bowireRenderSidcSymbol(milSymbol, sidc);
            if (!drawn) { sidcIcons.set(sidc, 'failed'); return; }
            sidcIcons.set(sidc, 'pending');
            bowireRegisterMapIcon(map, name, drawn.svg, {
                width: drawn.width, height: drawn.height, pixelRatio: 2
            }).then(function () {
                if (disposed) return;
                sidcIcons.set(sidc, 'ok');
                renderPoints();
            }, function (e) {
                sidcIcons.set(sidc, 'failed');
                console.warn('[bowire-map] could not register symbol for ' + sidc + ':', e);
            });
        }

        // A style swap drops every registered image; MapLibre asks for
        // the ones its layers still reference, and the sprite registry
        // answers by drawing them again.
        map.on('styleimagemissing', function (e) {
            var id = e && e.id;
            if (typeof id !== 'string' || id.indexOf('bowire-sidc-') !== 0) return;
            var sidc = id.slice('bowire-sidc-'.length);
            sidcIcons.delete(sidc);
            ensureSidcIcon(sidc);
        });

        function addPin(frame) {
            // Aggregated mode (Phase 3.2+ multi-pairing fold) returns
            // every (lat, lon) the frame carries, single-pairing mode
            // returns one. Either way, this loop emits a pin per
            // resolved coord. The shared `frameId` / `selected` /
            // `discriminator` properties tie each pin back to its
            // source frame for selection re-styling, exactly like the
            // single-coord path used to.
            var extracted = extractCoords(frame);
            if (extracted.length === 0) return;
            // The vertices of a tactical graphic are not entities: they
            // leave here as one geometry each and never become pins.
            var split = bowireSplitGraphics(extracted);
            var coords = split.pins;
            var frameOrdinal = framesSeen;
            framesSeen++;
            if (split.graphics.length > 0) noteGraphics(split.graphics, frame, frameOrdinal);
            if (coords.length === 0) {
                // A frame of nothing but graphics is still an update: it
                // is a moment the scrubber can show, it counts towards
                // the settling window the auto-fit uses, and it has to
                // be drawn.
                noteFrameOnTimeline(frame, frameOrdinal);
                renderGraphics();
                if (framesSeen <= 5 && !userMovedCamera) maybeFit();
                return;
            }
            collectTrackCandidates(frame, coords);
            var discriminator = (frame && frame.discriminator) || '*';
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

            for (var c = 0; c < coords.length; c++) {
                var coord = coords[c];
                // Per coord, not per frame: one multi-pairing frame
                // carries N entities, and resolving the track once for
                // the frame is exactly the bug that would put them all
                // on one line and one colour.
                var track = bowireTrackFor(
                    { discriminator: discriminator, sidc: coord.sidc,
                      parentPath: coord.parentPath }, coord);
                track.color = bowireMapDiscriminatorColor(track.key, paletteStore);
                noteTrackSeen(track);
                ensureSidcIcon(coord.sidc);
                trackSourceByPin.set(pinSeq + 1, coord);
                pointsSource.features.push({
                    type: 'Feature',
                    id: ++pinSeq,
                    geometry: { type: 'Point', coordinates: [coord.lon, coord.lat] },
                    properties: {
                        color: track.color,
                        discriminator: discriminator,
                        // #240 — resolved once, at arrival, against the
                        // root this coord came from. Re-resolving later
                        // is impossible (the frame is gone) and
                        // re-deriving from the pin would be a different
                        // answer, so the key travels with the pin.
                        trackKey: track.key,
                        trackId: track.trackId || '',
                        // #239 — which update this pin belongs to. The
                        // cursor moves over frames, so every pin has to
                        // say which one it came in on.
                        frameOrdinal: frameOrdinal,
                        frameId: frameId,
                        selected: selectedTag,
                        // Highlight tristate driven by the JSON↔map
                        // hover-sync. 'yes' lights up the halo, 'no'
                        // hides it. Independent of selection so
                        // hovering doesn't fight a Wireshark
                        // multi-select.
                        highlighted: 'no',
                        // affinity drives the symbol-layer's
                        // icon-image expression (friend/hostile/neutral/
                        // unknown → matching sprite). sidc is stashed
                        // for future per-pin tooltips and downstream
                        // detectors that key off the full identifier.
                        affinity: coord.affinity || 'unknown',
                        sidc: coord.sidc || '',
                        // JSON-source paths for hover-sync lookup. The
                        // JSON viewer stamps the matching
                        // `data-bowire-coord-path` on lat/lon spans;
                        // mouseenter passes the path through
                        // `highlightByPath(path)` which scans for a
                        // feature whose latPath / lonPath /
                        // parentPath matches.
                        latPath: coord.latPath || '',
                        lonPath: coord.lonPath || '',
                        parentPath: coord.parentPath || ''
                    }
                });
                // Extend the bounds for EVERY coord in this frame —
                // without this, a multi-pairing frame (e.g.
                // TacticalAPI's situationObjects[N]) only widened the
                // viewport with the loop's last `coord` thanks to
                // `var` hoisting, so the auto-fit centred on one pin
                // and the others sat off-screen.
                if (!bounds) {
                    bounds = new maplibregl.LngLatBounds(
                        [coord.lon, coord.lat], [coord.lon, coord.lat]);
                } else {
                    bounds.extend([coord.lon, coord.lat]);
                }
            }

            trimToCap();
            noteFrameOnTimeline(frame, frameOrdinal);
            renderPoints();
            if (split.graphics.length > 0) renderGraphics();

            // Auto-fit on the first few pins, then leave navigation to
            // the user. Avoids the jitter of a re-fit on every frame.
            // Once the user has moved the camera (drag/zoom) OR a
            // selection has driven the camera, skip auto-fit so we
            // don't fight the user.
            // Auto-fit while the picture is still settling, then leave
            // the camera alone. The old cutoff counted PINS, which a
            // multi-entity frame passes on its first message: thirteen
            // entities in one snapshot meant the map never fitted once
            // and sat at zoom 1 over the whole world with the data a
            // speck off Denmark. Counting frames is what the rule always
            // meant — "the first few updates" — and it behaves the same
            // way it used to for one entity per frame.
            if (framesSeen <= 5 && !userMovedCamera) maybeFit();

            // After the trim, so a track never keeps a vertex whose pin
            // the cap has already dropped.
            rebuildTrajectories();
        }

        // #240 — per-entity tracks.
        //
        // The path is stored against (workspace, service, method): which
        // field carries the track id is a property of THIS response, not
        // of the operator, and carrying it to the next method would point
        // at a field that is not there.
        var trackIdPath = String(mapMethodPrefs.get('trackIdPath', '') || '');

        // Tracks the operator has switched off in the legend, by key.
        var hiddenTracks = new Set();

        // pin id -> the coord this pin was resolved from.
        //
        // Without it, changing the track path can only affect frames that
        // have not arrived yet — and the moment an operator most wants to
        // change it is when the stream has finished and the question is
        // how to read what came in.
        //
        // The whole coord is kept, root included, because resolving a
        // track id means walking UP from the coordinate's parent: the id
        // may live one level above (a flat `position`) or five
        // (TacticalAPI's geoPoint), and only the data knows. An entity
        // node alone cannot be climbed from.
        //
        // That retains the frame — but once per FRAME, not once per pin:
        // every pin from one multi-entity snapshot shares the same root
        // object, so thirteen entities across forty-six frames hold
        // forty-six roots. Trimmed in lockstep with the pins, so it is
        // bounded by the same cap.
        var trackSourceByPin = new Map();

        // key -> { label, color, count }. Insertion-ordered, which is
        // first-seen order, which is the order the legend lists them in —
        // a legend that reshuffles as counts change is unreadable while
        // a stream is live.
        var trackMeta = new Map();

        /** Drop the leading `$.` so absolute and relative paths compare. */
        function stripRoot(path) {
            return String(path || '').replace(/^\$\.?/, '');
        }

        /**
         * The object a track id would be read from for this coord: the
         * entity in a multi-pairing frame, the frame itself otherwise.
         */
        function entityNodeFor(coord) {
            if (!coord || coord.root == null) return null;
            if (coord.parentPath && coord.parentPath !== '$') {
                var node = bowireResolveJsonPath(coord.root, coord.parentPath);
                if (node != null && typeof node === 'object') return node;
            }
            return coord.root;
        }

        /**
         * Resolve the operator's track-id path against one coord.
         *
         * Two path shapes, and the difference is the whole design.
         *
         * An ABSOLUTE path ($.entity.id) names one field on the frame.
         * That is right when the frame carries one entity.
         *
         * A RELATIVE path (unitId, or symbol.name) is resolved against
         * the coord's own parent — `situationObjects[3]` for the fourth
         * entity in a multi-pairing frame, `situationObjects[4]` for the
         * fifth. This is what makes N entities in ONE frame resolve to N
         * different ids.
         *
         * The ticket's example writes that second case as
         * `$.situationObjects[*].unitId`. A wildcard would have to be
         * added to the resolver and would then still need a rule for
         * matching the Nth match to the Nth coord — and the widget
         * already knows which parent each coord came from, so the
         * wildcard is a less precise way of saying something already
         * known exactly. The relative form says it directly.
         *
         * Absolute is tried first and relative second, rather than
         * branching on the leading `$`, so a path that happens to
         * resolve both ways prefers the reading the operator wrote
         * literally.
         */
        function resolveTrackId(coord, node) {
            if (!trackIdPath) return null;
            var entity = node || entityNodeFor(coord);
            if (entity == null) return null;

            // Absolute first: `$.entity.id` names one field on the frame.
            var direct = coord && coord.root != null
                ? bowireResolveJsonPath(coord.root, trackIdPath)
                : null;

            // Then relative, walking UP from the coordinate's own parent.
            //
            // The climb is the part that matters. `parentPath` is the
            // parent of lat and lon, which is the node that HOLDS the
            // coordinate — `position`, or in the TacticalAPI shape
            // `geoPoint`, five levels below the entity. The entity is
            // wherever the identifying field actually lives, and only
            // the data knows how deep that is. Resolving against the
            // immediate parent alone finds a track id exactly when the
            // producer happens to put the coordinate straight on the
            // entity, and silently falls back to grouping by symbol
            // type otherwise — which reads as "thirteen entities, eight
            // tracks" and looks like a plausible picture.
            //
            // bowireFindSidcForPair already climbs for the same reason;
            // this is the same walk with the operator's field instead of
            // a shape-matched one.
            var relative = stripRoot(trackIdPath);
            if ((direct == null || typeof direct === 'object') && relative !== '') {
                var probe = entity;
                var hit = bowireResolveJsonPath(probe, relative);
                if (hit == null || typeof hit === 'object') {
                    var path = coord && coord.parentPath ? coord.parentPath : null;
                    var safety = 16;
                    while (safety-- > 0 && path && path !== '$' && path !== '') {
                        var next = bowireDropLastSegment(path);
                        if (next === path) break;
                        path = next || '$';
                        var ancestor = bowireResolveJsonPath(coord.root, path);
                        if (ancestor == null || typeof ancestor !== 'object') continue;
                        hit = bowireResolveJsonPath(ancestor, relative);
                        if (hit != null && typeof hit !== 'object') break;
                        hit = null;
                    }
                }
                if (hit != null && typeof hit !== 'object') direct = hit;
            }
            // A track id has to be a scalar the operator can read back in
            // the legend. An object resolves to "[object Object]" and
            // merges every entity into one track, which looks like the
            // feature working until someone counts the rows.
            if (direct == null || typeof direct === 'object') return null;
            var text = String(direct);
            return text === '' ? null : text;
        }

        /**
         * The grouping key for one pin, and the label the legend shows.
         *
         * With a track-id configured, the id IS the grouping — that is
         * the point of #240, and it is also what makes the colour
         * per-entity rather than per-message-type.
         *
         * Without one, this falls back to exactly what #238 shipped:
         * discriminator paired with the entity's own identity. The
         * ticket asks for "no regression" on the empty path, and the
         * regression to avoid is not the pin colour — it is the
         * multi-entity zigzag that keying on discriminator alone
         * reintroduces.
         */
        function bowireTrackFor(props, coord, node) {
            var trackId = (coord || node)
                ? resolveTrackId(coord, node)
                : ((props && props.trackId) || null);
            if (trackId) {
                return { key: 'id' + BOWIRE_TRACK_KEY_SEP + trackId, label: trackId, trackId: trackId };
            }
            var discriminator = (props && props.discriminator) || '*';
            var identity = (props && (props.sidc || props.parentPath)) || '';
            return {
                key: discriminator + BOWIRE_TRACK_KEY_SEP + identity,
                label: discriminator === '*' ? (identity || 'default') : discriminator,
                trackId: null
            };
        }

        /**
         * Rebuild every LineString from the current pin collection and
         * push it in one setData. Insertion order of the pins IS stream
         * order, so walking the collection front-to-back appends each
         * track's coordinates in the order they arrived — no separate
         * sort, and no timestamp to trust.
         *
         * A no-op while the toggle is off, which keeps the default
         * render path exactly what it was before this feature.
         */
        function rebuildTrajectories() {
            if (!showTrajectory) return;

            var byTrack = Object.create(null);
            var order = [];
            for (var i = 0; i < pointsSource.features.length; i++) {
                var f = pointsSource.features[i];
                var props = f.properties || {};
                // Pins carry their own key, stamped at arrival. Rebuilding
                // it here would resolve the track path against a root the
                // pin no longer holds, and would silently regroup every
                // pin already on the map the moment the operator changes
                // the path — which is what reassignTracks() does on
                // purpose, in one place, rather than as a side effect of
                // drawing.
                var key = props.trackKey || '';
                // The trajectory answers to the cursor for the same
                // reason the pins do: a path drawn past the moment being
                // looked at shows the operator the future.
                if (!pinVisibleAt(props)) continue;
                var track = byTrack[key];
                if (!track) {
                    track = byTrack[key] = {
                        coords: [],
                        color: props.color,
                        discriminator: props.discriminator,
                        trackId: props.trackId || '',
                        selected: 'no'
                    };
                    order.push(key);
                }
                track.coords.push(f.geometry.coordinates);
                // One selected pin lights the whole path it belongs to.
                // Selecting a frame means selecting an entity, and the
                // point of the highlight is to answer "where has this
                // one been" — dimming the rest of its own track would
                // work against that.
                if (props.selected === 'yes') track.selected = 'yes';
            }

            var anySelected = selectedFrameIds.size > 0;
            var features = [];
            for (var k = 0; k < order.length; k++) {
                var track = byTrack[order[k]];
                // A LineString needs two positions to be valid geometry,
                // and a track with one pin has no path to show anyway.
                // MapLibre tolerates the degenerate case; GeoJSON does
                // not, and anything reading this source through the
                // widget's remote-control surface would be right to
                // reject it.
                if (track.coords.length < 2) continue;
                features.push({
                    type: 'Feature',
                    geometry: { type: 'LineString', coordinates: track.coords },
                    properties: {
                        color: track.color,
                        discriminator: track.discriminator,
                        trackId: track.trackId,
                        selected: track.selected === 'yes'
                            ? 'yes'
                            : (anySelected ? 'no-but-others-are' : 'no')
                    }
                });
            }

            linesSource.features = features;
            var src = map.getSource('bowire-lines');
            if (src) src.setData(linesSource);
        }

        /**
         * Flip the trajectory on or off: persist the choice, show or
         * hide the layer, and either build the geometry or drop it.
         *
         * Dropping it on the way out is deliberate. A hidden layer over
         * a populated source still holds every vertex alive, and a long
         * stream that the operator turned the trajectory off for is
         * precisely the case where that memory is not wanted.
         */
        function setTrajectoryEnabled(enabled) {
            showTrajectory = !!enabled;
            mapPrefs.set('trajectory', showTrajectory);
            // Keep the control honest. The checkbox is not the state —
            // showTrajectory is — so anything that sets the state without
            // going through a click (a remounted widget restoring a
            // preference, the handle, a future keyboard shortcut) has to
            // push it back, or the box reads unchecked over a visible
            // layer.
            if (trajectoryCheckbox) trajectoryCheckbox.checked = showTrajectory;
            try {
                map.setLayoutProperty('bowire-lines-layer', 'visibility',
                    showTrajectory ? 'visible' : 'none');
            } catch { /* style not ready or layer gone — nothing to show */ }
            if (showTrajectory) {
                rebuildTrajectories();
            } else {
                linesSource.features = [];
                var src = map.getSource('bowire-lines');
                if (src) src.setData(linesSource);
            }
        }

        /**
         * Record that a pin landed on a track. The legend reads this, so
         * it holds the label and colour rather than re-deriving them: a
         * track whose pins have all been trimmed away still has a row
         * until the operator clears, and re-deriving a label from pins
         * that no longer exist is not possible.
         */
        function noteTrackSeen(track) {
            var meta = trackMeta.get(track.key);
            if (!meta) {
                trackMeta.set(track.key, {
                    label: track.label,
                    color: track.color,
                    trackId: track.trackId || '',
                    count: 1
                });
                renderLegend();
                return;
            }
            meta.count++;
            // Cheap enough to repaint one row's count every frame; the
            // legend is a handful of rows, not the pin collection.
            renderLegendCounts();
        }

        /**
         * Trim the pin collection back to the cap.
         *
         * #240 asks for "5000 / N tracks, so one chatty entity doesn't
         * FIFO-trim the others". The budget is right; the division is
         * not. A per-track quota of CAP/N shrinks every existing track's
         * allowance the moment an N+1th track appears, which
         * retroactively deletes history from tracks that did nothing —
         * the quiet ones lose the most, since they are the ones whose
         * history spans the longest wall-clock.
         *
         * The same protection without that side effect: keep the budget
         * global and take each pin from whichever track is currently
         * longest. A chatty entity is by definition the longest track,
         * so it pays for its own noise, and a track nobody is feeding is
         * never trimmed on someone else's behalf. With one track this is
         * exactly the FIFO the widget had before.
         */
        function trimToCap() {
            var CAP = 5000;
            if (pointsSource.features.length <= CAP) return;

            var counts = new Map();
            for (var i = 0; i < pointsSource.features.length; i++) {
                var k = (pointsSource.features[i].properties || {}).trackKey || '';
                counts.set(k, (counts.get(k) || 0) + 1);
            }

            var over = pointsSource.features.length - CAP;
            while (over-- > 0) {
                var worstKey = null;
                var worstCount = -1;
                counts.forEach(function (n, k) {
                    if (n > worstCount) { worstCount = n; worstKey = k; }
                });
                if (worstKey === null) break;
                // Features are in arrival order, so the first one on that
                // track is its oldest.
                for (var j = 0; j < pointsSource.features.length; j++) {
                    if (((pointsSource.features[j].properties || {}).trackKey || '') === worstKey) {
                        trackSourceByPin.delete(pointsSource.features[j].id);
                        pointsSource.features.splice(j, 1);
                        break;
                    }
                }
                counts.set(worstKey, worstCount - 1);
            }
        }

        /**
         * Push the pin collection to MapLibre, minus any track the
         * operator switched off.
         *
         * Hiding is a render concern, never a state one. The master
         * collection keeps every pin, so a hidden track still counts in
         * the legend, still holds its selection, and comes back exactly
         * as it was — rather than being deleted and having to be
         * re-streamed, which for a finished stream means never.
         *
         * With nothing hidden the master array is handed over as-is; the
         * filtered copy is only built when there is something to filter.
         */
        function renderPoints() {
            var src = map.getSource('bowire-points');
            if (!src) return;
            // Nothing hidden and no cursor: hand the master array over
            // as-is. The filtered copy is only built when there is
            // something to filter.
            if (hiddenTracks.size === 0 && cursorOrdinal === null) {
                src.setData(pointsSource);
                return;
            }
            src.setData({
                type: 'FeatureCollection',
                features: pointsSource.features.filter(function (f) {
                    return pinVisibleAt(f.properties || {});
                })
            });
        }

        /**
         * Re-key every pin already on the map after the operator changes
         * the track-id path.
         *
         * The alternative — apply the new path only to pins arriving
         * from now on — leaves the map showing two groupings at once
         * with no way to tell which pin follows which, and for a stream
         * that has already finished it means the setting does nothing at
         * all. That is the case where an operator most wants to change
         * it: the data is in, and the question is how to read it.
         *
         * The id is re-resolved through the coord kept per pin — see
         * trackSourceByPin — which is the same input arrival had, so the
         * ancestor climb behaves the same way here as it does live.
         */
        function reassignTracks() {
            trackMeta.clear();
            hiddenTracks.clear();
            for (var i = 0; i < pointsSource.features.length; i++) {
                var feature = pointsSource.features[i];
                var props = feature.properties || {};
                // Re-resolved through exactly the arrival path, coord and
                // all, so the climb behaves identically. A pin whose
                // coord is gone falls back to its stamped id and then to
                // the #238 key rather than dropping out of the legend.
                var track = bowireTrackFor(
                    props, trackSourceByPin.get(feature.id) || null);
                props.trackId = track.trackId || '';
                track.color = bowireMapDiscriminatorColor(track.key, paletteStore);
                props.trackKey = track.key;
                props.color = track.color;
                var meta = trackMeta.get(track.key);
                if (meta) meta.count++;
                else trackMeta.set(track.key, {
                    label: track.label, color: track.color,
                    trackId: track.trackId || '', count: 1
                });
            }
            renderPoints();
            rebuildTrajectories();
            renderLegend();
        }

        // "Show trajectory" toggle, mounted as a MapLibre control so it
        // inherits the same placement, stacking and teardown as the
        // NavigationControl already on the map. Sits top-left, opposite
        // the zoom buttons, so neither covers the other on a narrow
        // pane. IControl is duck-typed — an object with onAdd/onRemove
        // is all MapLibre asks for.
        var trajectoryCheckbox = null;
        var trajectoryToggleControl = {
            onAdd: function () {
                var wrap = document.createElement('div');
                wrap.className = 'maplibregl-ctrl maplibregl-ctrl-group bowire-map-trajectory-ctrl';
                // Inline styles for the same reason the rest of this
                // widget uses them: an extension renders against any
                // theme and must not depend on bowire.css being loaded.
                wrap.style.padding = '4px 8px';
                wrap.style.font = '12px system-ui, sans-serif';
                bowireMapThemePanel(wrap, overlay);

                var label = document.createElement('label');
                label.style.display = 'flex';
                label.style.alignItems = 'center';
                label.style.gap = '6px';
                label.style.cursor = 'pointer';
                label.style.whiteSpace = 'nowrap';
                label.style.userSelect = 'none';

                var box = document.createElement('input');
                box.type = 'checkbox';
                box.checked = showTrajectory;
                box.style.cursor = 'pointer';
                box.style.margin = '0';
                box.addEventListener('change', function () {
                    setTrajectoryEnabled(box.checked);
                });
                trajectoryCheckbox = box;
                // MapLibre installs its own handlers on the canvas
                // container; without this a click on the control also
                // reaches the map and can start a drag under the
                // cursor.
                wrap.addEventListener('mousedown', function (e) { e.stopPropagation(); });
                wrap.addEventListener('dblclick', function (e) { e.stopPropagation(); });

                var text = document.createElement('span');
                text.textContent = t('map.showTrajectory');

                label.appendChild(box);
                label.appendChild(text);
                wrap.appendChild(label);
                this._container = wrap;
                return wrap;
            },
            onRemove: function () {
                if (this._container && this._container.parentNode) {
                    this._container.parentNode.removeChild(this._container);
                }
                this._container = null;
                trajectoryCheckbox = null;
            }
        };
        map.addControl(trajectoryToggleControl, 'top-left');

        // -----------------------------------------------------------
        // #239 — time cursor and playback
        // -----------------------------------------------------------
        //
        // The map has always shown the live tail. Once a stream stops
        // there is no way back to minute 7, and "where was it when the
        // alert fired" needs an external tool. The cursor answers that
        // by deciding which frames count as "already arrived"; the pin
        // layer, the trajectory and the legend all read the same answer,
        // so nothing else here has to learn about time.
        //
        // The axis is FRAMES, not pins. A frame is one update — the unit
        // an operator thinks in — and one frame may carry thirteen pins.
        // Scrubbing by pin would step through a multi-entity snapshot
        // thirteen times and show two thirds of a moment.

        // One entry per frame that produced at least one pin:
        // { ordinal, time }. `time` is the frame's own timestamp where it
        // has one, else its ordinal — see frameTimeOf.
        var frameTimeline = [];

        // Which frame the map is showing. null means "the tail", i.e.
        // live: a new frame moves the picture forward on its own.
        var cursorOrdinal = null;

        // 'live' until something stops the stream or the operator pauses.
        var playbackState = 'live';
        var streamEnded = false;
        var playTimer = null;
        var playbackSpeed = Number(mapPrefs.get('playbackSpeed', 1)) || 1;

        /**
         * The frame's own notion of when it happened.
         *
         * Producers disagree about the field name, and plenty of streams
         * carry no time at all. The ordinal is the honest fallback: it
         * preserves arrival order, which is the only thing the scrubber
         * actually needs, and it keeps the control usable on a stream
         * that would otherwise have no axis to offer.
         */
        function frameTimeOf(frame, ordinal) {
            var candidates = [
                frame && frame.timestamp,
                frame && frame.capturedAt,
                frame && frame.time,
                frame && frame.receivedAt
            ];
            for (var i = 0; i < candidates.length; i++) {
                var raw = candidates[i];
                if (raw == null) continue;
                var value = typeof raw === 'number' ? raw : Date.parse(raw);
                if (isFinite(value)) return { value: value, real: true };
            }
            return { value: ordinal, real: false };
        }

        /** Is the map currently showing everything it has? */
        function cursorAtTail() {
            return cursorOrdinal === null
                || frameTimeline.length === 0
                || cursorOrdinal >= frameTimeline[frameTimeline.length - 1].ordinal;
        }

        /**
         * Should this pin be drawn?
         *
         * Hidden rather than dimmed. The ticket allows either, and the
         * point of the cursor is to show what the map WOULD have shown at
         * that moment — a dimmed future is still a future the operator
         * can see, which is the thing being asked about.
         */
        function pinVisibleAt(props) {
            if (hiddenTracks.has(props.trackKey || '')) return false;
            if (cursorOrdinal === null) return true;
            var ordinal = props.frameOrdinal;
            return ordinal == null || ordinal <= cursorOrdinal;
        }

        /**
         * Move the cursor and repaint.
         *
         * Selection is deliberately untouched. A frame selected while the
         * cursor sits before it stays selected — it simply is not drawn,
         * and comes back the moment the cursor passes it. Clearing the
         * selection on a scrub would make rewinding destructive, and the
         * operator scrubs precisely to look around a selected frame.
         */
        function setCursor(ordinal) {
            var last = frameTimeline.length > 0
                ? frameTimeline[frameTimeline.length - 1].ordinal
                : null;
            if (last === null) { cursorOrdinal = null; return; }
            cursorOrdinal = Math.max(0, Math.min(ordinal, last));
            renderPoints();
            rebuildTrajectories();
            renderGraphics();
            renderLegendCounts();
            renderPlaybackBar();
        }

        function stopPlayback() {
            if (playTimer !== null) { clearTimeout(playTimer); playTimer = null; }
        }

        /**
         * Advance one frame and schedule the next.
         *
         * The delay between two frames is their timestamp difference,
         * divided by the speed — so playback runs at the rate the data
         * actually arrived rather than at a fixed tick. On a stream with
         * no usable timestamps the axis is ordinals, whose difference is
         * 1, so a frame every 500 ms at 1x reads as a steady replay
         * instead of a burst.
         *
         * Clamped at both ends: a producer that stamped two frames the
         * same millisecond must not spin, and one that left a five-minute
         * gap must not look frozen.
         */
        function scheduleNextFrame() {
            stopPlayback();
            if (playbackState !== 'playing' || frameTimeline.length === 0) return;

            var idx = indexOfOrdinal(cursorOrdinal);
            if (idx < 0 || idx >= frameTimeline.length - 1) {
                // Reached the end: stop rather than loop. A replay that
                // silently restarts makes a long stream impossible to
                // read — the operator cannot tell the second pass from
                // the first.
                setPlaybackState('paused');
                return;
            }

            var current = frameTimeline[idx];
            var next = frameTimeline[idx + 1];
            var gap = next.real && current.real ? next.value - current.value : 500;
            var delay = Math.min(Math.max(gap / playbackSpeed, 16), 2000);

            playTimer = setTimeout(function () {
                if (disposed || playbackState !== 'playing') return;
                setCursor(next.ordinal);
                scheduleNextFrame();
            }, delay);
        }

        function indexOfOrdinal(ordinal) {
            if (ordinal === null) return frameTimeline.length - 1;
            for (var i = 0; i < frameTimeline.length; i++) {
                if (frameTimeline[i].ordinal >= ordinal) return i;
            }
            return frameTimeline.length - 1;
        }

        function setPlaybackState(next) {
            playbackState = next;
            if (next === 'playing') {
                // Playing from the tail means replaying, so rewind first —
                // otherwise Play looks broken: it is already at the end
                // and there is nothing to advance to.
                if (cursorAtTail() && frameTimeline.length > 1) {
                    setCursor(frameTimeline[0].ordinal);
                }
                scheduleNextFrame();
            } else {
                stopPlayback();
                if (next === 'live') {
                    cursorOrdinal = null;
                    renderPoints();
                    rebuildTrajectories();
                    renderLegendCounts();
                }
            }
            renderPlaybackBar();
        }

        /**
         * Record a frame on the timeline.
         *
         * Called from addPin AFTER the pins are in, so a frame that
         * resolved no coordinates never reaches the axis — scrubbing onto
         * it would look like a stall.
         */
        function noteFrameOnTimeline(frame, ordinal) {
            var ts = frameTimeOf(frame, ordinal);
            frameTimeline.push({ ordinal: ordinal, value: ts.value, real: ts.real });

            // The timeline is trimmed with the pins: an ordinal whose pins
            // the cap has dropped is a position the scrubber cannot show.
            var oldest = pointsSource.features.length > 0
                ? (pointsSource.features[0].properties || {}).frameOrdinal
                : null;
            if (oldest != null) {
                var cut = 0;
                while (cut < frameTimeline.length && frameTimeline[cut].ordinal < oldest) cut++;
                if (cut > 0) frameTimeline.splice(0, cut);
            }
            renderPlaybackBar();
        }

        // Bottom strip: scrubber, transport buttons, speed.
        //
        // Built directly into the widget container rather than as a
        // MapLibre control: controls dock into a corner and size to their
        // content, and this needs the full width. It sits above the
        // canvas and below nothing, so the map keeps its own gestures
        // everywhere the bar is not.
        var playbackBar = null;
        var playbackParts = null;

        var BOWIRE_PLAYBACK_SPEEDS = [0.5, 1, 2, 4, 10];

        function buildPlaybackBar() {
            var bar = document.createElement('div');
            bar.className = 'bowire-map-playback';
            Object.assign(bar.style, {
                position: 'absolute', left: '0', right: '0', bottom: '0',
                display: 'none', alignItems: 'center', gap: '8px',
                padding: '6px 10px',
                font: '12px system-ui, sans-serif',
                zIndex: '5'
            });
            bowireMapThemePanel(bar, overlay);
            // The map's own drag/zoom handlers live on the canvas
            // container; without this a drag that starts on the scrubber
            // also pans the map underneath it.
            bar.addEventListener('mousedown', function (e) { e.stopPropagation(); });
            bar.addEventListener('dblclick', function (e) { e.stopPropagation(); });
            bar.addEventListener('wheel', function (e) { e.stopPropagation(); });

            function button(label, title) {
                var b = document.createElement('button');
                b.type = 'button';
                b.textContent = label;
                b.title = title;
                Object.assign(b.style, {
                    font: 'inherit', cursor: 'pointer', minWidth: '28px',
                    padding: '2px 6px', borderRadius: '3px',
                    border: '1px solid ' + overlay.controlBorder,
                    background: overlay.controlBg, color: 'inherit'
                });
                return b;
            }

            var playBtn = button('▶', t('map.play'));
            playBtn.addEventListener('click', function () {
                setPlaybackState(playbackState === 'playing' ? 'paused' : 'playing');
            });

            var stepBack = button('⏮', t('map.stepBack'));
            stepBack.addEventListener('click', function () {
                setPlaybackState('paused');
                var idx = indexOfOrdinal(cursorOrdinal);
                if (idx > 0) setCursor(frameTimeline[idx - 1].ordinal);
            });

            var stepFwd = button('⏭', t('map.stepForward'));
            stepFwd.addEventListener('click', function () {
                setPlaybackState('paused');
                var idx = indexOfOrdinal(cursorOrdinal);
                if (idx < frameTimeline.length - 1) setCursor(frameTimeline[idx + 1].ordinal);
            });

            var liveBtn = button('Live', t('map.followStream'));  // i18n-exempt: the protocol's own word for the live tail
            liveBtn.style.minWidth = '46px';
            liveBtn.addEventListener('click', function () { setPlaybackState('live'); });

            var range = document.createElement('input');
            range.type = 'range';
            range.min = '0';
            range.step = '1';
            range.style.flex = '1 1 auto';
            range.style.cursor = 'pointer';
            // `input`, not `change`: the ticket asks for the picture to
            // follow the handle as it moves, not to jump when released.
            range.addEventListener('input', function () {
                setPlaybackState('paused');
                var idx = Math.max(0, Math.min(Number(range.value), frameTimeline.length - 1));
                if (frameTimeline[idx]) setCursor(frameTimeline[idx].ordinal);
            });

            var speed = document.createElement('select');
            speed.style.font = 'inherit';
            speed.title = t('map.playbackSpeed');
            for (var i = 0; i < BOWIRE_PLAYBACK_SPEEDS.length; i++) {
                var opt = document.createElement('option');
                opt.value = String(BOWIRE_PLAYBACK_SPEEDS[i]);
                opt.textContent = BOWIRE_PLAYBACK_SPEEDS[i] + '×';
                if (BOWIRE_PLAYBACK_SPEEDS[i] === playbackSpeed) opt.selected = true;
                speed.appendChild(opt);
            }
            speed.addEventListener('change', function () {
                playbackSpeed = Number(speed.value) || 1;
                // Speed is a property of the operator, not of the data, so
                // it is the one piece of playback state that persists —
                // the cursor position deliberately does not.
                mapPrefs.set('playbackSpeed', playbackSpeed);
                if (playbackState === 'playing') scheduleNextFrame();
            });

            var readout = document.createElement('span');
            readout.style.opacity = '0.75';
            readout.style.fontVariantNumeric = 'tabular-nums';
            readout.style.whiteSpace = 'nowrap';
            readout.style.minWidth = '92px';
            readout.style.textAlign = 'right';

            bar.appendChild(stepBack);
            bar.appendChild(playBtn);
            bar.appendChild(stepFwd);
            bar.appendChild(range);
            bar.appendChild(readout);
            bar.appendChild(speed);
            bar.appendChild(liveBtn);
            container.appendChild(bar);

            playbackBar = bar;
            playbackParts = {
                play: playBtn, stepBack: stepBack, stepFwd: stepFwd,
                live: liveBtn, range: range, speed: speed, readout: readout
            };
        }

        /**
         * Reflect the current state on the bar.
         *
         * The bar hides itself until there is something to scrub, and its
         * transport disables while the stream is live: a cursor that
         * fights an arriving frame would flicker between the operator's
         * position and the tail, and #239 pins the cursor to "now" for
         * exactly that reason. Pause is the way out, so Play stays live.
         */
        function renderPlaybackBar() {
            if (!playbackBar || !playbackParts) return;
            var count = frameTimeline.length;
            playbackBar.style.display = count > 1 ? 'flex' : 'none';
            if (count === 0) return;

            var idx = indexOfOrdinal(cursorOrdinal);
            var p = playbackParts;
            p.range.max = String(count - 1);
            p.range.value = String(idx);
            p.range.disabled = playbackState === 'live';
            p.stepBack.disabled = playbackState === 'live' || idx <= 0;
            p.stepFwd.disabled = playbackState === 'live' || idx >= count - 1;
            p.live.disabled = playbackState === 'live';
            p.play.textContent = playbackState === 'playing' ? '❚❚' : '▶';
            p.play.title = playbackState === 'playing' ? t('map.pause') : t('map.play');

            p.readout.textContent = playbackState === 'live'
                ? t('map.liveCount', { count: count })
                : (idx + 1) + ' / ' + count;
        }

        buildPlaybackBar();

        // -----------------------------------------------------------
        // #240 — track legend and track-id path control
        // -----------------------------------------------------------
        //
        // Both live in one MapLibre control below the trajectory toggle:
        // the path decides what the tracks ARE and the legend lists what
        // came out, so splitting them across two corners would make the
        // operator connect a cause on one side of the canvas to its
        // effect on the other.

        var legendBody = null;      // rows container, rebuilt on track changes
        var legendRows = new Map(); // key -> { count, row } for cheap count updates
        var legendCollapsed = mapPrefs.get('legendCollapsed', false) === true;

        /**
         * Candidate track-id fields, derived from a frame we have
         * actually seen rather than from the schema.
         *
         * #240 asks for candidates "detected from the schema (REST/gRPC
         * inputType)". The schema is the wrong source twice over: for a
         * response stream the input type describes the request, and a
         * schema-derived list includes fields the producer never
         * populates while missing whatever a `google.protobuf.Struct` or
         * a free-form JSON body actually carried. The frames on the wire
         * answer the question the operator is asking — "which field
         * distinguishes these entities" — exactly.
         *
         * Paths are offered RELATIVE to a coordinate's parent where one
         * exists, because that is the form that resolves per entity in a
         * multi-pairing frame. Scalars are kept, objects and arrays
         * dropped: a track id has to be something the legend can print.
         */
        var trackCandidates = [];
        function collectTrackCandidates(frame, coords) {
            if (trackCandidates.length > 0 || !coords || coords.length === 0) return;
            var coord = coords[0];
            if (coord.root == null) return;

            // Walk the same ancestor chain resolveTrackId walks, nearest
            // first. Offering only the coordinate's immediate parent
            // would list `latitude` and `longitude` and nothing else:
            // that node HOLDS the position, and the field that names the
            // entity lives further up — one level for a flat
            // `position: {…}`, five for TacticalAPI's
            // symbol.location.content.point.geoPoint.
            //
            // Because resolution climbs too, a bare field name is enough
            // however deep the match turns out to be, so the offered
            // paths stay short and stay readable in the dropdown.
            var found = [];
            var seen = Object.create(null);
            var latLeaf = stripRoot(coord.latPath);
            var lonLeaf = stripRoot(coord.lonPath);

            function harvest(node) {
                if (node == null || typeof node !== 'object' || Array.isArray(node)) return;
                for (var k in node) {
                    if (!Object.prototype.hasOwnProperty.call(node, k)) continue;
                    var v = node[k];
                    if (v == null || typeof v === 'object') continue;
                    if (seen[k]) continue;
                    // A coordinate identifies a position, never an
                    // entity: grouping on one yields a track per ping.
                    if (latLeaf.endsWith('.' + k) || lonLeaf.endsWith('.' + k)) continue;
                    seen[k] = true;
                    found.push(k);
                }
            }

            var path = (coord.parentPath && coord.parentPath !== '$') ? coord.parentPath : '$';
            var safety = 16;
            while (safety-- > 0) {
                var node = path === '$'
                    ? coord.root
                    : bowireResolveJsonPath(coord.root, path);
                harvest(node);
                if (path === '$') break;
                var next = bowireDropLastSegment(path);
                path = (next === path || next === '') ? '$' : next;
            }

            trackCandidates = found.slice(0, 40);
            renderTrackPathOptions();
        }

        var trackPathSelect = null;
        function renderTrackPathOptions() {
            if (!trackPathSelect) return;
            var current = trackIdPath;
            trackPathSelect.textContent = '';
            var none = document.createElement('option');
            none.value = '';
            none.textContent = t('map.groupByMessageType');
            trackPathSelect.appendChild(none);
            var seen = false;
            for (var i = 0; i < trackCandidates.length; i++) {
                var opt = document.createElement('option');
                opt.value = trackCandidates[i];
                opt.textContent = trackCandidates[i];
                if (trackCandidates[i] === current) { opt.selected = true; seen = true; }
                trackPathSelect.appendChild(opt);
            }
            // A path typed by hand, or one carried over from a previous
            // session, is not necessarily in the observed set. Keeping it
            // as its own option means selecting something else and
            // changing your mind does not silently lose it.
            if (current && !seen) {
                var custom = document.createElement('option');
                custom.value = current;
                custom.textContent = t('map.trackPathCustom', { path: current });
                custom.selected = true;
                trackPathSelect.appendChild(custom);
            }
        }

        function setTrackIdPath(path) {
            var next = String(path || '').trim();
            if (next === trackIdPath) return;
            trackIdPath = next;
            mapMethodPrefs.set('trackIdPath', trackIdPath);
            reassignTracks();
            renderTrackPathOptions();
        }

        function toggleTrackHidden(key, hidden) {
            if (hidden) hiddenTracks.add(key);
            else hiddenTracks.delete(key);
            renderPoints();
            rebuildTrajectories();
        }

        /**
         * How many pins each track currently has ON THE MAP.
         *
         * With no cursor that is the running total the track kept as it
         * arrived — cheap, and the hot path while a stream is live. With
         * a cursor it is not: the legend describes the picture, and a row
         * reading 46 next to sixteen visible pins is the legend
         * disagreeing with the map it belongs to.
         *
         * The recount is one pass over the pins, and it runs on cursor
         * moves rather than on frames, so the live path stays untouched.
         */
        function visibleTrackCounts() {
            if (cursorOrdinal === null) return null;
            var counts = new Map();
            for (var i = 0; i < pointsSource.features.length; i++) {
                var props = pointsSource.features[i].properties || {};
                if (props.frameOrdinal != null && props.frameOrdinal > cursorOrdinal) continue;
                var key = props.trackKey || '';
                counts.set(key, (counts.get(key) || 0) + 1);
            }
            return counts;
        }

        /** Repaint only the counts — the hot path while a stream runs. */
        function renderLegendCounts() {
            var visible = visibleTrackCounts();
            trackMeta.forEach(function (meta, key) {
                var row = legendRows.get(key);
                if (!row || !row.count) return;
                row.count.textContent = String(
                    visible === null ? meta.count : (visible.get(key) || 0));
            });
        }

        function renderLegend() {
            if (!legendBody) return;
            legendBody.textContent = '';
            legendRows.clear();
            var visibleCounts = visibleTrackCounts();
            if (trackMeta.size === 0) {
                var empty = document.createElement('div');
                empty.textContent = t('map.noTracks');
                empty.style.opacity = '0.6';
                empty.style.padding = '2px 0';
                legendBody.appendChild(empty);
                return;
            }
            trackMeta.forEach(function (meta, key) {
                var row = document.createElement('label');
                row.style.display = 'flex';
                row.style.alignItems = 'center';
                row.style.gap = '6px';
                row.style.padding = '2px 0';
                row.style.cursor = 'pointer';
                row.style.userSelect = 'none';

                var box = document.createElement('input');
                box.type = 'checkbox';
                box.checked = !hiddenTracks.has(key);
                box.style.margin = '0';
                box.style.cursor = 'pointer';
                box.addEventListener('change', function () {
                    toggleTrackHidden(key, !box.checked);
                });

                var swatch = document.createElement('span');
                swatch.style.width = '10px';
                swatch.style.height = '10px';
                swatch.style.borderRadius = '2px';
                swatch.style.flex = '0 0 auto';
                swatch.style.background = meta.color || '#888';

                var name = document.createElement('span');
                name.textContent = meta.label;
                name.title = meta.label;
                name.style.overflow = 'hidden';
                name.style.textOverflow = 'ellipsis';
                name.style.whiteSpace = 'nowrap';
                name.style.maxWidth = '150px';

                // Frame count as a meta chip between the name and the
                // edge — the house pattern for a per-row count.
                var count = document.createElement('span');
                count.textContent = String(
                    visibleCounts === null ? meta.count : (visibleCounts.get(key) || 0));
                count.style.marginLeft = 'auto';
                count.style.opacity = '0.65';
                count.style.fontVariantNumeric = 'tabular-nums';

                row.appendChild(box);
                row.appendChild(swatch);
                row.appendChild(name);
                row.appendChild(count);
                legendBody.appendChild(row);
                legendRows.set(key, { count: count, row: row });
            });
        }

        var trackControl = {
            onAdd: function () {
                var wrap = document.createElement('div');
                wrap.className = 'maplibregl-ctrl maplibregl-ctrl-group bowire-map-track-ctrl';
                wrap.style.padding = '6px 8px';
                wrap.style.font = '12px system-ui, sans-serif';
                wrap.style.minWidth = '210px';
                wrap.style.maxWidth = '260px';
                bowireMapThemePanel(wrap, overlay);

                var head = document.createElement('div');
                head.style.display = 'flex';
                head.style.alignItems = 'center';
                head.style.gap = '6px';
                head.style.cursor = 'pointer';
                head.style.userSelect = 'none';

                var caret = document.createElement('span');
                caret.textContent = legendCollapsed ? '▸' : '▾';
                caret.style.opacity = '0.7';

                var title = document.createElement('strong');
                title.textContent = t('map.tracks');
                title.style.fontWeight = '600';

                head.appendChild(caret);
                head.appendChild(title);

                var body = document.createElement('div');
                body.hidden = legendCollapsed;
                body.style.marginTop = '6px';

                head.addEventListener('click', function () {
                    legendCollapsed = !legendCollapsed;
                    body.hidden = legendCollapsed;
                    caret.textContent = legendCollapsed ? '▸' : '▾';
                    mapPrefs.set('legendCollapsed', legendCollapsed);
                });

                var pathLabel = document.createElement('div');
                pathLabel.textContent = t('map.groupBy');
                pathLabel.style.opacity = '0.7';
                pathLabel.style.marginBottom = '2px';

                trackPathSelect = document.createElement('select');
                trackPathSelect.style.width = '100%';
                trackPathSelect.style.font = 'inherit';
                bowireMapThemeField(trackPathSelect, overlay);
                trackPathSelect.addEventListener('change', function () {
                    setTrackIdPath(trackPathSelect.value);
                });

                var custom = document.createElement('input');
                custom.type = 'text';
                custom.placeholder = 'or a path, e.g. entity.id';  // i18n-exempt: an example path a user replaces
                custom.value = trackIdPath;
                custom.style.width = '100%';
                custom.style.font = 'inherit';
                custom.style.marginTop = '4px';
                custom.style.boxSizing = 'border-box';
                bowireMapThemeField(custom, overlay);
                // Committed on Enter or blur, not per keystroke: every
                // change re-keys every pin on the map, and doing that
                // for each character of a half-typed path would regroup
                // the display against paths the operator never meant.
                custom.addEventListener('keydown', function (e) {
                    if (e.key === 'Enter') { e.preventDefault(); setTrackIdPath(custom.value); }
                });
                custom.addEventListener('blur', function () { setTrackIdPath(custom.value); });

                legendBody = document.createElement('div');
                legendBody.style.marginTop = '6px';
                legendBody.style.maxHeight = '160px';
                legendBody.style.overflowY = 'auto';

                body.appendChild(pathLabel);
                body.appendChild(trackPathSelect);
                body.appendChild(custom);
                body.appendChild(legendBody);

                wrap.appendChild(head);
                wrap.appendChild(body);

                // Same reason as the trajectory toggle: MapLibre's canvas
                // handlers would otherwise start a drag under the cursor.
                wrap.addEventListener('mousedown', function (e) { e.stopPropagation(); });
                wrap.addEventListener('dblclick', function (e) { e.stopPropagation(); });

                renderTrackPathOptions();
                renderLegend();
                this._container = wrap;
                return wrap;
            },
            onRemove: function () {
                if (this._container && this._container.parentNode) {
                    this._container.parentNode.removeChild(this._container);
                }
                this._container = null;
                legendBody = null;
                trackPathSelect = null;
            }
        };
        map.addControl(trackControl, 'top-left');



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
            renderPoints();
            rebuildTrajectories();
            renderGraphics();
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
            } finally {
                // #239 — the stream is done, so the scrubber can take
                // over. `finally`, not the happy path: a stream that ends
                // by throwing is exactly when someone wants to rewind and
                // look at what happened just before.
                streamEnded = true;
                if (!disposed && playbackState === 'live') setPlaybackState('paused');
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

        // -----------------------------------------------------------
        // JSON ↔ map hover-sync API
        // -----------------------------------------------------------
        //
        // The widget publishes a small remote-control surface so the
        // response-JSON viewer can:
        //   - flyTo(lon, lat)              — context menu's "Center
        //                                    on map" action
        //   - highlightByPath(path)        — mouseenter on a coord
        //                                    span in the JSON
        //   - clearHighlight()             — mouseleave
        //
        // Reverse direction (map → JSON) is handled here too: pin
        // mouseenter dispatches `bowire:map-coord-hover` carrying
        // the pin's parentPath; the JSON viewer listens for it and
        // tints the matching coord block.
        //
        // Registry pattern: every mount pushes its handle into
        // `window.__bowireMapWidgets`, unmount removes it. JSON
        // handlers iterate the registry rather than holding a
        // closure reference — works cleanly across morphdom diff
        // passes that drop / re-create the widget pane.

        /**
         * Re-flag every feature's `highlighted` property and push the
         * collection back to the source in a single `setData(...)`.
         * Cheap because MapLibre's data-driven `case` expressions on
         * the paint properties resolve the new value without a
         * layer rebuild. Same pattern as `applySelectionRestyle`.
         */
        function applyHighlightRestyle() {
            var src = map.getSource('bowire-points');
            if (src) src.setData(pointsSource);
        }

        /**
         * Mark every feature whose JSON path (lat / lon / parent)
         * matches `path` as highlighted. Path may be a JSONPath like
         * `$.position.lat` or the chain-variable form `position.lat`;
         * both are accepted so the JSON viewer can hand over whatever
         * shape its data-attribute carries without the caller having
         * to normalise.
         */
        function highlightByPath(path) {
            if (!path) { clearHighlight(); return; }
            var normalised = path;
            // Normalise both sides to the same shape — strip the
            // leading `$.` so a chain-variable `position.lat` matches
            // a JSONPath `$.position.lat` and vice versa.
            if (normalised.indexOf('$.') === 0) normalised = normalised.substring(2);
            else if (normalised === '$') normalised = '';
            var any = false;
            for (var i = 0; i < pointsSource.features.length; i++) {
                var props = pointsSource.features[i].properties || {};
                var matches = pathsMatch(props.latPath, normalised)
                    || pathsMatch(props.lonPath, normalised)
                    || pathsMatch(props.parentPath, normalised);
                props.highlighted = matches ? 'yes' : 'no';
                if (matches) any = true;
            }
            // A graphic lights up for any of its vertices, and for the
            // geometry itself: hovering `…polygon` or `…polygon.points`
            // in the JSON viewer is hovering the area.
            var graphicsDirty = false;
            graphics.forEach(function (versions) {
                var version = graphicAtCursor(versions);
                if (!version) return;
                var g = version.graphic;
                var hit = pathsMatch(g.geomPath, normalised) || pathUnder(normalised, g.geomPath);
                for (var v = 0; !hit && v < g.vertexPaths.length; v++) {
                    hit = pathsMatch(g.vertexPaths[v], normalised) || pathUnder(normalised, g.vertexPaths[v]);
                }
                if (!!g.highlighted !== hit) { g.highlighted = hit; graphicsDirty = true; }
                if (hit) any = true;
            });
            applyHighlightRestyle();
            if (graphicsDirty) renderGraphics();
            return any;
        }

        /** Is `path` inside `root` — a descendant, not the root itself? */
        function pathUnder(path, root) {
            if (!path || !root) return false;
            var nr = root.indexOf('$.') === 0 ? root.substring(2) : (root === '$' ? '' : root);
            if (!nr) return false;
            return path.indexOf(nr + '.') === 0 || path.indexOf(nr + '[') === 0;
        }

        /**
         * Clear every highlight flag. Called on mouseleave so the
         * halo goes dark when the operator moves the cursor off
         * the JSON viewer entirely.
         */
        function clearHighlight() {
            var dirty = false;
            for (var i = 0; i < pointsSource.features.length; i++) {
                var props = pointsSource.features[i].properties || {};
                if (props.highlighted === 'yes') {
                    props.highlighted = 'no';
                    dirty = true;
                }
            }
            if (dirty) applyHighlightRestyle();
            var graphicsDirty = false;
            graphics.forEach(function (versions) {
                versions.forEach(function (version) {
                    if (version.graphic.highlighted) { version.graphic.highlighted = false; graphicsDirty = true; }
                });
            });
            if (graphicsDirty) renderGraphics();
        }

        /**
         * Tolerant path equality — accepts both the JSONPath
         * (`$.foo.bar`) and chain-variable (`foo.bar`) conventions
         * on either side. Both register-time and viewer-time paths
         * get normalised down by stripping the leading `$.` before
         * comparison.
         */
        function pathsMatch(a, b) {
            if (!a || !b) return false;
            var na = a.indexOf('$.') === 0 ? a.substring(2) : (a === '$' ? '' : a);
            var nb = b.indexOf('$.') === 0 ? b.substring(2) : (b === '$' ? '' : b);
            return na === nb;
        }

        /**
         * Camera move. Called by the JSON-viewer's "Center on map"
         * context-menu entry. Same `flyTo` MapLibre exposes, with a
         * safe default zoom when the caller doesn't supply one.
         */
        function flyTo(opts) {
            if (!opts || !opts.center) return;
            // Mark the move as programmatic so the
            // dragstart/zoomstart heuristics don't latch
            // `userMovedCamera` and disable auto-fit.
            try {
                map.flyTo({
                    center: opts.center,
                    zoom: typeof opts.zoom === 'number' ? opts.zoom : 12,
                    duration: typeof opts.duration === 'number' ? opts.duration : 600
                });
            } catch {}
        }

        // Map → JSON direction: pin mouseenter / mouseleave dispatches
        // a `bowire:map-coord-hover` document event carrying the
        // parentPath; the JSON viewer listens and tints the matching
        // coord block. Listener installed on the canvas so the
        // workbench doesn't need to wire each pin individually.
        map.on('mouseenter', 'bowire-points-layer', function (e) {
            map.getCanvas().style.cursor = 'pointer';
            if (!e.features || e.features.length === 0) return;
            var props = e.features[0].properties || {};
            try {
                document.dispatchEvent(new CustomEvent('bowire:map-coord-hover', {
                    detail: {
                        parentPath: props.parentPath || '',
                        latPath: props.latPath || '',
                        lonPath: props.lonPath || ''
                    }
                }));
            } catch {}
        });
        map.on('mouseleave', 'bowire-points-layer', function () {
            map.getCanvas().style.cursor = '';
            try {
                document.dispatchEvent(new CustomEvent('bowire:map-coord-hover', {
                    detail: { parentPath: '', latPath: '', lonPath: '' }
                }));
            } catch {}
        });

        // Pin click → dispatch `bowire:map-coord-click`, the core
        // workbench listens and scrolls the JSON viewer to the
        // matching row (auto-expanding any collapsed ancestors).
        // We don't scroll from here directly because the JSON viewer
        // implementation lives in core — keeping the navigation
        // logic on that side means future extensions (image / chart
        // / audio) can reuse the same listener without duplicating
        // the auto-expand walk per bundle.
        map.on('click', 'bowire-points-layer', function (e) {
            if (!e || !e.features || e.features.length === 0) return;
            var props = e.features[0].properties || {};
            // Suppress the map's own click handler — without this
            // the underlying `map.on('click', ...)` (editor mode)
            // and any future "click empty to pan" affordances
            // would fire alongside the pin tap.
            try {
                if (e.originalEvent) {
                    e.originalEvent.stopPropagation();
                    e.originalEvent.preventDefault();
                }
            } catch {}
            try {
                document.dispatchEvent(new CustomEvent('bowire:map-coord-click', {
                    detail: {
                        parentPath: props.parentPath || '',
                        latPath: props.latPath || '',
                        lonPath: props.lonPath || ''
                    }
                }));
            } catch {}
        });

        // A graphic answers a hover and a click the way a pin does: the
        // JSON viewer tints, then scrolls to, its first vertex. The fill
        // layer is included so an area can be hit inside its outline,
        // not only on it.
        ['bowire-graphics-lines-layer', 'bowire-graphics-dashed-layer', 'bowire-graphics-fill-layer']
            .forEach(function (layerId) {
                map.on('mouseenter', layerId, function (e) {
                    map.getCanvas().style.cursor = 'pointer';
                    if (!e.features || e.features.length === 0) return;
                    var props = e.features[0].properties || {};
                    try {
                        document.dispatchEvent(new CustomEvent('bowire:map-coord-hover', {
                            detail: {
                                parentPath: props.parentPath || '',
                                latPath: props.latPath || '',
                                lonPath: props.lonPath || ''
                            }
                        }));
                    } catch {}
                });
                map.on('mouseleave', layerId, function () {
                    map.getCanvas().style.cursor = '';
                    try {
                        document.dispatchEvent(new CustomEvent('bowire:map-coord-hover', {
                            detail: { parentPath: '', latPath: '', lonPath: '' }
                        }));
                    } catch {}
                });
                map.on('click', layerId, function (e) {
                    if (!e || !e.features || e.features.length === 0) return;
                    var props = e.features[0].properties || {};
                    if (!props.parentPath && !props.latPath) return;
                    try {
                        if (e.originalEvent) {
                            e.originalEvent.stopPropagation();
                            e.originalEvent.preventDefault();
                        }
                    } catch {}
                    try {
                        document.dispatchEvent(new CustomEvent('bowire:map-coord-click', {
                            detail: {
                                parentPath: props.parentPath || '',
                                latPath: props.latPath || '',
                                lonPath: props.lonPath || ''
                            }
                        }));
                    } catch {}
                });
            });

        // Pin double-click → copy the parent path to the clipboard.
        // Same gesture the JSON viewer's dblclick gives the operator
        // ("Copy path"), now reachable from the map side too. We
        // suppress MapLibre's default doubleclick-to-zoom on the pin
        // (not globally — empty-map double-click still zooms). The
        // user's feedback explicitly preferred copy over zoom here.
        map.on('dblclick', 'bowire-points-layer', function (e) {
            if (!e || !e.features || e.features.length === 0) return;
            var props = e.features[0].properties || {};
            try {
                if (e.originalEvent) {
                    e.originalEvent.stopPropagation();
                    e.originalEvent.preventDefault();
                }
            } catch {}
            // Prefer the parent (e.g. `coordinate`) — same shape the
            // JSON viewer's dblclick copies. Falls back to the lat
            // path when parent is empty (single-coord at root).
            var raw = props.parentPath || props.latPath || '';
            if (!raw) return;
            var chain = bowireMapJsonPathToChainPath(raw);
            if (!chain) return;
            try {
                navigator.clipboard.writeText(chain).then(
                    function () {
                        var toast = (window.bowireToast || window.toast);
                        if (typeof toast === 'function') {
                            toast(t('rb.response.copiedPath', { path: chain }), 'success');
                        }
                    },
                    function () {
                        var toast = (window.bowireToast || window.toast);
                        if (typeof toast === 'function') {
                            toast(t('clipboard.failed'), 'error');
                        }
                    }
                );
            } catch {}
        });

        // Publish the widget handle into the workbench-global
        // registry. The JSON viewer iterates this list to dispatch
        // hover / flyTo calls — works cleanly across morphdom
        // re-renders because the JSON side never holds a stale
        // closure reference; it just reads window.__bowireMapWidgets
        // at event time.
        var registry = (window.__bowireMapWidgets = window.__bowireMapWidgets || []);
        var handle = {
            container: container,
            flyTo: flyTo,
            highlightByPath: highlightByPath,
            clearHighlight: clearHighlight,
            // #238 — the trajectory toggle, on the same surface as the
            // hover-sync. The JSON viewer does not drive it today; a
            // browser check does, and it is the only way to ask a
            // mounted widget what geometry it actually handed MapLibre
            // without reaching into the source's private state.
            setTrajectory: setTrajectoryEnabled,
            isTrajectoryEnabled: function () { return showTrajectory; },
            trajectoryGeoJson: function () { return linesSource; },
            setTrackIdPath: setTrackIdPath,
            trackIdPath: function () { return trackIdPath; },
            // Which SIDCs this map has drawn with milsymbol, and which
            // it could not — the only way to ask a mounted widget
            // without reaching into MapLibre's image manager.
            symbolIcons: function () {
                var out = {};
                sidcIcons.forEach(function (state, sidc) { out[sidc] = state; });
                return out;
            },
            // The tactical graphics: which library state they are drawn
            // in, and per graphic whether the renderer took it or the
            // bare geometry stands in.
            graphics: function () {
                var out = { library: milSymTs ? 'loaded' : (milSymTsFailed ? 'failed' : 'loading'), items: [] };
                graphics.forEach(function (versions) {
                    var version = graphicAtCursor(versions);
                    if (!version) return;
                    var g = version.graphic;
                    out.items.push({
                        key: g.key, kind: g.kind, sidc: g.sidc, renderSidc: g.renderSidc || null,
                        points: g.points.length,
                        designation: g.designation, versions: versions.length,
                        highlighted: !!g.highlighted,
                        drawn: !!milSymTs && !!g.renderSidc
                            && graphicErrors.get(g.key) !== (g.points.length + '|' + g.renderSidc)
                    });
                });
                return out;
            },
            trackCandidates: function () { return trackCandidates.slice(); },
            tracks: function () {
                var out = [];
                trackMeta.forEach(function (meta, key) {
                    out.push({
                        key: key, label: meta.label, color: meta.color,
                        trackId: meta.trackId, count: meta.count,
                        hidden: hiddenTracks.has(key)
                    });
                });
                return out;
            },
            setTrackHidden: toggleTrackHidden,
            // #239 — the time cursor. Same reasoning as the trajectory
            // surface: this is the only way to ask a mounted widget what
            // moment it is showing without reaching into MapLibre.
            setPlaybackState: setPlaybackState,
            setCursorIndex: function (index) {
                if (frameTimeline.length === 0) return;
                var i = Math.max(0, Math.min(index, frameTimeline.length - 1));
                setCursor(frameTimeline[i].ordinal);
            },
            visibleTrackCounts: function () {
                var visible = visibleTrackCounts();
                var out = {};
                trackMeta.forEach(function (meta, key) {
                    out[key] = visible === null ? meta.count : (visible.get(key) || 0);
                });
                return out;
            },
            playback: function () {
                return {
                    state: playbackState,
                    streamEnded: streamEnded,
                    frames: frameTimeline.length,
                    index: frameTimeline.length === 0 ? -1 : indexOfOrdinal(cursorOrdinal),
                    atTail: cursorAtTail(),
                    speed: playbackSpeed
                };
            }
        };
        registry.push(handle);

        return function unmount() {
            disposed = true;
            stopPlayback();
            var idx = registry.indexOf(handle);
            if (idx >= 0) registry.splice(idx, 1);
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
            container.textContent = t('map.editorUnavailable');
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
    // Response-tree integration — Phase 4.1
    //
    // The core workbench owns the JSON-tree DOM but doesn't import
    // any coordinate-specific knowledge. The map widget plugs in
    // through three extension hooks:
    //
    //   1) registerResponseTreeDecorator — stamps
    //      data-bowire-coord-path on lat / lon / parent spans the
    //      auto-detector marked, and binds mouseover/mouseout for
    //      the JSON → map hover sync.
    //   2) registerResponseTreeMenuContributor — returns
    //      [{label: 'Center on map', action, meta}] when the
    //      right-clicked span belongs to a resolved coord pair.
    //   3) Document listener for `bowire:map-coord-hover` — the
    //      map → JSON reverse direction. Pin mouseenter dispatches
    //      the event; the listener installed below tints the
    //      matching JSON-tree spans.
    //
    // All three live inside this bundle so the
    // Kuestenlogik.Bowire.Map → Kuestenlogik.Bowire dependency
    // direction stays one-way. Core ships zero coordinate-specific
    // strings.
    // ---------------------------------------------------------------

    /**
     * Strip the leading `$.` off a JSONPath so it matches the
     * chain-variable form (`position.lat`) the response JSON tree's
     * `data-json-path` attributes use. `$` alone normalises to the
     * empty string (root scope).
     */
    function bowireMapNormalisePath(p) {
        if (typeof p !== 'string') return '';
        if (p.indexOf('$.') === 0) return p.substring(2);
        if (p === '$') return '';
        return p;
    }

    /**
     * Convert a JSONPath-with-brackets (`$.foo[0].bar`) into the
     * dot-only chain form (`foo.0.bar`) the workbench's JSON viewer
     * uses for its `data-line-path` / `data-json-path` attributes.
     *
     * Without this conversion the reverse-hover selector
     * `[data-line-path="situationObjects[0].point.geoPoint"]` missed
     * actual viewer rows (which carry `situationObjects.0.point.geoPoint`)
     * and the cursor changed on pin-enter but the JSON tint never
     * fired. Same shape the core `bowireJsonPathToChainPath` helper
     * does — duplicated here because the map bundle loads as a
     * separate IIFE outside the core's closure, and pulling another
     * dependency for a 5-line regex didn't seem worth it.
     */
    function bowireMapJsonPathToChainPath(p) {
        if (typeof p !== 'string' || !p) return '';
        var s = p;
        if (s.indexOf('$.') === 0) s = s.substring(2);
        else if (s === '$') return '';
        s = s.replace(/\[(\d+)\]/g, '.$1');
        if (s.charAt(0) === '.') s = s.substring(1);
        return s;
    }

    /**
     * Walk the cached effective annotations for (service, method)
     * and group lat/lon companions by parent path. Returns an
     * array of `{ parentPath, latPath, lonPath }` records (paths
     * in the chain-variable form) — one per pair.
     */
    function bowireMapPairsForMethod(service, method) {
        var fw = window.__bowireExtFramework;
        if (!fw || typeof fw.effectiveCacheFor !== 'function') return [];
        var anns = fw.effectiveCacheFor(service, method);
        if (!Array.isArray(anns) || anns.length === 0) return [];
        var groups = {};
        for (var i = 0; i < anns.length; i++) {
            var a = anns[i];
            if (a.semantic !== 'coordinate.latitude'
                && a.semantic !== 'coordinate.longitude') continue;
            var raw = a.jsonPath || '';
            var dotIdx = raw.lastIndexOf('.');
            var parent = dotIdx > 0 ? raw.substring(0, dotIdx) : raw;
            if (!groups[parent]) groups[parent] = {};
            groups[parent][a.semantic] = raw;
        }
        var out = [];
        for (var key in groups) {
            if (!Object.prototype.hasOwnProperty.call(groups, key)) continue;
            var lat = groups[key]['coordinate.latitude'];
            var lon = groups[key]['coordinate.longitude'];
            if (!lat || !lon) continue;
            out.push({
                parentPath: bowireMapNormalisePath(key),
                latPath: bowireMapNormalisePath(lat),
                lonPath: bowireMapNormalisePath(lon)
            });
        }
        return out;
    }

    /**
     * Resolve a chain-variable path against a parsed JSON value.
     * Tokens are dot-separated; numeric tokens are array indices.
     * Same convention the response-JSON tree's data-json-path
     * attributes use.
     */
    function bowireMapResolveChainPath(root, path) {
        if (!path) return root;
        var tokens = String(path).split('.');
        var cur = root;
        for (var ti = 0; ti < tokens.length; ti++) {
            if (cur == null) return undefined;
            var key = tokens[ti];
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
     * Resolve a (lat, lon) pair against the active response. Tries
     * the per-tree explicit root first (streaming-detail body),
     * then the unary `responseData` global, peeling back a `data`
     * / `frame` envelope wrapper if present. Returns null when no
     * candidate root yields a valid WGS84 coord — the caller
     * suppresses the "Center on map" item in that case.
     */
    function bowireMapResolveLatLon(pair, explicitRoot) {
        if (!pair) return null;
        var roots = [];
        if (explicitRoot != null) {
            roots.push(explicitRoot);
            if (typeof explicitRoot === 'object') {
                if (explicitRoot.data !== undefined) roots.push(explicitRoot.data);
                if (explicitRoot.frame !== undefined) roots.push(explicitRoot.frame);
            }
        }
        if (typeof window !== 'undefined'
            && typeof window.responseData !== 'undefined'
            && window.responseData != null) {
            roots.push(window.responseData);
        }
        for (var i = 0; i < roots.length; i++) {
            var lat = bowireMapResolveChainPath(roots[i], pair.latPath);
            var lon = bowireMapResolveChainPath(roots[i], pair.lonPath);
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

    /**
     * Decorator hook — stamps data-bowire-coord-path on every
     * lat/lon/parent span, then binds delegated mouseover/mouseout
     * for the JSON → map hover sync.
     *
     * The hook also caches the (treeRoot, explicitRoot) pair on
     * the tree node itself so the menu-contributor below can find
     * the per-frame body the operator's pointing at when the
     * unary `responseData` global lags.
     */
    function bowireMapDecorateResponseTree(opts) {
        if (!opts || !opts.treeRoot || !opts.service || !opts.method) return;
        var fw = window.__bowireExtFramework;
        if (!fw) return;
        var treeRoot = opts.treeRoot;
        // Stash the explicit root so the menu contributor can find
        // it at event-time (morphdom preserves the tree across
        // method switches, so the cached pointer must be the most
        // recent decorate-pass's root).
        treeRoot.__bowireMapExplicitRoot = opts.explicitRoot;

        var loader;
        if (typeof fw.effectiveCacheFor === 'function'
            && fw.effectiveCacheFor(opts.service, opts.method)) {
            loader = Promise.resolve();
        } else if (typeof fw.fetchEffective === 'function') {
            loader = fw.fetchEffective(opts.service, opts.method);
        } else {
            return;
        }
        loader.then(function () {
            var pairs = bowireMapPairsForMethod(opts.service, opts.method);
            if (pairs.length === 0) return;
            // Index: chain-var path → pair record. lat / lon /
            // parent all map to the same record so a hover on any
            // of them lights the same pin.
            var index = {};
            for (var i = 0; i < pairs.length; i++) {
                index[pairs[i].latPath] = pairs[i];
                index[pairs[i].lonPath] = pairs[i];
                if (pairs[i].parentPath) index[pairs[i].parentPath] = pairs[i];
            }
            var picks = treeRoot.querySelectorAll('[data-json-path]');
            for (var p = 0; p < picks.length; p++) {
                var raw = picks[p].getAttribute('data-json-path') || '';
                if (!index[raw]) continue;
                // Use the lat-path as the canonical pair id —
                // highlightByPath accepts any of the three forms
                // but consistency keeps the DOM inspector readable.
                picks[p].setAttribute(
                    'data-bowire-coord-path', index[raw].latPath);
            }
            bowireMapAttachTreeHover(treeRoot);
        }).catch(function (err) {
            console.error('[bowire-map] tree decorator failed', err);
        });
    }

    /**
     * Bind the JSON → map hover handlers to the tree root. One
     * pair per tree; re-running is a no-op because morphdom
     * preserves the marked element across renders.
     */
    function bowireMapAttachTreeHover(treeRoot) {
        if (treeRoot.__bowireMapHoverMounted) return;
        treeRoot.__bowireMapHoverMounted = true;
        function closestCoord(target) {
            return target && target.closest
                ? target.closest('[data-bowire-coord-path]')
                : null;
        }
        treeRoot.addEventListener('mouseover', function (e) {
            var coord = closestCoord(e.target);
            if (!coord) return;
            var path = coord.getAttribute('data-bowire-coord-path') || '';
            if (!path) return;
            var widgets = window.__bowireMapWidgets || [];
            for (var i = 0; i < widgets.length; i++) {
                try { widgets[i].highlightByPath(path); } catch {}
            }
            coord.classList.add('bowire-coord-hover-source');
        });
        treeRoot.addEventListener('mouseout', function (e) {
            var coord = closestCoord(e.target);
            if (!coord) return;
            var related = e.relatedTarget;
            if (related && related.closest
                && related.closest('[data-bowire-coord-path]') === coord) return;
            coord.classList.remove('bowire-coord-hover-source');
            var widgets = window.__bowireMapWidgets || [];
            for (var i = 0; i < widgets.length; i++) {
                try { widgets[i].clearHighlight(); } catch {}
            }
        });
    }

    /**
     * Menu contributor hook — returns the "Center on map" entry
     * when the right-clicked span belongs to a resolved coord
     * pair. Returns `[]` (no items) when:
     *   - no map viewer is registered (paranoid; the contributor
     *     itself is only registered from this bundle, but a future
     *     "register but skip mounting" pattern could leave it
     *     dangling),
     *   - the target span has no data-bowire-coord-path,
     *   - the path doesn't resolve to a valid WGS84 coord.
     */
    function bowireMapMenuContributor(ctx) {
        if (!ctx || !ctx.target || !ctx.service || !ctx.method) return [];
        var coord = ctx.target.closest
            ? ctx.target.closest('[data-bowire-coord-path]')
            : null;
        if (!coord) return [];
        var path = coord.getAttribute('data-bowire-coord-path') || '';
        if (!path) return [];
        var pairs = bowireMapPairsForMethod(ctx.service, ctx.method);
        var pair = null;
        for (var i = 0; i < pairs.length; i++) {
            if (pairs[i].latPath === path
                || pairs[i].lonPath === path
                || pairs[i].parentPath === path) {
                pair = pairs[i];
                break;
            }
        }
        if (!pair) return [];
        var explicit = ctx.treeRoot && ctx.treeRoot.__bowireMapExplicitRoot;
        var loc = bowireMapResolveLatLon(pair, explicit);
        if (!loc) return [];
        return [{
            label: t('map.centre'),
            // 5-decimal lat/lon ≈ 1 m precision — same shape most
            // GIS tools print and what the operator expects on the
            // status bar of a desktop map app.
            meta: loc.lat.toFixed(5) + ', ' + loc.lon.toFixed(5),
            action: function () {
                var widgets = window.__bowireMapWidgets || [];
                for (var i = 0; i < widgets.length; i++) {
                    try { widgets[i].flyTo({ center: [loc.lon, loc.lat] }); } catch {}
                }
            }
        }];
    }

    /**
     * Inject the small CSS the JSON ↔ map hover-sync needs. Stays
     * inside the map bundle so the core workbench doesn't carry
     * coordinate-specific class names. Idempotent — re-running on a
     * page that already loaded the bundle is a no-op. Same shape
     * the MapLibre CSS injection uses (one-shot, <link>-style).
     */
    function bowireMapInjectCoordSyncStyles() {
        var styleId = 'bowire-map-coord-sync-styles';
        if (document.getElementById(styleId)) return;
        var css =
            '[data-bowire-coord-path]{'
            + 'text-decoration:underline dotted var(--bowire-text-tertiary, rgba(127,127,127,0.6));'
            + 'text-decoration-thickness:1px;'
            + 'text-underline-offset:2px;'
            + 'cursor:pointer;'
            + '}'
            + '.bowire-coord-hover-source,'
            + '.bowire-coord-hover-target{'
            + 'background:color-mix(in srgb, var(--bowire-accent, #4f46e5) 18%, transparent);'
            + 'border-radius:3px;'
            + 'transition:background 80ms ease-out;'
            + '}';
        var tag = document.createElement('style');
        tag.id = styleId;
        tag.textContent = css;
        document.head.appendChild(tag);
    }

    /**
     * Reverse-direction hover. The map widget dispatches
     * `bowire:map-coord-hover` on pin mouseenter/mouseleave; this
     * listener tints the matching JSON spans by adding the
     * `bowire-coord-hover-target` class.
     */
    function bowireMapInstallReverseHover() {
        if (window.__bowireMapReverseHoverInstalled) return;
        window.__bowireMapReverseHoverInstalled = true;
        document.addEventListener('bowire:map-coord-hover', function (e) {
            var prior = document.querySelectorAll('.bowire-coord-hover-target');
            for (var p = 0; p < prior.length; p++) {
                prior[p].classList.remove('bowire-coord-hover-target');
            }
            var detail = e && e.detail;
            if (!detail) return;
            // The map dispatches paths in JSONPath form (`$.foo[0].bar`).
            // The JSON viewer's row attributes are in chain form
            // (`foo.0.bar`). Convert both ends to the same shape
            // before building selectors — without the bracket→dot
            // step the hover never matched on responses with arrays
            // (situationObjects[N]), which is the most common shape
            // in practice.
            var paths = [];
            if (detail.parentPath) paths.push(bowireMapJsonPathToChainPath(detail.parentPath));
            if (detail.latPath) paths.push(bowireMapJsonPathToChainPath(detail.latPath));
            if (detail.lonPath) paths.push(bowireMapJsonPathToChainPath(detail.lonPath));
            if (paths.length === 0) return;
            var parts = [];
            for (var i = 0; i < paths.length; i++) {
                if (!paths[i]) continue;
                // Match helpers.js CSS-escape fallback so the same
                // set of attribute-selector specials is escaped when
                // CSS.escape isn't available — CodeQL alert #1777
                // (js/incomplete-sanitization).
                var safe = (typeof CSS !== 'undefined' && CSS.escape)
                    ? CSS.escape(paths[i])
                    : paths[i].replace(/[!"#$%&'()*+,./:;<=>?@[\\\]^`{|}~]/g, '\\$&');
                parts.push('[data-bowire-coord-path="' + safe + '"]');
                parts.push('[data-line-path="' + safe + '"]');
            }
            if (parts.length === 0) return;
            var matches = document.querySelectorAll(parts.join(','));
            for (var m = 0; m < matches.length; m++) {
                matches[m].classList.add('bowire-coord-hover-target');
            }
        });
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
                // #117 — a getter, not a value. Registration runs once at
                // bundle load; setLocale does not reload, so a label resolved
                // here would show whatever language this session booted in
                // for the rest of it.
                get label() { return t('map.label'); },
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
                get label() { return t('map.pick'); },
                // The coordinate editor only ever cares about a single
                // (lat, lon) pair, so leave it on the safe default.
                selectionMode: 'single',
                mount: bowireMapEditorMount
            }
        });
        framework.markBuiltIn('kuestenlogik.maplibre');

        // Phase 4.1 — response-tree integration. Wired here (not
        // inside the per-mount path) because the JSON tree exists
        // regardless of whether a map viewer is currently mounted:
        // the operator can right-click a coord field, see the
        // "Center on map" entry, and trigger a flyTo against
        // whichever map widgets ARE mounted at that moment. When
        // none are mounted the menu entry still surfaces but the
        // flyTo loop just no-ops — preferable to the alternative
        // of "menu entry disappears when the map tab is hidden".
        if (typeof window.BowireExtensions === 'object'
            && typeof window.BowireExtensions.registerResponseTreeDecorator === 'function') {
            window.BowireExtensions.registerResponseTreeDecorator(
                bowireMapDecorateResponseTree);
        }
        if (typeof window.BowireExtensions === 'object'
            && typeof window.BowireExtensions.registerResponseTreeMenuContributor === 'function') {
            window.BowireExtensions.registerResponseTreeMenuContributor(
                bowireMapMenuContributor);
        }
        bowireMapInjectCoordSyncStyles();
        bowireMapInstallReverseHover();
    })();
