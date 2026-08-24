import { test, expect } from '@playwright/test';

/**
 * Streaming pane layout — the frame list must scroll inside its own pane
 * instead of growing and pushing the detail pane out of view.
 *
 * This regression has now shipped twice. The CSS in bowire.css quotes the
 * operator report verbatim both times, and three throwaway probe scripts
 * (scripts/ux-tests/probe-stream-detail-shrink*.mjs) exist because each
 * round was found and verified by hand. Nothing held it in place, so it
 * came back. This spec is what holds it.
 *
 * The second occurrence is instructive and drives what is asserted here:
 * the fix was applied to `.bowire-response-output.streaming` and was
 * correct, but the *default* (non-split) path wraps that element in
 * `.bowire-streaming-with-placeholders`, which had no CSS at all. A block
 * box at content height voids a percentage height on its child, so the
 * fix one level down could never take effect. Asserting on the rendered
 * geometry rather than on any single element's computed style is
 * deliberate: it catches a break anywhere in that chain, including the
 * next wrapper someone inserts.
 *
 * Driven by the in-repo SSE sample (:5186) rather than harbor-demo, which
 * lives in Bowire.Samples and is not available in CI. It hosts the
 * workbench itself at /bowire with its endpoints already discovered, and
 * ticks once a second.
 */

const SSE_WORKBENCH = 'http://localhost:5186/bowire';

// One frame per second, and the list has to overflow before the assertions
// mean anything — so this spec is inherently slower than the UI specs.
test.setTimeout(120_000);

test.describe('Streaming pane layout', () => {
    // A short viewport on purpose. The assertions below only bite once the
    // frames outgrow the pane, and at the default 900px height that takes
    // ~30 frames — half a minute of waiting at one tick per second. Ample
    // headroom is not neutral here: it is the difference between a spec
    // that catches the regression and one that passes through it. Verified
    // by reverting the fix and watching this spec go red.
    test.use({ viewport: { width: 1440, height: 560 } });

    test('the frame list scrolls and the detail pane stays on screen', async ({ page }) => {
        await page.goto(SSE_WORKBENCH, { waitUntil: 'domcontentloaded' });
        await page.waitForSelector('#bowire-app', { timeout: 30_000 });

        // The sample seeds its own sources, so the ticker is already in
        // the rail — no URL typing, no localStorage priming.
        await page.locator('.bowire-method-item', { hasText: 'Ticker' }).first().click();
        await page.locator('#bowire-action-execute-btn').click();

        // Enough frames that the list cannot fit its pane. Below the
        // overflow point every assertion here passes trivially — 12 frames
        // in a 583px pane was not enough to reproduce the shipped bug.
        await page.waitForFunction(
            () => document.querySelectorAll('.bowire-stream-list-item').length >= 18,
            null, { timeout: 90_000 });

        // Guard the guard: if the frames would fit anyway, this spec proves
        // nothing, and a future layout change could silently take it back
        // below the threshold. Compare against the PANE, never against the
        // list — in the broken state the list grows to match its content,
        // so `rows > listClientHeight` is circular and reads as "not enough
        // frames" exactly when the bug is present.
        const pre = await page.evaluate(() => {
            const output = document.getElementById('bowire-stream-output');
            const paneBody = output?.closest('.bowire-pane-body') ?? output?.parentElement?.parentElement;
            const rows = [...document.querySelectorAll('.bowire-stream-list-item')]
                .reduce((sum, r) => sum + r.getBoundingClientRect().height, 0);
            return {
                rows: Math.round(rows),
                paneBodyH: paneBody ? Math.round(paneBody.getBoundingClientRect().height) : 0,
            };
        });
        expect(pre.rows,
            `precondition: frames (${pre.rows}px) must exceed the pane (${pre.paneBodyH}px), `
            + 'or this spec asserts nothing')
            .toBeGreaterThan(pre.paneBodyH);

        const geom = await page.evaluate(() => {
            const h = (el: Element | null | undefined) =>
                el ? Math.round(el.getBoundingClientRect().height) : 0;
            const output = document.getElementById('bowire-stream-output');
            const list = document.querySelector('.bowire-stream-list-pane');
            const detailBody = document.querySelector('.bowire-stream-detail-body');
            // Whatever sits between the output and the pane body — today
            // .bowire-streaming-with-placeholders, tomorrow possibly
            // something else. Walk up to the scroll host rather than
            // naming a class, so an inserted wrapper is covered too.
            const paneBody = output?.closest('.bowire-pane-body') ?? output?.parentElement?.parentElement;
            const detailRect = detailBody?.getBoundingClientRect();
            return {
                frames: document.querySelectorAll('.bowire-stream-list-item').length,
                paneBodyH: h(paneBody),
                outputH: h(output),
                listClientH: (list as HTMLElement | null)?.clientHeight ?? 0,
                listScrollH: (list as HTMLElement | null)?.scrollHeight ?? 0,
                detailBodyH: h(detailBody),
                detailBottom: detailRect ? Math.round(detailRect.bottom) : 0,
                viewportH: window.innerHeight,
            };
        });

        // The stream output must not outgrow the pane it lives in. This is
        // the single condition both regressions violated: it measured 1229
        // against a 555px pane the last time.
        expect(geom.outputH, `stream output (${geom.outputH}px) must fit its pane (${geom.paneBodyH}px)`)
            .toBeLessThanOrEqual(geom.paneBodyH + 1);

        // With more frames than fit, the list scrolls internally. When the
        // chain breaks, scrollHeight collapses onto clientHeight because
        // the list simply grew instead — that is the "no scrollbar"
        // symptom, and it is what this asserts against.
        expect(geom.listScrollH, `frame list must scroll (content ${geom.listScrollH}px in ${geom.listClientH}px)`)
            .toBeGreaterThan(geom.listClientH);

        // The detail pane is the whole point of the list: it shows what the
        // selected message actually contains. It must be rendered and fully
        // above the fold, not merely present in the DOM below it.
        expect(geom.detailBodyH, 'detail body must be rendered').toBeGreaterThan(0);
        expect(geom.detailBottom, `detail body must be within the viewport (${geom.viewportH}px)`)
            .toBeLessThanOrEqual(geom.viewportH);
    });
});
