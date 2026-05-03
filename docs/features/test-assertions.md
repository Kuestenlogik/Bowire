---
summary: 'Bowire ships with a built-in assertion runner so you can verify response shapes without leaving the request flow.'
---

# Test Assertions

Bowire ships with a built-in assertion runner so you can verify response shapes without leaving the request flow. Assertions are configured per method, run automatically after every successful invocation, and turn the **Tests** tab green or red so you can spot regressions at a glance.

## When you can use it

The Tests tab is **only visible for unary methods** -- it lives next to **Performance** in the response pane. Streaming, channel, and duplex methods don't show it. The reason is the same as for Performance Graphs: assertions need a single discrete response to compare against.

## Anatomy of an assertion

Each assertion is a row of **path / operator / expected value**:

| Field | Purpose |
|-------|---------|
| **Path** | JSON path against the response body, or the literal `status` for the response status name |
| **Operator** | How to compare actual against expected (see below) |
| **Expected** | The value to compare against (text input, hidden for `exists` / `notexists`) |

The path syntax mirrors [Request Chaining](response-chaining.md):

| Path | Resolves to |
|------|-------------|
| `response` | The whole response body |
| `response.id` | `body.id` |
| `response.user.email` | `body.user.email` |
| `response.items.0.tags.2` | Third tag of the first item |
| `status` | The gRPC / HTTP status name (e.g. `OK`, `NotFound`, `Unauthenticated`) |

## Operators

| Operator | Meaning | Example |
|----------|---------|---------|
| `eq` | Equality (loose: numbers compare numerically, objects compare via JSON) | `response.id` `eq` `42` |
| `ne` | Not equal | `status` `ne` `Error` |
| `gt`, `gte`, `lt`, `lte` | Numeric comparisons | `response.count` `gte` `1` |
| `contains` | Substring (string) or membership (array) | `response.tags` `contains` `"work"` |
| `matches` | Regex against the string form of the value | `response.email` `matches` `^[^@]+@example\.com$` |
| `exists` | Path resolves to a non-null value | `response.id` `exists` |
| `notexists` | Path is missing or null | `response.error` `notexists` |
| `type` | typeof check (`string`, `number`, `boolean`, `object`, `array`, `null`) | `response.items` `type` `array` |

`eq` and `ne` use **loose equality** so `"42"` from a text input matches the number `42` from a parsed response. Object comparison is via `JSON.stringify`, so the order of keys matters in nested objects.

## Adding assertions

1. Open a unary method
2. Click the **Tests** tab in the response pane
3. Click **+ Add Assertion**
4. Fill in path, pick an operator, type the expected value
5. Fire a request — the assertion runs automatically against the response

You don't need to save anything explicitly. Edits are persisted to `localStorage` as soon as you change a field.

## Auto-run

Every successful unary invocation automatically runs all assertions configured for that method. The tab label updates to show the result:

| Label | Meaning |
|-------|---------|
| `Tests` | No assertions yet |
| `Tests (3)` | 3 assertions configured, none have run yet |
| `Tests (3/3 ✓)` (green) | All assertions passed |
| `Tests (2/3 ✗)` (red) | At least one assertion failed |

Inside the tab, each row shows a status icon, color-coded left border (green/red/gray), and on failure a one-line diff with the actual value.

## Manual re-run

The **Run against last response** button at the bottom re-evaluates every assertion against the most recent response without firing a new request. Useful when you've just added or edited an assertion and want to test it.

## Removing assertions

- Per-row: click the `×` button on the right
- All at once: **Remove all** button at the bottom (with confirmation)

## Storage

Assertions live in `localStorage` under the key `bowire_tests`:

```json
{
  "Todos::GetTodo": [
    { "id": "t_abc123", "path": "status", "op": "eq", "expected": "OK" },
    { "id": "t_def456", "path": "response.id", "op": "eq", "expected": "1" },
    { "id": "t_ghi789", "path": "response.tags", "op": "contains", "expected": "work" }
  ]
}
```

They are **not** persisted to disk -- the assertion store is per-browser, like history and favorites. They are also not exported as part of environment export (because they're method-bound, not environment-bound).

## Substitution

Expected values pass through the same `${var}` substitution engine as everything else. You can store the expected value as an environment variable and reference it:

```
Variables (Dev env):
  prodVersion = v2.1.4

Assertion:
  response.version  eq  ${prodVersion}
```

This makes assertion suites portable across environments — the same test passes against Dev, Staging, and Prod when each environment defines the right values.

System variables (`${now}`, `${uuid}`, ...) work too, though they're rarely useful in expectations.

## Export, import and CI

The **Export collection** button in the Tests tab downloads a portable JSON file describing your assertions plus the request body and active server URL. The format is documented and stable:

```json
{
  "name": "Todos smoke tests",
  "serverUrl": "http://localhost:5006/openapi/v1.json",
  "protocol": "rest",
  "environment": { "todoId": "1" },
  "tests": [
    {
      "name": "Get todo by id",
      "service": "Todos",
      "method": "GetTodo",
      "messages": ["{\"id\": ${todoId}}"],
      "metadata": {},
      "assert": [
        { "path": "status", "op": "eq", "expected": "OK" },
        { "path": "response.id", "op": "eq", "expected": "1" },
        { "path": "response.title", "op": "exists" }
      ]
    }
  ]
}
```

**Import collection** loads a previously exported file back into the Tests tab — useful for sharing assertion suites across team members or restoring after a localStorage wipe.

### `bowire test` — CI runner

The same JSON file is consumed by Bowire's command-line test runner:

```bash
bowire test ./api-smoke.json
bowire test ./api-smoke.json --report report.html
```

The runner:

- Loads the collection file
- Spins up the protocol registry **in-process** -- no HTTP detour, no need for a running Bowire server
- For each test: discovers the service (REST/gRPC/SignalR/SSE/MCP), invokes the method, runs assertions
- Prints colorized pass/fail output to stdout
- Exits with code **0** when all tests pass, **1** when any test fails -- ideal for CI pipelines
- With `--report path.html`, generates a self-contained HTML report (inlined CSS, dark theme matching the Bowire UI) for use as a CI artifact

```text
  Bowire Test Runner   collection: api-smoke.json

  PASS  List all todos   OK · 23ms
        ✓ status eq OK
        ✓ response type array
  PASS  Get todo by id   OK · 3ms
        ✓ status eq OK
        ✓ response.id eq 1
        ✓ response.title exists
  FAIL  Get nonexistent todo   OK · 1ms
        ✗ status eq NotFound   actual: OK

  3/4 tests passed   6/7 assertions   in 234 ms
```

The CLI runner uses the **exact same operators and substitution engine** as the in-browser Tests tab, including system variables (`${now}`, `${uuid}`, `${now+3600}`, etc.). Collections are fully portable in both directions.

### Environment variables in collections

Collections can declare a top-level `environment` map (and per-test override) so the same suite can run against Dev, Staging, and Prod with different values:

```json
{
  "environment": {
    "baseUrl": "https://api.example.com",
    "apiKey": "sk-prod-..."
  },
  "tests": [
    {
      "service": "Users",
      "method": "GetUser",
      "metadata": { "Authorization": "Bearer ${apiKey}" },
      ...
    }
  ]
}
```

For CI, override per-environment values via env-specific collection files or by templating the JSON before invoking `bowire test`.

### CI report formats

`bowire test` writes two complementary reports:

| Flag | Format | Consumed by |
|------|--------|-------------|
| `--report path.html` | Self-contained HTML | Humans / PR comments |
| `--junit path.xml` | JUnit XML (`<testsuites><testsuite><testcase>`) | Jenkins, GitLab CI, Azure DevOps, GitHub Actions test reporters |

Both flags can be combined; either one alone is fine. Each Bowire test maps to one `<testcase>`; assertion mismatches and invocation errors become `<failure>` children with a human-readable message and the diff detail inside.

```bash
bowire test api-smoke.json \
  --report ./artifacts/test-report.html \
  --junit  ./artifacts/junit.xml
```

The exit code is `0` only when every test passed, so CI gates work the same way they do for any unit-test runner.

## What's missing

Tracked on the [roadmap](https://github.com/Kuestenlogik/Bowire/blob/main/ROADMAP.md):

- **JSONPath syntax** support for queries like `$.items[?(@.done == true)].id`
- **Pre-request scripts** for dynamic setup (currently use the chaining feature instead)

## Tips

- **Start with `status`** — every method should at least assert `status eq OK` so you catch errors immediately
- **Mix with chaining** — assertions and chaining share the same path engine, so once you've crafted a chaining placeholder you can copy the same path into an assertion
- **Use environment variables for expected values** — keeps the assertion suite portable across stages
- **Don't over-test** — assertions add friction. Pick a few high-value invariants (status, ID round-trip, count > 0) instead of hundreds of brittle field checks
