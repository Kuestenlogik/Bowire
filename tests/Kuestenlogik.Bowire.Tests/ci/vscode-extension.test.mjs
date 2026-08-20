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

describe('resolveCli', () => {
    const never = () => { throw new Error('PATH must not be probed when a path is configured'); };

    it('prefers the configured path over PATH', () => {
        // Someone who names a path has a reason — a local build, a second
        // version alongside the installed one. Silently preferring PATH would
        // run a different binary than the one they asked for.
        const r = workbench.resolveCli({ configuredPath: '/opt/bowire', runner: never, exists: () => true });
        assert.deepEqual(r, { path: '/opt/bowire', source: 'setting' });
    });

    it('reports a configured path that does not exist instead of falling back', () => {
        // Falling through to PATH on a typo would look like the setting was
        // ignored — the harder of the two failures to diagnose.
        const r = workbench.resolveCli({ configuredPath: '/nope/bowire', runner: never, exists: () => false });
        assert.equal(r.path, null);
        assert.equal(r.source, 'setting');
        assert.equal(r.configured, '/nope/bowire');
    });

    it('expands ${workspaceFolder} so the setting can be committed and shared', () => {
        const r = workbench.resolveCli({
            configuredPath: '${workspaceFolder}/tools/bowire',
            variables: { workspaceFolder: '/repo' },
            runner: never,
            exists: p => p === '/repo/tools/bowire',
        });
        assert.equal(r.path, '/repo/tools/bowire');
    });

    it('searches PATH when the setting is empty or whitespace', () => {
        for (const configuredPath of ['', '   ', undefined]) {
            const r = workbench.resolveCli({
                configuredPath,
                runner: () => ({ status: 0, stdout: '/usr/bin/bowire\n' }),
            });
            assert.deepEqual(r, { path: '/usr/bin/bowire', source: 'path' });
        }
    });

    it('reports source path when nothing is found at all', () => {
        const r = workbench.resolveCli({ runner: () => ({ status: 1, stdout: '' }) });
        assert.deepEqual(r, { path: null, source: 'path' });
    });
});

describe('describeSpawnError', () => {
    const enoent = Object.assign(new Error('spawn C:\\tools\\bowire.exe ENOENT'), { code: 'ENOENT' });

    it('blames the missing working directory, not the executable', () => {
        // Node reports a missing cwd as ENOENT against the command, so the
        // raw message names a file that is sitting right there — which sends
        // the reader off checking entirely the wrong thing.
        const msg = workbench.describeSpawnError(enoent, {
            cli: 'C:\\tools\\bowire.exe',
            cwd: 'C:\\gone',
            exists: () => false,
        });
        assert.match(msg, /working directory does not exist/);
        assert.match(msg, /C:\\gone/);
    });

    it('blames the executable when the working directory is fine', () => {
        const msg = workbench.describeSpawnError(enoent, {
            cli: 'C:\\tools\\bowire.exe',
            cwd: 'C:\\here',
            exists: () => true,
        });
        assert.match(msg, /bowire\.exe could not be executed/);
        assert.doesNotMatch(msg, /working directory/);
    });

    it('names a permission problem as one', () => {
        const err = Object.assign(new Error('spawn EACCES'), { code: 'EACCES' });
        assert.match(
            workbench.describeSpawnError(err, { cli: '/opt/bowire', cwd: '/tmp', exists: () => true }),
            /not executable/);
    });

    it('passes anything else through rather than swallowing it', () => {
        const err = Object.assign(new Error('something odd'), { code: 'EPERM' });
        assert.match(workbench.describeSpawnError(err, { cli: 'bowire' }), /something odd/);
    });
});

describe('checkCliVersion', () => {
    it('accepts the minimum and anything newer', () => {
        for (const v of ['2.0.0', '2.4.1-alpha.0.104+d86f781', '3.0.0', '2.10.0']) {
            assert.equal(workbench.checkCliVersion(v).ok, true, v);
        }
    });

    it('rejects a CLI older than the arguments the extension passes', () => {
        const result = workbench.checkCliVersion('1.9.3');
        assert.equal(result.ok, false);
        assert.equal(result.version, '1.9.3');
        // The message has to name a way out, not just the problem.
        assert.match(result.message, /2\.0\.0 or newer/);
        assert.match(result.message, /bowire\.cliPath/);
    });

    it('treats a prerelease as below the release it leads to', () => {
        // 2.0.0-alpha is not yet 2.0.0, and the arguments may not be there yet.
        assert.equal(workbench.checkCliVersion('2.0.0-alpha.1').ok, false);
    });

    it('accepts output it cannot parse rather than blocking on it', () => {
        // A reworded banner is cosmetic; refusing to start over one would turn
        // it into an outage.
        for (const v of ['Bowire (dev build)', '', null, undefined]) {
            assert.equal(workbench.checkCliVersion(v).ok, true);
        }
    });
});

describe('missingCliMessage', () => {
    it('offers install routes when nothing is on PATH', () => {
        const msg = workbench.missingCliMessage({ source: 'path' });
        // The extension requires the CLI rather than bundling ~120 MB per
        // platform, so this message is the whole onboarding path.
        assert.match(msg, /winget install/);
        assert.match(msg, /choco install/);
        assert.match(msg, /dotnet tool install/);
        assert.match(msg, /bowire\.cliPath/);
    });

    it('quotes the bad path back when the setting is wrong', () => {
        // Install commands cannot fix a typo, so this case must not get them.
        const msg = workbench.missingCliMessage({ source: 'setting', configured: '/nope/bowire' });
        assert.match(msg, /\/nope\/bowire/);
        assert.match(msg, /bowire\.cliPath/);
        assert.doesNotMatch(msg, /winget install/);
    });

    it('defaults to the PATH wording', () => {
        assert.match(workbench.missingCliMessage(), /winget install/);
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

describe('manifest', () => {
    it('declares bowire.cliPath, or the setting is unreachable from the UI', () => {
        // resolveCli reading a setting VS Code never renders would leave the
        // escape hatch working only for people who hand-edit settings.json.
        const pkg = require(resolve(__dirname, '../../../extensions/bowire-vscode/package.json'));
        const prop = pkg.contributes.configuration.properties['bowire.cliPath'];
        assert.ok(prop, 'bowire.cliPath is not contributed');
        assert.equal(prop.type, 'string');
        assert.equal(prop.default, '');
        // Machine-overridable: the right path differs per machine even when
        // the rest of the workspace settings are shared.
        assert.equal(prop.scope, 'machine-overridable');
        assert.match(prop.markdownDescription, /\$\{workspaceFolder\}/);
    });
});
