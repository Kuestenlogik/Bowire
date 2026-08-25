# Changelog

All notable changes to the Bowire VS Code extension.

The extension version is deliberately independent of the Bowire version it
hosts — it drives whatever CLI you have installed, from 2.0 upwards.

## [0.1.1] — 2026-08-25

### Changed

- **README: the requirements section no longer reads as a precondition.** It
  said "Bowire itself, version 2.0 or newer" above three install commands,
  which made a manual install look mandatory — while the section right below
  it already documented that the extension fetches and manages a CLI when it
  finds none (#590). Both were accurate; together they misled. The requirement
  is stated as what it is, with the manual install offered as the option you
  take when CI already pins a version.

## [0.1.2] — 2026-08-25

### Fixed

- **The CLI-resolution diagram rendered as raw text on the Marketplace.** It
  was a Mermaid flowchart, which GitHub renders and the VS Code Marketplace
  does not — so the extension's own listing showed twenty lines of
  `flowchart TD` source to anyone reading the description. Replaced with a
  table, which reads the same in both places and, for a chain this linear,
  arguably better.

## [Unreleased]

### Added

- **`bowire.cliPath` setting.** Points at a `bowire` executable directly, for a
  build that is not on `PATH` — a local checkout, a portable copy, a second
  version beside the installed one. Supports `${workspaceFolder}`, so a
  project-local build can be committed to `.vscode/settings.json` and shared.
- **Tool-manifest resolution.** A repo that pins Bowire in
  `.config/dotnet-tools.json` (or a bare `dotnet-tools.json`) gets that
  version, run as `dotnet tool run bowire` — so everyone who clones it drives
  the version the repo is tested with rather than whatever their machine has.
  Sits between `bowire.cliPath` and `PATH`: specific beats shared beats
  ambient. A pinned-but-unrestored tool is told to run `dotnet tool restore`;
  a machine with no .NET SDK falls through to `PATH` instead of being sent in
  a circle ([#589](https://github.com/Kuestenlogik/Bowire/issues/589)).
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
