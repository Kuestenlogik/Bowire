import { test, expect } from '@playwright/test';
import { bootFresh } from './helpers';

/**
 * Hiding a protocol from your own sidebar (#638).
 *
 * The point of running this in a browser rather than asserting the store:
 * the store already has unit coverage, and what it cannot tell us is whether
 * the row actually moves, whether the way back is on the page, and whether
 * the choice survives a reload. A preference whose undo nobody can find is a
 * bug with a nice name, and only this suite can catch that.
 *
 * A fresh CI instance loads the bundled protocols (gRPC, REST, MQTT, …), so
 * there is always at least one row to act on. The spec takes whichever comes
 * first rather than naming one — which protocols ship is not what is under
 * test here.
 *
 * #740 — that claim used to sit above a `test.skip(rows.count() === 0)`,
 * which is a switch for the case the sentence says cannot happen. In a full
 * suite run it happened every time, and the run stayed green with both tests
 * silently doing nothing; run alone the spec passed. A spec that checks
 * nothing half the time is worse than one that is missing: it is in the list
 * and reads like coverage.
 *
 * The cause is a race, not an absence. `protocols` is filled by a fetch of
 * /api/protocols at the top of a discovery pass, and the Protocols page
 * renders an empty card and returns when that list has not arrived yet
 * (settings.js, `renderSettingsConfigureProtocols`). Alone the machine is
 * idle and the list is there before the click; in a full run the shared
 * workbench on :5180 is busy and it is not.
 *
 * So the switch is gone and the spec waits instead, which is both the right
 * assertion and the fix. If the rows never arrive it fails and says what was
 * on the page and what the server thinks it has loaded — because "a row was
 * missing" on its own decides nothing, which is how this went unread once
 * already.
 */
test.describe('Per-identity protocol visibility (#638)', () => {
    test.beforeEach(async ({ page }) => {
        await bootFresh(page);
    });

    type Pg = import('@playwright/test').Page;

    /**
     * What the page and the server say about protocols, for a failure message
     * that is worth reading. Every part is best-effort: this runs when
     * something is already wrong, and a diagnostic that throws replaces the
     * finding with its own stack trace.
     */
    async function describeProtocolState(page: Pg): Promise<string> {
        const parts: string[] = [];

        const served = await page.evaluate(async () => {
            try {
                const prefix = (window as any).__BOWIRE_CONFIG__?.prefix ?? '';
                const r = await fetch(prefix + '/api/protocols');
                if (!r.ok) return `HTTP ${r.status}`;
                const list = await r.json();
                return Array.isArray(list)
                    ? list.map((p: { id?: string }) => p?.id ?? '?').join(', ') || '(empty array)'
                    : JSON.stringify(list);
            } catch (e) { return 'fetch failed: ' + String(e); }
        }).catch(() => '(could not ask the server)');
        parts.push(`/api/protocols now: ${served}`);

        // The empty card is the page saying "the list had not arrived" rather
        // than "every protocol is hidden" — the two look identical from the
        // row count alone, and they are different findings.
        const emptyCard = await page.locator('.bowire-settings-section .bowire-empty-card')
            .count().catch(() => -1);
        parts.push(`empty-state cards on the page: ${emptyCard}`);

        const disclosure = await page.locator('.bowire-settings-hidden-disclosure')
            .textContent().catch(() => null);
        parts.push(`hidden disclosure: ${disclosure ?? '(none)'}`);

        const banner = await page.locator('.bowire-settings-plugin-health')
            .allTextContents().then(t => t.join(' / ')).catch(() => null);
        parts.push(`health banner: ${(banner ?? '(none)').replace(/\s+/g, ' ').trim()}`);

        return parts.join(' | ');
    }

    /**
     * The protocol rows, waited for rather than counted once.
     *
     * The wait is the point: the list arrives on a fetch, so counting on the
     * first paint asks a question whose answer has not been written yet.
     */
    async function protocolRows(page: Pg) {
        const rows = page.locator('.bowire-settings-plugin-row-with-lifecycle');
        try {
            await expect(rows.first()).toBeVisible({ timeout: 20_000 });
        } catch {
            throw new Error(
                'No protocol row on the Protocols page after 20s. A fresh instance '
                + 'loads the bundled protocols, so none is the finding rather than a '
                + `reason to skip. State: ${await describeProtocolState(page)}`);
        }
        return rows;
    }

    async function openProtocols(page: Pg) {
        await page.locator('.bowire-rail-settings').click();
        await expect(page.locator('.bowire-settings-overlay')).toBeVisible();
        // The Plugins group header navigates to Protocols itself — its own
        // comment calls it "the most common entry" — so this needs no tree
        // expansion, which is what the Protocols leaf would have needed.
        await page.locator('.bowire-settings-left').getByText('Plugins', { exact: true }).click();
        await expect(page.locator('#bowire-settings-right-configure-protocols')).toBeVisible();
    }

    test('a protocol can be hidden, found again, and shown', async ({ page }) => {
        await openProtocols(page);

        const rows = await protocolRows(page);
        const before = await rows.count();

        // Hover-reveal: the control is display:none until the pointer is on
        // the row, which is also the assertion that it is not a wall of
        // buttons down the page.
        const firstRow = rows.first();
        const hide = firstRow.locator('.bowire-settings-plugin-hide-toggle');
        await expect(hide).toBeHidden();
        await firstRow.hover();
        await expect(hide).toBeVisible();

        await hide.click();

        // The row moved rather than vanished: the disclosure names how many
        // are behind it, which is the answer to "where did MQTT go".
        const disclosure = page.locator('.bowire-settings-hidden-disclosure');
        await expect(disclosure).toBeVisible();
        await expect(disclosure).toContainText('hidden protocol');

        // Hiding expands the section, so the way back is already on screen.
        const hiddenBox = page.locator('.bowire-settings-hidden-protocols');
        await expect(hiddenBox).toBeVisible();
        const show = hiddenBox.locator('.bowire-settings-plugin-hide-toggle').first();
        await expect(show).toHaveText('Show');

        await show.click();

        await expect(page.locator('.bowire-settings-hidden-disclosure')).toHaveCount(0);
        await expect(rows).toHaveCount(before);
    });

    test('the choice survives a reload', async ({ page }) => {
        await openProtocols(page);

        const rows = await protocolRows(page);

        const firstRow = rows.first();
        await firstRow.hover();
        await firstRow.locator('.bowire-settings-plugin-hide-toggle').click();
        await expect(page.locator('.bowire-settings-hidden-disclosure')).toBeVisible();

        // Through the file in the identity's slot and back — the half a
        // component test cannot reach.
        await page.reload();
        await openProtocols(page);

        await expect(page.locator('.bowire-settings-hidden-disclosure')).toBeVisible({ timeout: 10_000 });

        // Leave the instance as we found it: the suite shares one workbench.
        await page.locator('.bowire-settings-hidden-disclosure').click();
        const hiddenBox = page.locator('.bowire-settings-hidden-protocols');
        await hiddenBox.locator('.bowire-settings-plugin-hide-toggle').first().click();
        await expect(page.locator('.bowire-settings-hidden-disclosure')).toHaveCount(0);
    });
});
