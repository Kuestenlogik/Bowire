import { test, expect, type Page } from '@playwright/test';

/**
 * MCP tasks (#801), driven from the workbench the way a person drives them.
 *
 * The MCP sample (:5190) is a task-capable server: generate_report works for
 * a few seconds and answers, confirm_shipment asks the client a question
 * (elicitation) before it does. A tool call on either streams the task's
 * status, which the response pane draws as a status card above the frames.
 *
 * Every wait here is on the card's own state (`data-mcp-status`), never on
 * time: the card is rebuilt from the stream whenever a task frame arrives.
 */

const WORKBENCH = 'http://localhost:5190/bowire';

test.setTimeout(90_000);

const card = (page: Page) => page.locator('.bowire-mcp-task-card');
const cardWithStatus = (page: Page, status: string) =>
    page.locator(`.bowire-mcp-task-card[data-mcp-status="${status}"]`);

async function openTool(page: Page, tool: string): Promise<void> {
    const rail = page.locator('.bowire-rail-btn[data-rail-mode-id="discover"]');
    if (await rail.count()) await rail.click();
    await page.locator('.bowire-method-item', { hasText: tool }).first().click({ timeout: 30_000 });
    await expect(page.locator('input.bowire-form-input:visible').first()).toBeVisible({ timeout: 15_000 });
}

async function open(page: Page): Promise<void> {
    await page.goto(WORKBENCH, { waitUntil: 'domcontentloaded' });
    await page.waitForSelector('#bowire-app', { timeout: 30_000 });
}

/** generate_report with the given work time; returns the task id. */
async function startReport(page: Page, seconds: number): Promise<string> {
    await openTool(page, 'generate_report');
    // The form lists sensorId, then seconds.
    await page.locator('input.bowire-form-input:visible').nth(1).fill(String(seconds));
    await page.locator('#bowire-action-execute-btn').click();
    await expect(cardWithStatus(page, 'working')).toBeVisible({ timeout: 15_000 });
    return (await card(page).getAttribute('data-task-id'))!;
}

test.describe('MCP tasks in the workbench', () => {
    test('a task tool shows its card going from working to completed with the result', async ({ page }) => {
        await open(page);
        const taskId = await startReport(page, 2);
        expect(taskId).toBeTruthy();
        await expect(card(page).locator('.bowire-mcp-task-cancel')).toBeVisible();
        // The frame list labels the task frame with its status.
        await expect(page.locator('.bowire-mcp-frame-task.is-working').first()).toBeVisible();

        await expect(cardWithStatus(page, 'completed')).toBeVisible({ timeout: 20_000 });
        await expect(card(page).locator('.bowire-mcp-task-outcome.is-completed')).toContainText('sensor-summary');
        await expect(card(page).locator('.bowire-mcp-task-cancel')).toHaveCount(0);
        await expect(page.locator('.bowire-mcp-frame-badge.bowire-mcp-frame-result')).toBeVisible();
    });

    test('a task that asks for input is answered from the form and completes with the answer', async ({ page }) => {
        await open(page);
        await openTool(page, 'confirm_shipment');
        await page.locator('#bowire-action-execute-btn').click();

        const waiting = cardWithStatus(page, 'input_required');
        await expect(waiting).toBeVisible({ timeout: 15_000 });
        await expect(waiting.locator('.bowire-mcp-task-request-message')).toContainText('Book shipment');
        await waiting.locator('[data-field="quantity"] input').fill('3');
        await waiting.locator('[data-field="carrier"] select').selectOption('rail');
        await waiting.locator('.bowire-mcp-task-accept').click();

        await expect(cardWithStatus(page, 'completed')).toBeVisible({ timeout: 20_000 });
        const outcome = card(page).locator('.bowire-mcp-task-outcome.is-completed');
        await expect(outcome).toContainText('"booked":true');
        await expect(outcome).toContainText('"quantity":3');
        await expect(outcome).toContainText('"carrier":"rail"');
    });

    test('an answer outside the schema is held back with a field error', async ({ page }) => {
        await open(page);
        await openTool(page, 'confirm_shipment');
        await page.locator('#bowire-action-execute-btn').click();

        const waiting = cardWithStatus(page, 'input_required');
        await expect(waiting).toBeVisible({ timeout: 15_000 });
        await waiting.locator('[data-field="quantity"] input').fill('99');
        await waiting.locator('.bowire-mcp-task-accept').click();
        await expect(waiting.locator('[data-field="quantity"] .bowire-mcp-task-field-error')).toBeVisible();

        // Declining is an answer too; the tool reports it was not booked.
        await waiting.locator('.bowire-mcp-task-decline').click();
        await expect(cardWithStatus(page, 'completed')).toBeVisible({ timeout: 20_000 });
        await expect(card(page).locator('.bowire-mcp-task-outcome')).toContainText('"booked":false');
    });

    test('cancel asks the server, says it is cooperative, and the stream ends cancelled', async ({ page }) => {
        await open(page);
        await startReport(page, 60);

        await card(page).locator('.bowire-mcp-task-cancel').click();
        await expect(card(page).locator('.bowire-mcp-task-notice')).toContainText('cooperative', { timeout: 15_000 });
        await expect(cardWithStatus(page, 'cancelled')).toBeVisible({ timeout: 20_000 });
        await expect(card(page).locator('.bowire-mcp-task-outcome')).toHaveCount(0);
    });

    test('a task still running after a reload can be followed again', async ({ page }) => {
        await open(page);
        const taskId = await startReport(page, 30);

        await page.reload({ waitUntil: 'domcontentloaded' });
        await page.waitForSelector('#bowire-app', { timeout: 30_000 });
        await openTool(page, 'generate_report');

        const row = page.locator(`.bowire-mcp-task-resume-row[data-task-id="${taskId}"]`);
        await expect(row).toBeVisible({ timeout: 15_000 });
        await row.locator('.bowire-mcp-task-resume').click();

        // Tasks/follow picks the same task up; the card draws there.
        const followed = page.locator(`.bowire-mcp-task-card[data-task-id="${taskId}"][data-mcp-status="working"]`);
        await expect(followed).toBeVisible({ timeout: 15_000 });

        // Leave nothing running on the server behind the test.
        await followed.locator('.bowire-mcp-task-cancel').click();
        await expect(page.locator(`.bowire-mcp-task-card[data-task-id="${taskId}"][data-mcp-status="cancelled"]`))
            .toBeVisible({ timeout: 20_000 });
    });
});
