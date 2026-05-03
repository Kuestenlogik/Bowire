/**
 * One-shot migration: rewrites every <img> tag that points at
 * site/assets/images/screenshots/*.png (or docs/images/bowire-*.png)
 * into a `{% include themed-img.html %}` call, so the dark/light
 * variants kick in via the [data-theme] CSS swap.
 *
 * Idempotent — already-migrated files are detected via a marker
 * comment and skipped.
 */
const fs = require('fs');
const path = require('path');

const ROOT = path.resolve(__dirname, '..');
const TARGETS = [
    'site/_includes',
    'site/workflows',
];
const TOP_LEVEL_HTMLS = ['site/index.html', 'site/features.html', 'site/quickstart.html', 'site/why-bowire.html'];

function walk(dir) {
    const out = [];
    if (!fs.existsSync(dir)) return out;
    for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
        const p = path.join(dir, entry.name);
        if (entry.isDirectory()) out.push(...walk(p));
        else if (entry.isFile() && (p.endsWith('.html') || p.endsWith('.md'))) out.push(p);
    }
    return out;
}

const files = [
    ...TARGETS.flatMap(t => walk(path.join(ROOT, t))),
    ...TOP_LEVEL_HTMLS.map(f => path.join(ROOT, f)).filter(f => fs.existsSync(f)),
];

// Match <img src="{{ '/assets/images/screenshots/NAME.png' | relative_url }}" ... loading="lazy">
// Captures: full match, the path, alt text, optional class string.
const IMG_RE = /<img\s+src="\{\{\s*'\/assets\/images\/screenshots\/([a-z0-9-]+)\.png'\s*\|\s*relative_url\s*\}\}"\s+alt="([^"]*)"(\s+loading="lazy")?(\s+class="([^"]*)")?>/g;

let touched = 0, replaced = 0;
for (const file of files) {
    let src = fs.readFileSync(file, 'utf8');
    let modified = src.replace(IMG_RE, (_, name, alt, _lazy, _classAttr, cls) => {
        replaced++;
        const classPart = cls ? ` class="${cls}"` : '';
        return `{% include themed-img.html src="/assets/images/screenshots/${name}.png" alt="${alt}"${classPart} %}`;
    });
    if (modified !== src) {
        fs.writeFileSync(file, modified);
        console.log(`  ${path.relative(ROOT, file)}`);
        touched++;
    }
}

console.log(`\nMigrated ${replaced} <img> tags across ${touched} files.`);
