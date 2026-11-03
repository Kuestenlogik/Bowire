// Checks that a vendored copy of the branding tool is exactly the version its pin names, and that the product's brand
// files beside it are exactly the ones vendored from the Kuestenlogik repository, so neither the tool nor a product's
// drawing can drift from the shared source unnoticed. See README.md beside this file.
//
//   node check-vendored.mjs [--dir <vendored folder>] [--against <tools/branding of a Kuestenlogik checkout>]
//
// The folder (default: this script's) holds pin.json, written by vendor.mjs: the tool's version, the commit of the
// Kuestenlogik repository it was taken from, the SHA-256 of every file of the tool, and under "brand" the folder of
// the Kuestenlogik repository the product's brand files came from (brand/products/<product>) with the SHA-256 of each
// (the drawings, brand.json, wordmark.json). The brand files sit in the folder above the tool (scripts/branding/).
// The check fails when a pinned file is missing or differs, when a file is there that the pin does not name
// (node_modules aside; in the brand folder the tool's folder and a README.md aside), or when package.json states
// another version than the pin. With --against it also compares the copy with a checkout of the shared tool and its
// brand folder and says whether the copy is behind.
//
// Hashes are taken over the text with CRLF turned into LF, so a checkout's line endings do not count.
import crypto from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

export const pinFile = 'pin.json';
// Files and folders of the shared tool that stay in the Kuestenlogik repository and are never vendored.
export const metaOnly = ['vendor.mjs', '.gitignore', 'test'];
export const isMetaOnly = (file) => metaOnly.some((m) => file === m || file.startsWith(`${m}/`));
// Files of a product's brand folder that are the product's own and not vendored.
const ownBrandFiles = new Set(['README.md']);
const ignored = new Set([pinFile, 'node_modules']);

export const hashOf = (file) =>
  crypto.createHash('sha256').update(Buffer.from(fs.readFileSync(file, 'latin1').replace(/\r\n/g, '\n'), 'latin1')).digest('hex');

// The files of a tool folder, relative, with forward slashes, sorted.
export function filesOf(dir, sub = '') {
  const result = [];
  for (const entry of fs.readdirSync(path.join(dir, sub), { withFileTypes: true })) {
    const rel = sub ? `${sub}/${entry.name}` : entry.name;
    if (!sub && ignored.has(entry.name)) continue;
    if (entry.isDirectory()) result.push(...filesOf(dir, rel)); else result.push(rel);
  }
  return result.sort();
}

// The problems of a vendored copy, empty when it matches its pin.
export function checkVendored(dir) {
  const pinPath = path.join(dir, pinFile);
  if (!fs.existsSync(pinPath)) return [`${pinFile} is missing: vendor the tool with vendor.mjs`];
  const pin = JSON.parse(fs.readFileSync(pinPath, 'utf8'));
  const problems = [];
  const present = filesOf(dir);
  for (const [file, hash] of Object.entries(pin.files)) {
    if (!present.includes(file)) problems.push(`${file}: pinned, but missing`);
    else if (hashOf(path.join(dir, file)) !== hash) problems.push(`${file}: differs from version ${pin.version} (edit the shared tool, not the copy)`);
  }
  for (const file of present) if (!(file in pin.files)) problems.push(`${file}: not part of version ${pin.version}`);
  if (!pin.brand) problems.push(`${pinFile} names no brand files: vendor the tool and the product's brand folder again with vendor.mjs (1.1.0 or later)`);
  else {
    const brandDir = path.dirname(dir);
    const tool = path.basename(dir);
    for (const [file, hash] of Object.entries(pin.brand.files)) {
      const at = path.join(brandDir, file);
      if (!fs.existsSync(at)) problems.push(`../${file}: pinned (${pin.brand.from}), but missing`);
      else if (hashOf(at) !== hash) problems.push(`../${file}: differs from ${pin.brand.from} (edit it in the Kuestenlogik repository and vendor it again)`);
    }
    for (const entry of fs.readdirSync(brandDir, { withFileTypes: true })) {
      if (entry.name === tool || ownBrandFiles.has(entry.name)) continue;
      if (!(entry.name in pin.brand.files)) problems.push(`../${entry.name}: not part of ${pin.brand.from}`);
    }
  }
  const pkg = path.join(dir, 'package.json');
  if (fs.existsSync(pkg)) {
    const version = JSON.parse(fs.readFileSync(pkg, 'utf8')).version;
    if (version !== pin.version) problems.push(`package.json states ${version}, the pin ${pin.version}`);
  }
  return problems;
}

// The brand files of a product in a Kuestenlogik checkout (brand/products/<product>): the files directly in it.
export const brandFilesOf = (dir) => fs.readdirSync(dir, { withFileTypes: true }).filter((e) => e.isFile()).map((e) => e.name).sort();

// How a copy compares with a checkout of the shared tool (and the brand folder its pin names): the files that differ.
export function compareWith(dir, shared) {
  const pin = JSON.parse(fs.readFileSync(path.join(dir, pinFile), 'utf8'));
  const differ = [];
  for (const file of Object.keys(pin.files)) {
    const other = path.join(shared, file);
    if (!fs.existsSync(other) || hashOf(other) !== pin.files[file]) differ.push(file);
  }
  for (const file of filesOf(shared)) if (!isMetaOnly(file) && !(file in pin.files)) differ.push(file);
  if (pin.brand) {
    const brand = path.resolve(shared, '..', '..', pin.brand.from);
    if (!fs.existsSync(brand)) differ.push(`${pin.brand.from}: missing in ${path.resolve(shared, '..', '..')}`);
    else {
      for (const file of Object.keys(pin.brand.files)) {
        const other = path.join(brand, file);
        if (!fs.existsSync(other) || hashOf(other) !== pin.brand.files[file]) differ.push(`${pin.brand.from}/${file}`);
      }
      for (const file of brandFilesOf(brand)) if (!(file in pin.brand.files)) differ.push(`${pin.brand.from}/${file}`);
    }
  }
  return differ;
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const args = process.argv.slice(2);
  const arg = (name) => (args.includes(name) ? path.resolve(args[args.indexOf(name) + 1]) : null);
  const dir = arg('--dir') ?? path.dirname(fileURLToPath(import.meta.url));
  const problems = checkVendored(dir);
  if (problems.length) {
    console.error(`${dir}: the vendored branding tool does not match its pin:`);
    for (const p of problems) console.error('  ' + p);
    process.exit(1);
  }
  const pin = JSON.parse(fs.readFileSync(path.join(dir, pinFile), 'utf8'));
  console.log(`branding tool ${pin.version} (Kuestenlogik ${pin.commit.slice(0, 7)}) and ${pin.brand.from}: the copy matches its pin`);
  const against = arg('--against');
  if (against) {
    const differ = compareWith(dir, against);
    if (differ.length) {
      console.error(`behind or apart from ${against}: ${differ.join(', ')}; vendor it again with vendor.mjs`);
      process.exit(1);
    }
    console.log(`and equals ${against}`);
  }
}
