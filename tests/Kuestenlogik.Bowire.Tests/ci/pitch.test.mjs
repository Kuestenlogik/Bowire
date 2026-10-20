// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0
//
// #668 - wherever Bowire gets one line next to its name, it is the same line. A pitch that
// differs between the repository, the site and a package listing reads as three products.
// The line and its exceptions are in docs/brand-guide.md ("Writing Bowire").

import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join, resolve } from 'node:path';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '../../..');
const read = file => readFileSync(join(root, file), 'utf8');

const PITCH = 'The API workbench that embeds in your own service.';
// "Bowire — the API workbench …" and similar forms carry it mid-sentence.
const PITCH_INLINE = 'API workbench that embeds in your own service';

const SURFACES = [
    'README.md',
    '.asyncapi-tool',
    'CONTRIBUTING.md',
    'docs/brand-guide.md',
    'docs/index.md',
    'site/_config.yml',
    'site/_includes/hero.html',
    'site/index.html',
    'site/about.html',
    'site/brand.html',
    'site/llms.txt',
    'site/llms-full.txt',
    'images/og-image.svg',
    'scripts/site/og-image-template.html',
    'src/Kuestenlogik.Bowire/Kuestenlogik.Bowire.csproj',
    'src/Kuestenlogik.Bowire.Tool/Kuestenlogik.Bowire.Tool.csproj',
    'packaging/chocolatey/bowire.nuspec',
    'packaging/winget/template/KuestenLogik.Bowire.locale.en-US.yaml',
    'packaging/linux/nfpm.yaml',
    'packaging/linux/aur/PKGBUILD',
    'packaging/linux/bowire.desktop',
    'packaging/macos/Formula/bowire.rb',
    'packaging/msi/Bowire.wxs',
    'extensions/bowire-vscode/package.json',
    'extensions/bowire-vscode/README.md',
];

// Where the old line may stay: Bowire describing itself inside the product (shown inside other
// people's services, so not the pitch), a dated architecture note, and a test fixture.
const OLD_LINE_ALLOWED = new Set([
    'ROADMAP.md',
    'docs/brand-guide.md', // names the in-product descriptor as the exception
    'docs/architecture/security-testing.md',
    'docs/embedding/options.md',
    'src/Kuestenlogik.Bowire/BowireOptions.cs',
    'src/Kuestenlogik.Bowire/wwwroot/locales/en.json',
    'src/Kuestenlogik.Bowire.Ai/wwwroot/js/ai.js',
    'src/Kuestenlogik.Bowire.Tool/Cli/BowireCli.cs',
    'tests/Kuestenlogik.Bowire.Tests/BowireOptionsTests.cs',
    'tests/Kuestenlogik.Bowire.Tests/ci/english-only.test.mjs',
    'tests/Kuestenlogik.Bowire.Tests/ci/pitch.test.mjs',
]);

// Release notes are a dated record of what changed, so they may name the old line: the 2.9.1
// notes say the product keeps calling itself a multi-protocol API workbench inside a host.
const OLD_LINE_ALLOWED_DIRS = ['docs/release-notes/'];

describe('the one-line pitch', () => {
    it('is written down once, verbatim, in the brand guide', () => {
        assert.ok(read('docs/brand-guide.md').includes(`**${PITCH}**`));
    });

    for (const file of SURFACES) {
        it(`is carried by ${file}`, () => {
            assert.ok(read(file).includes(PITCH_INLINE), `${file} does not carry "${PITCH_INLINE}"`);
        });
    }

    it('has no leftover of the old line outside the product itself', () => {
        const hits = execFileSync('git', ['grep', '-l', '-i', 'multi-protocol API workbench'], { cwd: root, encoding: 'utf8' })
            .split('\n').filter(Boolean)
            .filter(file => !OLD_LINE_ALLOWED.has(file) && !OLD_LINE_ALLOWED_DIRS.some(dir => file.startsWith(dir)));
        assert.deepEqual(hits, []);
    });
});
