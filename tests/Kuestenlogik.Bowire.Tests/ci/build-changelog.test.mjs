// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0
//
// scripts/site/build-changelog.mjs — the changelog page is the release notes, newest first (#671).

import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { dirname, resolve } from 'node:path';

const __dirname = dirname(fileURLToPath(import.meta.url));
const { parseNotes, demoteHeadings, compareDesc } = await import(pathToFileURL(resolve(__dirname, '../../../scripts/site/build-changelog.mjs')).href);

describe('parseNotes', () => {
    it('reads the title and version from the front-matter and keeps the body', () => {
        const n = parseNotes('---\n# a comment\ntitle: A map you can read\nversion: 2.7.0\n---\n\nThe map widget…\n\n## The map\n');
        assert.equal(n.title, 'A map you can read');
        assert.equal(n.version, '2.7.0');
        assert.ok(n.body.startsWith('The map widget'));
        assert.ok(!n.body.includes('title:'));
    });
    it('takes a file without front-matter as all body', () => {
        assert.deepEqual(parseNotes('## Only a body'), { title: null, version: null, body: '## Only a body' });
    });
});

describe('demoteHeadings', () => {
    it('puts every section one level under the version heading', () => {
        assert.equal(demoteHeadings('## Section\n### Sub\ntext'), '### Section\n#### Sub\ntext');
    });
    it('leaves code alone', () => {
        assert.equal(demoteHeadings('```bash\n# a comment\n```\n## After'), '```bash\n# a comment\n```\n### After');
    });
});

describe('compareDesc', () => {
    it('puts the newest release first, 2.10 before 2.9', () => {
        assert.deepEqual(['2.9.0', '2.10.0', '2.8.1', '3.0.0'].sort(compareDesc), ['3.0.0', '2.10.0', '2.9.0', '2.8.1']);
    });
});
