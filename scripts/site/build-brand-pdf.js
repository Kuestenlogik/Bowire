/**
 * Renders the marketing brand-guidelines page to a downloadable PDF.
 *
 * Mirrors scripts/site/build-docs-pdf.js: we use the @playwright/test
 * Chromium we already depend on (NOT `docfx pdf`, which this repo
 * dropped — its bundled-Playwright auto-install races on CI). The
 * brand page ships its own `@media print` rules, so we just render it
 * in light theme with print emulation and let Chromium paginate.
 *
 * Usage:
 *   node scripts/site/build-brand-pdf.js          # after `jekyll build`
 *
 * Input:
 *   site/_site/brand.html  (+ its /assets/* referenced from there)
 *
 * Output:
 *   site/assets/brand/bowire-brand-guidelines.pdf
 *
 * The ZIP brand kit (logos + this PDF + README) is a trivial archive of
 * site/assets/brand/ built by the release/site tooling — e.g.
 *   (cd site/assets/brand && zip -q bowire-brand-kit.zip \
 *      bowire-*.svg bowire-brand-guidelines.pdf README.txt)
 */
const http = require('node:http');
const fs = require('node:fs');
const path = require('node:path');
const { chromium } = require('@playwright/test');

const ROOT = path.resolve(__dirname, '..', '..');
const SITE = path.join(ROOT, 'site', '_site');
const OUT = path.join(ROOT, 'site', 'assets', 'brand', 'bowire-brand-guidelines.pdf');

const MIME = {
  '.html': 'text/html', '.css': 'text/css', '.js': 'text/javascript',
  '.svg': 'image/svg+xml', '.png': 'image/png', '.ico': 'image/x-icon',
  '.json': 'application/json', '.woff2': 'font/woff2', '.avif': 'image/avif',
  '.webp': 'image/webp', '.pdf': 'application/pdf',
};

async function main() {
  if (!fs.existsSync(path.join(SITE, 'brand.html'))) {
    throw new Error(`Missing ${path.join(SITE, 'brand.html')} — run \`jekyll build\` in site/ first.`);
  }

  // Serve the built site so absolute /assets/* URLs in brand.html resolve.
  const server = http.createServer((req, res) => {
    try {
      let p = decodeURIComponent(req.url.split('?')[0]);
      if (p.endsWith('/')) p += 'index.html';
      const file = path.normalize(path.join(SITE, p));
      if (!file.startsWith(SITE) || !fs.existsSync(file) || fs.statSync(file).isDirectory()) {
        res.writeHead(404); res.end('not found'); return;
      }
      res.writeHead(200, { 'content-type': MIME[path.extname(file)] || 'application/octet-stream' });
      res.end(fs.readFileSync(file));
    } catch (e) {
      // Don't leak the exception body (stack frames, file paths) into
      // the response — CodeQL alerts #1775 + #1776 (xss-through-
      // exception + stack-trace-exposure). The error still lands in
      // build-time stderr where the operator can read it.
      console.error('[brand-pdf] static-server error:', e);
      res.writeHead(500);
      res.end('internal error');
    }
  });
  await new Promise((r) => server.listen(0, r));
  const port = server.address().port;

  const browser = await chromium.launch();
  try {
    const ctx = await browser.newContext();
    // Light theme so the printed page stays on white paper.
    await ctx.addInitScript(() => localStorage.setItem('theme', 'light'));
    const page = await ctx.newPage();
    await page.goto(`http://localhost:${port}/brand.html`, { waitUntil: 'networkidle' });
    await page.emulateMedia({ media: 'print' });
    await page.waitForTimeout(400);

    // The page is served from localhost, so its relative links resolve to
    // the localhost origin — dead once the PDF is downloaded. Rewrite every
    // same-origin link to the production domain before printing.
    const rewritten = await page.evaluate(() => {
      let n = 0;
      for (const a of document.querySelectorAll('a[href]')) {
        let u;
        try { u = new URL(a.getAttribute('href'), location.href); } catch { continue; }
        if (u.origin === location.origin) {
          a.setAttribute('href', 'https://bowire.io' + u.pathname + u.search + u.hash);
          n++;
        }
      }
      return n;
    });
    console.log(`Rewrote ${rewritten} same-origin link(s) to https://bowire.io`);
    fs.mkdirSync(path.dirname(OUT), { recursive: true });
    await page.pdf({
      path: OUT,
      format: 'A4',
      printBackground: true,
      margin: { top: '14mm', bottom: '14mm', left: '12mm', right: '12mm' },
    });
  } finally {
    await browser.close();
    server.close();
  }
  const kb = Math.round(fs.statSync(OUT).size / 1024);
  console.log(`Brand guidelines PDF -> ${path.relative(ROOT, OUT)} (${kb} KB)`);
}

main().catch((e) => { console.error(e); process.exit(1); });
