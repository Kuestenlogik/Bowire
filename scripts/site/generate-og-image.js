/**
 * Renders scripts/og-image-template.html to site/assets/images/og-image.png
 * at the Open Graph standard 1200×630. Run after editing the template (e.g.
 * adding / removing a protocol name) so the social-media preview stays in
 * sync with the actual feature list.
 *
 * Usage:   node scripts/generate-og-image.js
 * Requires Playwright (npm i -g playwright or run from a node_modules with it).
 *
 * Notes on Discord / social previews:
 * - The PNG goes edge-to-edge dark by design — any white chrome you see in
 *   Discord embeds is the embed-card background, not the image itself.
 * - Discord caches og-images per URL for ~24-48 h. After regenerating +
 *   deploying, share a new URL (e.g. https://bowire.io/?v=2026-05-11) to
 *   force a fresh fetch; existing posts stay on the cached version.
 */
const { chromium } = require('playwright');
const path = require('path');
const fs = require('fs');

const TEMPLATE = path.resolve(__dirname, 'og-image-template.html');
const OUT = path.resolve(__dirname, '..', 'site', 'assets', 'images', 'og-image.png');

(async () => {
    if (!fs.existsSync(TEMPLATE)) {
        console.error(`template not found: ${TEMPLATE}`);
        process.exit(1);
    }

    const browser = await chromium.launch({ headless: true });
    try {
        const ctx = await browser.newContext({
            viewport: { width: 1200, height: 630 },
            deviceScaleFactor: 1,
            // Discord re-encodes large og-images aggressively; 1× is the
            // documented OG resolution and what every social platform
            // expects. Going 2× costs >4× the bytes for no visible gain.
        });
        const page = await ctx.newPage();
        await page.goto('file://' + TEMPLATE.replace(/\\/g, '/'));
        // Let the radial-gradient and any inline font fallback settle
        // before snapping.
        await page.waitForLoadState('networkidle');
        await page.waitForTimeout(150);

        await page.screenshot({
            path: OUT,
            type: 'png',
            // Default would be the whole viewport, which is what we want
            // here; explicit clip in case viewport-overshoot ever drifts.
            clip: { x: 0, y: 0, width: 1200, height: 630 },
        });

        const stat = fs.statSync(OUT);
        console.log(`wrote ${OUT} (${stat.size} bytes, 1200×630)`);
    } finally {
        await browser.close();
    }
})();
