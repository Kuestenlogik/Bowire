import { test, expect, type Page, type Route } from '@playwright/test';
import { openRail } from './helpers';

/**
 * Lint findings where the method is (#583).
 *
 * The Lint rail listed findings, and that was the only place they showed. A
 * person reading a method had to go there to learn that its response carries
 * a password. Now the linter also runs after every discovery, and the result
 * shows as a pill on the method's sidebar row and a strip under its header;
 * a finding in the rail opens its method — which the docs had promised all
 * along while the row had no click handler.
 *
 * /api/lint is answered here rather than left to the server's rules: which
 * rules fire on which surface is pinned in C#, and what this suite can show
 * that those tests cannot is the path from a finding to the screen. The
 * handler reads the services the workbench posts and aims its findings at
 * real methods of the SSE sample, so nothing depends on guessing names.
 */

const SSE_WORKBENCH = 'http://localhost:5186/bowire';

test.setTimeout(90_000);

type Method = { name: string; summary?: string; description?: string };
type Service = { name: string; methods?: Method[] };

/** The discovered method whose visible text mentions `label`. */
function find(services: Service[], label: string): { service: string; method: string } | null {
    for (const s of services) {
        for (const m of s.methods ?? []) {
            const text = `${m.name} ${m.summary ?? ''} ${m.description ?? ''}`;
            if (text.includes(label)) return { service: s.name, method: m.name };
        }
    }
    return null;
}

/** A High finding on Ticker, an Info one on Status report. */
async function answerLint(route: Route): Promise<void> {
    const body = route.request().postDataJSON() as { services?: Service[] };
    const services = body?.services ?? [];
    const ticker = find(services, 'Ticker');
    const report = find(services, 'Status report');
    const findings = [];
    if (ticker) findings.push({
        ruleId: 'BWR-LINT-SENSITIVE-RESPONSE', severity: 'High',
        service: ticker.service, method: ticker.method, field: 'token',
        message: "Field 'token' looks like a secret.",
    });
    if (report) findings.push({
        ruleId: 'BWR-LINT-MIXED-FIELD-NAMING', severity: 'Info',
        service: report.service, method: report.method, field: 'report_line',
        message: "Field 'report_line' is snake_case.",
    });
    await route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({ findings, summary: { total: findings.length } }),
    });
}

async function openWorkbench(page: Page): Promise<void> {
    await page.route('**/api/lint', answerLint);
    await page.goto(SSE_WORKBENCH, { waitUntil: 'domcontentloaded' });
    await page.waitForSelector('#bowire-app', { timeout: 30_000 });
    await page.evaluate(() => {
        for (const k of Object.keys(localStorage)) {
            if (/bowire_(request_tabs|panes|rail_mode)$/.test(k)) localStorage.removeItem(k);
        }
    });
    await page.reload({ waitUntil: 'domcontentloaded' });
    await page.waitForSelector('#bowire-app', { timeout: 30_000 });
}

const methodRow = (page: Page, label: string) => page.locator('.bowire-method-item', { hasText: label }).first();

test.describe('Lint findings on the method (#583)', () => {
    test('a finding of Low or worse puts a pill on the row; an Info one does not', async ({ page }) => {
        await openWorkbench(page);

        // The background run after discovery — nobody opened the Lint rail.
        const pill = methodRow(page, 'Ticker').locator('.bowire-method-xf-pill-lint-high');
        await expect(pill).toBeVisible({ timeout: 20_000 });
        // A naming nit on half the rows would teach people to ignore the pill.
        await expect(methodRow(page, 'Status report').locator('[class*="bowire-method-xf-pill-lint"]')).toHaveCount(0);
    });

    test('the method shows its findings under its header, folded to one line until opened', async ({ page }) => {
        await openWorkbench(page);
        await expect(methodRow(page, 'Ticker').locator('.bowire-method-xf-pill-lint-high')).toBeVisible({ timeout: 20_000 });

        await methodRow(page, 'Ticker').click();
        const strip = page.locator('.bowire-lint-hints');
        await expect(strip).toBeVisible();
        await expect(strip).toHaveClass(/bowire-lint-hints-high/);
        await expect(strip.locator('.bowire-lint-hints-body')).toHaveCount(0);

        await strip.locator('.bowire-lint-hints-toggle').click();
        await expect(strip.locator('.bowire-lint-hints-body')).toContainText('token');
        await expect(strip.locator('.bowire-lint-hints-body')).toContainText('BWR-LINT-SENSITIVE-RESPONSE');
    });

    test('an Info finding still shows on its own method — just not in the sidebar', async ({ page }) => {
        await openWorkbench(page);
        await expect(methodRow(page, 'Ticker').locator('.bowire-method-xf-pill-lint-high')).toBeVisible({ timeout: 20_000 });

        await methodRow(page, 'Status report').click();
        await expect(page.locator('.bowire-lint-hints')).toHaveClass(/bowire-lint-hints-info/);
    });

    test('a finding in the Lint rail opens its method with the findings unfolded', async ({ page }) => {
        await openWorkbench(page);
        await expect(methodRow(page, 'Ticker').locator('.bowire-method-xf-pill-lint-high')).toBeVisible({ timeout: 20_000 });

        await openRail(page, 'lint');

        const row = page.locator('.bowire-lint-row.clickable', { hasText: 'token' });
        await expect(row).toBeVisible({ timeout: 20_000 });
        await row.click();

        await expect(page.locator('.bowire-lint-hints-body')).toContainText('token');
    });
});
