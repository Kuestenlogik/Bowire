import { defineConfig, devices } from '@playwright/test';

/**
 * Bowire Playwright E2E config.
 *
 * Targets the standalone Tool at http://localhost:5180. The Tool is
 * expected to be running already (`dotnet run` on
 * src/Kuestenlogik.Bowire.Tool); the harness doesn't auto-spawn it
 * because the build cycle is long and the user typically keeps a
 * dev instance up across iterations.
 *
 * Specs live under tests/e2e/ and follow the manual walkthrough
 * (docs/testing/manual-walkthrough.md). Each spec is independent —
 * beforeEach wipes localStorage so order doesn't matter.
 */
export default defineConfig({
    testDir: './tests/e2e',
    fullyParallel: false,
    forbidOnly: !!process.env.CI,
    retries: process.env.CI ? 1 : 0,
    workers: 1,
    reporter: process.env.CI ? [['list'], ['html', { open: 'never' }]] : 'list',
    timeout: 30_000,
    expect: { timeout: 5_000 },
    use: {
        baseURL: process.env.BOWIRE_BASE_URL || 'http://localhost:5180',
        actionTimeout: 5_000,
        navigationTimeout: 15_000,
        trace: 'retain-on-failure',
        screenshot: 'only-on-failure',
        video: 'retain-on-failure',
        colorScheme: 'dark',
        ignoreHTTPSErrors: true,
        viewport: { width: 1440, height: 900 }
    },
    projects: [
        { name: 'chromium', use: { ...devices['Desktop Chrome'] } }
    ]
});
