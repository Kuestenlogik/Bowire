// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0
//
// #101 — the VS Code extension's logic, tested without VS Code.
//
// extension.js keeps only what genuinely needs the vscode API; everything
// below lives in lib/workbench.js precisely so it can be pinned here. The
// parts most worth pinning are the ones that fail quietly in an editor:
// a CLI that isn't found, a startup banner that isn't recognised (leaving the
// panel pointed at a dead port), and a webview CSP that would silently block
// the iframe.

import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
import { fileURLToPath } from 'node:url';
import { dirname, resolve } from 'node:path';

const __dirname = dirname(fileURLToPath(import.meta.url));
const require = createRequire(import.meta.url);
const workbench = require(resolve(__dirname, '../../../extensions/bowire-vscode/lib/workbench.js'));

describe('findCli', () => {
    it('returns the first path the probe reports', () => {
        // `where` on Windows can list several hits; the first one wins,
        // which is the one that would actually run.
        const found = workbench.findCli(() => ({
            status: 0,
            stdout: 'C:\\tools\\bowire.exe\r\nC:\\other\\bowire.exe\r\n',
        }));
        assert.equal(found, 'C:\\tools\\bowire.exe');
    });

    it('returns null when the probe finds nothing', () => {
        assert.equal(workbench.findCli(() => ({ status: 1, stdout: '' })), null);
    });

    it('returns null rather than throwing when the probe itself fails', () => {
        // No `which` on a stripped container, for instance. The extension
        // must degrade into its install prompt, not crash on activation.
        assert.equal(workbench.findCli(() => { throw new Error('no such tool'); }), null);
    });

    it('ignores blank lines in the probe output', () => {
        assert.equal(
            workbench.findCli(() => ({ status: 0, stdout: '\n\n/usr/local/bin/bowire\n' })),
            '/usr/local/bin/bowire');
    });
});

describe('buildArgs', () => {
    it('passes the port and seeds a workspace', () => {
        assert.deepEqual(workbench.buildArgs(5123), ['--port', '5123', '--auto-create-initial-workspace']);
    });
});

describe('parseListeningUrl', () => {
    it('reads the URL out of the startup banner', () => {
        assert.equal(
            workbench.parseListeningUrl('  Bowire is running at:  http://localhost:5080/'),
            'http://localhost:5080');
    });

    it('accepts the loopback address too', () => {
        assert.equal(
            workbench.parseListeningUrl('Now listening on: http://127.0.0.1:5099'),
            'http://127.0.0.1:5099');
    });

    it('reports the port the CLI actually bound, not the one we asked for', () => {
        // The whole reason for parsing rather than assuming: a fallback bind
        // would otherwise leave the panel pointed at a port nobody serves.
        assert.equal(workbench.parseListeningUrl('Now listening on: http://localhost:5555'), 'http://localhost:5555');
    });

    it('returns null while the process is still starting', () => {
        assert.equal(workbench.parseListeningUrl('Restoring plugins…'), null);
        assert.equal(workbench.parseListeningUrl(''), null);
        assert.equal(workbench.parseListeningUrl(undefined), null);
    });

    it('does not mistake an unrelated remote URL for the local workbench', () => {
        assert.equal(workbench.parseListeningUrl('Discovering https://petstore3.swagger.io/api'), null);
    });
});

describe('portForWorkspace', () => {
    it('is stable for the same workspace', () => {
        const a = workbench.portForWorkspace('/home/dev/orders-api');
        const b = workbench.portForWorkspace('/home/dev/orders-api');
        assert.equal(a, b);
    });

    it('differs between workspaces so two windows do not collide', () => {
        assert.notEqual(
            workbench.portForWorkspace('/home/dev/orders-api'),
            workbench.portForWorkspace('/home/dev/billing-api'));
    });

    it('stays clear of the CLI default so a hand-started workbench keeps 5080', () => {
        for (const p of ['/a', '/b', 'C:\\work\\x', '']) {
            const port = workbench.portForWorkspace(p);
            assert.ok(port >= 5099 && port < 6099, `${port} out of range`);
            assert.notEqual(port, 5080);
        }
    });
});

describe('buildWebviewHtml', () => {
    it('frames the workbench and allows exactly that origin', () => {
        const html = workbench.buildWebviewHtml('http://localhost:5123');
        assert.match(html, /<iframe src="http:\/\/localhost:5123"/);
        // A CSP that forgot frame-src would silently render an empty panel.
        assert.match(html, /frame-src http:\/\/localhost:5123/);
        assert.match(html, /default-src 'none'/);
    });

    it('strips anything beyond host and port instead of escaping it', () => {
        // The URL is rebuilt from parsed components, so a path, a query or
        // markup cannot reach the document at all. Escaping quotes alone left
        // <script> text sitting inside an attribute — inert, but inert for
        // one reason only.
        const html = workbench.buildWebviewHtml('http://localhost:1/"><script>x()</script>');
        assert.doesNotMatch(html, /<script>x\(\)<\/script>/);
        assert.match(html, /<iframe src="http:\/\/localhost:1"/);
    });

    it('refuses to frame anything that is not a local workbench', () => {
        for (const bad of ['https://example.com:5080', 'file:///etc/passwd', 'not a url', '']) {
            assert.throws(() => workbench.buildWebviewHtml(bad), /Refusing to frame/);
        }
    });
});

describe('normaliseWorkbenchUrl', () => {
    it('keeps a local URL down to scheme, host and port', () => {
        assert.equal(workbench.normaliseWorkbenchUrl('http://localhost:5080/x?y=1#z'), 'http://localhost:5080');
        assert.equal(workbench.normaliseWorkbenchUrl('http://127.0.0.1:5099'), 'http://127.0.0.1:5099');
        assert.equal(workbench.normaliseWorkbenchUrl('http://localhost'), 'http://localhost');
    });

    it('rejects remote hosts and non-http schemes', () => {
        // The panel frames a process this extension started; anything else
        // reaching that iframe would be a different program's UI.
        assert.equal(workbench.normaliseWorkbenchUrl('http://evil.example.com'), null);
        assert.equal(workbench.normaliseWorkbenchUrl('file:///etc/passwd'), null);
        assert.equal(workbench.normaliseWorkbenchUrl('javascript:alert(1)'), null);
        assert.equal(workbench.normaliseWorkbenchUrl('garbage'), null);
    });
});

describe('marketplace icon', () => {
    it('reuses the one canonical logo and meets the marketplace rules', async () => {
        // The icon is copied in at package time from images/bowire_logo_small.png
        // — the same file NuGet packs — rather than committed a second time.
        // If that image is moved or renamed, this fails here instead of
        // publishing a blank marketplace tile nobody notices.
        const mod = await import(
            new URL('../../../extensions/bowire-vscode/scripts/copy-icon.mjs', import.meta.url).href);

        const size = mod.pngSize(mod.SOURCE);
        assert.ok(size, `${mod.SOURCE} is missing or not a PNG`);
        assert.equal(size.width, size.height, 'marketplace art must be square');
        assert.ok(size.width >= 128, `marketplace minimum is 128x128; got ${size.width}`);
    });

    it('package.json points at the copied icon', () => {
        const pkg = require(resolve(__dirname, '../../../extensions/bowire-vscode/package.json'));
        assert.equal(pkg.icon, 'icon.png');
        assert.match(pkg.scripts['vscode:prepublish'], /copy-icon/);
    });
});

describe('missingCliMessage', () => {
    it('names every install route that works today', () => {
        const msg = workbench.missingCliMessage();
        // The extension requires the CLI rather than bundling ~100 MB per
        // platform, so this message is the whole onboarding path.
        assert.match(msg, /winget install/);
        assert.match(msg, /choco install/);
        assert.match(msg, /dotnet tool install/);
    });
});
