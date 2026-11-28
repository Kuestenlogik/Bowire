#!/usr/bin/env node
/**
 * #754 — every scaffold combination, end to end: generate the stub with
 * `bowire scaffold`, build and start it, run its own smoke test with
 * `bowire test`, stop it. A combination passes when the smoke test does.
 *
 * Run (after building the tool):
 *   node scripts/scaffold/verify.mjs
 *   node scripts/scaffold/verify.mjs --languages go,python --protocols rest,grpc,graphql
 *   node scripts/scaffold/verify.mjs --work C:/Projekte/_tmp/scaffold-verify
 *
 * Needs the toolchain of each language it runs: the .NET SDK, Go 1.22+,
 * Node 20+, Python 3.10+, Rust (cargo). A language whose toolchain is
 * missing is reported as such, not as a failure of its stub. Go is also
 * looked for in its default install folder; Rust on Windows needs LIB set
 * to the MSVC and Windows SDK libraries, as for any cargo build.
 *
 * The stub reports readiness with its "listening on" line, which is what
 * the run waits for; the limit on that wait is only the point at which a
 * stub that never comes up counts as failed.
 */
import { spawn, spawnSync } from 'node:child_process';
import { mkdirSync, rmSync, writeFileSync, existsSync, readdirSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, resolve, dirname, delimiter } from 'node:path';
import { fileURLToPath } from 'node:url';

const __dirname = dirname(fileURLToPath(import.meta.url));
const REPO = resolve(__dirname, '..', '..');
const TOOL = join(REPO, 'artifacts', 'bin', 'Kuestenlogik.Bowire.Tool', 'release', 'bowire.dll');

const args = process.argv.slice(2);
const opt = (name, fallback) => (args.includes(name) ? args[args.indexOf(name) + 1] : fallback);
const LANGUAGES = opt('--languages', 'csharp,go,node,python,rust').split(',');
const PROTOCOLS = opt('--protocols', 'rest').split(',');
const WORK = resolve(opt('--work', join(tmpdir(), 'bowire-scaffold-verify')));
const READY_LIMIT_MS = Number(opt('--ready-limit-ms', '900000'));

const env = { ...process.env };
const goBin = process.platform === 'win32' ? 'C:\\Program Files\\Go\\bin' : '/usr/local/go/bin';
if (existsSync(goBin)) env.PATH = goBin + delimiter + env.PATH;

const RUN = {
    csharp: { tool: ['dotnet', '--version'], start: ['dotnet', ['run']] },
    go: { tool: ['go', 'version'], start: ['go', ['run', '.']] },
    node: { tool: ['node', '--version'], start: ['node', ['server.mjs']] },
    python: { tool: [process.platform === 'win32' ? 'python' : 'python3', '--version'], start: [process.platform === 'win32' ? 'python' : 'python3', ['server.py']] },
    rust: { tool: ['cargo', '--version'], start: ['cargo', ['run', '--quiet']] },
};

const PYTHON = process.platform === 'win32' ? 'python' : 'python3';
const VENV_PYTHON = process.platform === 'win32' ? join('.venv', 'Scripts', 'python.exe') : join('.venv', 'bin', 'python');

// What a gRPC stub needs before it starts: its dependencies, and for Go its
// generated code. Each step is [command, args]; a failing step fails the
// combination with its output.
const PREPARE = {
    'grpc/node': [['npm install --no-audit --no-fund', null]],
    'grpc/python': [[PYTHON, ['-m', 'venv', '.venv']], [VENV_PYTHON, ['-m', 'pip', 'install', '--quiet', '-r', 'requirements.txt']]],
    'grpc/go': [['go', ['generate', './...']], ['go', ['mod', 'tidy']]],
    'graphql/node': [['npm install --no-audit --no-fund', null]],
    'graphql/python': [[PYTHON, ['-m', 'venv', '.venv']], [VENV_PYTHON, ['-m', 'pip', 'install', '--quiet', '-r', 'requirements.txt']]],
};
const START = {
    'grpc/python': [VENV_PYTHON, ['server.py']],
    'graphql/python': [VENV_PYTHON, ['server.py']],
};
// The languages each protocol has stubs in; the others are skipped.
const AVAILABLE = { rest: ['csharp', 'go', 'node', 'python', 'rust'], grpc: ['csharp', 'go', 'node', 'python', 'rust'], graphql: ['csharp', 'node', 'python'] };

// A spec that exercises what differs between languages: a required field,
// numbers, a bool, and a field named like a keyword (type).
function spec(protocol, language, port) {
    return {
        entity: 'Order', protocol, language, service: 'Orders',
        baseUrl: `http://localhost:${port}`,
        fields: [
            { name: 'customer', type: 'string', required: true },
            { name: 'quantity', type: 'int' },
            { name: 'express', type: 'bool' },
            { name: 'type', type: 'string' },
        ],
    };
}

function has(tool) {
    const r = spawnSync(tool[0], tool.slice(1), { env, encoding: 'utf8' });
    return r.status === 0;
}

function bowire(cwd, ...rest) {
    return spawnSync('dotnet', [TOOL, ...rest], { cwd, env, encoding: 'utf8' });
}

function stop(child) {
    if (child.exitCode !== null) return;
    if (process.platform === 'win32') spawnSync('taskkill', ['/pid', String(child.pid), '/t', '/f'], { stdio: 'ignore' });
    else child.kill('SIGTERM');
}

// Resolves when the stub prints its listening line, rejects when it exits
// first or the limit passes.
function started(child, log) {
    return new Promise((resolveReady, reject) => {
        const timer = setTimeout(() => reject(new Error('no "listening on" line within the limit')), READY_LIMIT_MS);
        const onData = (chunk) => {
            const text = chunk.toString();
            log.push(text);
            if (/listening on|Now listening on/i.test(text)) { clearTimeout(timer); resolveReady(); }
        };
        child.stdout.on('data', onData);
        child.stderr.on('data', (c) => log.push(c.toString()));
        child.on('exit', (code) => { clearTimeout(timer); reject(new Error(`the stub exited with ${code} before it listened`)); });
    });
}

async function verify(protocol, language, port) {
    const dir = join(WORK, `${protocol}-${language}`);
    rmSync(dir, { recursive: true, force: true });
    mkdirSync(dir, { recursive: true });
    const specFile = join(WORK, `${protocol}-${language}.spec.json`);
    writeFileSync(specFile, JSON.stringify(spec(protocol, language, port), null, 2));

    const gen = bowire(WORK, 'scaffold', '--spec', specFile, '--out', dir, '--force');
    if (gen.status !== 0) return { result: 'FAIL', detail: 'scaffold: ' + (gen.stderr || gen.stdout).trim() };

    for (const [step, stepArgs] of PREPARE[`${protocol}/${language}`] || []) {
        // npm is a .cmd on Windows and needs a shell; its line has no input in it.
        const r = stepArgs === null
            ? spawnSync(step, { cwd: dir, env, encoding: 'utf8', shell: true })
            : spawnSync(step, stepArgs, { cwd: dir, env, encoding: 'utf8' });
        if (r.status !== 0) return { result: 'FAIL', detail: `${step} ${(stepArgs || []).join(' ')}:
${r.stdout}${r.stderr}` };
    }
    const [cmd, cmdArgs] = START[`${protocol}/${language}`] || RUN[language].start;
    const child = spawn(cmd, cmdArgs, { cwd: dir, env });
    const log = [];
    try {
        await started(child, log);
        const testFile = readdirSync(join(dir, 'bowire')).find((f) => f.endsWith('.smoke-test.json'));
        const test = bowire(dir, 'test', join('bowire', testFile));
        if (test.status !== 0) return { result: 'FAIL', detail: 'bowire test:\n' + test.stdout + test.stderr };
        return { result: 'PASS', detail: (test.stdout.match(/\d+ passed[^\n]*/) || [''])[0] };
    } catch (err) {
        return { result: 'FAIL', detail: err.message + '\n' + log.join('').slice(-3000) };
    } finally {
        stop(child);
    }
}

if (!existsSync(TOOL)) {
    console.error(`Build the tool first: ${TOOL} is missing.`);
    process.exit(2);
}
mkdirSync(WORK, { recursive: true });
const rows = [];
let port = 5300;
for (const protocol of PROTOCOLS) {
    for (const language of LANGUAGES) {
        port++;
        if (!RUN[language]) { rows.push([protocol, language, 'UNKNOWN', 'no such language']); continue; }
        if (!(AVAILABLE[protocol] || []).includes(language)) continue;
        if (!has(RUN[language].tool)) { rows.push([protocol, language, 'NO TOOLCHAIN', RUN[language].tool[0] + ' not found']); continue; }
        process.stdout.write(`${protocol} / ${language} … `);
        const r = await verify(protocol, language, port);
        console.log(r.result);
        rows.push([protocol, language, r.result, r.detail]);
    }
}
console.log('');
for (const [p, l, r, d] of rows) console.log(`${p.padEnd(6)} ${l.padEnd(8)} ${r.padEnd(12)} ${r === 'PASS' ? d : ''}`);
for (const [p, l, r, d] of rows) if (r === 'FAIL') console.log(`\n--- ${p} / ${l}\n${d}`);
process.exit(rows.some((row) => row[2] === 'FAIL') ? 1 : 0);
