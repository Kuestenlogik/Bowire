import { test, expect, type Page } from '@playwright/test';
import { bootFresh } from './helpers';

/**
 * `?rail=help&topic=<id>` — the other half of the documented deep link (#736).
 *
 * `?rail=` alone lands on the Help rail's topic picker, which is not what the
 * docs promise and not what a chat link or an onboarding tour needs: those
 * name a topic. The rail half is covered by rail-deeplink.spec.ts; what is
 * only visible here is that the topic is fetched and rendered before anybody
 * clicks a list entry.
 *
 * The topic id is read from the workbench's own topic list rather than
 * written into the spec. A hard-coded id would turn a renamed help page into
 * a failure of the deep link, which is not what this spec is about.
 */

const APP = '#bowire-app.bowire-app-ready';

async function firstTopicId(page: Page): Promise<string> {
    const id = await page.evaluate(async () => {
        const prefix = (window as any).__BOWIRE_CONFIG__?.prefix ?? '';
        const res = await fetch(prefix + '/api/help/topics');
        if (!res.ok) return null;
        const body = await res.json();
        const topics = Array.isArray(body?.topics) ? body.topics : [];
        return topics.length ? topics[0].id : null;
    });
    expect(id, 'the workbench serves at least one help topic').not.toBeNull();
    return id as string;
}

async function open(page: Page, query: string): Promise<void> {
    await page.goto('/' + query, { waitUntil: 'domcontentloaded' });
    await page.waitForSelector(APP, { timeout: 20_000 });
}

test.describe('The ?topic= half of a help deep link', () => {
    test.beforeEach(async ({ page }) => {
        await bootFresh(page);
    });

    test('opens the topic the link names, not the picker', async ({ page }) => {
        const topic = await firstTopicId(page);

        await open(page, `?rail=help&topic=${encodeURIComponent(topic)}`);

        // The undock link carries the id of the topic actually rendered, so
        // this asserts on the id rather than on a title that translation or
        // an edit to the help page would change.
        const undock = page.locator('.bowire-help-undock-btn');
        await expect(undock).toBeVisible({ timeout: 15_000 });
        await expect(undock).toHaveAttribute(
            'href', new RegExp(`/help/topic/${encodeURIComponent(topic).replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}$`),
            { timeout: 5_000 });
    });

    test('a topic this build does not have says so', async ({ page }) => {
        // Landing on the topic picker in silence reads as "this is the page
        // you were sent to". The id is quoted back, because a typo in a
        // shared link is the ordinary way to get here.
        await open(page, '?rail=help&topic=no-such-topic');

        const toast = page.locator('.bowire-toast').last();
        await expect(toast).toBeVisible({ timeout: 15_000 });
        await expect(toast).toContainText('no-such-topic', { timeout: 5_000 });
    });

    test('without a topic the rail still opens on its own choice', async ({ page }) => {
        // The rail half must keep working on its own — and it must not
        // complain about a topic nobody named.
        await open(page, '?rail=help');

        await expect(page.locator('#bowire-main-help')).toBeVisible({ timeout: 15_000 });
        await expect(page.locator('.bowire-toast')).toHaveCount(0);
    });
});
