// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0
//
// scripts/ci/discussion-title.mjs — the title of a release's Announcements discussion.
//
// The discussion took the release's name, which is the version alone, so from 2.8.0 on every
// announcement read "2.8.0" and nothing else. The discussion is renamed to the version and the
// delivery's title, the one `#` the release body opens with.

import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { dirname, resolve } from 'node:path';

const __dirname = dirname(fileURLToPath(import.meta.url));
const script = resolve(__dirname, '../../../scripts/ci/discussion-title.mjs');
const { deliveryTitle, discussionTitle, discussionNumber } = await import(pathToFileURL(script).href);

describe('discussionTitle', () => {
    it('is the version and the delivery\'s title', () => {
        assert.equal(
            discussionTitle('v2.10.0', '# A Bowire that follows long-running MCP tools\n\n2.10 is …\n\n## Highlights\n'),
            '2.10.0 — A Bowire that follows long-running MCP tools');
    });

    it('is the version alone when the body carries no title', () => {
        assert.equal(discussionTitle('v2.10.1', '## Highlights\n\n### A fix\n'), '2.10.1');
        assert.equal(discussionTitle('v2.10.1', ''), '2.10.1');
    });
});

describe('deliveryTitle', () => {
    it('takes the first `#` heading, not a `##` one', () => {
        assert.equal(deliveryTitle('## Highlights\n# The delivery\n'), 'The delivery');
        assert.equal(deliveryTitle('## Highlights\n'), null);
    });

    it('reads a body with Windows line ends', () => {
        assert.equal(deliveryTitle('# The delivery\r\n\r\nText'), 'The delivery');
    });
});

describe('discussionNumber', () => {
    it('reads the number off a release\'s discussion_url', () => {
        assert.equal(discussionNumber('https://github.com/Kuestenlogik/Bowire/discussions/985'), 985);
        assert.equal(discussionNumber('https://github.com/o/r/discussions/12?x=1'), 12);
    });

    it('is null for a release without a discussion', () => {
        assert.equal(discussionNumber(null), null);
        assert.equal(discussionNumber('https://github.com/o/r/releases/tag/v1'), null);
    });
});
