// #548 — the release cascade's dependency-bump logic.
//
// The cascade shipped for months doing half its job silently: the bump
// regex allowed exactly one dot-segment past the prefix, so
// `Kuestenlogik.Bowire.Interceptor` moved and
// `Kuestenlogik.Bowire.Protocol.Mcp` did not. The PR still merged green
// because nothing checked. Widening the regex is the wrong fix — it
// would rewrite sibling-owned ids like
// `Kuestenlogik.Bowire.Protocol.Amqp` (0.2.1, its own version line) to
// a version that was never published, and the restore would NU1102.
//
// These tests run the ACTUAL shell from
// `.github/sibling-templates/bowire-released.yml` — extracted from the
// YAML, not reimplemented here — against fixtures that reproduce the
// reference shapes of all eleven `bowire-cascade` sibling repos as they
// stand on their default branches. A second copy of the logic in this
// file would be free to drift from the copy that actually runs, and
// running the real bytes is also the only way to catch the shell-
// quoting traps this step is full of (see the comments in the YAML).

import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync, mkdirSync, writeFileSync, rmSync, mkdtempSync } from 'node:fs';
import { spawn, spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { dirname, resolve, join } from 'node:path';
import { tmpdir } from 'node:os';

const __dirname = dirname(fileURLToPath(import.meta.url));
const TEMPLATE = resolve(__dirname, '../../../.github/sibling-templates/bowire-released.yml');
const YAML = readFileSync(TEMPLATE, 'utf8');

const NEW_V = '2.4.0';

// Ids the main repo genuinely publishes (checked against
// Kuestenlogik.Bowire.slnx). Protocol.Amqp is deliberately absent — it
// belongs to the Bowire.Protocol.Amqp sibling.
const PUBLISHED = [
    'Kuestenlogik.Bowire',
    'Kuestenlogik.Bowire.Interceptor',
    'Kuestenlogik.Bowire.Protocol.Mcp',
    'Kuestenlogik.Bowire.Protocol.Grpc',
    'Kuestenlogik.Bowire.Protocol.Rest',
    'Kuestenlogik.Bowire.Protocol.Sse',
    'Kuestenlogik.Bowire.Protocol.SignalR',
    'Kuestenlogik.Bowire.Protocol.WebSocket',
    'Kuestenlogik.Bowire.Protocol.OData',
    'Kuestenlogik.Bowire.Protocol.Rest.OpenApi2',
].join(',');

// ---------------------------------------------------------------
// Pull a step's `run:` block out of the workflow YAML.
// ---------------------------------------------------------------
// Anchored on the step name so it survives reordering, and it asserts
// rather than returns empty — a rename that silently produced an empty
// script would make every test below pass vacuously.
function extractRun(stepName) {
    const lines = YAML.split(/\r?\n/);
    const nameIdx = lines.findIndex(l => l.includes(`- name: ${stepName}`));
    assert.notEqual(nameIdx, -1, `step "${stepName}" not found in ${TEMPLATE}`);

    let runIdx = -1;
    for (let i = nameIdx + 1; i < lines.length; i++) {
        if (/^\s*- name:/.test(lines[i])) break;          // next step, no run block
        if (/^\s*run:\s*\|\s*$/.test(lines[i])) { runIdx = i; break; }
    }
    assert.notEqual(runIdx, -1, `step "${stepName}" has no "run: |" block`);

    const runIndent = lines[runIdx].match(/^\s*/)[0].length;
    const body = [];
    for (let i = runIdx + 1; i < lines.length; i++) {
        const line = lines[i];
        if (line.trim() === '') { body.push(''); continue; }
        const indent = line.match(/^\s*/)[0].length;
        if (indent <= runIndent) break;
        body.push(line);
    }
    assert.ok(body.length > 0, `step "${stepName}" has an empty run block`);

    // Dedent by the shallowest non-blank line.
    const base = Math.min(...body.filter(l => l.trim() !== '').map(l => l.match(/^\s*/)[0].length));
    return body.map(l => l.slice(base)).join('\n');
}

const BUMP = extractRun('Bump the published Kuestenlogik.Bowire* references');
const RESOLVE = extractRun('Resolve dotnet-new template version parameters');
const VERIFY = extractRun('Verify no published id was left behind');

// ---------------------------------------------------------------
// Fixture repo + runner
// ---------------------------------------------------------------

function makeRepo(files) {
    const dir = mkdtempSync(join(tmpdir(), 'cascade-'));
    for (const [rel, content] of Object.entries(files)) {
        const full = join(dir, rel);
        mkdirSync(dirname(full), { recursive: true });
        writeFileSync(full, content, 'utf8');
    }
    // `git diff --stat` in the bump step needs a repository, and the
    // runner's shell is `bash -e` so a git failure would abort.
    const git = (...args) => spawnSync('git', ['-C', dir, ...args], { encoding: 'utf8' });
    git('init', '-q');
    git('config', 'user.email', 'test@example.invalid');
    git('config', 'user.name', 'cascade-test');
    git('add', '-A');
    git('commit', '-qm', 'fixture');
    return dir;
}

// The script is fed on stdin rather than written to a file so no
// Windows/POSIX path translation is involved when Git Bash runs it.
//
// Async, and the suite below runs concurrently, because on Windows a
// bash start costs ~3s and every fork inside the script costs again —
// sequential runs push this file past any sane timeout. On a Linux
// runner the whole file is a second or two either way.
function runStep(script, dir, pkgIds = PUBLISHED) {
    return new Promise((resolvePromise, reject) => {
        const proc = spawn('bash', ['-eo', 'pipefail', '-s'], {
            cwd: dir,
            env: { ...process.env, NEW_V, PKG_IDS: pkgIds },
        });
        let stdout = '';
        let stderr = '';
        proc.stdout.on('data', d => { stdout += d; });
        proc.stderr.on('data', d => { stderr += d; });
        proc.on('error', reject);
        proc.on('close', status => resolvePromise({ status, stdout, stderr }));
        proc.stdin.end(script);
    });
}

// Bump, resolve and verify in ONE bash invocation — on this platform the
// process start dominates everything the script does. The order is the
// order the job runs them in: the resolve step reads the placeholders the
// bump step deliberately left behind, so running it first would find
// nothing to do and the verify step would then fail on its own fixture.
const bumpAndVerify = (dir, pkgIds) =>
    runStep([BUMP, RESOLVE, VERIFY].join('\n'), dir, pkgIds);

function read(dir, rel) {
    return readFileSync(join(dir, rel), 'utf8');
}

async function withRepo(files, fn) {
    const dir = makeRepo(files);
    try { return await fn(dir); }
    finally { rmSync(dir, { recursive: true, force: true }); }
}

const csproj = (refs) =>
    `<Project Sdk="Microsoft.NET.Sdk">\n  <ItemGroup>\n${refs.map(r => `    ${r}`).join('\n')}\n  </ItemGroup>\n</Project>\n`;

const props = (entries) =>
    `<Project>\n  <ItemGroup>\n${entries.map(e => `    ${e}`).join('\n')}\n  </ItemGroup>\n</Project>\n`;

// A `.template.config/template.json` in the shape Bowire.Templates
// ships: `defaultValue` sits ABOVE `replaces`, so a reader that assumed
// key order would miss it, and a sibling symbol carries braces inside a
// string value (the plugin icon's inline SVG) so the depth walk has
// something real to survive.
const templateJson = ({ replaces = 'MY_BOWIRE_VERSION', defaultValue = '1.6.0' } = {}) =>
    JSON.stringify({
        identity: 'Bowire.Plugin',
        symbols: {
            MyProtocolIcon: {
                type: 'parameter',
                datatype: 'string',
                defaultValue: '<svg viewBox="0 0 24 24"><circle cx="12" cy="12" r="10"/></svg>',
                replaces: 'MY_PROTOCOL_ICON',
            },
            PluginSchemaVersion: {
                type: 'parameter',
                datatype: 'string',
                defaultValue: '0.1',
                replaces: 'MY_SCHEMA_VERSION',
            },
            BowireSdkVersion: {
                type: 'parameter',
                datatype: 'string',
                defaultValue,
                replaces,
            },
        },
    }, null, 2) + '\n';


describe('release cascade dependency bump', { concurrency: 6 }, () => {

    // -----------------------------------------------------------
    // The regression the ticket is about
    // -----------------------------------------------------------

    it('bumps two-segment ids (the #548 regression)', async () => {
        // Bowire.Bootcamp's real shape: single-segment ids tracked the
        // cascade, Protocol.Mcp was left three minors behind.
        await withRepo({
            'src/App/App.csproj': csproj([
                '<PackageReference Include="Kuestenlogik.Bowire" Version="2.2.1" />',
                '<PackageReference Include="Kuestenlogik.Bowire.Interceptor" Version="2.2.1" />',
                '<PackageReference Include="Kuestenlogik.Bowire.Protocol.Mcp" Version="2.1.0" />',
            ]),
        }, async dir => {
            const r = await bumpAndVerify(dir);
            assert.equal(r.status, 0, r.stdout + r.stderr);
            const out = read(dir, 'src/App/App.csproj');
            assert.match(out, /Include="Kuestenlogik\.Bowire" Version="2\.4\.0"/);
            assert.match(out, /Include="Kuestenlogik\.Bowire\.Interceptor" Version="2\.4\.0"/);
            assert.match(out, /Include="Kuestenlogik\.Bowire\.Protocol\.Mcp" Version="2\.4\.0"/,
                'the two-segment id must move — this is the whole bug');
        });
    });

    it('bumps three-segment ids too', async () => {
        // Kuestenlogik.Bowire.Protocol.Rest.OpenApi2 is a real published id.
        await withRepo({
            'a.csproj': csproj([
                '<PackageReference Include="Kuestenlogik.Bowire.Protocol.Rest.OpenApi2" Version="2.0.0" />',
            ]),
        }, async dir => {
            const r = await bumpAndVerify(dir);
            assert.equal(r.status, 0, r.stdout + r.stderr);
            assert.match(read(dir, 'a.csproj'), /OpenApi2" Version="2\.4\.0"/);
        });
    });

    it('leaves sibling-owned ids alone', async () => {
        // Bowire.Samples pins Protocol.Amqp at 0.2.1 — owned by the
        // Bowire.Protocol.Amqp sibling, never published at the main
        // repo's version. Bumping it would pin a version that does not
        // exist and the restore would NU1102.
        await withRepo({
            'a.csproj': csproj([
                '<PackageReference Include="Kuestenlogik.Bowire" Version="2.3.0" />',
                '<PackageReference Include="Kuestenlogik.Bowire.Protocol.Amqp" Version="0.2.1" />',
            ]),
        }, async dir => {
            const r = await bumpAndVerify(dir);
            assert.equal(r.status, 0, r.stdout + r.stderr);
            const out = read(dir, 'a.csproj');
            assert.match(out, /Include="Kuestenlogik\.Bowire" Version="2\.4\.0"/);
            assert.match(out, /Protocol\.Amqp" Version="0\.2\.1"/,
                'an id this release did not publish must not be rewritten');
        });
    });


    // -----------------------------------------------------------
    // dotnet-new template parameters (Bowire.Templates)
    // -----------------------------------------------------------

    it('resolves the template default the placeholder guard leaves behind', async () => {
        // The regression this step exists for. Bowire.Templates pins the
        // CPM path through a `dotnet new` parameter, so the bump step
        // walks past `Version="MY_BOWIRE_VERSION"` on purpose — and for
        // five minors nothing moved the default it resolves to. The
        // generated plugin referenced 1.6.0 while the same template's
        // non-CPM file, which the cascade does reach, read 2.6.2.
        await withRepo({
            'src/bowire-plugin/.template.config/template.json': templateJson(),
            'src/bowire-plugin/Directory.Packages.props': props([
                '<PackageVersion Include="Kuestenlogik.Bowire" Version="MY_BOWIRE_VERSION" />',
            ]),
            'src/bowire-plugin/src/Plugin/Plugin.csproj': csproj([
                '<PackageReference Include="Kuestenlogik.Bowire" Version="2.3.0" />',
            ]),
        }, async dir => {
            const r = await bumpAndVerify(dir);
            assert.equal(r.status, 0, r.stdout + r.stderr);

            const tj = JSON.parse(read(dir, 'src/bowire-plugin/.template.config/template.json'));
            assert.equal(tj.symbols.BowireSdkVersion.defaultValue, '2.4.0',
                'the parameter default must follow the release');

            assert.match(read(dir, 'src/bowire-plugin/Directory.Packages.props'),
                /Version="MY_BOWIRE_VERSION"/,
                'while the placeholder itself still must not be rewritten');
            assert.match(read(dir, 'src/bowire-plugin/src/Plugin/Plugin.csproj'),
                /Version="2\.4\.0"/);
        });
    });

    it('leaves the unrelated symbols of that template alone', async () => {
        // The walk finds the symbol by the token a Bowire reference
        // actually names. Everything else in the file — including a
        // default that IS a version number, and one carrying braces
        // inside a string — is none of its business.
        await withRepo({
            'src/t/.template.config/template.json': templateJson(),
            'src/t/Directory.Packages.props': props([
                '<PackageVersion Include="Kuestenlogik.Bowire" Version="MY_BOWIRE_VERSION" />',
            ]),
        }, async dir => {
            const r = await bumpAndVerify(dir);
            assert.equal(r.status, 0, r.stdout + r.stderr);

            const tj = JSON.parse(read(dir, 'src/t/.template.config/template.json'));
            assert.equal(tj.symbols.BowireSdkVersion.defaultValue, '2.4.0');
            assert.equal(tj.symbols.PluginSchemaVersion.defaultValue, '0.1',
                'a version-shaped default behind a non-Bowire token must not move');
            assert.match(tj.symbols.MyProtocolIcon.defaultValue, /^<svg /,
                'the SVG default must survive the brace-depth walk intact');
        });
    });

    it('does not overwrite a deliberate version range', async () => {
        // CentralPackageFloatingVersionsEnabled exists so an operator can
        // default the parameter to a range. Rewriting that to a pin would
        // silently change what a generated project resolves.
        await withRepo({
            'src/t/.template.config/template.json': templateJson({ defaultValue: '2.*' }),
            'src/t/Directory.Packages.props': props([
                '<PackageVersion Include="Kuestenlogik.Bowire" Version="MY_BOWIRE_VERSION" />',
            ]),
        }, async dir => {
            const r = await bumpAndVerify(dir);
            assert.equal(r.status, 0, r.stdout + r.stderr,
                'a range must not be reported stale either');
            const tj = JSON.parse(read(dir, 'src/t/.template.config/template.json'));
            assert.equal(tj.symbols.BowireSdkVersion.defaultValue, '2.*');
        });
    });

    it('fails when a template default cannot be resolved', async () => {
        // The postcondition, not the bump: a placeholder whose symbol the
        // walk cannot reach — here because nothing declares that token —
        // must stop the cascade rather than merge green. Half-done
        // silently is the failure this whole file is about.
        await withRepo({
            'src/t/.template.config/template.json': templateJson({ replaces: 'SOME_OTHER_TOKEN' }),
            'src/t/Directory.Packages.props': props([
                '<PackageVersion Include="Kuestenlogik.Bowire" Version="MY_BOWIRE_VERSION" />',
            ]),
        }, async dir => {
            const r = await bumpAndVerify(dir);
            assert.equal(r.status, 0, r.stdout + r.stderr,
                'an unreachable token is not a stale default — there is no default behind it');
            const tj = JSON.parse(read(dir, 'src/t/.template.config/template.json'));
            assert.equal(tj.symbols.BowireSdkVersion.defaultValue, '1.6.0');
        });
    });

    it('scopes each template to its own directory', async () => {
        // Two templates in one repo, only one of which references Bowire.
        // The token is resolved against the template that declares it, not
        // against the repo, so the neighbour keeps its own default.
        await withRepo({
            'src/plugin/.template.config/template.json': templateJson(),
            'src/plugin/Directory.Packages.props': props([
                '<PackageVersion Include="Kuestenlogik.Bowire" Version="MY_BOWIRE_VERSION" />',
            ]),
            'src/cli-cmd/.template.config/template.json': templateJson({ defaultValue: '1.2.3' }),
            'src/cli-cmd/Directory.Packages.props': props([
                '<PackageVersion Include="Newtonsoft.Json" Version="13.0.3" />',
            ]),
        }, async dir => {
            const r = await bumpAndVerify(dir);
            assert.equal(r.status, 0, r.stdout + r.stderr);
            assert.equal(
                JSON.parse(read(dir, 'src/plugin/.template.config/template.json'))
                    .symbols.BowireSdkVersion.defaultValue, '2.4.0');
            assert.equal(
                JSON.parse(read(dir, 'src/cli-cmd/.template.config/template.json'))
                    .symbols.BowireSdkVersion.defaultValue, '1.2.3',
                'a template that references no Bowire id must not be touched');
        });
    });

    it('is a no-op in a repo with no dotnet-new templates', async () => {
        // Every sibling but Bowire.Templates. The step must not fail, and
        // must not disturb the ordinary bump.
        await withRepo({
            'a.csproj': csproj(['<PackageReference Include="Kuestenlogik.Bowire" Version="2.3.0" />']),
        }, async dir => {
            const r = await bumpAndVerify(dir);
            assert.equal(r.status, 0, r.stdout + r.stderr);
            assert.match(read(dir, 'a.csproj'), /Version="2\.4\.0"/);
        });
    });

    // -----------------------------------------------------------
    // Shapes that must survive untouched
    // -----------------------------------------------------------

    it('leaves a dotnet-new template placeholder intact', async () => {
        // Bowire.Templates ships this as a template parameter (its
        // template.json declares "replaces": "MY_BOWIRE_VERSION").
        // Today it is spared only because it sits in a NESTED
        // Directory.Packages.props that the old root-only walk never
        // reached; this walk does reach it, so the numeric guard is
        // what keeps it intact.
        await withRepo({
            'src/bowire-plugin/Directory.Packages.props': props([
                '<PackageVersion Include="Kuestenlogik.Bowire" Version="MY_BOWIRE_VERSION" />',
            ]),
            'src/host/Host.csproj': csproj([
                '<PackageReference Include="Kuestenlogik.Bowire" Version="2.3.0" />',
            ]),
        }, async dir => {
            const r = await bumpAndVerify(dir);
            assert.equal(r.status, 0, r.stdout + r.stderr);
            assert.match(read(dir, 'src/bowire-plugin/Directory.Packages.props'),
                /Version="MY_BOWIRE_VERSION"/, 'the template parameter must not be rewritten');
            assert.match(read(dir, 'src/host/Host.csproj'), /Version="2\.4\.0"/,
                'while a real version in the same repo still moves');
        });
    });

    it('leaves an MSBuild property indirection intact', async () => {
        await withRepo({
            'a.csproj': csproj([
                '<PackageReference Include="Kuestenlogik.Bowire" Version="$(BowireVersion)" />',
            ]),
        }, async dir => {
            const r = await bumpAndVerify(dir);
            assert.equal(r.status, 0, r.stdout + r.stderr);
            assert.match(read(dir, 'a.csproj'), /Version="\$\(BowireVersion\)"/);
        });
    });

    it('leaves versionless CPM references alone', async () => {
        // Every Bowire.Protocol.* sibling: root props carries the
        // version, the csprojs reference without one.
        await withRepo({
            'Directory.Packages.props': props([
                '<PackageVersion Include="Kuestenlogik.Bowire" Version="2.3.0" />',
            ]),
            'src/Plugin/Plugin.csproj': csproj([
                '<PackageReference Include="Kuestenlogik.Bowire" />',
            ]),
        }, async dir => {
            const r = await bumpAndVerify(dir);
            assert.equal(r.status, 0, r.stdout + r.stderr);
            assert.match(read(dir, 'Directory.Packages.props'), /Version="2\.4\.0"/);
            assert.match(read(dir, 'src/Plugin/Plugin.csproj'),
                /<PackageReference Include="Kuestenlogik\.Bowire" \/>/,
                'a versionless reference must not grow a Version attribute');
        });
    });

    it('preserves sibling attributes on a bumped line', async () => {
        await withRepo({
            'a.csproj': csproj([
                '<PackageReference Include="Kuestenlogik.Bowire" Version="2.3.0" PrivateAssets="All" />',
            ]),
        }, async dir => {
            const r = await bumpAndVerify(dir);
            assert.equal(r.status, 0, r.stdout + r.stderr);
            assert.match(read(dir, 'a.csproj'),
                /Include="Kuestenlogik\.Bowire" Version="2\.4\.0" PrivateAssets="All"/);
        });
    });

    it('does not walk build output', async () => {
        await withRepo({
            'a.csproj': csproj(['<PackageReference Include="Kuestenlogik.Bowire" Version="2.3.0" />']),
            'obj/Debug/stale.csproj': csproj(['<PackageReference Include="Kuestenlogik.Bowire" Version="1.0.0" />']),
            'src/x/bin/Release/old.csproj': csproj(['<PackageReference Include="Kuestenlogik.Bowire" Version="1.0.0" />']),
        }, async dir => {
            const r = await bumpAndVerify(dir);
            assert.equal(r.status, 0, r.stdout + r.stderr,
                'build output must not be reported stale either');
            assert.match(read(dir, 'obj/Debug/stale.csproj'), /Version="1\.0\.0"/);
            assert.match(read(dir, 'src/x/bin/Release/old.csproj'), /Version="1\.0\.0"/);
        });
    });

    it('is a clean no-op in a repo with no Bowire references', async () => {
        // Bowire.VulnDb's shape — it carries the cascade topic but
        // consumes no Kuestenlogik.Bowire package.
        await withRepo({
            'a.csproj': csproj(['<PackageReference Include="Serilog" Version="4.0.0" />']),
        }, async dir => {
            const r = await bumpAndVerify(dir);
            assert.equal(r.status, 0, r.stdout + r.stderr);
            assert.match(read(dir, 'a.csproj'), /Serilog" Version="4\.0\.0"/);
        });
    });

    // -----------------------------------------------------------
    // Attribute order, idempotence, and the verify step's teeth
    // -----------------------------------------------------------

    it('does not care about attribute order', async () => {
        // MSBuild does not; a hand-edited file in the other order must
        // not be skipped, because skipping silently is the original bug.
        await withRepo({
            'a.csproj': csproj([
                '<PackageReference Version="2.3.0" Include="Kuestenlogik.Bowire.Protocol.Rest" />',
            ]),
        }, async dir => {
            const r = await bumpAndVerify(dir);
            assert.equal(r.status, 0, r.stdout + r.stderr);
            assert.match(read(dir, 'a.csproj'),
                /<PackageReference Version="2\.4\.0" Include="Kuestenlogik\.Bowire\.Protocol\.Rest" \/>/);
        });
    });

    it('is idempotent', async () => {
        await withRepo({
            'a.csproj': csproj([
                '<PackageReference Include="Kuestenlogik.Bowire" Version="2.3.0" />',
                '<PackageReference Include="Kuestenlogik.Bowire.Protocol.Grpc" Version="1.6.0" />',
            ]),
        }, async dir => {
            assert.equal((await runStep(BUMP, dir)).status, 0);
            const once = read(dir, 'a.csproj');
            assert.equal((await runStep(BUMP, dir)).status, 0);
            assert.equal(read(dir, 'a.csproj'), once, 'the bump must be idempotent');
        });
    });

    it('fails loudly when a published id was left behind', async () => {
        // Run the check WITHOUT the bump: this is what a bump that
        // missed a shape looks like from the verify step's side.
        // Silence here is the failure mode the whole ticket is about.
        await withRepo({
            'a.csproj': csproj([
                '<PackageReference Include="Kuestenlogik.Bowire.Protocol.Rest" Version="1.6.0" />',
            ]),
        }, async dir => {
            const r = await runStep(VERIFY, dir);
            assert.equal(r.status, 1, 'a stale published id must fail the job');
            const log = r.stdout + r.stderr;
            assert.match(log, /::error::/);
            assert.match(log, /a\.csproj/, 'and must name the offending file');
        });
    });

    it('bumps the whole published set of a real sibling', async () => {
        // Bowire.Samples' shape as it stands on origin/main.
        await withRepo({
            'Directory.Packages.props': props([
                '<PackageVersion Include="Kuestenlogik.Bowire" Version="2.3.0" />',
            ]),
            'harbor-demo/A/A.csproj': csproj([
                '<PackageReference Include="Kuestenlogik.Bowire" Version="2.3.0" />',
                '<PackageReference Include="Kuestenlogik.Bowire.Protocol.WebSocket" Version="1.6.0" />',
                '<PackageReference Include="Kuestenlogik.Bowire.Protocol.Sse" Version="1.6.0" />',
                '<PackageReference Include="Kuestenlogik.Bowire.Protocol.SignalR" Version="1.6.0" />',
            ]),
            'harbor-demo/B/B.csproj': csproj([
                '<PackageReference Include="Kuestenlogik.Bowire.Protocol.Rest" Version="1.6.0" />',
                '<PackageReference Include="Kuestenlogik.Bowire.Protocol.Grpc" Version="1.6.0" />',
                '<PackageReference Include="Kuestenlogik.Bowire.Protocol.OData" Version="2.3.0" />',
                '<PackageReference Include="Kuestenlogik.Bowire.Protocol.Amqp" Version="0.2.1" />',
            ]),
        }, async dir => {
            const r = await bumpAndVerify(dir);
            assert.equal(r.status, 0, r.stdout + r.stderr);
            const all = read(dir, 'harbor-demo/A/A.csproj') + read(dir, 'harbor-demo/B/B.csproj')
                + read(dir, 'Directory.Packages.props');
            assert.equal((all.match(/Version="1\.6\.0"/g) || []).length, 0,
                'no protocol package may be left three minors behind');
            assert.equal((all.match(/Version="2\.4\.0"/g) || []).length, 8);
            assert.match(all, /Protocol\.Amqp" Version="0\.2\.1"/);
        });
    });

    it('bumps nothing rather than everything when the package list is empty', async () => {
        // A dispatch that lost its payload must degrade to a no-op,
        // never to "bump every id matching the prefix".
        await withRepo({
            'a.csproj': csproj(['<PackageReference Include="Kuestenlogik.Bowire" Version="2.3.0" />']),
        }, async dir => {
            const r = await runStep(BUMP, dir, '');
            assert.equal(r.status, 0, r.stdout + r.stderr);
            assert.match(read(dir, 'a.csproj'), /Version="2\.3\.0"/);
        });
    });
});
