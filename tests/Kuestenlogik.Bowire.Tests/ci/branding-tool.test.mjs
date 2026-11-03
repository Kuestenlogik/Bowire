// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0
//
// scripts/branding/tool/ is a vendored copy of the shared branding tool (Kuestenlogik/Kuestenlogik, tools/branding),
// pinned by its pin.json: the version, the commit it came from and the SHA-256 of every file. These tests hold that
// the copy is exactly that version, so it cannot drift from the shared tool: a change goes into the shared tool and is
// vendored again (scripts/branding/tool/README.md, "Vendoring into a product").
//
// Run: node --test tests/Kuestenlogik.Bowire.Tests/ci/*.test.mjs

import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import crypto from 'node:crypto';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const tool = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../../../scripts/branding/tool');
const pin = JSON.parse(fs.readFileSync(path.join(tool, 'pin.json'), 'utf8'));
// The hash the pin uses, taken here on its own rather than through the copy's check-vendored.mjs: SHA-256 over the
// bytes with CRLF turned into LF.
const hash = (file) => crypto.createHash('sha256')
    .update(Buffer.from(fs.readFileSync(file, 'latin1').replace(/\r\n/g, '\n'), 'latin1')).digest('hex');
const filesOf = (dir, sub = '') => fs.readdirSync(path.join(dir, sub), { withFileTypes: true })
    .filter((e) => sub || (e.name !== 'pin.json' && e.name !== 'node_modules'))
    .flatMap((e) => (e.isDirectory() ? filesOf(dir, `${sub}${e.name}/`) : [`${sub}${e.name}`]));

describe('the vendored branding tool', () => {
    it('holds exactly the files of its pinned version', () => {
        assert.deepEqual(filesOf(tool).sort(), Object.keys(pin.files).sort());
    });
    it('has every file as pinned', () => {
        const differing = Object.entries(pin.files).filter(([f, h]) => hash(path.join(tool, f)) !== h).map(([f]) => f);
        assert.deepEqual(differing, [], `edited in place: ${differing.join(', ')}; change the shared tool and vendor it again`);
    });
    it('states the pinned version and commit', () => {
        assert.equal(JSON.parse(fs.readFileSync(path.join(tool, 'package.json'), 'utf8')).version, pin.version);
        assert.match(pin.commit, /^[0-9a-f]{40}$/);
    });
    it('passes its own check, which fails a copy edited in place', async () => {
        const { checkVendored } = await import(pathToFileURL(path.join(tool, 'check-vendored.mjs')).href);
        assert.deepEqual(checkVendored(tool), []);
        const copy = fs.mkdtempSync(path.join(os.tmpdir(), 'branding-tool-'));
        try {
            for (const f of [...filesOf(tool), 'pin.json']) {
                fs.mkdirSync(path.dirname(path.join(copy, f)), { recursive: true });
                fs.copyFileSync(path.join(tool, f), path.join(copy, f));
            }
            fs.appendFileSync(path.join(copy, 'generate-logos.mjs'), '\n// edited\n');
            fs.writeFileSync(path.join(copy, 'extra.mjs'), '');
            const problems = checkVendored(copy);
            assert.ok(problems.some((p) => p.startsWith('generate-logos.mjs: differs')), problems.join('\n'));
            assert.ok(problems.some((p) => p.startsWith('extra.mjs: not part')), problems.join('\n'));
        } finally {
            fs.rmSync(copy, { recursive: true, force: true });
        }
    });
});
