---
summary: "Bowire ships with built-in auth helpers so you don't have to hand-craft Authorization headers for every request."
---

# Authentication

Bowire ships with built-in auth helpers so you don't have to hand-craft `Authorization` headers for every request. Auth lives **per environment** -- switching from Dev to Prod automatically swaps credentials.

## Supported types

| Type | What it does | Resulting header |
|------|--------------|-------------------|
| **No Auth** | Default. Nothing is added. | -- |
| **Bearer Token** | Adds an OAuth-style bearer token | `Authorization: Bearer <token>` |
| **Basic Auth** | UTF-8 safe base64 of `username:password` | `Authorization: Basic <encoded>` |
| **API Key** | Custom header name and value, **or** query-string parameter | `<HeaderName>: <value>` or `?<name>=<value>` |
| **JWT (HMAC / RSA / ECDSA)** | Builds and signs a JWT client-side via Web Crypto | `Authorization: Bearer <signed-jwt>` |
| **OAuth 2.0 (client_credentials)** | Fetches a token from your token endpoint, caches, auto-refreshes | `Authorization: Bearer <fetched-token>` |
| **OAuth 2.0 (authorization_code + PKCE)** | Browser redirect flow to a real IdP, refresh_token, popup callback | `Authorization: Bearer <fetched-token>` |
| **Custom Token Endpoint** | POST credentials to an arbitrary `/login` endpoint, pluck the token from a JSON path, auto-refresh | `Authorization: Bearer <token>` (configurable prefix) |
| **AWS Signature v4** | Signs the entire HTTP request (headers + body hash) with the AWS Sig v4 algorithm. **REST-only.** | `Authorization: AWS4-HMAC-SHA256 Credential=…, SignedHeaders=…, Signature=…` plus `X-Amz-Date`, `X-Amz-Content-Sha256`, optional `X-Amz-Security-Token` |
| **mTLS** | Client certificate + private key (PEM), optional CA bundle, optional passphrase. Wire-level TLS auth shared by REST, gRPC, WebSocket, SignalR, and the Kafka plugin. | TLS handshake — no `Authorization` header needed |
| **Cookie jar (REST)** | Per-environment in-memory `CookieContainer`. Replays cookies a previous response set on the same origin. **REST-only.** | `Cookie: …` set automatically on follow-up calls |

## Configuration

Open the **Environments & Variables** manager (gear icon next to the env dropdown), pick an environment, and you'll see an **Authentication** section above the variables grid.

1. Pick a type from the dropdown
2. Fill in the type-specific fields
3. Save is automatic -- the auth config is persisted to disk along with the rest of the environment

When an environment has auth configured, a small lock badge appears next to the env dropdown in the sidebar so you always know what's being applied.

## System variables

Bowire ships with a set of built-in placeholders that resolve at substitution time. You don't have to define them anywhere -- they just work, in any field that supports `${var}` substitution (request body, metadata, server URL, **and auth fields**).

| Placeholder | Resolves to |
|-------------|-------------|
| `${now}` | Current Unix timestamp in **seconds** |
| `${now+N}` | `${now}` plus N seconds (e.g. `${now+3600}` -- one hour from now) |
| `${now-N}` | `${now}` minus N seconds |
| `${nowMs}` | Current Unix timestamp in **milliseconds** |
| `${timestamp}` | Current ISO 8601 timestamp (`2026-04-06T10:00:00.000Z`) |
| `${uuid}` | Random RFC 4122 v4 UUID |
| `${random}` | Random unsigned 32-bit integer |

System variables take precedence over user-defined variables with the same name. They are particularly useful for JWT claims, correlation IDs, and any time-bound or random values you need in test requests.

## Variables in auth fields

Every auth field passes through the same `${var}` substitution as request bodies and metadata. That means you can store secrets as environment variables and reference them in the auth config:

```
Variables:
  apiKey      = sk-prod-secret-xyz
  serviceUser = ci-bot

Authentication: Basic Auth
  Username    = ${serviceUser}
  Password    = ${apiKey}
```

This is the recommended pattern for shared environments -- the variable list stays editable and the auth section becomes a stable template.

## Bearer Token

A single text field for the token value. The token is sent as `Bearer <value>` in the `Authorization` header.

```
Authentication: Bearer Token
  Token = ${jwt}
```

The token field is rendered as a password input, so the value isn't visible by default.

## Basic Auth

Two fields, username and password. Bowire encodes them as `username:password` and base64-encodes the result. The encoder is UTF-8 safe -- non-ASCII characters in either field work as expected.

```
Authentication: Basic Auth
  Username = admin@example.com
  Password = ${adminPassword}
```

## API Key

A custom header name plus its value. Use this for any header-based auth scheme that isn't bearer or basic -- `X-Api-Key`, `X-Auth-Token`, `apikey`, anything.

```
Authentication: API Key
  Location    = Header
  Header Name = X-Api-Key
  Value       = ${apiKey}
```

### Query-string location

Some legacy REST APIs (and many public APIs that want a clickable URL) expect the key in the URL itself rather than in an HTTP header. Switch the **Location** dropdown from `Header` to `Query string` and the field label flips from "Header name" to "Query parameter name":

```
Authentication: API Key
  Location              = Query string
  Query parameter name  = api_key
  Value                 = ${apiKey}
```

Bowire appends `?api_key=<value>` to the request URL right before it goes on the wire. Existing query parameters on the URL are preserved (the helper picks `&` vs `?` based on whether the URL already has a query string), and both the parameter name and the value are URI-escaped via `Uri.EscapeDataString` so unusual characters round-trip cleanly.

The query-string mode is wired into all three request paths (unary, streaming, channel-open) so it works with REST, GraphQL, MCP, SSE, SignalR connect URLs, and WebSocket upgrade URLs alike. gRPC ignores it because gRPC doesn't carry query parameters.

## JWT (HMAC / RSA / ECDSA)

Builds and signs a JWT entirely client-side using the Web Crypto API. The result is sent as `Authorization: Bearer <signed-jwt>`.

Supported algorithms (grouped in the dropdown):

| Family | Algorithms | Key shape |
|--------|------------|-----------|
| **HMAC** (shared secret) | HS256, HS384, HS512 | Plain text secret |
| **RSA** (PEM private key) | RS256, RS384, RS512 | PEM-encoded PKCS#8 private key |
| **ECDSA** (PEM private key) | ES256 (P-256), ES384 (P-384), ES512 (P-521) | PEM-encoded PKCS#8 private key |

The **Secret** field flips between a single-line password input (HMAC) and a multi-line PEM textarea (RSA / ECDSA) automatically when you change the algorithm. RSA and ECDSA keys must be in PKCS#8 format — convert from the OpenSSL traditional `BEGIN RSA PRIVATE KEY` / `BEGIN EC PRIVATE KEY` form with:

```bash
openssl pkcs8 -topk8 -nocrypt -in old.pem -out new.pem
```

ECDSA signatures are returned by Web Crypto in the IEEE-P1363 fixed-width format that JWS expects directly (64 bytes for ES256, 96 for ES384, 132 for ES512), so no DER unwrapping is needed — the signature length is verified to match the curve's expected size.

Fields:

| Field | Purpose |
|-------|---------|
| **Algorithm** | HS256 (default), HS384, HS512, RS256/384/512, ES256/384/512 |
| **Header** | JSON template for the JWT header. `alg` and `typ` are filled automatically when missing. |
| **Payload** | JSON template for the JWT payload (claims). |
| **Secret / Private Key** | HMAC secret (text) or PEM-encoded PKCS#8 private key (textarea) depending on algorithm. Supports `${var}` substitution. |

Both the header and payload pass through `${var}` substitution **before** parsing, so dynamic claims work naturally. The default payload Bowire drops in for new JWT configs uses system variables:

```json
{
  "sub": "user123",
  "iat": ${now},
  "exp": ${now+3600}
}
```

Click the **Sign** preview button in the auth section to see the resulting token without firing a request -- handy for sanity checks and for grabbing a token to paste into another tool. The preview turns red and shows the parser/signing error if anything is wrong.

The JWT is **regenerated for every request**, so `${now}` and `${now+N}` are always fresh. There's no caching -- each call signs a new token with the current timestamp.

## OAuth 2.0 (client_credentials)

Performs the standard OAuth 2.0 client_credentials grant against your token endpoint, caches the resulting access token, and auto-refreshes it when it's close to expiry.

Fields:

| Field | Purpose |
|-------|---------|
| **Token URL** | Your OAuth 2.0 token endpoint (`https://login.example.com/oauth2/token`) |
| **Client ID** | OAuth client identifier |
| **Client Secret** | OAuth client secret (supports `${var}`) |
| **Scope** | Optional, space-separated list of scopes |
| **Audience** | Optional, audience claim (some IdPs require it) |

Bowire routes the actual token request through a server-side proxy at `POST /bowire/api/auth/oauth-token`. This avoids browser CORS restrictions -- most token endpoints don't allow cross-origin requests, so going through the Bowire server is the only reliable approach.

The browser keeps an in-memory cache keyed by `(tokenUrl, clientId, scope, audience)`. As long as the cached token is more than ~60 seconds away from expiry, no network call is made. When the token is missing or near expiry, Bowire transparently fetches a new one before sending your request.

Click the **Fetch token** preview button in the auth section to test your config -- it bypasses the cache, hits the token endpoint, and shows the resulting access token (or the error). Useful for verifying credentials before you start firing real calls.

The cache is **in-memory only** -- it's cleared on browser refresh and whenever you change any of the OAuth config fields. Tokens are never persisted to disk.

### OAuth proxy endpoint

The proxy is a thin pass-through. Bowire does **not** inspect or modify the response body -- whatever the token endpoint returns is what the browser sees. Bowire relies on the standard `access_token` and `expires_in` fields.

| Endpoint | Method | Body |
|----------|--------|------|
| `/bowire/api/auth/oauth-token` | `POST` | `{ tokenUrl, clientId, clientSecret, scope, audience }` |

The proxy uses a 15-second timeout and returns HTTP 502 with an error message if the token endpoint is unreachable, returns non-2xx, or sends back invalid JSON.

## OAuth 2.0 (authorization_code + PKCE)

Full browser-redirect OAuth 2.0 flow with PKCE. Use this when you need to authenticate as a real user rather than as a client (the API expects an end-user identity, not a service account). The flow is:

1. You click **Authorize** in the auth panel.
2. Bowire opens a popup window pointing at the IdP's authorization URL with PKCE `code_challenge` (S256), a CSRF `state`, and the Bowire-served `redirect_uri`.
3. You log in / consent at the IdP.
4. The IdP redirects the popup to Bowire's callback page (`/{prefix}/oauth-callback`) with `?code=...&state=...`.
5. The callback page posts the code back to the main Bowire window via `postMessage` (origin-checked) and closes itself.
6. Bowire trades the code for tokens via the server-side proxy at `/api/auth/oauth-code-exchange`.
7. Tokens are cached in memory keyed by environment + config hash. Subsequent requests pull the access token straight from the cache.
8. When the token is within 60 seconds of expiry, Bowire calls `/api/auth/oauth-refresh` automatically — no user interaction needed.

Fields:

| Field | Purpose |
|-------|---------|
| **Authorization URL** | IdP's `/authorize` endpoint |
| **Token URL** | IdP's `/token` endpoint |
| **Client ID** | OAuth client identifier |
| **Client Secret** | Optional. Public clients (mobile / SPA) leave this empty — PKCE is enabled for them automatically. Confidential clients fill it in. |
| **Scope** | Space-separated list (e.g. `openid profile email`) |

The **Redirect URI** is shown read-only in the auth panel — copy it and register it verbatim with your IdP. The exact value depends on Bowire's port and the configured prefix, e.g. `http://localhost:5080/bowire/oauth-callback`.

Status panel shows the current authorization state with a TTL countdown ("Authorized — token expires in 1842s"), an **Authorize** / **Re-authorize** button, and a **Sign out** button that clears the cache for the current environment.

**Tokens are stored in memory only by design.** XSS / browser extensions can't pluck a refresh_token out of localStorage at the cost of having to re-authorize after a page reload, which is the right tradeoff for a developer tool. The token cache is also wiped whenever you edit any of the OAuth config fields.

### Authorization-code proxy endpoints

| Endpoint | Method | Body |
|----------|--------|------|
| `/bowire/api/auth/oauth-code-exchange` | `POST` | `{ tokenUrl, code, redirectUri, clientId, clientSecret?, codeVerifier }` |
| `/bowire/api/auth/oauth-refresh` | `POST` | `{ tokenUrl, refreshToken, clientId, clientSecret?, scope }` |
| `/{prefix}/oauth-callback` | `GET` | Static HTML callback page that postMessages the code back to the opener |

Both POST endpoints follow the same proxy contract as the client_credentials helper above — Bowire doesn't inspect the response body, just pipes it back to the browser, with HTTP 502 + error message on network / parsing failures.

## Custom Token Endpoint

For internal services where the auth endpoint isn't strictly OAuth — common pattern is a simple `POST /login` with a JSON body that returns `{ token, expiresIn }`. Bowire lets you describe that endpoint declaratively:

| Field | Purpose |
|-------|---------|
| **Token URL** | Your token / login endpoint |
| **Method** | `GET`, `POST`, or `PUT` |
| **Content-Type** | Defaults to `application/json` — change for form-encoded endpoints |
| **Request Body** | The body to send. Supports `${var}` substitution so you can store credentials as environment variables. |
| **Request Headers (JSON)** | Optional. JSON object of additional headers to send with the token request, e.g. `{ "X-Api-Key": "${apiKey}" }` |
| **Token JSON path** | Dotted path into the response JSON to find the token (`token`, `data.access_token`, `auth.tokens.0`, ...) |
| **Expiry JSON path** | Optional. Dotted path to the TTL in seconds. Defaults to 1 hour when not set. |
| **Authorization prefix** | What to prepend in front of the token in the `Authorization` header. Defaults to `"Bearer "` (with trailing space). Set to empty string for tokens that aren't bearer-style. |

The `readJsonPath` helper supports array index segments — `auth.tokens.0` walks into the first element of the `tokens` array.

Like OAuth client_credentials, the token is cached in memory and auto-refreshed ~60 seconds before expiry. The token request goes through a server-side proxy at `/bowire/api/auth/custom-token` to avoid CORS. A **Fetch token** preview button in the auth panel lets you validate the config without firing a real API call.

## AWS Signature v4

Signs the entire HTTP request with the AWS Sig v4 algorithm. **REST-only** — gRPC, SignalR, and other non-HTTP plugins ignore this auth type. Designed for AWS API Gateway, S3, DynamoDB, and any other AWS service that requires Sig v4.

| Field | Purpose |
|-------|---------|
| **Access Key ID** | AKIA... (supports `${var}`) |
| **Secret Access Key** | Secret access key (supports `${var}`) |
| **Region** | e.g. `us-east-1` |
| **Service** | e.g. `execute-api`, `s3`, `dynamodb` |
| **Session Token** | Optional. STS session token for temporary credentials. |

The signing happens in the REST plugin's `RestInvoker` right before the wire write — the signature includes a SHA-256 hash of the request body, so it has to happen after the body is built. The JS auth helper marks the credentials with a magic `__bowireAwsSigV4__` metadata key; `RestInvoker` strips it before forwarding the rest as HTTP headers and calls `AwsSigV4Signer.SignAsync` to add the `Authorization`, `X-Amz-Date`, `X-Amz-Content-Sha256`, and optional `X-Amz-Security-Token` headers in place.

The signer is hand-rolled (no AWSSDK dependency) and uses `System.Security.Cryptography.SHA256` + `HMACSHA256`. It honours the canonical-request → string-to-sign → derived-signing-key → HMAC chain from the AWS spec, with the empty-body fast path using the well-known SHA-256 of the empty string.

## Where it's applied

The auth helper runs just before the request fires, on top of any metadata you already added in the **Metadata** tab. It applies to:

- **Unary calls** (gRPC, REST, GraphQL queries / mutations, SignalR Hub methods, MCP tools)
- **Server-streaming calls** (gRPC server streaming, SSE subscriptions, GraphQL subscriptions)
- **Client-streaming and duplex channels** (gRPC, SignalR streams, WebSocket frames)

For gRPC, headers go through `CallOptions.Headers`. For SignalR, they go through `HubConnectionBuilder.WithUrl(... headers ...)` so they apply to the WebSocket handshake. For WebSocket / GraphQL-transport-ws / MCP, headers ride along on the upgrade or POST request.

## Manual override

User-supplied metadata always wins on key collisions, case-insensitive. If you add `Authorization: Bearer custom-token` directly in the **Metadata** tab, the auth helper will leave it alone -- your manual entry takes precedence.

This makes it easy to debug a single request without disabling the env-wide auth.

## Storage

Auth lives on the environment document at `~/.bowire/environments.json`:

```json
{
  "environments": [
    {
      "id": "env_prod01",
      "name": "Prod",
      "vars": { "apiKey": "sk-..." },
      "auth": {
        "type": "bearer",
        "token": "${apiKey}"
      }
    }
  ]
}
```

Like the rest of the env document this is human-readable and editable -- handy for seeding environments from CI scripts.

## mTLS

mTLS is the only auth helper that doesn't add an HTTP header — the client certificate is bound to the underlying transport. Bowire ships **one** mTLS implementation (`Kuestenlogik.Bowire.Auth.MtlsConfig`) that the protocol plugins consume:

| Plugin | How |
|--------|-----|
| **REST** | Per-call `HttpClientHandler` carries the client cert + optional CA bundle |
| **gRPC** | `SocketsHttpHandler.SslOptions.ClientCertificates`, plus a custom `RemoteCertificateValidationCallback` when a CA is supplied |
| **WebSocket** | `ClientWebSocket.Options.ClientCertificates` |
| **SignalR** | Same handler the WebSocket transport uses, propagated through `HubConnectionBuilder` |
| **Kafka** | `SecurityProtocol = Ssl` + `SslCertificatePem` / `SslKeyPem` / `SslCaPem` (no temp files — Confluent.Kafka reads PEM strings directly) |

### Form fields

| Field | Required | Notes |
|-------|----------|-------|
| Client certificate (PEM) | Yes | `-----BEGIN CERTIFICATE-----` …; pasted or uploaded |
| Private key (PEM) | Yes | `PRIVATE KEY` or `RSA PRIVATE KEY`; encrypted keys are decrypted client-side using the passphrase below |
| Passphrase | No | Decrypt-only, never sent to the server |
| CA certificate (PEM) | No | When provided, Bowire pins the chain to this root instead of the OS trust store |
| Allow self-signed | No | When checked, the chain validation callback returns `true` for any path — use only for dev / pinned-cert scenarios |

### Wire shape

The JS layer ships PEM material to the plugin invokers via a magic metadata marker — `__bowireMtls__` — carrying a JSON payload:

```json
{
  "certificate": "-----BEGIN CERTIFICATE-----\n…",
  "privateKey":  "-----BEGIN PRIVATE KEY-----\n…",
  "passphrase":  "optional",
  "caCertificate": "-----BEGIN CERTIFICATE-----\n…",
  "allowSelfSigned": false
}
```

Plugins call `MtlsConfig.TryParseFromMetadata(...)`, then `MtlsConfig.StripMarker(...)` on the metadata before forwarding it as protocol-level headers (gRPC `Metadata`, HTTP request headers, …) so the secret payload never reaches the wire as a regular header.

### Cookie jar (REST)

When the Cookie-jar helper is on, the REST plugin attaches a per-environment `CookieContainer` to the request handler. Cookies the server sets via `Set-Cookie` are stored, then replayed on the next call against the same origin. Use it to model real session flows:

1. `POST /login` with credentials in the body → server returns `Set-Cookie: session=abc`
2. `GET /me` against the same env → Bowire automatically attaches `Cookie: session=abc`

Memory only by design (process restart wipes the jar), so a stale token can't haunt the next workbench session. The "Clear cookies" button on the env-auth page wipes the container ahead of the next call.

mTLS and cookie-jar mode compose: when both are active, the same `HttpClientHandler` carries the client cert and the per-env `CookieContainer`.

## Tips

- **Never hardcode secrets** in the auth fields directly. Store them as variables so the auth section stays git-friendly when exported.
- **One env per stage** -- Dev / Staging / Prod each with their own credentials. Switch with the dropdown.
- **Test the override** -- if a request misbehaves, drop a manual `Authorization` row in the metadata tab to bypass the helper for that single call.
- **Lock indicator** -- if you don't see the lock badge next to the env selector, no auth is active for the current environment.
