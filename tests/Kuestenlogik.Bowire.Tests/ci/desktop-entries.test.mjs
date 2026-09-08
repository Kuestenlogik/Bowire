// #685 — the clickable way into Bowire, on each platform.
//
// Packaging breaks quietly: a .desktop entry that never gets installed, an
// icon path nothing writes to, a shortcut dropped from the WiX feature. None
// of it shows up in a build or a test run — you find out when someone
// installs the package and cannot find the application. These assertions are
// cheap because every one of them is a file the repo already carries.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, resolve } from 'node:path';

const REPO = resolve(dirname(fileURLToPath(import.meta.url)), '../../..');
const read = (p) => readFileSync(resolve(REPO, p), 'utf8');

// ---- Linux: the Freedesktop entry ----

test('the desktop entry declares what a menu needs to draw it', () => {
    const entry = read('packaging/linux/bowire.desktop');
    const keys = Object.fromEntries(
        entry.split('\n')
            .filter((l) => l.includes('=') && !l.trimStart().startsWith('#'))
            .map((l) => [l.slice(0, l.indexOf('=')).trim(), l.slice(l.indexOf('=') + 1).trim()])
    );

    assert.ok(entry.startsWith('[Desktop Entry]'), 'the group header must come first');
    assert.equal(keys.Type, 'Application');
    assert.equal(keys.Name, 'Bowire');
    assert.equal(keys.Exec, '/usr/bin/bowire');
    assert.equal(keys.Icon, 'bowire', 'must match the installed icon basename');
    // The whole point of a menu entry: launching it must not open a terminal.
    assert.equal(keys.Terminal, 'false');
    assert.ok(keys.Categories.includes('Development'), 'belongs under Development');
});

test('deb and rpm install both the entry and the icon it names', () => {
    const nfpm = read('packaging/linux/nfpm.yaml');
    assert.match(nfpm, /dst: \/usr\/share\/applications\/bowire\.desktop/);
    // hicolor/256x256/apps/bowire.png is what Icon=bowire resolves to.
    assert.match(nfpm, /dst: \/usr\/share\/icons\/hicolor\/256x256\/apps\/bowire\.png/);
});

test('the AUR package installs them too, and lists them as sources', () => {
    const pkgbuild = read('packaging/linux/aur/PKGBUILD');
    // It builds from the release tarball, not a checkout, so the two files
    // have to be fetched rather than copied.
    assert.match(pkgbuild, /source=\(/);
    assert.match(pkgbuild, /packaging\/linux\/bowire\.desktop/);
    assert.match(pkgbuild, /images\/bowire_logo_small\.png/);
    assert.match(pkgbuild, /usr\/share\/applications\/\$\{pkgname\}\.desktop/);
    assert.match(pkgbuild, /hicolor\/256x256\/apps\/\$\{pkgname\}\.png/);
});

// ---- Windows: the MSI shortcuts ----

test('the MSI drops a desktop shortcut as well as the Start-menu one', () => {
    const wxs = read('packaging/msi/Bowire.wxs');
    assert.match(wxs, /StandardDirectory Id="DesktopFolder"/);
    assert.match(wxs, /Id="BowireDesktopShortcut"/);
    // A component that is never referenced by the feature ships nothing.
    assert.match(wxs, /<ComponentRef Id="DesktopShortcut" \/>/);
});

test('the desktop shortcut can be declined by a silent install', () => {
    const wxs = read('packaging/msi/Bowire.wxs');
    // No wizard ships with this MSI, so a public property is the only place
    // the choice can live.
    assert.match(wxs, /<Property Id="DESKTOPSHORTCUT" Value="1" \/>/);
    assert.match(wxs, /Condition="DESKTOPSHORTCUT = &quot;1&quot;"/);
});

// ---- macOS: the app bundle ----

test('the app bundle names an executable and an icon that exist', () => {
    const plist = read('packaging/macos/Bowire.app/Contents/Info.plist');
    const value = (key) => {
        const at = plist.indexOf(`<key>${key}</key>`);
        assert.ok(at >= 0, `${key} missing from Info.plist`);
        const open = plist.indexOf('<string>', at);
        return plist.slice(open + 8, plist.indexOf('</string>', open));
    };

    assert.equal(value('CFBundleExecutable'), 'Bowire');
    assert.equal(value('CFBundleIconFile'), 'bowire');
    assert.equal(value('CFBundlePackageType'), 'APPL');
    assert.equal(value('CFBundleIdentifier'), 'de.kuestenlogik.bowire');

    // Both files the plist points at have to be there, under the exact names
    // macOS looks for.
    assert.ok(read('packaging/macos/Bowire.app/Contents/MacOS/Bowire').length > 0);
    assert.ok(readFileSync(
        resolve(REPO, 'packaging/macos/Bowire.app/Contents/Resources/bowire.icns')).length > 0);
});

test('the bundle carries no binary of its own — it finds the CLI at launch', () => {
    const launcher = read('packaging/macos/Bowire.app/Contents/MacOS/Bowire');
    assert.ok(launcher.startsWith('#!'), 'must be a script, not a compiled copy');
    assert.match(launcher, /BOWIRE_BIN/);
    assert.match(launcher, /\/opt\/homebrew\/bin\/bowire/);
    assert.match(launcher, /exec /);
});

test('the icns is a real container of PNG entries', () => {
    // Written by scripts/packaging/make-icns.py rather than iconutil, because
    // the release pipeline cross-publishes the macOS artefacts on Ubuntu. If
    // that writer ever goes wrong the bundle gets a blank icon and nothing
    // else complains.
    const icns = readFileSync(
        resolve(REPO, 'packaging/macos/Bowire.app/Contents/Resources/bowire.icns'));

    assert.equal(icns.subarray(0, 4).toString('ascii'), 'icns');
    assert.equal(icns.readUInt32BE(4), icns.length, 'header length must match the file');

    const seen = [];
    for (let off = 8; off < icns.length;) {
        const type = icns.subarray(off, off + 4).toString('ascii');
        const len = icns.readUInt32BE(off + 4);
        assert.ok(len >= 8 && off + len <= icns.length, `entry ${type} has a bad length`);
        const png = icns.subarray(off + 8, off + len);
        assert.deepEqual(
            [...png.subarray(0, 4)], [0x89, 0x50, 0x4e, 0x47], `entry ${type} is not a PNG`);
        seen.push(type);
        off += len;
    }
    assert.ok(seen.includes('ic08'), `expected the 256px entry, got ${seen.join(', ')}`);
});

test('the release pipeline stages the bundle into both macOS archives', () => {
    const release = read('.github/workflows/release.yml');
    assert.match(release, /Stage the macOS app bundle/);
    // Copied in before the tar step, or the archives go out without it.
    // Compare the step declarations, not any mention: the file's header
    // comment names 'Archive standalone binaries' long before the step does.
    const step = (name) => {
        const at = release.indexOf(`- name: ${name}`);
        assert.ok(at >= 0, `step '${name}' not found in release.yml`);
        return at;
    };
    assert.ok(
        step('Stage the macOS app bundle') < step('Archive standalone binaries'),
        'the bundle must be staged before the archives are made');
    assert.match(release, /__VERSION__/, 'the plist placeholder has to be stamped');
});

test('the Homebrew formula installs the bundle and says how to reach it', () => {
    const formula = read('packaging/macos/Formula/bowire.rb');
    assert.match(formula, /Bowire\.app/);
    // A formula may not write to /Applications, so it has to tell the user.
    assert.match(formula, /def caveats/);
    assert.match(formula, /\/Applications/);
});

// ---- the docs that make any of it discoverable ----

test('the setup docs answer how to start Bowire, and what a reboot means', () => {
    const setup = read('docs/setup/index.md');
    assert.match(setup, /### After installing: how to start it/);
    assert.match(setup, /### After a reboot/);
    // Every platform's route has to be named, or the section only helps some.
    for (const platform of ['Windows', 'Linux', 'macOS']) {
        assert.ok(setup.includes(platform), `${platform} missing from the start-up section`);
    }
});
