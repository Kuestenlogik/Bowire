// #738 — every quickstart path ends on its finish flag.
//
// site/quickstart.html is one page with a path picker; each step is an
// <article data-path="…"> shown for the paths it names. The four original
// paths each ended on a flag bubble; the two added later (Cruise ship,
// Tugboat) came without one and simply stopped on a number. These tests
// read the page as it ships and hold, for every path the picker offers:
// its steps count up without gaps, and the last one is exactly one flag.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, resolve } from 'node:path';

const __dirname = dirname(fileURLToPath(import.meta.url));
// Comments may quote markup; only live elements count. A scan rather than a
// regex replace: no `<!--` survives, and an unclosed comment runs to the end
// of the file, as it does in a browser.
function stripComments(text) {
    let out = '';
    let at = 0;
    for (;;) {
        const open = text.indexOf('<!--', at);
        if (open < 0) return out + text.slice(at);
        out += text.slice(at, open);
        const close = text.indexOf('-->', open + 4);
        if (close < 0) return out;
        at = close + 3;
    }
}
const html = stripComments(readFileSync(resolve(__dirname, '../../../site/quickstart.html'), 'utf8'));

// The paths the picker offers.
function pickerPaths() {
    const picker = html.match(/data-path-picker[\s\S]*?<\/div>/);
    assert.ok(picker, 'the path picker is on the page');
    return [...picker[0].matchAll(/data-path="([^"]+)"/g)].map((m) => m[1]);
}

// Every step article in page order: its paths, whether it is a finish,
// and the number in its bubble (null for a flag).
function steps() {
    return [...html.matchAll(/<article class="([^"]*quickstart-page-step[^"]*)"[^>]*\bdata-path="([^"]+)"[^>]*>([\s\S]*?)<\/article>/g)]
        .map((m) => {
            const num = m[3].match(/<div class="quickstart-page-step-num"[^>]*>\s*(\d+)\s*<\/div>/);
            return {
                finish: m[1].split(/\s+/).includes('quickstart-page-step-finish'),
                paths: m[2].split(/\s+/),
                num: num ? Number(num[1]) : null,
                id: (m[0].match(/\bid="([^"]+)"/) || [])[1],
            };
        });
}

test('the picker offers the six boats', () => {
    assert.deepEqual(pickerPaths().sort(), ['container', 'embedded', 'mock', 'multiuser', 'standalone', 'vscode']);
});

for (const path of pickerPaths()) {
    test(`the ${path} path counts up from 2 and ends on one flag`, () => {
        const own = steps().filter((s) => s.paths.includes(path));
        assert.ok(own.length >= 2, `${path} has steps`);

        const finishes = own.filter((s) => s.finish);
        assert.equal(finishes.length, 1, `${path} has exactly one finish flag`);
        assert.equal(own[own.length - 1].finish, true, `${path} ends on its flag, not on ${own[own.length - 1].id}`);
        assert.equal(finishes[0].num, null, 'a flag carries no number');

        // Step 1 is the shared path picker; each path continues at 2.
        const numbers = own.filter((s) => !s.finish).map((s) => s.num);
        assert.deepEqual(numbers, numbers.map((_, i) => i + 2), `${path} numbers its steps without gaps`);
    });
}
