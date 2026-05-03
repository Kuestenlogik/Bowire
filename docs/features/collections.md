---
summary: 'Named, ordered groups of saved requests.'
---

# Collections

Named, ordered groups of saved requests. Each collection holds items that store the protocol, service, method, request body, metadata, and server URL &mdash; replay the whole collection, share it with teammates, or commit it to a repo.

## Saving a request to a collection

From the action bar beneath any request editor, click **Save to Collection**. A dropdown appears listing your existing collections plus a "**+ New Collection**" option. Select a collection and the current request -- including its body, metadata, and server URL -- is appended as a new item.

If you have no collections yet, picking "**+ New Collection**" creates one and adds the request in a single step.

## Collection Manager

Open the Collection Manager from the sidebar. The modal has two panes:

- **Left pane** -- lists all collections with item counts. Click the **+** button to create a new empty collection.
- **Right pane** -- shows the selected collection's detail: an editable name, a toolbar, and the item list.

### Toolbar actions

| Button | Description |
|--------|-------------|
| **Run All** | Execute every item sequentially with the current environment |
| **Export** | Download the collection as a `.blc` JSON file |
| **Import Postman** | Import a Postman Collection v2.1 JSON file |
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

Exported collections use the `.blc` extension and contain the full collection JSON:

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

Share `.blc` files with teammates or commit them to version control.

## Tips

- Use collections to **group related requests** by feature area, API version, or test scenario.
- The **Run All** button with different environments makes collections ideal for smoke-testing Dev vs. Staging vs. Prod.
- Import Postman collections to **migrate existing API test suites** into Bowire without recreating every request manually.
- Combine with [Flows](flows.md) when you need conditions, delays, or variable capture between steps.

See also: [Flows](flows.md), [Request Chaining](response-chaining.md), [Environments & Variables](environments.md), [Export & Import](export-import.md)
