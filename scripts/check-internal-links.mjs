#!/usr/bin/env node
// Walk every Jekyll-served HTML page under site/ and verify every internal
// link resolves to a file the build will produce. Marketing-site pages live
// in site/*.html, site/solutions/*.html, site/workflows/*.html; DocFX docs
// live in docs/**/*.md (built into docs/ on the deployed site).
//
// Resolves Jekyll's {{ '/path' | relative_url }} wrapper, strips anchor
// fragments, skips external URLs / mailto / tel / javascript. Anything
// that resolves to a relative or root-anchored path is checked against
// the on-disk file set.
//
// Run: `node scripts/check-internal-links.mjs`
// Exit 0 if everything resolves, exit 1 if there are dead links.

import { readFile, readdir, stat } from 'node:fs/promises';
import { dirname, join, resolve, relative, posix } from 'node:path';
import { fileURLToPath } from 'node:url';

const __dirname = dirname(fileURLToPath(import.meta.url));
const ROOT = resolve(__dirname, '..');
const SITE_DIR = join(ROOT, 'site');
const DOCS_DIR = join(ROOT, 'docs');

async function walk(dir, exts) {
  const out = [];
  async function recurse(d) {
    let entries;
    try { entries = await readdir(d, { withFileTypes: true }); }
    catch { return; }
    for (const e of entries) {
      if (e.name.startsWith('_site') || e.name === '_site' || e.name === 'node_modules') continue;
      const p = join(d, e.name);
      if (e.isDirectory()) await recurse(p);
      else if (exts.some(ext => e.name.endsWith(ext))) out.push(p);
    }
  }
  await recurse(dir);
  return out;
}

function extractHrefs(content) {
  // Captures href="..." values (both single and double quotes).
  const re = /\bhref\s*=\s*("([^"]*)"|'([^']*)')/g;
  const found = [];
  let m;
  while ((m = re.exec(content)) !== null) {
    const raw = m[2] !== undefined ? m[2] : m[3];
    found.push(raw);
  }
  return found;
}

// Markdown link extractor — matches [text](url). Skips reference-style
// links and image syntax. URL ends at the first unescaped ')' that
// isn't inside balanced parens (rare but does happen in URLs).
function extractMarkdownLinks(content) {
  const found = [];
  // Strip code blocks / inline code first so links *inside* code samples
  // don't get matched. The codebase's docs use ```fenced``` + `inline`.
  const stripped = content
    .replace(/```[\s\S]*?```/g, '')
    .replace(/`[^`\n]*`/g, '');
  // [text](url) — text can't contain unescaped ']', url is everything
  // up to the next ')'.
  const re = /(?<!!)\[([^\]]*)\]\(([^)\s]+)(?:\s+"[^"]*")?\)/g;
  let m;
  while ((m = re.exec(stripped)) !== null) {
    found.push(m[2]);
  }
  return found;
}

function resolveJekyllPath(href) {
  // Jekyll's {{ '/path/to/page.html' | relative_url }} wrapper. We don't
  // know the site's baseurl in pure JS terms, but for marketing-site link
  // checking the inner string is what matters — relative_url just adds the
  // baseurl on top, which is the same prefix for every link on the site.
  const m = href.match(/\{\{\s*['"]([^'"]+)['"]\s*\|\s*relative_url\s*\}\}/);
  if (m) return m[1];
  return href;
}

function classify(href) {
  if (!href || href.trim() === '') return { kind: 'empty' };
  const h = href.trim();
  if (/^(https?:|mailto:|tel:|javascript:|data:|xref:)/i.test(h)) return { kind: 'external' };
  if (h.startsWith('#')) return { kind: 'anchor', target: h };
  if (h.startsWith('//')) return { kind: 'external' };
  const resolved = resolveJekyllPath(h);
  // Strip query + fragment for resolution
  const [pathOnly] = resolved.split('#');
  const [cleanPath] = pathOnly.split('?');
  if (!cleanPath) return { kind: 'anchor', target: h };
  return { kind: 'internal', target: cleanPath, raw: h };
}

async function exists(p) {
  try { await stat(p); return true; }
  catch { return false; }
}

function toPosix(p) { return p.split('\\').join('/'); }

async function resolveTarget(target, sourceFile) {
  // Targets are typically root-anchored ('/quickstart.html') after Jekyll's
  // relative_url. Map them under site/. /docs/* targets land in docs/ at the
  // top level (DocFX-built; we check the .md source instead of the built
  // .html since the build hasn't run when this script does).
  if (target.startsWith('/docs/')) {
    // /docs/setup/embedded.html -> docs/setup/embedded.md
    let p = target.replace(/^\/docs\//, '');
    p = p.replace(/\.html$/, '.md');
    if (p === '' || p.endsWith('/')) p += 'index.md';
    return { kind: 'docs', path: join(DOCS_DIR, p) };
  }
  if (target.startsWith('/')) {
    // Marketing-site root-anchored: '/solutions/security.html' -> site/solutions/security.html
    let p = target.replace(/^\//, '');
    if (p === '' || p.endsWith('/')) p += 'index.html';
    return { kind: 'site', path: join(SITE_DIR, p) };
  }
  // Relative path — resolve against the source file's directory.
  // For Markdown sources in docs/, a relative ../foo.md is the common shape;
  // for HTML sources, relative is less common but supported.
  const dir = dirname(sourceFile);
  let p = target;
  if (p === '' || p.endsWith('/')) {
    p += sourceFile.endsWith('.md') ? 'index.md' : 'index.html';
  }
  // .html -> .md when the source is a .md file and the link probably
  // points to another doc rendered to .html by DocFX. (Some docs use
  // .html-suffixed links between .md files; resolve them by checking
  // for the .md sibling.)
  const candidate = resolve(dir, p);
  return { kind: 'relative', path: candidate };
}

// Whether a source file is one we author. Skips DocFX-generated API doc
// HTML (site/docs/api/**) and Pagefind output (site/pagefind/**) — those
// are built artefacts, not pages we maintain, and they carry JS-string
// pseudo-hrefs (e.g. `' + rootHref + '`) that aren't real links.
function isAuthoredPage(file) {
  const p = toPosix(file);
  if (p.includes('/site/docs/')) return false;       // DocFX output (api/, conceptual/)
  if (p.includes('/site/pagefind/')) return false;   // Pagefind output
  if (p.includes('/_site/')) return false;           // Jekyll local build output
  return true;
}

// Whether an href is something we can statically check. Skips templated
// hrefs that resolve at Jekyll render time from a variable (e.g.
// `{{ rel.url }}` inside a {% for %} loop) — we'd need to execute Liquid
// to know the value.
function isStaticHref(href) {
  if (!href) return false;
  // Resolve Jekyll's {{ '...' | relative_url }} wrapper first; that one
  // is safe to check (the inner string is the path).
  const stripped = resolveJekyllPath(href);
  // Any remaining {{ ... }} after the unwrap means it's a variable href,
  // not a literal — skip.
  if (/\{\{/.test(stripped)) return false;
  // Pagefind assets are built into _combined/pagefind/ in CI; they're
  // never present in the source tree, so don't flag them.
  if (stripped.startsWith('/pagefind/')) return false;
  return true;
}

async function checkOne(file, hrefs, issues) {
  let checked = 0;
  for (const raw of hrefs) {
    if (!isStaticHref(raw)) continue;
    const c = classify(raw);
    if (c.kind !== 'internal') continue;
    checked++;
    const resolved = await resolveTarget(c.target, file);
    let ok = await exists(resolved.path);
    // Markdown sources often link to siblings with .html suffix (DocFX
    // renders to .html). Let the .html link resolve if the sibling .md
    // exists, and vice versa.
    if (!ok && resolved.path.endsWith('.html')) {
      ok = await exists(resolved.path.replace(/\.html$/, '.md'));
    } else if (!ok && resolved.path.endsWith('.md')) {
      ok = await exists(resolved.path.replace(/\.md$/, '.html'));
    }
    // Directory-style targets ('../setup/') — try index.{md,html}.
    if (!ok) {
      if (await exists(join(resolved.path, 'index.md'))) ok = true;
      else if (await exists(join(resolved.path, 'index.html'))) ok = true;
    }
    if (!ok) {
      issues.push({
        file: toPosix(relative(ROOT, file)),
        href: c.raw,
        resolvedTo: toPosix(relative(ROOT, resolved.path)),
        targetKind: resolved.kind,
      });
    }
  }
  return checked;
}

async function main() {
  // Source 1: Marketing-site HTML pages (top-level + solutions + workflows
  // + includes + layouts). DocFX-built site/docs/** is filtered out below.
  const siteFiles = (await walk(SITE_DIR, ['.html'])).filter(isAuthoredPage);
  // Source 2: DocFX Markdown sources under docs/**. These carry both
  // [text](url) Markdown links and raw <a href="..."> HTML links, so the
  // extractor combines both extractors.
  const docsFiles = await walk(DOCS_DIR, ['.md']);

  const issues = [];
  let totalChecked = 0;

  for (const file of siteFiles) {
    let content;
    try { content = await readFile(file, 'utf8'); }
    catch { continue; }
    totalChecked += await checkOne(file, extractHrefs(content), issues);
  }

  for (const file of docsFiles) {
    let content;
    try { content = await readFile(file, 'utf8'); }
    catch { continue; }
    const hrefs = [...extractHrefs(content), ...extractMarkdownLinks(content)];
    totalChecked += await checkOne(file, hrefs, issues);
  }

  console.log(`[link-check] scanned ${siteFiles.length} site HTML + ${docsFiles.length} docs MD, checked ${totalChecked} internal links`);
  if (issues.length === 0) {
    console.log('[link-check] all internal links resolve.');
    return;
  }

  console.log(`[link-check] ${issues.length} broken link(s):`);
  // Group by file so the output is easier to act on.
  const byFile = new Map();
  for (const i of issues) {
    if (!byFile.has(i.file)) byFile.set(i.file, []);
    byFile.get(i.file).push(i);
  }
  for (const [file, list] of [...byFile.entries()].sort()) {
    console.log(`\n  ${file}`);
    for (const i of list) {
      console.log(`    [${i.targetKind}]  href="${i.href}"`);
      console.log(`        -> ${i.resolvedTo}`);
    }
  }
  process.exitCode = 1;
}

main().catch(err => {
  console.error(err);
  process.exit(1);
});
