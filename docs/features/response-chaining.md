---
summary: 'Reference fields from the previous response in your next request via ${response.path.to.field} placeholders.'
---

# Response chaining

Reference fields from the **previous response** in your next request via `${response.path.to.field}` placeholders. Useful when one call returns an ID, token, or cursor that the next call needs.

## Basic usage

After any successful invocation, the response body is captured in memory. Use `${response.X}` anywhere placeholders work -- request body, metadata values, server URL, auth fields.

```json
// Step 1: call CreateUser, response is { "id": 42, "name": "Alice" }
{ "name": "Alice" }

// Step 2: call GetUser, body uses the id from the previous response
{ "id": ${response.id} }
```

## Walking nested paths

Path segments are separated by dots. Object keys and array indices both use the dot notation.

| Placeholder | Resolves to |
|-------------|-------------|
| `${response}` | The whole response body (as JSON string) |
| `${response.id}` | `body.id` |
| `${response.user.email}` | `body.user.email` |
| `${response.items.0.name}` | First element's `name` field |
| `${response.items.0.tags.2}` | Third tag of the first item |

If the path doesn't exist, the placeholder is **left untouched** so chaining bugs are visible (you'll see `${response.foo.bar}` in the outgoing request rather than an empty string).

## Streaming and channels

For server-streaming methods, the **last message** wins -- each new event overwrites the previous capture. Same for duplex/client-streaming channels: every received message updates the captured response.

This makes a typical chained workflow easy:

1. Open a stream that emits a session token as its first message
2. Switch to a unary method that uses `${response.sessionToken}` as auth header

## What gets captured

| Source | What's captured |
|--------|-----------------|
| Unary call | The full response body |
| Server streaming | The latest received message (overwrites on each event) |
| Channel (duplex / client streaming) | The latest received message |
| **Errors** | Nothing -- the previous successful response stays available |

## Privacy

Captured responses live **only in memory**. They are:

- **Never persisted to disk** -- responses can contain tokens or other secrets
- **Cleared on page reload** -- there's no way to recover them after refreshing
- **Not exported** -- the export endpoint covers environments only, not the chaining cache
- **Not shared across browser tabs** -- each tab has its own capture

If you need durable cross-call state, copy the value into an environment variable instead.

## Combining with environment variables

Chaining and environment variables play well together. You can store auth tokens in env vars, but capture short-lived dynamic IDs from the response:

```
Variables (env):
  apiKey = sk-prod-abc

Authentication: API Key
  Header Name = X-Api-Key
  Value       = ${apiKey}

Step 1 — POST /sessions:
  Body: {}
  Response: { "sessionId": "s-7b3", "expiresIn": 3600 }

Step 2 — POST /actions:
  Body: { "session": "${response.sessionId}", "ts": ${now} }
```

## Tips

- **Test your path** with the [Console / Log View](console.md) -- expand the previous response entry and confirm the JSON shape before writing your placeholder.
- **Use system variables in chains** -- `${now}` and `${uuid}` work alongside `${response.X}` in the same template.
- **Escape with `$$`** -- `$${response.foo}` emits the literal string `${response.foo}` if you need it for documentation or templating.
- **Chain across protocols** -- a SignalR hub method's return value can flow into a gRPC unary call's request body. Source-source filtering applies to discovery, not chaining.
