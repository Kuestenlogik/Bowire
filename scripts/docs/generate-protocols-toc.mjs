#!/usr/bin/env node
// Regenerate docs/protocols/toc.yml.
//
//   node scripts/docs/generate-protocols-toc.mjs          # write
//   node scripts/docs/generate-protocols-toc.mjs --check   # fail if stale
//
// The navigation used to be a hand-kept list, which is the other half of why a
// plugin could not contribute its own page: even with the file fetched, nothing
// would link to it until somebody edited a file in this repository. Now the
// in-repo protocols keep their deliberate order and everything contributed by a
// plugin repository is appended automatically, so a new plugin appears on the
// site the first time it publishes a page.
//
// Titles come from each page's YAML front matter, so the navigation label and
// the page heading cannot drift apart.

import { readFile, writeFile, readdir } from 'node:fs/promises';
import { join } from 'node:path';

const DIR = join('docs', 'protocols');
const TOC = join(DIR, 'toc.yml');

// Protocols implemented in this repository, in the order they should read.
// Deliberate rather than alphabetical: the ones most people arrive for come
// first. Anything not listed here and not `index`/`custom` is treated as
// plugin-contributed.
const IN_REPO_ORDER = [
    'grpc', 'signalr', 'sse', 'rest', 'graphql', 'websocket', 'mcp',
    'mqtt', 'nats', 'soap', 'jsonrpc', 'pulsar', 'socketio', 'odata',
];

// Contributed pages whose position is fixed because they predate discovery;
// anything not named here sorts alphabetically after them. Keeping the current
// order means turning the generator on changes no URLs and no navigation.
const CONTRIBUTED_ORDER = [
    'surgewave', 'kafka', 'amqp', 'dis', 'udp', 'akka', 'tacticalapi',
];

/** The `title:` from a page's YAML front matter, or null. */
async function titleOf(slug) {
    const text = await readFile(join(DIR, `${slug}.md`), 'utf8');
    if (!text.trimStart().startsWith('---')) return null;
    const end = text.indexOf('\n---', 3);
    if (end < 0) return null;
    for (const line of text.slice(0, end).split('\n')) {
        const m = line.match(/^title:\s*(.+?)\s*$/);
        if (!m) continue;
        return m[1].replace(/^['"]|['"]$/g, '');
    }
    return null;
}

async function build() {
    const files = (await readdir(DIR))
        .filter(f => f.endsWith('.md'))
        .map(f => f.slice(0, -3));

    const known = new Set([...IN_REPO_ORDER, 'index', 'custom']);
    const contributed = files.filter(f => !known.has(f));

    // Fixed order first, then anything new, alphabetically.
    const ordered = [
        ...CONTRIBUTED_ORDER.filter(s => contributed.includes(s)),
        ...contributed.filter(s => !CONTRIBUTED_ORDER.includes(s)).sort(),
    ];

    const missing = IN_REPO_ORDER.filter(s => !files.includes(s));
    if (missing.length) throw new Error(`listed in IN_REPO_ORDER but absent: ${missing.join(', ')}`);

    const entries = [
        { name: 'Overview', href: 'index.md' },
        ...await Promise.all(IN_REPO_ORDER.map(async s => ({ name: await titleOf(s) ?? s, href: `${s}.md` }))),
        ...await Promise.all(ordered.map(async s => ({ name: await titleOf(s) ?? s, href: `${s}.md` }))),
        { name: 'Custom Protocols', href: 'custom.md' },
    ];

    return entries.map(e => `- name: ${e.name}\n  href: ${e.href}`).join('\n') + '\n';
}

const yaml = await build();

if (process.argv.includes('--check')) {
    const current = await readFile(TOC, 'utf8');
    if (current.replace(/\r\n/g, '\n') !== yaml) {
        console.error('docs/protocols/toc.yml is stale — run scripts/docs/generate-protocols-toc.mjs');
        process.exit(1);
    }
    console.log('toc.yml is up to date.');
} else {
    await writeFile(TOC, yaml, 'utf8');
    console.log(`Wrote ${TOC}.`);
}
