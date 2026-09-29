// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0
//
// scripts/ci/resolve-milestone.mjs — which milestone a tag delivers.
//
// A milestone is a release again (`v2.8 — <theme>`), so the version finds it; nothing is inferred.
// A patch release has its own milestone when it is planned (`v2.8.1 — …`), a second product in
// the same repository its own prefix (`VS Code v1.1 — …`), and neither is picked up by a Bowire tag.

import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { dirname, resolve as resolvePath } from 'node:path';

const __dirname = dirname(fileURLToPath(import.meta.url));
const script = resolvePath(__dirname, '../../../scripts/ci/resolve-milestone.mjs');
const { resolve, outputs, theme } = await import(pathToFileURL(script).href);

const ms = (number, title, state = 'open') => ({ number, title, state });
const REPO = 'Kuestenlogik/Bowire';
const all = [
    ms(17, 'v2.7 — Geospatial map', 'closed'),
    ms(19, 'v2.8 — Localisation, test pillar, MCP completion and the agent hub'),
    ms(22, 'v2.9 — Discoverability'),
    ms(28, 'VS Code v1.1 — Marketplace publish via Entra ID'),
    ms(30, 'v2.8.1 — Fixes'),
];

describe('resolve', () => {
    it('finds the release by its version', () => {
        assert.deepEqual(resolve('v2.8.0', all, REPO).map(m => m.number), [19]);
        assert.deepEqual(resolve('v2.7.0', all, REPO).map(m => m.number), [17]);
    });
    it('gives a patch release its own milestone, not its minor line', () => {
        assert.deepEqual(resolve('v2.8.1', all, REPO).map(m => m.number), [30]);
    });
    it('takes a prerelease as the release it previews', () => {
        assert.deepEqual(resolve('v2.9.0-rc.1', all, REPO).map(m => m.number), [22]);
    });
    it('does not pick up another product of the same repository', () => {
        assert.deepEqual(resolve('v1.1.0', all, REPO), []);
    });
    it('is nothing for a release nobody planned', () => {
        assert.deepEqual(resolve('v2.14.0', all, REPO), []);
    });
    it('uses the repository product for a plugin repository', () => {
        const akka = [ms(3, 'v1.2 — Cluster sharding')];
        assert.deepEqual(resolve('v1.2.0', akka, 'Kuestenlogik/Bowire.Protocol.Akka').map(m => m.number), [3]);
    });
});

describe('outputs', () => {
    it('names the milestone, its theme and its number', () => {
        const out = outputs(resolve('v2.8.0', all, REPO));
        assert.equal(out.milestone, 'v2.8 — Localisation, test pillar, MCP completion and the agent hub');
        assert.equal(out.theme, 'Localisation, test pillar, MCP completion and the agent hub');
        assert.equal(out.numbers, '19');
    });
    it('is empty when nothing resolved', () => {
        assert.deepEqual(outputs([]), { milestones: '', milestone: '', theme: '', numbers: '' });
    });
    it('reads the theme of a second product', () => {
        assert.equal(theme('VS Code v1.1 — Marketplace publish via Entra ID'), 'Marketplace publish via Entra ID');
    });
});
