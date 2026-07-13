import { test, expect } from '@playwright/test';
import { bootFresh } from './helpers';

/**
 * Monitoring rail (#102) — the read-only workbench surface over the probe
 * outcome ledger. Probes are written by `bowire monitor run` into
 * ~/.bowire/monitoring on the machine hosting the Tool, so a fresh CI run
 * has an empty ledger; these specs assert the rail is contributed, routes,
 * and renders EITHER the ledger content (sidebar rows + overview cards +
 * sparklines) when outcomes exist, or the explanatory empty state when
 * they don't. Both branches exercise the full fragment → renderer-key →
 * /api/monitoring/probes path.
 */
test.describe('Monitoring rail (#102)', () => {
    test.beforeEach(async ({ page }) => {
        await bootFresh(page);
    });

    test('rail button is contributed and switches railMode', async ({ page }) => {
        const monitorBtn = page.locator('.bowire-rail-btn[data-rail-mode-id="monitoring"]');
        await expect(monitorBtn).toBeVisible();
        await monitorBtn.click();
        await expect(monitorBtn).toHaveClass(/active/);
        const persistedMode = await page.evaluate(() => localStorage.getItem('bowire_rail_mode'));
        expect(persistedMode).toBe('monitoring');
    });

    test('main pane renders the ledger surface (overview cards or empty state)', async ({ page }) => {
        await page.locator('.bowire-rail-btn[data-rail-mode-id="monitoring"]').click();
        await expect(page.locator('#bowire-main-monitoring')).toBeVisible();

        // The initial fetch resolves async — the pane paints a transient
        // 'Loading probe ledger…' card first. Wait for that to clear
        // before deciding which branch (cards vs empty state) applies,
        // otherwise the branch check races the fetch.
        const card = page.locator('.bowire-mon-card').first();
        const empty = page.locator('#bowire-main-monitoring .bowire-empty-card-headline');
        await expect(empty.filter({ hasText: 'Loading' })).toHaveCount(0, { timeout: 10_000 });
        await expect(card.or(empty).first()).toBeVisible({ timeout: 10_000 });

        if (await card.isVisible()) {
            // Ledger has outcomes — every card carries a name, a status
            // chip (never color-alone), and the latency sparkline.
            await expect(card.locator('.bowire-mon-card-name')).toBeVisible();
            await expect(card.locator('.bowire-mon-chip')).toBeVisible();
            await expect(card.locator('.bowire-mon-spark')).toBeVisible();

            // Sidebar mirrors the probes as rows; clicking one opens the
            // detail pane with the outcome table.
            await expect(page.locator('#bowire-monitoring-list .bowire-env-list-item').first()).toBeVisible();
            await card.click();
            await expect(page.locator('.bowire-mon-detail-head')).toBeVisible();
            await expect(page.locator('.bowire-mon-table tbody tr').first()).toBeVisible();
        } else {
            // Fresh ledger — the empty state explains the CLI entry point.
            await expect(empty).toHaveText('No probe outcomes yet');
        }
    });
});
