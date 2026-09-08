/**
 * Crop a UI-guide surface from the element itself, not from coordinates
 * somebody measured once by eye.
 *
 * The clips this replaces were hardcoded and drifted: the request-pane crop
 * (`x: 348, width: 560`) started a hundred pixels inside the sidebar and cut
 * the pane's own toolbar off on the right, because the pane actually sits at
 * x=449 with width=475. Its own comment said "roughly" and "~", which was
 * honest and is exactly the problem.
 *
 * Selectors are tried in order and the first laid-out match wins. Nothing is
 * cropped from a selector that matched nothing — the caller is told instead,
 * so a renamed class produces a reported gap rather than a picture of the
 * wrong part of the screen.
 */

/** Selector candidates per surface. Pane ids carry the selected method. */
const SURFACE_SELECTORS = {
    'rail-strip':    ['#bowire-activity-rail', '.bowire-activity-rail', '.bowire-rail'],
    'sidebar':       ['#bowire-sidebar', '.bowire-sidebar', 'aside.bowire-sidebar'],
    'request-pane':  ['[id^="bowire-request-pane-"]', '#bowire-request-pane'],
    'response-pane': ['[id^="bowire-response-pane-"]', '#bowire-response-pane'],
    'action-bar':    ['#bowire-action-bar', '.bowire-action-bar', '.bowire-actions'],
    'topbar':        ['.bowire-topbar', 'header.bowire-header', '#bowire-topbar'],
};

/**
 * Measure a surface in the page. Returns the clip rect plus the selector
 * that produced it, or null when nothing matched.
 *
 * `pad` grows the rect on every side so a crop does not shave the region's
 * own border — a one-pixel shave reads as a rendering bug in the docs.
 */
async function measureSurface(page, surface, pad = 0) {
    const selectors = SURFACE_SELECTORS[surface];
    if (!selectors) throw new Error(`no selectors registered for surface '${surface}'`);

    return page.evaluate(({ selectors, pad }) => {
        for (const sel of selectors) {
            const el = document.querySelector(sel);
            if (!el || !(el.offsetWidth || el.offsetHeight)) continue;
            const r = el.getBoundingClientRect();
            return {
                sel,
                x: Math.max(0, Math.round(r.x) - pad),
                y: Math.max(0, Math.round(r.y) - pad),
                width: Math.round(r.width) + pad * 2,
                height: Math.round(r.height) + pad * 2,
            };
        }
        return null;
    }, { selectors, pad });
}

/**
 * Screenshot one surface into `path`. Returns the rect used, or null when
 * the surface was not on screen — never a crop from a guessed rectangle.
 */
async function shootSurface(page, surface, path, { pad = 0, viewport } = {}) {
    const rect = await measureSurface(page, surface, pad);
    if (!rect) return null;

    // Keep the clip inside the viewport; Playwright rejects one that spills.
    const vp = viewport || page.viewportSize() || { width: 1400, height: 900 };
    const clip = {
        x: rect.x,
        y: rect.y,
        width: Math.min(rect.width, vp.width - rect.x),
        height: Math.min(rect.height, vp.height - rect.y),
    };
    await page.screenshot({ path, clip });
    return { ...rect, clip };
}

module.exports = { SURFACE_SELECTORS, measureSurface, shootSurface };
