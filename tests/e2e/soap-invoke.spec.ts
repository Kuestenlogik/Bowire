import { test, expect, type Page } from '@playwright/test';

/**
 * A SOAP call, driven from the form the way a person drives it.
 *
 * This exists because of what the other layers cannot see. The sample smoke
 * test invokes every discovered method and asserts the answer is not
 * 404 / 405 / 5xx — so a request whose arguments were silently dropped passes
 * it, because the server answers 200 with a result computed from defaults.
 * The unit tests around `SoapEnvelopeBuilder` assert the envelope's shape, but
 * nothing asserted that what the *form* collects reaches the server at all.
 *
 * Between those two lay the bug: the form produces a JSON object, the envelope
 * builder's XML branch accepted it without complaining (`<root>{"a":2}</root>`
 * is well-formed XML whose content is a text node), and every SOAP call from
 * the workbench arrived as `<Add>{"a":2,"b":3}</Add>`. Add(2, 3) answered 0.
 *
 * So the assertion here is arithmetic, not markup: fill in two numbers, press
 * Execute, and read the number the server computed. A wrong envelope cannot
 * produce 5.
 *
 * Driven against a workbench of its own on :5191 (playwright.config.ts),
 * started pointed at the SOAP sample's WSDL on :5195.
 */

const WORKBENCH = 'http://localhost:5191/';

test.setTimeout(90_000);

async function openCalculator(page: Page): Promise<void> {
    await page.goto(WORKBENCH, { waitUntil: 'domcontentloaded' });
    await page.waitForSelector('#bowire-app', { timeout: 30_000 });

    // The workbench opens on Home; the service list lives in Explore.
    // The rail button rather than the Home shortcut: the shortcut is part of
    // the welcome card, which is not there once a workspace exists — and not
    // there in the same shape when none does.
    await page.locator('.bowire-rail-btn[data-rail-mode-id="discover"]').click();

    await expect(page.locator('.bowire-method-item').first()).toBeVisible({ timeout: 30_000 });
}

/** The form's argument boxes, in the order the WSDL declares them. */
const args = (page: Page) => page.locator('input.bowire-form-input:visible');

test.describe('SOAP invoke from the request form', () => {
    test('the arguments typed into the form reach the service', async ({ page }) => {
        await openCalculator(page);

        await page.locator('.bowire-method-item', { hasText: 'Add' }).first().click();
        await expect(args(page).first()).toBeVisible({ timeout: 15_000 });
        await expect(args(page)).toHaveCount(2);

        await args(page).nth(0).fill('2');
        await args(page).nth(1).fill('3');
        await page.locator('#bowire-action-execute-btn').click();

        // 5, not 0. Zero is what an envelope that lost its arguments produces,
        // and it comes back with HTTP 200 like any other answer.
        await expect(page.locator('.bowire-response-output'))
            .toContainText('<result>5</result>', { timeout: 20_000 });
    });

    test('a different operation computes with the same arguments', async ({ page }) => {
        // One method could pass by coincidence — 0 is a plausible sum of
        // nothing. Subtract(9, 4) is 5 and could not come from empty parts.
        await openCalculator(page);

        await page.locator('.bowire-method-item', { hasText: 'Subtract' }).first().click();
        await expect(args(page).first()).toBeVisible({ timeout: 15_000 });

        await args(page).nth(0).fill('9');
        await args(page).nth(1).fill('4');
        await page.locator('#bowire-action-execute-btn').click();

        await expect(page.locator('.bowire-response-output'))
            .toContainText('<result>5</result>', { timeout: 20_000 });
    });

    test('a method invoked with nothing filled in still answers rather than failing', async ({ page }) => {
        // The empty payload the smoke test sends. It has to stay a clean
        // answer — the fix must not turn "no arguments" into a broken body.
        await openCalculator(page);

        await page.locator('.bowire-method-item', { hasText: 'Multiply' }).first().click();
        await expect(args(page).first()).toBeVisible({ timeout: 15_000 });
        await page.locator('#bowire-action-execute-btn').click();

        await expect(page.locator('.bowire-response-output'))
            .toContainText('MultiplyResponse', { timeout: 20_000 });
    });

    test('the console records the request the browser actually sent', async ({ page }) => {
        // What made the bug findable: the payload is visible to the operator.
        // If this stops being true, the next envelope defect is invisible again.
        const sent: string[] = [];
        page.on('request', r => {
            if (r.url().includes('/api/invoke')) sent.push(r.postData() ?? '');
        });

        await openCalculator(page);
        await page.locator('.bowire-method-item', { hasText: 'Add' }).first().click();
        await expect(args(page).first()).toBeVisible({ timeout: 15_000 });
        await args(page).nth(0).fill('7');
        await args(page).nth(1).fill('1');
        await page.locator('#bowire-action-execute-btn').click();
        await expect(page.locator('.bowire-response-output'))
            .toContainText('<result>8</result>', { timeout: 20_000 });

        // Parsed, not string-matched: the payload is JSON inside JSON, so a
        // substring check passes or fails on escaping rather than on content.
        expect(sent).toHaveLength(1);
        const envelope = JSON.parse(sent[0]) as { messages: string[] };
        expect(JSON.parse(envelope.messages[0])).toEqual({ a: '7', b: '1' });
    });
});
