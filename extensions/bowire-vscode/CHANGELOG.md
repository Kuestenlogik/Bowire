# Changelog

All notable changes to the Bowire VS Code extension.

The extension version is deliberately independent of the Bowire version it
hosts — it drives whatever CLI you have installed, from 2.0 upwards.

## [Unreleased]

### Added

- **`bowire.cliPath` setting.** Points at a `bowire` executable directly, for a
  build that is not on `PATH` — a local checkout, a portable copy, a second
  version beside the installed one. Supports `${workspaceFolder}`, so a
  project-local build can be committed to `.vscode/settings.json` and shared.
- **Version check before launch.** A CLI too old to understand the arguments
  the extension passes used to exit with "Bowire exited before it started
  serving", which named nothing. It now says which version it found and what
  the minimum is.

### Fixed

- **Starting with no folder open.** `globalStorageUri` is a path VS Code does
  not create, so the spawn failed with `ENOENT` naming an executable that was
  plainly present — Node reports a missing working directory against the
  *command*. The directory is created first, and the error message now
  distinguishes a missing working directory from a missing binary.
- **Error messages fit the case.** A wrong `bowire.cliPath` gets the offending
  path quoted back and a button to the setting; only a genuinely absent CLI
  gets install instructions, which cannot fix a typo.

- **Per-repository storage.** A repo whose `.bowire/project.json` carries
  `"storage": "project"` keeps its collections, environments and recordings
  beside its code instead of in the machine-wide `~/.bowire/`. Opt-in, so
  nothing you already have moves; and because it is read from the repository
  rather than passed by the editor, the same checkout resolves to the same
  store from a terminal or from CI ([#591](https://github.com/Kuestenlogik/Bowire/issues/591)).
