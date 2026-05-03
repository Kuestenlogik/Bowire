    // ---- Auth Helpers ----
    // Derive headers from the active environment's auth config and merge them
    // into the user-supplied metadata. User metadata wins on key collisions
    // (case-insensitive) so a manual Authorization row always overrides the
    // helper. All auth field values pass through ${var} substitution so users
    // can store secrets as variables and reference them.

    function utf8ToBase64(str) {
        // btoa() chokes on non-ASCII; route through encodeURIComponent first.
        return btoa(unescape(encodeURIComponent(str)));
    }

    function metadataHasKey(meta, key) {
        if (!meta) return false;
        var lower = key.toLowerCase();
        for (var k in meta) {
            if (Object.prototype.hasOwnProperty.call(meta, k) && k.toLowerCase() === lower) return true;
        }
        return false;
    }

    async function applyAuth(metadata) {
        var env = getActiveEnv();
        var auth = env && env.auth;
        var out = Object.assign({}, metadata || {});

        // Cookie-jar toggle is independent of the auth type — works
        // alongside (or instead of) Bearer / Basic / mTLS / etc. The
        // RestInvoker reads the marker, hands the per-env CookieContainer
        // to a fresh HttpClientHandler so Set-Cookie persists across calls.
        if (env && auth && auth.persistCookies && env.id) {
            out['__bowireCookieEnv__'] = env.id;
        }

        if (!auth || !auth.type || auth.type === 'none') return out;

        if (auth.type === 'bearer') {
            var token = substituteVars(auth.token || '');
            if (token && !metadataHasKey(out, 'Authorization')) {
                out['Authorization'] = 'Bearer ' + token;
            }
        } else if (auth.type === 'basic') {
            var user = substituteVars(auth.username || '');
            var pass = substituteVars(auth.password || '');
            if (user && !metadataHasKey(out, 'Authorization')) {
                out['Authorization'] = 'Basic ' + utf8ToBase64(user + ':' + pass);
            }
        } else if (auth.type === 'apikey') {
            var keyName = substituteVars(auth.key || '').trim();
            var keyValue = substituteVars(auth.value || '');
            if (keyName) {
                var loc = (auth.location || 'header');
                if (loc === 'query') {
                    // Magic-prefixed key — the API endpoint strips the prefix
                    // and appends the value to the request URL as a query
                    // parameter instead of forwarding it as an HTTP header.
                    out['__bowireQuery__' + keyName] = keyValue;
                } else if (!metadataHasKey(out, keyName)) {
                    out[keyName] = keyValue;
                }
            }
        } else if (auth.type === 'jwt') {
            try {
                var jwt = await buildJwt(auth);
                if (jwt && !metadataHasKey(out, 'Authorization')) {
                    out['Authorization'] = 'Bearer ' + jwt;
                }
            } catch (e) {
                toast('JWT signing failed: ' + e.message, 'error');
            }
        } else if (auth.type === 'oauth2_cc') {
            try {
                var token2 = await fetchOauthClientCredentialsToken(auth);
                if (token2 && !metadataHasKey(out, 'Authorization')) {
                    out['Authorization'] = 'Bearer ' + token2;
                }
            } catch (e) {
                toast('OAuth token fetch failed: ' + e.message, 'error');
            }
        } else if (auth.type === 'custom_token') {
            try {
                var token3 = await fetchCustomToken(auth);
                if (token3 && !metadataHasKey(out, 'Authorization')) {
                    var prefix = (auth.tokenPrefix == null ? 'Bearer ' : auth.tokenPrefix);
                    out['Authorization'] = prefix + token3;
                }
            } catch (e) {
                toast('Custom token fetch failed: ' + e.message, 'error');
            }
        } else if (auth.type === 'oauth2_ac') {
            try {
                var envId = getActiveEnvId();
                var token4 = await ensureOauth2AcAccessToken(envId, auth);
                if (token4 && !metadataHasKey(out, 'Authorization')) {
                    out['Authorization'] = 'Bearer ' + token4;
                }
            } catch (e) {
                toast('OAuth (auth code) token unavailable: ' + e.message, 'error');
            }
        } else if (auth.type === 'aws_sigv4') {
            // AWS Sig v4 has to be applied AFTER the body is built because
            // the signature includes the body hash. We can't do that here in
            // the JS layer because the actual HTTP request is built inside
            // the REST plugin's invoker. So we ship the credentials inline
            // as a magic-prefixed metadata entry; the invoker recognises
            // the marker, pulls the JSON out, and signs the request just
            // before it goes on the wire. Other plugins ignore the marker.
            var awsCfg = {
                accessKey: substituteVars(auth.accessKey || ''),
                secretKey: substituteVars(auth.secretKey || ''),
                region: substituteVars(auth.region || 'us-east-1'),
                service: substituteVars(auth.service || ''),
                sessionToken: auth.sessionToken ? substituteVars(auth.sessionToken) : null
            };
            if (awsCfg.accessKey && awsCfg.secretKey && awsCfg.service) {
                out['__bowireAwsSigV4__'] = JSON.stringify(awsCfg);
            }
        } else if (auth.type === 'mtls') {
            // mTLS lives below HTTP — cert + key affect the TLS handshake,
            // not request headers. We ship the PEM material inline as a
            // magic-prefixed metadata entry; the invoker pulls it out
            // before the request goes on the wire and uses it to build a
            // per-request HttpClient with X509Certificate2 client auth.
            // Other plugins (grpc / websocket / signalr) honour the same
            // marker once they're wired up — see the Bowire roadmap.
            var mtlsCfg = {
                certificate: substituteVars(auth.certificate || ''),
                privateKey: substituteVars(auth.privateKey || ''),
                passphrase: auth.passphrase ? substituteVars(auth.passphrase) : '',
                caCertificate: auth.caCertificate ? substituteVars(auth.caCertificate) : '',
                allowSelfSigned: !!auth.allowSelfSigned
            };
            if (mtlsCfg.certificate && mtlsCfg.privateKey) {
                out['__bowireMtls__'] = JSON.stringify(mtlsCfg);
            }
        }

        return out;
    }

    function activeEnvHasAuth() {
        var env = getActiveEnv();
        return !!(env && env.auth && env.auth.type && env.auth.type !== 'none');
    }

    // ---- JWT Builder ----
    // Build a signed JWT entirely client-side using Web Crypto. Header and
    // payload are JSON templates and pass through ${var} substitution before
    // signing, so dynamic claims like ${now}, ${now+3600}, ${uuid} just work.
    // Supported algorithms: HS256, HS384, HS512.

    function base64UrlEncodeString(str) {
        return base64UrlEncodeBytes(new TextEncoder().encode(str));
    }

    function base64UrlEncodeBytes(bytes) {
        var b64 = btoa(String.fromCharCode.apply(null, new Uint8Array(bytes)));
        return b64.replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
    }

    // Symmetric (HMAC) → hash name only.
    // Asymmetric (RSA / ECDSA) → SubtleCrypto algorithm descriptor + the
    // expected raw signature size for ECDSA (used to fix the fixed-width
    // JWT signature format).
    var JWT_ALGORITHMS = {
        HS256: { kind: 'hmac', hash: 'SHA-256' },
        HS384: { kind: 'hmac', hash: 'SHA-384' },
        HS512: { kind: 'hmac', hash: 'SHA-512' },
        RS256: { kind: 'rsa',  hash: 'SHA-256' },
        RS384: { kind: 'rsa',  hash: 'SHA-384' },
        RS512: { kind: 'rsa',  hash: 'SHA-512' },
        ES256: { kind: 'ecdsa', hash: 'SHA-256', namedCurve: 'P-256', sigSize: 64 },
        ES384: { kind: 'ecdsa', hash: 'SHA-384', namedCurve: 'P-384', sigSize: 96 },
        ES512: { kind: 'ecdsa', hash: 'SHA-512', namedCurve: 'P-521', sigSize: 132 }
    };

    function isAsymmetricJwtAlg(alg) {
        var spec = JWT_ALGORITHMS[(alg || '').toUpperCase()];
        return !!(spec && (spec.kind === 'rsa' || spec.kind === 'ecdsa'));
    }

    /**
     * Strip the PEM header/footer (-----BEGIN PRIVATE KEY----- ...) and the
     * surrounding whitespace, then base64-decode the body to a Uint8Array
     * of PKCS8 bytes ready for crypto.subtle.importKey('pkcs8', ...).
     * Accepts both PKCS#8 ("BEGIN PRIVATE KEY") and the OpenSSL legacy
     * "BEGIN RSA/EC PRIVATE KEY" markers (we don't reject either, the
     * importKey call will fail clearly if the wrong format is supplied).
     */
    function pemToPkcs8Bytes(pem) {
        var trimmed = pem
            .replace(/-----BEGIN [A-Z ]+-----/g, '')
            .replace(/-----END [A-Z ]+-----/g, '')
            .replace(/\s+/g, '');
        if (!trimmed) throw new Error('PEM body is empty');
        var bin;
        try { bin = atob(trimmed); }
        catch (e) { throw new Error('PEM is not valid base64'); }
        var bytes = new Uint8Array(bin.length);
        for (var i = 0; i < bin.length; i++) bytes[i] = bin.charCodeAt(i);
        return bytes;
    }

    async function buildJwt(auth) {
        var alg = (auth.algorithm || 'HS256').toUpperCase();
        var spec = JWT_ALGORITHMS[alg];
        if (!spec) throw new Error('Unsupported algorithm: ' + alg);
        if (typeof crypto === 'undefined' || !crypto.subtle) {
            throw new Error('Web Crypto API not available');
        }

        // Header — auto-fill alg/typ unless the user already set them
        var headerObj;
        try {
            headerObj = auth.header ? JSON.parse(substituteVars(auth.header)) : {};
        } catch (e) {
            throw new Error('Invalid JWT header JSON: ' + e.message);
        }
        if (!headerObj.alg) headerObj.alg = alg;
        if (!headerObj.typ) headerObj.typ = 'JWT';

        // Payload — substitute first so ${now}, ${now+3600} resolve before parse
        var payloadObj;
        try {
            payloadObj = auth.payload ? JSON.parse(substituteVars(auth.payload)) : {};
        } catch (e) {
            throw new Error('Invalid JWT payload JSON: ' + e.message);
        }

        var secret = substituteVars(auth.secret || '');
        if (!secret) throw new Error('JWT secret is empty');

        var headerB64 = base64UrlEncodeString(JSON.stringify(headerObj));
        var payloadB64 = base64UrlEncodeString(JSON.stringify(payloadObj));
        var signingInput = headerB64 + '.' + payloadB64;
        var signingBytes = new TextEncoder().encode(signingInput);

        var sigBytes;

        if (spec.kind === 'hmac') {
            var hmacKey = await crypto.subtle.importKey(
                'raw',
                new TextEncoder().encode(secret),
                { name: 'HMAC', hash: spec.hash },
                false,
                ['sign']
            );
            sigBytes = await crypto.subtle.sign('HMAC', hmacKey, signingBytes);
        }
        else if (spec.kind === 'rsa') {
            // Secret is a PEM private key (PKCS#8). We don't try to convert
            // OpenSSL traditional "BEGIN RSA PRIVATE KEY" — users must
            // export as PKCS#8 (`openssl pkcs8 -topk8 -nocrypt`).
            var rsaPkcs8 = pemToPkcs8Bytes(secret);
            var rsaKey = await crypto.subtle.importKey(
                'pkcs8',
                rsaPkcs8,
                { name: 'RSASSA-PKCS1-v1_5', hash: spec.hash },
                false,
                ['sign']
            );
            sigBytes = await crypto.subtle.sign('RSASSA-PKCS1-v1_5', rsaKey, signingBytes);
        }
        else { // ecdsa
            var ecPkcs8 = pemToPkcs8Bytes(secret);
            var ecKey = await crypto.subtle.importKey(
                'pkcs8',
                ecPkcs8,
                { name: 'ECDSA', namedCurve: spec.namedCurve },
                false,
                ['sign']
            );
            // Web Crypto already returns the JWS / IEEE-P1363 fixed-width
            // signature for ECDSA — no DER unwrapping needed. Just verify
            // the size matches what the JWS spec expects for this curve.
            sigBytes = await crypto.subtle.sign(
                { name: 'ECDSA', hash: spec.hash },
                ecKey,
                signingBytes
            );
            if (sigBytes.byteLength !== spec.sigSize) {
                throw new Error('Unexpected ECDSA signature size: '
                    + sigBytes.byteLength + ' bytes (expected ' + spec.sigSize + ')');
            }
        }

        var sigB64 = base64UrlEncodeBytes(sigBytes);
        return signingInput + '.' + sigB64;
    }

    // ---- OAuth 2.0 client_credentials (token cache + auto-refresh) ----
    // Tokens are fetched via a server-side proxy at /api/auth/oauth-token to
    // avoid CORS. Cached in memory keyed by config hash. Auto-refresh kicks in
    // ~60s before expiry.

    var oauthTokenCache = {}; // { cacheKey: { accessToken, expiresAt } }
    var REFRESH_GRACE_SECONDS = 60;

    function oauthCacheKey(auth) {
        return [
            substituteVars(auth.tokenUrl || ''),
            substituteVars(auth.clientId || ''),
            substituteVars(auth.scope || ''),
            substituteVars(auth.audience || '')
        ].join('|');
    }

    async function fetchOauthClientCredentialsToken(auth) {
        var key = oauthCacheKey(auth);
        var cached = oauthTokenCache[key];
        var nowSec = Math.floor(Date.now() / 1000);
        if (cached && cached.expiresAt - REFRESH_GRACE_SECONDS > nowSec) {
            return cached.accessToken;
        }

        var body = {
            tokenUrl: substituteVars(auth.tokenUrl || ''),
            clientId: substituteVars(auth.clientId || ''),
            clientSecret: substituteVars(auth.clientSecret || ''),
            scope: substituteVars(auth.scope || ''),
            audience: substituteVars(auth.audience || '')
        };
        if (!body.tokenUrl) throw new Error('Token URL is empty');
        if (!body.clientId) throw new Error('Client ID is empty');

        var resp = await fetch(config.prefix + '/api/auth/oauth-token', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(body)
        });
        var result;
        try { result = await resp.json(); }
        catch { throw new Error('Token endpoint returned invalid JSON (HTTP ' + resp.status + ')'); }

        if (!resp.ok || result.error) {
            throw new Error(result.error || ('HTTP ' + resp.status));
        }
        if (!result.access_token) throw new Error('Token response missing access_token');

        var ttl = parseInt(result.expires_in, 10);
        if (!ttl || ttl < 0) ttl = 3600;
        oauthTokenCache[key] = {
            accessToken: result.access_token,
            expiresAt: nowSec + ttl
        };
        return result.access_token;
    }

    function clearOauthTokenCache() {
        oauthTokenCache = {};
    }

    // ---- OAuth 2.0 authorization_code (PKCE flow) ----
    // Stores tokens per-environment in memory only — refreshing the page
    // forces a re-authorize. The tradeoff is that XSS or browser extensions
    // can't pluck a refresh_token out of localStorage. Cache key includes
    // both the env id and the config hash so switching env or editing the
    // config invalidates the stored token cleanly.
    var oauth2AcTokenCache = {}; // { cacheKey: { accessToken, refreshToken, expiresAt } }

    function oauth2AcCacheKey(envId, auth) {
        return [
            envId || '',
            substituteVars(auth.authorizationUrl || ''),
            substituteVars(auth.tokenUrl || ''),
            substituteVars(auth.clientId || ''),
            substituteVars(auth.scope || '')
        ].join('|');
    }

    function clearOauth2AcTokenCacheForEnv(envId) {
        var prefix = envId + '|';
        for (var k in oauth2AcTokenCache) {
            if (Object.prototype.hasOwnProperty.call(oauth2AcTokenCache, k) && k.startsWith(prefix)) {
                delete oauth2AcTokenCache[k];
            }
        }
    }

    /** Generate a cryptographically random string for PKCE / state. */
    function randomUrlSafe(byteLen) {
        var arr = new Uint8Array(byteLen);
        crypto.getRandomValues(arr);
        return base64UrlEncodeBytes(arr);
    }

    /** PKCE code_challenge = base64url(SHA-256(code_verifier)). */
    async function pkceChallenge(verifier) {
        var hash = await crypto.subtle.digest('SHA-256', new TextEncoder().encode(verifier));
        return base64UrlEncodeBytes(new Uint8Array(hash));
    }

    /** Build the redirect URI that Bowire serves the OAuth callback page on. */
    function oauthRedirectUri() {
        return window.location.origin + config.prefix + '/oauth-callback';
    }

    /**
     * Open the IdP authorization endpoint in a popup, wait for the
     * callback page to postMessage the code back, exchange the code for
     * tokens via the server-side proxy, and store the result in the
     * per-env token cache. Returns when the cache is populated; throws
     * a string error message on user-cancel / IdP error / network error.
     */
    async function authorizeOauth2Ac(envId, auth) {
        var authUrl = substituteVars(auth.authorizationUrl || '');
        var tokenUrl = substituteVars(auth.tokenUrl || '');
        var clientId = substituteVars(auth.clientId || '');
        var scope = substituteVars(auth.scope || '');
        var clientSecret = auth.clientSecret ? substituteVars(auth.clientSecret) : '';

        if (!authUrl) throw new Error('Authorization URL is empty');
        if (!tokenUrl) throw new Error('Token URL is empty');
        if (!clientId) throw new Error('Client ID is empty');

        var redirectUri = oauthRedirectUri();
        var state = randomUrlSafe(16);
        var verifier = randomUrlSafe(32);
        var challenge = await pkceChallenge(verifier);

        var params = new URLSearchParams({
            response_type: 'code',
            client_id: clientId,
            redirect_uri: redirectUri,
            state: state,
            code_challenge: challenge,
            code_challenge_method: 'S256'
        });
        if (scope) params.append('scope', scope);

        var fullAuthUrl = authUrl + (authUrl.indexOf('?') >= 0 ? '&' : '?') + params.toString();
        var popup = window.open(fullAuthUrl, 'bowire-oauth', 'width=520,height=640');
        if (!popup) throw new Error('Popup blocked — allow popups for this site');

        var callback = await waitForOauthCallback(popup, state);
        if (callback.error) {
            throw new Error('IdP returned error: ' + callback.error +
                (callback.errorDescription ? ' — ' + callback.errorDescription : ''));
        }
        if (!callback.code) throw new Error('Authorization callback did not include a code');

        var resp = await fetch(config.prefix + '/api/auth/oauth-code-exchange', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({
                tokenUrl: tokenUrl,
                code: callback.code,
                redirectUri: redirectUri,
                clientId: clientId,
                clientSecret: clientSecret || undefined,
                codeVerifier: verifier
            })
        });
        var result;
        try { result = await resp.json(); }
        catch { throw new Error('Token endpoint returned invalid JSON (HTTP ' + resp.status + ')'); }
        if (!resp.ok || result.error) throw new Error(result.error || ('HTTP ' + resp.status));
        if (!result.access_token) throw new Error('Token response missing access_token');

        var ttl = parseInt(result.expires_in, 10);
        if (!ttl || ttl < 0) ttl = 3600;
        var nowSec = Math.floor(Date.now() / 1000);
        var key = oauth2AcCacheKey(envId, auth);
        oauth2AcTokenCache[key] = {
            accessToken: result.access_token,
            refreshToken: result.refresh_token || null,
            expiresAt: nowSec + ttl,
            scope: result.scope || null
        };
        return oauth2AcTokenCache[key];
    }

    /**
     * Wait for a postMessage from the popup with the OAuth callback
     * payload. Polls the popup window for early-close as well so the user
     * can cancel by closing the window.
     */
    function waitForOauthCallback(popup, expectedState) {
        return new Promise(function (resolve, reject) {
            var done = false;
            function cleanup() {
                done = true;
                window.removeEventListener('message', onMessage);
                clearInterval(closeTimer);
            }
            function onMessage(event) {
                if (event.source !== popup) return;
                if (!event.data || event.data.type !== 'bowire-oauth-callback') return;
                if (event.data.state !== expectedState) {
                    cleanup();
                    reject(new Error('OAuth state mismatch — possible CSRF or stale popup'));
                    return;
                }
                cleanup();
                resolve(event.data);
            }
            window.addEventListener('message', onMessage);
            var closeTimer = setInterval(function () {
                if (done) return;
                if (popup.closed) {
                    cleanup();
                    reject(new Error('Authorization window closed before completion'));
                }
            }, 500);
        });
    }

    /** Refresh an existing oauth2_ac token via the proxy endpoint. */
    async function refreshOauth2AcToken(envId, auth, cached) {
        var resp = await fetch(config.prefix + '/api/auth/oauth-refresh', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({
                tokenUrl: substituteVars(auth.tokenUrl || ''),
                refreshToken: cached.refreshToken,
                clientId: substituteVars(auth.clientId || ''),
                clientSecret: auth.clientSecret ? substituteVars(auth.clientSecret) : undefined,
                scope: substituteVars(auth.scope || '') || undefined
            })
        });
        var result;
        try { result = await resp.json(); }
        catch { throw new Error('Refresh endpoint returned invalid JSON'); }
        if (!resp.ok || result.error) throw new Error(result.error || ('HTTP ' + resp.status));
        if (!result.access_token) throw new Error('Refresh response missing access_token');

        var ttl = parseInt(result.expires_in, 10);
        if (!ttl || ttl < 0) ttl = 3600;
        var nowSec = Math.floor(Date.now() / 1000);
        var key = oauth2AcCacheKey(envId, auth);
        oauth2AcTokenCache[key] = {
            accessToken: result.access_token,
            // Some IdPs rotate the refresh token on every refresh — others don't.
            refreshToken: result.refresh_token || cached.refreshToken,
            expiresAt: nowSec + ttl,
            scope: result.scope || cached.scope || null
        };
        return oauth2AcTokenCache[key];
    }

    async function ensureOauth2AcAccessToken(envId, auth) {
        var key = oauth2AcCacheKey(envId, auth);
        var cached = oauth2AcTokenCache[key];
        if (!cached) {
            throw new Error('Not authorized — click Authorize in the auth panel');
        }
        var nowSec = Math.floor(Date.now() / 1000);
        if (cached.expiresAt - REFRESH_GRACE_SECONDS > nowSec) {
            return cached.accessToken;
        }
        if (cached.refreshToken) {
            var fresh = await refreshOauth2AcToken(envId, auth, cached);
            return fresh.accessToken;
        }
        throw new Error('Access token expired and no refresh_token available — click Authorize again');
    }

    // ---- Custom Token Endpoint with auto-refresh ----
    // For non-OAuth token endpoints — common pattern at internal services
    // where you POST credentials to /login and get back { token, expiresIn }
    // (or any other shape). Cached identically to OAuth, with the same
    // 60-second refresh grace window. The body, method, content-type, and
    // headers are all configurable; the token is plucked from the response
    // via a dotted JSON path so { data: { auth: { jwt: "..." } } } works
    // just like a flat { token: "..." }.
    var customTokenCache = {}; // { cacheKey: { token, expiresAt } }

    function customTokenCacheKey(auth) {
        return [
            substituteVars(auth.tokenUrl || ''),
            (auth.tokenMethod || 'POST').toUpperCase(),
            substituteVars(auth.tokenBody || ''),
            substituteVars(auth.tokenJsonPath || 'token')
        ].join('|');
    }

    /**
     * Pluck a value out of an object via a dotted path.
     * "token"           → obj.token
     * "data.access"     → obj.data.access
     * "auth.tokens.0"   → obj.auth.tokens[0]
     * Returns undefined when any segment is missing.
     */
    function readJsonPath(obj, path) {
        if (obj == null || !path) return undefined;
        var segments = String(path).split('.');
        var cur = obj;
        for (var i = 0; i < segments.length; i++) {
            if (cur == null) return undefined;
            var seg = segments[i];
            // Numeric index → array access
            var asNum = Number(seg);
            cur = (Number.isInteger(asNum) && asNum >= 0 && Array.isArray(cur))
                ? cur[asNum]
                : cur[seg];
        }
        return cur;
    }

    async function fetchCustomToken(auth) {
        var key = customTokenCacheKey(auth);
        var cached = customTokenCache[key];
        var nowSec = Math.floor(Date.now() / 1000);
        if (cached && cached.expiresAt - REFRESH_GRACE_SECONDS > nowSec) {
            return cached.token;
        }

        var tokenUrl = substituteVars(auth.tokenUrl || '');
        if (!tokenUrl) throw new Error('Token URL is empty');

        // Optional headers map: parse from a JSON object in auth.tokenHeaders
        // ({ "X-Api-Key": "${apiKey}" }) so users can mix request headers
        // and ${var} substitution.
        var headers = null;
        if (auth.tokenHeaders) {
            try {
                var raw = JSON.parse(substituteVars(auth.tokenHeaders));
                if (raw && typeof raw === 'object') {
                    headers = {};
                    for (var hk in raw) {
                        if (Object.prototype.hasOwnProperty.call(raw, hk)) headers[hk] = String(raw[hk]);
                    }
                }
            } catch (e) {
                throw new Error('Invalid headers JSON: ' + e.message);
            }
        }

        var body = {
            url: tokenUrl,
            method: (auth.tokenMethod || 'POST').toUpperCase(),
            body: substituteVars(auth.tokenBody || ''),
            contentType: auth.tokenContentType || 'application/json',
            headers: headers
        };

        var resp = await fetch(config.prefix + '/api/auth/custom-token', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(body)
        });

        var responseText = await resp.text();
        var responseJson = null;
        try { responseJson = JSON.parse(responseText); } catch { /* leave null */ }

        if (!resp.ok) {
            var msg = (responseJson && responseJson.error) || ('HTTP ' + resp.status);
            throw new Error(msg);
        }

        // Pluck the token. Default path is "token" — works for the
        // common { token: "..." } shape.
        var path = substituteVars(auth.tokenJsonPath || 'token');
        var token = responseJson != null ? readJsonPath(responseJson, path) : null;
        if (token == null && responseJson == null) {
            // Plain-text response — use the body verbatim
            token = responseText.trim();
        }
        if (!token) throw new Error('Token not found at path "' + path + '" in response');

        // Optional TTL via JSON path. Default to 1 hour when not provided.
        var ttl = 3600;
        if (auth.expiresInJsonPath && responseJson != null) {
            var rawTtl = readJsonPath(responseJson, substituteVars(auth.expiresInJsonPath));
            var parsed = parseInt(rawTtl, 10);
            if (!isNaN(parsed) && parsed > 0) ttl = parsed;
        }

        customTokenCache[key] = {
            token: String(token),
            expiresAt: nowSec + ttl
        };
        return String(token);
    }

    function clearCustomTokenCache() {
        customTokenCache = {};
    }

    function genEnvId() {
        // Cryptographically strong tail so two envs created in the same
        // millisecond don't collide. Falls back to Math.random for the
        // pre-Web-Crypto edge case.
        var tail;
        if (typeof crypto !== 'undefined' && typeof crypto.getRandomValues === 'function') {
            var buf = new Uint32Array(1);
            crypto.getRandomValues(buf);
            tail = buf[0].toString(36).slice(0, 4);
        } else {
            tail = Math.random().toString(36).slice(2, 6);
        }
        return 'env_' + Date.now().toString(36) + tail;
    }

    // Predefined palette so each new environment gets a distinct color.
    var envColorPalette = ['#6366f1', '#10b981', '#f59e0b', '#ef4444', '#8b5cf6', '#3b82f6', '#ec4899', '#14b8a6'];
    var envColorIndex = 0;

    function createEnvironment(name) {
        var envs = getEnvironments();
        var color = envColorPalette[envColorIndex % envColorPalette.length];
        envColorIndex++;
        var env = { id: genEnvId(), name: name || 'New Environment', vars: {}, color: color };
        envs.push(env);
        saveEnvironments(envs);
        return env;
    }

    function deleteEnvironment(id) {
        var envs = getEnvironments().filter(function (e) { return e.id !== id; });
        saveEnvironments(envs);
        if (getActiveEnvId() === id) setActiveEnvId('');
    }

    function restoreEnvironment(envObj) {
        var envs = getEnvironments();
        envs.push(envObj);
        saveEnvironments(envs);
    }

    function updateEnvironment(id, patch) {
        var envs = getEnvironments();
        var idx = envs.findIndex(function (e) { return e.id === id; });
        if (idx < 0) return;
        envs[idx] = Object.assign({}, envs[idx], patch);
        saveEnvironments(envs);
    }

    function exportEnvironments() {
        return JSON.stringify({
            globals: getGlobalVars(),
            environments: getEnvironments(),
            activeEnvId: getActiveEnvId()
        }, null, 2);
    }

    function importEnvironments(json) {
        var data = JSON.parse(json);
        if (data.globals && typeof data.globals === 'object') saveGlobalVars(data.globals);
        if (Array.isArray(data.environments)) saveEnvironments(data.environments);
        if (typeof data.activeEnvId === 'string') setActiveEnvId(data.activeEnvId);
    }

    function timeAgo(ts) {
        const diff = Date.now() - ts;
        if (diff < 60000) return Math.round(diff / 1000) + 's ago';
        if (diff < 3600000) return Math.round(diff / 60000) + 'm ago';
        if (diff < 86400000) return Math.round(diff / 3600000) + 'h ago';
        return Math.round(diff / 86400000) + 'd ago';
    }

    function replayHistoryEntry(entry) {
        // Find and select the matching service/method
        var foundSvc = null, foundMethod = null;
        for (const svc of services) {
            for (const m of svc.methods) {
                if (svc.name === entry.service && m.name === entry.method) {
                    foundSvc = svc;
                    foundMethod = m;
                    break;
                }
            }
            if (foundSvc) break;
        }
        if (!foundSvc || !foundMethod) return;
        openTab(foundSvc, foundMethod);

        // Populate request data
        if (entry.messages && entry.messages.length > 0) {
            requestMessages = entry.messages.slice();
        } else if (entry.body && !entry.body.startsWith('(channel:')) {
            requestMessages = [entry.body];
        }

        activeRequestTab = 'body';
        requestInputMode = 'json';
        render();

        // After render, trigger execution
        requestAnimationFrame(function () {
            handleExecute();
        });
    }

    function repeatLastCall() {
        if (!selectedService || !selectedMethod) return;
        const history = getHistory();
        const last = history.find(function (h) {
            return h.service === selectedService.name && h.method === selectedMethod.name;
        });
        if (last) replayHistoryEntry(last);
    }

