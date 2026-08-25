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
import { dirname, resolve, join } from 'node:path';
import { createHash } from 'node:crypto';
import { spawnSync } from 'node:child_process';
import { tmpdir } from 'node:os';
import fsp, { mkdtemp, rm, readdir } from 'node:fs/promises';

const __dirname = dirname(fileURLToPath(import.meta.url));
const require = createRequire(import.meta.url);
const workbench = require(resolve(__dirname, '../../../extensions/bowire-vscode/lib/workbench.js'));
const download = require(resolve(__dirname, '../../../extensions/bowire-vscode/lib/download.js'));
const uninstall = require(resolve(__dirname, '../../../extensions/bowire-vscode/lib/uninstall.js'));

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
    /** No tool manifest anywhere up the tree. */
    const noManifest = () => false;

    it('prefers the configured path over PATH', () => {
        // Someone who names a path has a reason — a local build, a second
        // version alongside the installed one. Silently preferring PATH would
        // run a different binary than the one they asked for.
        const r = workbench.resolveCli({ configuredPath: '/opt/bowire', runner: never, exists: () => true });
        assert.equal(r.command, '/opt/bowire');
        assert.deepEqual(r.prefixArgs, []);
        assert.equal(r.source, 'setting');
    });

    it('reports a configured path that does not exist instead of falling back', () => {
        // Falling through to PATH on a typo would look like the setting was
        // ignored — the harder of the two failures to diagnose.
        const r = workbench.resolveCli({ configuredPath: '/nope/bowire', runner: never, exists: () => false });
        assert.equal(r.command, null);
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
        assert.equal(r.command, '/repo/tools/bowire');
    });

    it('searches PATH when the setting is empty or whitespace', () => {
        for (const configuredPath of ['', '   ', undefined]) {
            const r = workbench.resolveCli({
                configuredPath,
                exists: noManifest,
                runner: () => ({ status: 0, stdout: '/usr/bin/bowire\n' }),
            });
            assert.equal(r.command, '/usr/bin/bowire');
            assert.deepEqual(r.prefixArgs, []);
            assert.equal(r.source, 'path');
        }
    });

    it('reports source path when nothing is found at all', () => {
        const r = workbench.resolveCli({ exists: noManifest, runner: () => ({ status: 1, stdout: '' }) });
        assert.equal(r.command, null);
        assert.equal(r.source, 'path');
    });
});

describe('tool manifest resolution (#589)', () => {
    const MANIFEST = { tools: { 'Kuestenlogik.Bowire.Tool': { version: '2.6.0', commands: ['bowire'] } } };

    /** exists()/read() pair backed by a fake filesystem keyed by path. */
    const fakeFs = files => ({
        exists: p => Object.hasOwn(files, p),
        read: p => files[p],
    });

    // Resolved, not literal: findToolManifest normalises its start directory
    // (as it must, to walk parents), so on Windows "/repo" becomes "C:\repo".
    // A fake filesystem keyed by the literal string would never be hit — which
    // is exactly how the first version of these tests failed.
    const REPO = require('node:path').resolve('/repo');
    const manifestAt = dir => require('node:path').join(dir, '.config', 'dotnet-tools.json');

    /**
     * A runner that answers the two probes resolveCli makes, separately:
     * `dotnet --list-sdks` and the PATH lookup. They have to be distinguishable
     * — a single canned response makes "no SDK" and "nothing on PATH" the same
     * state, which is precisely the pair these tests exist to tell apart.
     */
    const runner = ({ sdk = true, onPath = null } = {}) => (cmd) =>
        cmd === 'dotnet'
            ? { status: sdk ? 0 : 1, stdout: sdk ? '10.0.100 [/usr/share/dotnet/sdk]\n' : '' }
            : { status: onPath ? 0 : 1, stdout: onPath ? `${onPath}\n` : '' };

    it('runs the pinned tool instead of whatever is on PATH', () => {
        // The point of the pin: the repo says which Bowire it is tested with,
        // and that beats whatever this machine happens to have installed.
        const files = { [manifestAt(REPO)]: JSON.stringify(MANIFEST) };
        const r = workbench.resolveCli({
            workspaceDir: REPO,
            ...fakeFs(files),
            runner: () => ({ status: 0, stdout: '/usr/bin/bowire\n' }),
        });

        assert.equal(r.source, 'manifest');
        assert.equal(r.command, 'dotnet');
        assert.deepEqual(r.prefixArgs, ['tool', 'run', 'bowire']);
    });

    it('finds a manifest at the repo root from a subdirectory', () => {
        // Same walk .NET itself does — a pin at the root governs a command run
        // several directories down.
        const files = { [manifestAt(REPO)]: JSON.stringify(MANIFEST) };
        const found = workbench.findToolManifest(
            require('node:path').join(REPO, 'src', 'inner'),
            fakeFs(files).exists, fakeFs(files).read);

        assert.equal(found, manifestAt(REPO));
    });

    it('ignores a manifest that does not list Bowire', () => {
        // A repo pinning only, say, dotnet-ef has not expressed anything about
        // Bowire — falling through to PATH is correct there.
        const files = {
            [manifestAt(REPO)]: JSON.stringify({ tools: { 'dotnet-ef': { version: '9.0.0' } } }),
        };
        const r = workbench.resolveCli({
            workspaceDir: REPO,
            ...fakeFs(files),
            runner: () => ({ status: 0, stdout: '/usr/bin/bowire\n' }),
        });

        assert.equal(r.source, 'path');
        assert.equal(r.command, '/usr/bin/bowire');
    });

    it('ignores a malformed manifest rather than failing the launch', () => {
        // `dotnet tool restore` reports a broken manifest far better than this
        // extension could. Refusing to start over one would be worse than
        // starting with the machine's own Bowire.
        const files = { [manifestAt(REPO)]: '{ not json' };
        const r = workbench.resolveCli({
            workspaceDir: REPO,
            ...fakeFs(files),
            runner: () => ({ status: 0, stdout: '/usr/bin/bowire\n' }),
        });

        assert.equal(r.source, 'path');
    });

    it('the explicit setting still wins over a manifest', () => {
        // Specific beats shared: someone who named a path is debugging
        // something, and a repo-wide pin must not override that.
        const files = { [manifestAt(REPO)]: JSON.stringify(MANIFEST) };
        const r = workbench.resolveCli({
            configuredPath: '/opt/bowire',
            workspaceDir: REPO,
            exists: p => p === '/opt/bowire' || Object.hasOwn(files, p),
            read: p => files[p],
            runner: () => { throw new Error('PATH must not be probed'); },
        });

        assert.equal(r.source, 'setting');
        assert.equal(r.command, '/opt/bowire');
    });

    it('accepts a bare dotnet-tools.json beside the .config variant', () => {
        // `dotnet new tool-manifest` produced exactly this against a real SDK,
        // and `dotnet tool run` resolves it. Looking only in .config/ missed a
        // manifest the SDK itself honours.
        const bare = require('node:path').join(REPO, 'dotnet-tools.json');
        const files = { [bare]: JSON.stringify(MANIFEST) };
        const r = workbench.resolveCli({
            workspaceDir: REPO,
            ...fakeFs(files),
            runner: () => ({ status: 0, stdout: '/usr/bin/bowire\n' }),
        });

        assert.equal(r.source, 'manifest');
        assert.equal(r.manifest, bare);
    });

    it('prefers .config/ when both exist, as .NET does', () => {
        const files = {
            [manifestAt(REPO)]: JSON.stringify(MANIFEST),
            [require('node:path').join(REPO, 'dotnet-tools.json')]: JSON.stringify(MANIFEST),
        };
        const r = workbench.resolveCli({ workspaceDir: REPO, ...fakeFs(files), runner: runner({ onPath: null }) });

        assert.equal(r.manifest, manifestAt(REPO));
    });

    it('reports the version the manifest pins', () => {
        // The output channel names the manifest; without this it would then
        // say nothing about what that manifest pins, because the version probe
        // is deliberately skipped for this case — asking a tool that may not be
        // restored yet fails for a reason that has nothing to do with versions.
        const files = { [manifestAt(REPO)]: JSON.stringify(MANIFEST) };
        const r = workbench.resolveCli({
            workspaceDir: REPO,
            ...fakeFs(files),
            runner: runner({ onPath: '/usr/bin/bowire' }),
        });

        assert.equal(r.pinnedVersion, '2.6.0');
    });

    it('survives a manifest that pins Bowire without naming a version', () => {
        const files = { [manifestAt(REPO)]: JSON.stringify({ tools: { 'Kuestenlogik.Bowire.Tool': {} } }) };
        const r = workbench.resolveCli({
            workspaceDir: REPO,
            ...fakeFs(files),
            runner: runner({ onPath: '/usr/bin/bowire' }),
        });

        assert.equal(r.source, 'manifest');
        assert.equal(r.pinnedVersion, null);
    });

    it('falls through to PATH when there is no SDK to honour the pin with', () => {
        // `dotnet tool run` needs the SDK, not just the runtime. Without one
        // the manifest names a version nothing here can produce, and refusing
        // to start over a file the user may not know is in the repo — while a
        // working Bowire sits on PATH — is the wrong trade.
        const files = { [manifestAt(REPO)]: JSON.stringify(MANIFEST) };
        const r = workbench.resolveCli({
            workspaceDir: REPO,
            ...fakeFs(files),
            runner: runner({ sdk: false, onPath: '/usr/bin/bowire' }),
        });

        assert.equal(r.source, 'path');
        assert.equal(r.command, '/usr/bin/bowire');
    });

    it('a runtime-only install does not count as an SDK', () => {
        // `dotnet --version` answers on a runtime-only machine, which is why
        // the probe asks for the SDK list instead — it comes back empty there.
        const files = { [manifestAt(REPO)]: JSON.stringify(MANIFEST) };
        const r = workbench.resolveCli({
            workspaceDir: REPO,
            ...fakeFs(files),
            runner: cmd => cmd === 'dotnet'
                ? { status: 0, stdout: '   \n' }        // exit 0, nothing listed
                : { status: 0, stdout: '/usr/bin/bowire\n' },
        });

        assert.equal(r.source, 'path');
    });

    it('no SDK and nothing on PATH still reports a plain miss', () => {
        // Not an SDK complaint: the manifest was never usable here, so the
        // answer is the same one a repo without a manifest would get.
        const files = { [manifestAt(REPO)]: JSON.stringify(MANIFEST) };
        const r = workbench.resolveCli({
            workspaceDir: REPO,
            ...fakeFs(files),
            runner: runner({ sdk: false, onPath: null }),
        });

        assert.equal(r.command, null);
        assert.equal(r.source, 'path');
    });

    it('no workspace folder means no manifest lookup', () => {
        const r = workbench.resolveCli({
            workspaceDir: '',
            exists: () => false,
            runner: () => ({ status: 1, stdout: '' }),
        });

        assert.equal(r.source, 'path');
    });
});

describe('manifestStartFailureMessage (#589)', () => {
    it('offers restore when the tool is simply not fetched yet', () => {
        const msg = workbench.manifestStartFailureMessage('/repo/.config/dotnet-tools.json', 'exited with code 1');
        assert.match(msg, /dotnet tool restore/);
        assert.match(msg, /dotnet-tools\.json/);
    });

    it('does not send someone without an SDK round in circles', () => {
        // Telling them to run `restore` when there is nothing to restore with
        // is advice that cannot succeed.
        const msg = workbench.manifestStartFailureMessage(
            '/repo/.config/dotnet-tools.json', "'dotnet' is not recognized as an internal or external command");
        assert.match(msg, /\.NET SDK is not available/);
        assert.doesNotMatch(msg, /dotnet tool restore/);
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
    it('asks the OS for a port and names the file to report it in', () => {
        // --port 0 rather than a number we picked: choosing one ourselves
        // meant racing every other process between the choice and the bind,
        // including a second window on the same folder.
        assert.deepEqual(workbench.buildArgs('/tmp/run/wb.json'),
            ['--port', '0', '--port-file', '/tmp/run/wb.json',
             '--auto-create-initial-workspace', '--no-browser']);
    });
});

describe('parsePortFile', () => {
    const doc = (over = {}) => JSON.stringify({ version: 1, url: 'http://127.0.0.1:51234/', pid: 42, ...over });

    it('reads the bound URL', () => {
        assert.equal(workbench.parsePortFile(doc()), 'http://127.0.0.1:51234');
    });

    it('accepts localhost as well as the loopback literal', () => {
        assert.equal(workbench.parsePortFile(doc({ url: 'http://localhost:5080/' })), 'http://localhost:5080');
    });

    // Everything below is the same rule stated four ways: this value is handed
    // to a webview, and a file on disk is not trustworthy just because we
    // named the path. Anything unexpected has to read as "not ready".
    it('rejects a document it does not understand the version of', () => {
        assert.equal(workbench.parsePortFile(doc({ version: 2 })), null);
        assert.equal(workbench.parsePortFile(doc({ version: undefined })), null);
    });

    it('rejects a half-written or non-JSON file rather than throwing', () => {
        assert.equal(workbench.parsePortFile('{"version":1,"url":"http://127.0'), null);
        assert.equal(workbench.parsePortFile(''), null);
        assert.equal(workbench.parsePortFile('null'), null);
    });

    it('refuses a port file that names a remote host', () => {
        // A workbench we started is on loopback. Anything else is either not
        // ours or someone steering the panel at a page of their choosing.
        assert.equal(workbench.parsePortFile(doc({ url: 'https://evil.example.com/' })), null);
        assert.equal(workbench.parsePortFile(doc({ url: 'http://10.0.0.5:5080/' })), null);
    });

    it('refuses a url that is not a string', () => {
        assert.equal(workbench.parsePortFile(doc({ url: 5080 })), null);
        assert.equal(workbench.parsePortFile(doc({ url: null })), null);
    });
});

describe('waitForPortFile (existence is the readiness signal)', () => {
    const clock = (start = 0) => { let t = start; return { now: () => t, sleep: async (ms) => { t += ms; } }; };

    it('returns the URL as soon as the file appears', async () => {
        const { now, sleep } = clock();
        let reads = 0;
        const readFile = async () => {
            // ENOENT until the CLI has bound — the normal startup shape.
            if (++reads < 4) throw Object.assign(new Error('ENOENT'), { code: 'ENOENT' });
            return JSON.stringify({ version: 1, url: 'http://127.0.0.1:60001/', pid: 7 });
        };

        assert.equal(await workbench.waitForPortFile('/x', { readFile, now, sleep }),
            'http://127.0.0.1:60001');
        assert.equal(reads, 4);
    });

    it('keeps waiting through a file that is not yet valid', async () => {
        // The CLI writes through a temp file and renames, so a torn read
        // should not happen — but treating one as "not ready" costs a poll
        // and treating it as fatal costs the panel.
        const { now, sleep } = clock();
        let reads = 0;
        const readFile = async () => (++reads < 3
            ? '{"version":1,"url":"http://127.'
            : JSON.stringify({ version: 1, url: 'http://127.0.0.1:60002/', pid: 7 }));

        assert.equal(await workbench.waitForPortFile('/x', { readFile, now, sleep }),
            'http://127.0.0.1:60002');
    });

    it('gives up at the timeout instead of hanging', async () => {
        const { now, sleep } = clock();
        const readFile = async () => { throw Object.assign(new Error('ENOENT'), { code: 'ENOENT' }); };

        assert.equal(await workbench.waitForPortFile('/x', { readFile, now, sleep, timeoutMs: 1000 }), null);
    });
});

describe('portFilePathFor', () => {
    const root = '/storage';

    it('is stable for the same workspace', () => {
        assert.equal(workbench.portFilePathFor(root, '/home/dev/orders-api'),
                     workbench.portFilePathFor(root, '/home/dev/orders-api'));
    });

    it('differs between workspaces so two windows do not share a file', () => {
        assert.notEqual(workbench.portFilePathFor(root, '/home/dev/orders-api'),
                        workbench.portFilePathFor(root, '/home/dev/billing-api'));
    });

    it('lives under the extension storage, not in the user\'s repo', () => {
        // Otherwise it shows up in the explorer and in `git status` of the
        // project being worked on — and a read-only workspace folder breaks.
        // Split on anything that is not a name character, so the assertion
        // reads the same whichever separator path.join emitted.
        const seg = (x) => x.split(/[^A-Za-z0-9._-]+/).filter(Boolean);
        const p = workbench.portFilePathFor(root, '/home/dev/orders-api');
        assert.deepEqual(seg(p).slice(0, 2), ['storage', 'run'], p);
        assert.ok(!p.includes('orders-api'), p);
    });

    it('still answers when there is no folder open', () => {
        assert.ok(workbench.portFilePathFor(root, undefined));
        assert.ok(workbench.portFilePathFor(root, ''));
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

describe('marketplace assets', () => {
    const prepare = () => import(
        new URL('../../../extensions/bowire-vscode/scripts/prepare-package.mjs', import.meta.url).href);

    it('reuses the one canonical logo and meets the marketplace rules', async () => {
        // The icon is copied in at package time from images/bowire_logo_small.png
        // — the same file NuGet packs — rather than committed a second time.
        // If that image is moved or renamed, this fails here instead of
        // publishing a blank marketplace tile nobody notices.
        const mod = await prepare();

        const size = mod.pngSize(mod.SOURCE);
        assert.ok(size, `${mod.SOURCE} is missing or not a PNG`);
        assert.equal(size.width, size.height, 'marketplace art must be square');
        assert.ok(size.width >= 128, `marketplace minimum is 128x128; got ${size.width}`);
    });

    it('finds a licence to pack', async () => {
        // vsce warns when the extension folder has no LICENSE, and a listing
        // with no licence is one nobody inside a company can adopt. Same
        // copy-at-package-time treatment as the icon, so this catches the
        // repo licence being moved.
        const mod = await prepare();
        const { existsSync } = await import('node:fs');
        assert.ok(existsSync(mod.LICENSE_SOURCE), `${mod.LICENSE_SOURCE} is missing`);
    });

    it('package.json points at the copied icon and runs the prepare step', () => {
        const pkg = require(resolve(__dirname, '../../../extensions/bowire-vscode/package.json'));
        assert.equal(pkg.icon, 'icon.png');
        assert.match(pkg.scripts['vscode:prepublish'], /prepare-package/);
    });

    it('does not promise workspace-local storage in the marketplace blurb', () => {
        // The description is the listing's subtitle, and it used to claim
        // collections are "stored in the workspace so they travel with the
        // repo". Since #591 that CAN be true — but only for a repo whose
        // manifest opts in with `"storage": "project"`. Stating it as a flat
        // fact on the storefront would still be a promise the default does not
        // keep, so the blurb stays out of it and the README explains the
        // choice.
        const pkg = require(resolve(__dirname, '../../../extensions/bowire-vscode/package.json'));
        assert.doesNotMatch(pkg.description, /stored in the workspace/i);
        assert.doesNotMatch(pkg.description, /travel with the repo/i);
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

    it('declares bowire.autoDownload with prompt as the default (#590)', () => {
        // The default is the feature's whole safety story: a download that
        // happens without being asked for is the thing this setting exists to
        // prevent, and `never` has to be reachable for environments that
        // refuse outbound traffic.
        const pkg = require(resolve(__dirname, '../../../extensions/bowire-vscode/package.json'));
        const prop = pkg.contributes.configuration.properties['bowire.autoDownload'];
        assert.ok(prop, 'bowire.autoDownload is not contributed');
        assert.equal(prop.default, 'prompt');
        assert.deepEqual(prop.enum, ['prompt', 'always', 'never']);
        assert.equal(prop.enum.length, prop.enumDescriptions.length);
    });
});

describe('managed download (#590)', () => {
    describe('ridFor', () => {
        it('maps the six published platform/arch pairs', () => {
            assert.equal(download.ridFor('win32', 'x64'), 'win-x64');
            assert.equal(download.ridFor('win32', 'arm64'), 'win-arm64');
            assert.equal(download.ridFor('linux', 'x64'), 'linux-x64');
            assert.equal(download.ridFor('linux', 'arm64'), 'linux-arm64');
            assert.equal(download.ridFor('darwin', 'x64'), 'osx-x64');
            assert.equal(download.ridFor('darwin', 'arm64'), 'osx-arm64');
        });

        it('returns null rather than guessing for platforms with no build', () => {
            // A guessed RID becomes a 404 the user can do nothing about;
            // "no build for this platform" is a complete answer.
            assert.equal(download.ridFor('linux', 'arm'), null);
            assert.equal(download.ridFor('freebsd', 'x64'), null);
            assert.equal(download.ridFor('win32', 'ia32'), null);
        });
    });

    describe('asset names', () => {
        it('matches what the release workflow actually publishes', () => {
            // These names are pinned by release.yml (zip for Windows RIDs,
            // tar.gz for the rest) and linked by the marketing site through
            // releases/latest/download. A rename here downloads nothing.
            assert.equal(download.assetNameFor('win-x64'), 'bowire-win-x64.zip');
            assert.equal(download.assetNameFor('win-arm64'), 'bowire-win-arm64.zip');
            assert.equal(download.assetNameFor('linux-x64'), 'bowire-linux-x64.tar.gz');
            assert.equal(download.assetNameFor('osx-arm64'), 'bowire-osx-arm64.tar.gz');
        });

        it('builds release URLs against the v-prefixed tag', () => {
            const urls = download.downloadUrls('2.5.0', 'linux-x64');
            assert.match(urls.archive, /\/releases\/download\/v2\.5\.0\/bowire-linux-x64\.tar\.gz$/);
            assert.match(urls.checksums, /\/releases\/download\/v2\.5\.0\/checksums\.txt$/);
        });
    });

    describe('expectedDigest', () => {
        const hash = 'a'.repeat(64);
        const other = 'b'.repeat(64);

        it('reads the binary-mode form sha256sum writes on Windows runners', () => {
            const manifest = `${other} *bowire-win-x64.zip\n${hash} *bowire-linux-x64.tar.gz\n`;
            assert.equal(download.expectedDigest(manifest, 'bowire-linux-x64.tar.gz'), hash);
        });

        it('reads the text-mode form GNU coreutils writes on Linux', () => {
            // Which form a release carries depends on the runner that produced
            // it, so a parser that knows only one works until it doesn't.
            const manifest = `${hash}  bowire-linux-x64.tar.gz\n`;
            assert.equal(download.expectedDigest(manifest, 'bowire-linux-x64.tar.gz'), hash);
        });

        it('does not match an asset by prefix', () => {
            // `bowire-win-x64.zip` must not satisfy a lookup for
            // `bowire-win-x64.zip.sig` or vice versa.
            const manifest = `${hash} *bowire-win-x64.zip.sig\n`;
            assert.equal(download.expectedDigest(manifest, 'bowire-win-x64.zip'), null);
        });

        it('returns null when the release lists no digest for the asset', () => {
            const manifest = `${hash} *bowire-osx-x64.tar.gz\n`;
            assert.equal(download.expectedDigest(manifest, 'bowire-win-x64.zip'), null);
        });

        it('ignores lines that are not digests', () => {
            const manifest = `# generated\n\n${hash} *bowire-win-x64.zip\n`;
            assert.equal(download.expectedDigest(manifest, 'bowire-win-x64.zip'), hash);
        });
    });

    describe('install paths', () => {
        it('points inside the directory the archives carry', () => {
            // The archives unpack to `bowire-<rid>/`, so the binary is one
            // level down — assuming a flat archive would produce a path that
            // never exists.
            assert.equal(
                download.managedCliPath('/store', '2.5.0', 'linux-x64', 'linux'),
                join('/store', 'cli', '2.5.0', 'bowire-linux-x64', 'bowire'));
            assert.equal(
                download.managedCliPath('/store', '2.5.0', 'win-x64', 'win32'),
                join('/store', 'cli', '2.5.0', 'bowire-win-x64', 'bowire.exe'));
        });

        it('keeps the pinned version and drops the rest', () => {
            assert.deepEqual(
                download.staleCliVersions(['2.3.0', '2.5.0', '2.4.0'], '2.5.0'),
                ['2.3.0', '2.4.0']);
            assert.deepEqual(download.staleCliVersions([], '2.5.0'), []);
        });
    });

    describe('resolution order', () => {
        it('prefers PATH over a CLI it downloaded earlier', () => {
            // An installed Bowire is the one the terminal and CI use. Quietly
            // preferring our private copy would have the editor drive a
            // different version than everything else on the machine.
            const managed = download.managedCliPath('/store', download.PINNED_CLI_VERSION,
                download.ridFor() ?? 'linux-x64');
            const resolution = workbench.resolveCli({
                managedRoot: '/store',
                runner: () => ({ status: 0, stdout: '/usr/bin/bowire\n' }),
                exists: p => p === managed,
            });
            assert.equal(resolution.source, 'path');
            assert.equal(resolution.command, '/usr/bin/bowire');
        });

        it('uses the downloaded CLI when PATH has nothing', () => {
            const managed = download.managedCliPath('/store', download.PINNED_CLI_VERSION,
                download.ridFor() ?? 'linux-x64');
            const resolution = workbench.resolveCli({
                managedRoot: '/store',
                runner: () => ({ status: 1, stdout: '' }),
                exists: p => p === managed,
            });
            assert.equal(resolution.source, 'managed');
            assert.equal(resolution.command, managed);
        });

        it('reports a plain miss when the download is not there either', () => {
            // Removing the storage folder has to restore the not-found state
            // cleanly rather than leaving a path to a file that is gone.
            const resolution = workbench.resolveCli({
                managedRoot: '/store',
                runner: () => ({ status: 1, stdout: '' }),
                exists: () => false,
            });
            assert.equal(resolution.command, null);
            assert.equal(resolution.source, 'path');
        });

        it('never reaches the download when a manifest pins Bowire', () => {
            const resolution = workbench.resolveCli({
                workspaceDir: '/repo',
                managedRoot: '/store',
                // An SDK has to be reported, or the manifest is skipped for a
                // reason that has nothing to do with what this test asserts.
                runner: cmd => cmd === 'dotnet'
                    ? { status: 0, stdout: '10.0.100 [/usr/share/dotnet/sdk]\n' }
                    : { status: 1, stdout: '' },
                exists: p => String(p).replace(/\\/g, '/').endsWith('/repo/.config/dotnet-tools.json'),
                read: () => JSON.stringify({ tools: { 'kuestenlogik.bowire.tool': { version: '2.4.0' } } }),
            });
            assert.equal(resolution.source, 'manifest');
        });
    });

    describe('installManagedCli', () => {
        // A tiny fake release: one text response for checksums.txt, one byte
        // stream for the archive. Enough to drive the real ordering logic
        // without touching the network.
        function fakeRelease({ payload = 'archive-bytes', manifestFor = null } = {}) {
            const bytes = Buffer.from(payload);
            const digest = createHash('sha256').update(bytes).digest('hex');
            const asset = download.assetNameFor(download.ridFor('linux', 'x64'));
            const manifest = manifestFor ?? `${digest} *${asset}\n`;
            const seen = [];
            const fetchImpl = async (url) => {
                seen.push(url);
                if (url.endsWith('checksums.txt')) {
                    return { ok: true, status: 200, text: async () => manifest };
                }
                return {
                    ok: true,
                    status: 200,
                    headers: { get: () => String(bytes.length) },
                    body: (async function* () { yield bytes; })(),
                };
            };
            return { fetchImpl, digest, asset, seen };
        }

        it('fetches digests before the payload', async () => {
            // Discovering a release cannot be verified after pulling 60 MB
            // down a metered connection is a poor trade for one request.
            const release = fakeRelease();
            const dir = await mkdtemp(join(tmpdir(), 'bowire-590-'));
            await download.installManagedCli({
                root: dir,
                version: '9.9.9',
                platform: 'linux',
                arch: 'x64',
                fetchImpl: release.fetchImpl,
                runner: () => ({ status: 0 }),
                fs: { ...fsp, chmod: async () => {} },
            });
            assert.match(release.seen[0], /checksums\.txt$/);
            await rm(dir, { recursive: true, force: true });
        });

        it('refuses a release that publishes no digest for the asset', async () => {
            const release = fakeRelease({ manifestFor: `${'c'.repeat(64)} *something-else.zip\n` });
            const dir = await mkdtemp(join(tmpdir(), 'bowire-590-'));
            await assert.rejects(
                () => download.installManagedCli({
                    root: dir,
                    version: '9.9.9',
                    platform: 'linux',
                    arch: 'x64',
                    fetchImpl: release.fetchImpl,
                    runner: () => ({ status: 0 }),
                }),
                /publishes no checksums\.txt/);
            // And it stopped before spending the bandwidth.
            assert.equal(release.seen.length, 1);
            await rm(dir, { recursive: true, force: true });
        });

        it('unpacks nothing when the digest does not match', async () => {
            const release = fakeRelease({ manifestFor: `${'d'.repeat(64)} *bowire-linux-x64.tar.gz\n` });
            const dir = await mkdtemp(join(tmpdir(), 'bowire-590-'));
            let extracted = false;
            await assert.rejects(
                () => download.installManagedCli({
                    root: dir,
                    version: '9.9.9',
                    platform: 'linux',
                    arch: 'x64',
                    fetchImpl: release.fetchImpl,
                    runner: () => { extracted = true; return { status: 0 }; },
                }),
                /does not match the checksum/);
            assert.equal(extracted, false, 'the extractor ran on an unverified archive');

            // And the partial file is gone rather than left to be mistaken for
            // a finished download.
            const left = await readdir(join(dir, 'cli', '9.9.9'));
            assert.deepEqual(left, []);
            await rm(dir, { recursive: true, force: true });
        });

        it('stops mid-download when cancelled, and installs nothing', async () => {
            // The progress notification is cancellable, which is only true if
            // the abort actually reaches the stream — a token wired to nothing
            // still renders a Cancel button, and the download continues behind
            // a dialog the user believes they dismissed.
            const controller = new AbortController();
            const dir = await mkdtemp(join(tmpdir(), 'bowire-590-'));
            const asset = download.assetNameFor('linux-x64');
            let extracted = false;

            const fetchImpl = async (url) => {
                if (url.endsWith('checksums.txt')) {
                    return { ok: true, status: 200, text: async () => `${'e'.repeat(64)} *${asset}\n` };
                }
                return {
                    ok: true,
                    status: 200,
                    headers: { get: () => '1000' },
                    body: (async function* () {
                        yield Buffer.from('first-chunk');
                        controller.abort();          // the user hits Cancel here
                        if (controller.signal.aborted) {
                            const err = new Error('aborted');
                            err.name = 'AbortError';
                            throw err;
                        }
                        yield Buffer.from('never-sent');
                    })(),
                };
            };

            await assert.rejects(
                () => download.installManagedCli({
                    root: dir,
                    version: '9.9.9',
                    platform: 'linux',
                    arch: 'x64',
                    fetchImpl,
                    signal: controller.signal,
                    runner: () => { extracted = true; return { status: 0 }; },
                }),
                (err) => err.name === 'AbortError');

            assert.equal(extracted, false, 'a cancelled download was still unpacked');
            // The partial file may remain — it is named `.part` precisely so it
            // cannot be mistaken for a finished download — but nothing that
            // looks installed may exist.
            const left = await readdir(join(dir, 'cli', '9.9.9'));
            assert.deepEqual(left.filter(n => !n.endsWith('.part')), []);
            await rm(dir, { recursive: true, force: true });
        });

        it('refuses a platform with no published build', async () => {
            await assert.rejects(
                () => download.installManagedCli({ root: '/store', platform: 'sunos', arch: 'x64' }),
                /publishes no build for sunos\/x64/);
        });

        it('leaves the binary executable on Unix', async (t) => {
            // The one criterion that cannot be checked by reasoning: an archive
            // that carries no mode — which is what a zip written on a Windows
            // runner produces — extracts to a file nobody can run, and the
            // failure surfaces as EACCES at spawn time rather than here.
            //
            // Windows has no mode bits to assert on, so this is the CI Linux
            // runner's job. It also exercises the real tar.gz path end to end:
            // a genuine archive in the layout release.yml produces, streamed
            // through the digest check and unpacked by the system tar.
            // `return`, not just `t.skip()` — node:test marks the result as
            // skipped but keeps running the body, which on Windows meant six
            // seconds of real work whose failure was then reported as a skip.
            if (process.platform === 'win32') {
                t.skip('no Unix mode bits on Windows');
                return;
            }

            const rid = 'linux-x64';
            const work = await mkdtemp(join(tmpdir(), 'bowire-590-exec-'));
            const stage = join(work, 'stage', `bowire-${rid}`);
            await fsp.mkdir(stage, { recursive: true });
            // Deliberately not executable to begin with — otherwise the test
            // would pass on a mode the archive supplied rather than one the
            // installer set.
            await fsp.writeFile(join(stage, 'bowire'), '#!/bin/sh\necho hi\n', { mode: 0o644 });

            const asset = download.assetNameFor(rid);
            const archive = join(work, asset);
            const packed = spawnSync('tar', ['-czf', archive, `bowire-${rid}`],
                { cwd: join(work, 'stage'), encoding: 'utf8' });
            assert.equal(packed.status, 0, `packing failed: ${packed.stderr}`);

            const bytes = await fsp.readFile(archive);
            const digest = createHash('sha256').update(bytes).digest('hex');
            const fetchImpl = async (url) => url.endsWith('checksums.txt')
                ? { ok: true, status: 200, text: async () => `${digest} *${asset}\n` }
                : {
                    ok: true,
                    status: 200,
                    headers: { get: () => String(bytes.length) },
                    body: (async function* () { yield bytes; })(),
                };

            const cli = await download.installManagedCli({
                root: join(work, 'storage'),
                version: '9.9.9',
                platform: 'linux',
                arch: 'x64',
                fetchImpl,
            });

            const mode = (await fsp.stat(cli)).mode & 0o777;
            assert.ok(mode & 0o111, `binary is not executable (mode 0${mode.toString(8)})`);
            await rm(work, { recursive: true, force: true });
        });
    });
});

describe('waitUntilServing (banner is not readiness)', () => {
    const fakeClock = () => {
        let t = 0;
        return { now: () => t, sleep: async (ms) => { t += ms; } };
    };

    it('returns once the workbench answers', async () => {
        const c = fakeClock();
        let calls = 0;
        const fetchImpl = async () => {
            calls++;
            if (calls < 3) throw new Error('ECONNREFUSED');
            return { status: 200 };
        };
        assert.equal(await workbench.waitUntilServing('http://localhost:5099', { ...c, fetchImpl }), true);
        assert.equal(calls, 3, 'should keep polling while the connection is refused');
    });

    it('treats a redirect as served', async () => {
        const c = fakeClock();
        const fetchImpl = async () => ({ status: 302 });
        assert.equal(await workbench.waitUntilServing('http://localhost:5099', { ...c, fetchImpl }), true);
    });

    it('keeps waiting on an error page rather than showing it', async () => {
        // The whole point: a 404 while the pipeline is still coming up must
        // not be handed to the webview as if it were the workbench.
        const c = fakeClock();
        let calls = 0;
        const fetchImpl = async () => (++calls < 4 ? { status: 404 } : { status: 200 });
        assert.equal(await workbench.waitUntilServing('http://localhost:5099', { ...c, fetchImpl }), true);
        assert.equal(calls, 4);
    });

    it('gives up at the timeout instead of hanging', async () => {
        const c = fakeClock();
        const fetchImpl = async () => { throw new Error('ECONNREFUSED'); };
        assert.equal(
            await workbench.waitUntilServing('http://localhost:5099', { ...c, fetchImpl, timeoutMs: 1000 }),
            false);
    });
});

describe('uninstall (the managed CLI does not outlive the extension)', () => {
    const { candidateStorageDirs, isOurStorageDir, removeManagedCli, EXTENSION_ID } = uninstall;
    const seg = (p) => p.split(/[\\/]+/).filter(Boolean);

    it('lands under APPDATA on Windows', () => {
        const dirs = candidateStorageDirs({
            platform: 'win32',
            env: { APPDATA: 'C:\\Users\\x\\AppData\\Roaming' },
            home: 'C:\\Users\\x',
        });
        assert.equal(seg(dirs[0]).slice(-5).join('/'),
            `Roaming/Code/User/globalStorage/${EXTENSION_ID}`);
    });

    it('respects XDG_CONFIG_HOME on Linux', () => {
        const [first] = candidateStorageDirs({
            platform: 'linux', env: { XDG_CONFIG_HOME: '/cfg' }, home: '/home/x',
        });
        // Compared as segments: path.join() emits the host separator, so a
        // literal POSIX string here would only ever pass on POSIX.
        assert.deepEqual(seg(first), ['cfg', 'Code', 'User', 'globalStorage', EXTENSION_ID]);
    });

    it('covers the forks people actually run, not just stable', () => {
        const dirs = candidateStorageDirs({ platform: 'linux', env: {}, home: '/home/x' });
        const products = dirs.map((d) => seg(d).slice(-4)[0]);
        for (const p of ['Code', 'Code - Insiders', 'VSCodium'])
            assert.ok(products.includes(p), `missing ${p}`);
    });

    it('answers with the portable directory alone when one is set', () => {
        const dirs = candidateStorageDirs({
            platform: 'win32', env: { VSCODE_PORTABLE: 'D:\\vsc', APPDATA: 'C:\\ignored' }, home: 'C:\\Users\\x',
        });
        assert.equal(dirs.length, 1);
        assert.ok(seg(dirs[0]).includes('user-data'));
        assert.ok(!dirs[0].includes('ignored'));
    });

    // The guard is the whole safety story: this code deletes recursively,
    // unattended, from a path it reconstructed by guessing.
    it('refuses any path that is not our own storage directory', () => {
        for (const bad of ['/', 'C:\\', '/home/x', '/home/x/.config',
                           `/globalStorage/${EXTENSION_ID}`,
                           '/a/b/User/globalStorage/some.other-extension',
                           '/a/b/User/notGlobalStorage/' + EXTENSION_ID])
            assert.equal(isOurStorageDir(bad), false, `should reject ${bad}`);

        assert.equal(isOurStorageDir(`/home/x/.config/Code/User/globalStorage/${EXTENSION_ID}`), true);
    });

    it('removes only the cli subtree, never the storage directory itself', async () => {
        const asked = [];
        const dirs = candidateStorageDirs({ platform: 'linux', env: {}, home: '/home/x' });
        const removed = await removeManagedCli({ dirs, rm: async (p) => { asked.push(p); } });

        assert.equal(asked.length, dirs.length);
        for (const p of asked) assert.equal(seg(p).at(-1), 'cli');
        assert.deepEqual(removed, asked);
    });

    it('keeps going when one copy is locked', async () => {
        const dirs = candidateStorageDirs({ platform: 'linux', env: {}, home: '/home/x' });
        const removed = await removeManagedCli({
            dirs,
            rm: async (p) => { if (p.includes('Insiders')) throw new Error('EBUSY'); },
        });
        assert.equal(removed.length, dirs.length - 1);
        assert.ok(!removed.some((p) => p.includes('Insiders')));
    });
});
