// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0
//
// scripts/ci/release-body.mjs — what a release is published as.
//
// The release name is the version and nothing else, because the Releases list truncates it to
// about that. The theme that used to sit beside it named the *milestone*, which is a section
// holding several deliveries, so v2.8.0 and v2.8.1 would have carried the same words. What the
// delivery is about belongs in the body, as the one `#` the existing `##` sections sit under.

import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { dirname, resolve } from 'node:path';

const __dirname = dirname(fileURLToPath(import.meta.url));
const script = resolve(__dirname, '../../../scripts/ci/release-body.mjs');
const { stripFrontMatter, frontMatterTitle, isPlaceholderTitle, compose, releaseName } =
    await import(pathToFileURL(script).href);

const notes = (title, body = '## The map\n\n### A path\n\nText.\n') =>
    `---\ntitle: ${title}\nversion: 2.7.0\n---\n\n${body}`;

describe('releaseName', () => {
    it('is the version, without the tag\'s v', () => {
        assert.equal(releaseName('v2.7.0'), '2.7.0');
        assert.equal(releaseName('v2.10.1'), '2.10.1');
    });

    it('takes a version that was handed over without one', () => {
        assert.equal(releaseName('2.7.0'), '2.7.0');
    });

    it('keeps a prerelease whole', () => {
        // The name has to say which artifact this is, and an rc is its own download.
        assert.equal(releaseName('v3.0.0-rc.1'), '3.0.0-rc.1');
    });
});

describe('stripFrontMatter', () => {
    it('removes the block the publisher must not print', () => {
        assert.equal(stripFrontMatter(notes('A title')), '## The map\n\n### A path\n\nText.\n');
    });

    it('leaves a body that never had one', () => {
        assert.equal(stripFrontMatter('## Only a body\n'), '## Only a body\n');
    });

    it('leaves an unterminated block alone rather than eating the file', () => {
        // A truncated front-matter is a broken file; swallowing the rest of it would publish an
        // empty release and look like the notes were never written.
        const broken = '---\ntitle: no end marker\n\n## The map\n';
        assert.equal(stripFrontMatter(broken), broken);
    });
});

describe('frontMatterTitle', () => {
    it('reads the delivery\'s own title', () => {
        assert.equal(
            frontMatterTitle(notes('A map you can read, and a gRPC plugin that needs no reflection')),
            'A map you can read, and a gRPC plugin that needs no reflection');
    });

    it('drops the quotes a YAML writer may have used', () => {
        assert.equal(frontMatterTitle(notes('"Quoted, because of the comma"')),
            'Quoted, because of the comma');
    });

    it('is nothing when the file carries no front-matter at all', () => {
        assert.equal(frontMatterTitle('## The map\n'), null);
    });
});

describe('isPlaceholderTitle', () => {
    it('catches the template', () => {
        assert.ok(isPlaceholderTitle('<fill in before the tag>'));
        assert.ok(isPlaceholderTitle(''));
        assert.ok(isPlaceholderTitle('   '));
    });

    it('catches a title that only repeats the version', () => {
        // v2.6.0 shipped with exactly this. As a heading it says nothing the tag does not.
        assert.ok(isPlaceholderTitle('Bowire v2.6.0'));
        assert.ok(isPlaceholderTitle('v2.6.0'));
        assert.ok(isPlaceholderTitle('2.6'));
    });

    it('lets a real sentence through, including one that names a version', () => {
        assert.ok(!isPlaceholderTitle('Storage isolation, and a rate limiter that throttled v2.6'));
        assert.ok(!isPlaceholderTitle('A map you can read'));
    });
});

describe('compose', () => {
    it('heads the body with the delivery\'s title', () => {
        const out = compose(notes('A map you can read'));
        assert.ok(out.startsWith('# A map you can read\n\n## The map'), out.slice(0, 60));
    });

    it('leaves the sections at the level they were written', () => {
        // The complaint was that the notes start at `##`. The fix is a heading above them, not a
        // promotion of every section — that would put four `#` next to each other.
        const out = compose(notes('A map you can read'));
        assert.equal((out.match(/^# /gm) ?? []).length, 1);
        assert.ok(out.includes('\n## The map'));
        assert.ok(out.includes('\n### A path'));
    });

    it('does not add a second title to a body that already has one', () => {
        const out = compose(notes('Front-matter title', '# The body brought its own\n\nText.\n'));
        assert.equal((out.match(/^# /gm) ?? []).length, 1);
        assert.ok(out.startsWith('# The body brought its own'));
    });

    it('publishes the body unheaded rather than heading it with a placeholder', () => {
        // Better a release without a title line than one titled "Bowire v2.6.0". The gate in
        // release.yml is what stops this reaching the page at all.
        const out = compose(notes('Bowire v2.6.0'));
        assert.ok(out.startsWith('## The map'), out.slice(0, 40));
    });

    it('does not leave the blank line the front-matter left behind', () => {
        assert.ok(!compose(notes('A title')).includes('\n\n\n'));
    });
});
