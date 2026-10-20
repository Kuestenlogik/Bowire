// Tests for the Help package's in-browser markdown renderer
// (helpRenderMarkdown in src/Kuestenlogik.Bowire.Help/wwwroot/js/help.js).
//
// The renderer is a line-by-line loop: every branch must advance the
// cursor, or the tab freezes. A line that no block branch claims but that
// the paragraph collector refuses (it looks structural) used to spin
// forever, so each render runs under a vm timeout — a regression fails
// the test instead of hanging the suite.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import vm from 'node:vm';
import { compileFragment } from './_load-fragment.mjs';

const load = compileFragment(
    '../../../src/Kuestenlogik.Bowire.Help/wwwroot/js/help.js',
    [],
    '\nreturn { helpRenderMarkdown: helpRenderMarkdown };'
);
const { helpRenderMarkdown } = load();

function render(md) {
    return vm.runInNewContext('render(md)', { render: helpRenderMarkdown, md }, { timeout: 2000 });
}

test('a heading marker with no text renders as a paragraph instead of hanging', () => {
    assert.equal(render('# '), '<p class="bowire-help-md-p"># </p>');
    assert.equal(render('## '), '<p class="bowire-help-md-p">## </p>');
});

test('a fence with a non-word info string renders as a code block instead of hanging', () => {
    assert.equal(
        render('```c++\nint x;\n```'),
        '<pre class="bowire-help-md-pre" data-lang="c++"><code>int x;</code></pre>');
});

test('a structural-looking line in the middle of a paragraph still ends it cleanly', () => {
    const html = render('first line\n# \nafter');
    assert.equal(
        html,
        '<p class="bowire-help-md-p">first line</p>\n'
        + '<p class="bowire-help-md-p"># </p>\n'
        + '<p class="bowire-help-md-p">after</p>');
});

test('ordinary headings and fenced blocks still render as before', () => {
    assert.equal(render('# Title'), '<h1 class="bowire-help-md-h1">Title</h1>');
    assert.equal(
        render('```js\na < b\n```'),
        '<pre class="bowire-help-md-pre" data-lang="js"><code>a &lt; b</code></pre>');
});
