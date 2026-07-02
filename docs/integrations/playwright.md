# Playwright → Bowire

[Playwright](https://playwright.dev) can record every network request a
browser makes into a **HAR** file. Bowire imports that HAR and turns it
into a replayable recording — so an end-to-end browser test doubles as
the source of a mock server, a regression suite, or a shareable trace.

The flow is:

```
Playwright (recordHar)  →  trace.har  →  bowire import har  →  recording.bwr  →  bowire mock / test
```

## 1. Capture a HAR with Playwright

Enable HAR recording on the browser context. Every request the page
makes during the test is written to the file when the context closes.

```ts
// playwright: record all traffic for a scenario
import { test } from '@playwright/test';

test('checkout flow', async ({ browser }) => {
  const context = await browser.newContext({
    recordHar: { path: 'checkout.har', content: 'embed' },
  });
  const page = await context.newPage();

  await page.goto('https://shop.example.com');
  await page.getByRole('button', { name: 'Add to cart' }).click();
  await page.getByRole('button', { name: 'Checkout' }).click();

  // Flush the HAR to disk.
  await context.close();
});
```

Or globally, for every test, via `playwright.config.ts`:

```ts
export default defineConfig({
  use: {
    recordHar: { mode: 'full', content: 'embed' },
  },
});
```

- `content: 'embed'` inlines response bodies into the HAR — Bowire needs
  those to replay responses. (`'attach'` writes them to sidecar files,
  which the importer does not follow.)
- `mode: 'full'` keeps request **and** response bodies; `'minimal'`
  drops bodies and yields an empty mock.

## 2. Import the HAR

```bash
bowire import har checkout.har
# → checkout.bwr next to the input file
```

Options:

| Flag | Effect |
|------|--------|
| `-o, --out <path>` | Output path. Use `-` to stream the recording JSON to stdout. |
| `-n, --name <name>` | Recording name. Defaults to the HAR `creator.name` (`"Playwright"`) or `"Imported HAR"`. |

Each HAR entry becomes one recording step: the request verb + path, the
request/response bodies, headers as metadata, and the wall-clock
duration land on the step. Non-`2xx` responses keep their literal
status code so mock mismatches stay visible.

### Protocol classification

- **REST** — the default for ordinary HTTP entries.
- **gRPC-Web** — entries with an `application/grpc-web*` content-type are
  classified as `grpc` steps, with service + method taken from the
  `/package.Service/Method` path. (Native gRPC uses HTTP/2 frames that
  DevTools / Playwright don't serialise into a HAR, so gRPC-Web is the
  only gRPC dialect that survives a browser trace.)

Entries without a method or URL are skipped — they would replay as 404s.

## 3. Replay or test

Spin the imported recording up as a local mock:

```bash
bowire mock checkout.bwr
```

…or run it as an assertion suite in CI:

```bash
bowire test checkout.bwr --junit checkout.xml
```

## Round-trip

Bowire's **Export HAR** (recording manager toolbar) is the inverse of
this importer: a recording exported as HAR and re-imported round-trips
its unary steps. Lossy HAR fields (`cache`, page IDs, server IP,
per-phase `timings` beyond the total) are intentionally dropped — the
recording format only carries what the replayer needs.

## See also

- [CLI mode](../features/cli-mode.md) — `bowire import har`, `mock`, `test`
- [Recording & replay](../features/recording.md) — Export HAR from the workbench
- [Mock servers](../features/mock-server.md)
