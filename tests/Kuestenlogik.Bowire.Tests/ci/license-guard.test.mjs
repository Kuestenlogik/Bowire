// Copyright 2026 Küstenlogik
// SPDX-License-Identifier: Apache-2.0
//
// scripts/ci/check-license.mjs — #647, everything in this repository is Apache-2.0.
//
// The guard keeps the decision true without anyone watching it: every C# file says
// Apache-2.0 in its header, no project packs a licence of its own, and the root LICENSE
// is the Apache licence the packages ship.

import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, mkdirSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { dirname, join, resolve } from 'node:path';

const __dirname = dirname(fileURLToPath(import.meta.url));
const guard = await import(pathToFileURL(resolve(__dirname, '../../../scripts/ci/check-license.mjs')).href);
const { sourceFinding, buildFileFinding, licenseFileFinding, scan, trackedFiles } = guard;

const HEADER = '// Copyright 2026 Küstenlogik\n// SPDX-License-Identifier: Apache-2.0\n\nnamespace X;\n';

describe('sourceFinding', () => {
    it('accepts the Apache-2.0 header', () => {
        assert.equal(sourceFinding(HEADER), null);
    });

    it('accepts a byte order mark in front of the header', () => {
        assert.equal(sourceFinding('﻿' + HEADER), null);
    });

    it('reports a file without a header', () => {
        assert.match(sourceFinding('using System;\n\nnamespace X;\n'), /no SPDX-License-Identifier/);
    });

    it('reports a header that names another licence', () => {
        assert.match(sourceFinding('// SPDX-License-Identifier: BUSL-1.1\nnamespace X;\n'), /BUSL-1\.1/);
    });

    it('only looks at the top of the file, where a header belongs', () => {
        const late = 'namespace X;\n' + '\n'.repeat(20) + '// SPDX-License-Identifier: Apache-2.0\n';
        assert.match(sourceFinding(late), /no SPDX-License-Identifier/);
    });
});

describe('buildFileFinding', () => {
    it('leaves a project without licence properties alone', () => {
        assert.equal(buildFileFinding('src/A/A.csproj', '<Project><PropertyGroup><IsPackable>true</IsPackable></PropertyGroup></Project>'), null);
    });

    it('reports a project that packs a licence of its own', () => {
        assert.match(buildFileFinding('src/A/A.csproj', '<PackageLicenseExpression>MIT</PackageLicenseExpression>'), /PackageLicenseExpression/);
        assert.match(buildFileFinding('src/A/Directory.Build.props', '<PackageLicenseFile>LICENSE-BSL</PackageLicenseFile>'), /PackageLicenseFile/);
    });

    it('requires the root props to pack the root LICENSE', () => {
        assert.equal(buildFileFinding('Directory.Build.props', '<PackageLicenseFile>LICENSE</PackageLicenseFile>'), null);
        assert.match(buildFileFinding('Directory.Build.props', '<PackageLicenseExpression>MIT</PackageLicenseExpression>'), /root/);
    });
});

describe('licenseFileFinding', () => {
    it('accepts the Apache licence', () => {
        assert.equal(licenseFileFinding('                                 Apache License\n                           Version 2.0, January 2004\n'), null);
    });

    it('reports anything else', () => {
        assert.match(licenseFileFinding('MIT License\n\nCopyright (c) 2026'), /Apache License, Version 2\.0/);
    });
});

describe('scan', () => {
    it('reports each offending file by name', () => {
        const root = mkdtempSync(join(tmpdir(), 'license-guard-'));
        mkdirSync(join(root, 'src'));
        writeFileSync(join(root, 'LICENSE'), 'Apache License\nVersion 2.0, January 2004\n');
        writeFileSync(join(root, 'Directory.Build.props'), '<PackageLicenseFile>LICENSE</PackageLicenseFile>');
        writeFileSync(join(root, 'src', 'Good.cs'), HEADER);
        writeFileSync(join(root, 'src', 'Bad.cs'), 'namespace X;\n');
        writeFileSync(join(root, 'src', 'Bad.csproj'), '<PackageLicenseExpression>MIT</PackageLicenseExpression>');

        const files = ['LICENSE', 'Directory.Build.props', 'src/Good.cs', 'src/Bad.cs', 'src/Bad.csproj', 'README.md'];
        const findings = scan(files, { root });

        assert.deepEqual(findings.map(f => f.file).sort(), ['src/Bad.cs', 'src/Bad.csproj']);
    });

    it('passes on this repository', () => {
        assert.deepEqual(scan(trackedFiles()), []);
    });
});
