---
title: Collections
summary: 'Named, ordered groups of saved requests, managed from the Compose rail side panel.'
---

# Collections

Named, ordered groups of saved requests. Each collection holds items that store the protocol, service, method, request body, metadata, and server URL &mdash; replay the whole collection, share it with teammates, or commit it to a repo.

## Where collections live: the Compose rail side panel

Collections + Presets are managed from the **Compose rail's side panel**. Open the Compose rail from the left strip, then expand the side panel from the gutter handle: the panel shows both buckets in tabs, the current collection's items in a draggable list, and per-item replay buttons inline.

Every save flow lands in the same panel:

- **Discover** &mdash; right-click a method and pick **Add to&hellip; &rarr; Collection**.
- **Request builder** &mdash; click **Save to collection** in the action bar beneath any request editor.
- **Recordings** &mdash; on any captured step, **Open in Compose** drops the step into the side panel as a new item.

The Compose rail is the canonical surface for managing collections: editing items, running them in sequence, and importing Postman JSON all happen here.

### Standalone Collections rail (optional)

The dedicated full-pane Collections editor still ships, but it's **off by default** as of v2.0 and retired as a standalone package in v2.1 ([release notes — Breaking changes](../release-notes/v2.1.0.md#breaking-changes)). Operators who want the dedicated editor back can re-enable it via **Settings &rarr; Rails** &rarr; check **Collections**. Once enabled, the rail-strip icon, the workspace-tree `Collections` node, and the per-method `C` pill all reappear.

Re-enabling does not change the dispatch path: `railMode === 'collections'` keeps working for embedded hosts that route there programmatically (deep links, custom shells), regardless of the toggle.

## Saving a request to a collection

From the action bar beneath any request editor, click **Save to collection**. A dropdown appears listing your existing collections plus a "**+ New collection**" option. Select a collection and the current request -- including its body, metadata, and server URL -- is appended as a new item.

If you have no collections yet, picking "**+ New collection**" creates one and adds the request in a single step.

## Collection Manager (standalone rail, when enabled)

When the standalone Collections rail is enabled, the Collection Manager opens as a two-pane editor:

- **Left pane** -- lists all collections with item counts. Click the **+** button to create a new empty collection.
- **Right pane** -- shows the selected collection's detail: an editable name, a toolbar, and the item list.

### Toolbar actions

| Button | Description |
|--------|-------------|
| **Run All** | Execute every item sequentially with the current environment, once per row when a data file is attached |
| **Data file…** | Attach a CSV or JSON data set for a data-driven run (see below) |
| **Export** | Download the collection as a `.bwc` JSON file |
| **Import Postman** | Import a Postman Collection v2.1 JSON file |
| **Import collection** | Import an Insomnia export, a Bruno collection or `.http` files (see below) |
| **Export .http** | Download the collection as a `.http` file plus `http-client.env.json` |
| **Delete** | Delete the entire collection (with confirmation) |

Each item row shows its index, protocol badge, service/method, and a trash button to remove it.

## Collection Runner

Click **Run All** to execute the collection from top to bottom. The runner applies environment variable substitution to every item's body, metadata values, and server URL before invoking it.

Execution is sequential -- each item must complete before the next one starts. After each successful response, the result is captured for response chaining, so `${response.X}` placeholders in later items resolve to values from the previous response.

```
Item 1: POST AuthService/Login       body: { "user": "${username}" }
Item 2: GET  UserService/GetProfile   metadata: Authorization: Bearer ${response.token}
Item 3: PUT  UserService/UpdateName   body: { "id": ${response.id}, "name": "New Name" }
```

### Run results

Each completed item shows a **PASS** (green) or **FAIL** (red) badge with the response duration. After all items finish, a summary line shows the pass/total count (e.g. "3 / 3 passed").

### Data-driven runs

**Data file…** attaches a CSV or JSON data set to the collection; **Run All** then runs every item once per row. A row's columns are variables that shadow the active environment, so `{{userId}}` in a body or URL takes the row's value. `{{iteration}}` and `{{iterationCount}}` are set as well; outside a data-driven run they read 1 and 1.

The file is read by the DataSets service, the same one `bowire test --data` uses, so a file that does not parse is refused here with the same message, naming the line or item. The toolbar shows the attached file and its row count; the ✕ detaches it. **Stop at the first failing row** ends the run after the first row with a failing item.

Under the items, one line per row shows PASS or FAIL, the row's source (`users.csv line 4`), and for a failing row which items failed with which status. The attached file is kept for the session only; it is not written into the workspace. See [test assertions](test-assertions.md#data-driven-runs) for the CSV and JSON rules.

### Streaming methods

Only **Unary** methods are executed during a collection run. Items with streaming method types (ServerStreaming, ClientStreaming, Duplex) are automatically skipped with a "Skipped" status, matching the same behavior as recording replay.

## Postman Collection v2.1 import

Click **Import Postman** in the toolbar and select a Postman Collection v2.1 JSON file. Bowire parses the file and converts it into a native collection:

- Nested Postman folders are **flattened** into a single item list.
- Postman `{{variable}}` placeholders are converted to Bowire `${variable}` syntax in URLs, headers, and request bodies.
- HTTP method, URL, headers, and raw body are mapped to Bowire collection item fields.
- Items without a request definition are skipped.

After import, the new collection appears in the manager with the original Postman collection name and a toast confirms the item count.

### Variable mapping example

```
Postman:   {{baseUrl}}/api/users/{{userId}}
Bowire:   ${baseUrl}/api/users/${userId}
```

Define `baseUrl` and `userId` in a Bowire environment and the imported collection runs without further edits.

## .http files: import and export

Bowire reads and writes the `.http` format that Rider, IntelliJ, Visual Studio 2022 and VS Code's REST Client run.

**Import.** Click **Import collection** in the toolbar (or right-click **Collections** in the Workspaces tree) and pick one or more `.http` / `.rest` files, together with `http-client.env.json` and `http-client.private.env.json` when you have them. Each file becomes a collection; each request becomes an item named after its `###` title.

- `{{variables}}` stay as references. A file's own `@name = value` lines become an environment named `<file> (file variables)`.
- Dynamic values map to Bowire's: `{{$uuid}}` / `{{$guid}}` → `{{uuid}}`, `{{$timestamp}}` → `{{now}}`, `{{$isoTimestamp}}` → `{{timestamp}}`, `{{$randomInt}}` → `{{random}}`.
- `http-client.env.json` environments become Bowire environments, with `$shared` merged into each.
- Response handlers (`> {% %}`), output redirects (`>>`), file bodies (`< file`) and WebSocket / gRPC requests are left out.

The import ends with a report: what was left out and why, what changed shape on the way, and which credentials (an `Authorization` header, a private environment value) came across in clear text, so you can move them into secrets.

**Export.** **Export .http** writes the collection as `<name>.http` and the workspace's environments as `http-client.env.json`. Discovered REST methods take their verb and path from the schema. Secrets are never written: `{{secret.x}}` and `{{keyring.svc/acct}}` become plain `{{x}}` / `{{svc_acct}}` names, listed at the top of the file, for you to define in `http-client.private.env.json`. Non-REST and streaming items are skipped and named in the report.

**One request.** The Code tab offers **.http** next to curl and the CLI line. **Keep {{variables}}** leaves references in place for `http-client.env.json` to resolve; otherwise their current values are written. Secrets stay references either way.

**CLI and MCP.** The same import and export run outside the workbench:

```
bowire import orders.http            # writes orders.bww next to the input
bowire export http orders.bww        # writes orders.http and http-client.env.json
```

MCP clients call `bowire.import` and `bowire.export.http`. All of them, and the workbench, call the same Interop gRPC service (`bowire.interop.v1.Interop`), which is also reachable as JSON at `POST {base}/api/v1/interop/import` and `POST {base}/api/v1/interop/export-http`.

## Insomnia import

**Import collection** also reads Insomnia exports: the v4 export (*Export Data → Insomnia v4*, JSON or YAML) and the v5 collection file (`type: collection.insomnia.rest/5.0`, YAML). Bowire tells them apart by their content.

- Each workspace becomes a collection. Folders are kept as a path in front of each item's name.
- `{{ _.name }}` becomes `{{name}}`. Nested environment values (`{{ _.api.version }}`) become dotted names (`{{api.version}}`). `{% uuid %}` becomes `{{uuid}}`, and `{% now %}` becomes `{{timestamp}}`, `{{now}}` or `{{nowMs}}`. Every other tag (`{% response %}`, faker, prompts) stays as written and is named in the report.
- Query parameters are appended to the URL. Form bodies are joined into a urlencoded body. A GraphQL request becomes a POST with its `{"query": …}` body.
- Each sub-environment becomes a Bowire environment, with the base environment's values merged in. Folder variables are merged into every environment. Values from private environments are flagged as clear-text credentials.
- Insomnia keeps auth per request, Bowire per environment. Bearer, Basic (without variables) and API key auth become headers, and a folder's auth reaches its requests. OAuth 2, AWS IAM, Digest and NTLM are named in the report, so you can set them on the Bowire environment.
- Left out, each with the reason: gRPC and WebSocket requests (Bowire discovers those from the server), pre-request and after-response scripts, multipart and file bodies, disabled headers and parameters, cookie jars, API specs and unit tests.

## Bruno import

Bruno stores a collection as a folder. Pick **Import collection → Import folder…** and choose that folder (or run `bowire import <folder>`). Both of Bruno's file formats are read:

- **Bru** (`.bru` files with `bruno.json`, the format before Bruno 3).
- **OpenCollection YAML** (`opencollection.yml` with one YAML file per request, the default since Bruno 3.1). A bundled OpenCollection file, the whole collection in one `.yml`, is imported with **Import files…**.

What comes across:

- One collection named after the Bruno collection. Folders are kept as a path in front of each item's name (the folder's display name when `folder.bru` / `folder.yml` has one), in Bruno's `seq` order.
- `{{variables}}` stay as they are. `{{$randomUUID}}`, `{{$timestamp}}`, `{{$isoTimestamp}}` and `{{$randomInt}}` become Bowire's system variables. `{{process.env.X}}` and other dynamic values stay as written and are reported.
- Path parameters (`:id`) are filled in from `params:path`. Collection and folder headers reach every request below them.
- Auth inherits as in Bruno: a request with `auth: inherit` takes its folder's or the collection's auth. Bearer, Basic (without variables) and API key become headers or a query parameter. AWS SigV4, Digest, NTLM, OAuth 1/2 and WSSE are reported for the Bowire environment.
- Each environment becomes a Bowire environment; OpenCollection's `extends` is resolved. Secret variables have no value in the files: they come across empty and are named in the report, so you can fill them or move them into Bowire secrets. A `.env` file is never imported.
- Left out, each with the reason: gRPC and WebSocket requests, scripts, tests, assertions, request and post-response variables, multipart and file bodies.

## Response chaining between items

The collection runner captures each item's response in the same chaining store used by manual requests. This means `${response.path}` placeholders in any item resolve to the **previous item's** response body.

Chaining works across protocols -- a REST item's response can feed a gRPC item's request body, provided the JSON path resolves.

If an item fails, the chaining store retains the last successful response. Failed items do not overwrite the capture.

## Persistence

Collections are stored in two places:

1. **Browser localStorage** (`bowire_collections`) -- instant updates without server roundtrips.
2. **Disk** via a debounced PUT to `/bowire/api/collections` -- survives browser changes and profile switches.

The two stores are kept in sync automatically. Every edit (add, remove, rename, reorder) triggers a 400 ms debounced write to disk.

## Export format

Exported collections use the `.bwc` extension and contain the full collection JSON:

```json
{
  "id": "col_abc123",
  "name": "User API Tests",
  "items": [
    {
      "id": "ci_def456",
      "protocol": "grpc",
      "service": "UserService",
      "method": "CreateUser",
      "methodType": "Unary",
      "body": "{ \"name\": \"Alice\" }",
      "messages": ["{ \"name\": \"Alice\" }"],
      "metadata": { "authorization": "Bearer ${token}" },
      "serverUrl": "https://localhost:5001"
    }
  ],
  "createdAt": 1712700000000
}
```

Share `.bwc` files with teammates or commit them to version control.

## Tips

- Use collections to **group related requests** by feature area, API version, or test scenario.
- The **Run All** button with different environments makes collections ideal for smoke-testing Dev vs. Staging vs. Prod.
- Import Postman collections to **migrate existing API test suites** into Bowire without recreating every request manually.
- Combine with [Flows](flows.md) when you need conditions, delays, or variable capture between steps.

See also: [Compose](compose.md), [Flows](flows.md), [Request Chaining](response-chaining.md), [Workspaces](workspaces.md), [Export & Import](export-import.md)
