# Changelog

All notable changes to the Bowire VS Code extension.

The extension version is deliberately independent of the Bowire version it
hosts — it drives whatever CLI you have installed, from 2.5 upwards.

## [1.0.0] — 2026-09-03

1.0 is a promise about the surface, not a claim about ambition: the
`bowire.*` settings keys and the command ids stay where they are, and moving
one is a major version from here on. The extension reached the point where
that promise is cheap to make and expensive to postpone — every further 0.x
trains people to expect churn that has already stopped.

Nothing in this release changes behaviour. What changes is that the work
already on `main` finally reaches the Marketplace, which has been serving
0.1.2 from 18 August while three construction sites closed behind it:
resolving the CLI from a workspace tool manifest
([#589](https://github.com/Kuestenlogik/Bowire/issues/589)), offering a
managed download when none is installed
([#590](https://github.com/Kuestenlogik/Bowire/issues/590)), and
workspace-local storage
([#591](https://github.com/Kuestenlogik/Bowire/issues/591)).

### Stable from here

- **Settings keys** — `bowire.cliPath`, and its siblings, keep their names.
- **Command ids** — including `Bowire: Show resolved CLI`, added in 0.2.0.
- **The CLI floor stays 2.5.0.** The extension drives an installed Bowire
  rather than bundling one, and hosts anything from the floor upwards. Its
  version therefore stays independent of Bowire's
  ([#101](https://github.com/Kuestenlogik/Bowire/issues/101)) — a shared
  number would assert a coupling that does not exist and force an extension
  release on every Bowire release.

## [0.2.0] — 2026-08-25

Everything below has been in the repository for a while but never reached the
Marketplace: the last thing published there was 0.1.0, and the workflow only
publishes when asked. 0.1.1 through 0.1.3 ship with this release too.

### Changed

- **The workbench URL no longer comes from the console banner.** The extension
  used to learn where Bowire was listening by matching "Bowire is running at:"
  in its output. That is a log line, so it disappears at a quieter log level,
  and it was printed before the bind was known to have worked — a Bowire
  started on a taken port announced a URL and then threw. The CLI now reports
  the address it actually bound through `--port-file`
  ([#615](https://github.com/Kuestenlogik/Bowire/issues/615)), and the file
  appears only once the server is listening.
- **The extension no longer picks a port.** It asked for one derived from a
  hash of the workspace path, which kept two different workspaces apart but
  not two windows on the same folder. It now passes `--port 0` and lets the
  OS assign a free one. A Bowire you started yourself is unaffected either
  way: it keeps its port, its process and its lifetime, and the extension runs
  its own alongside it.

### Added

- **`Bowire: Show resolved CLI`.** Says which `bowire` would be used and which
  resolution step produced it, without starting anything — the question that
  comes up when you are trying to point the extension at a different build.
- **`bowire.cliPath` is checked when you change it.** A path that does not
  exist, names the folder rather than the executable, or points at a build too
  old for this extension is reported at the setting, rather than surfacing
  later as a spawn failure that blames the spawn.

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

- **Uninstalling the extension now removes the CLI it downloaded.** The
  managed copy is ~120 MB in a storage directory nobody browses, and VS Code
  keeps extension storage when an extension is removed — so the one copy
  nothing could use any more was also the one copy nothing cleaned up. A
  Bowire you installed yourself is never touched.
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

## [0.1.3] — 2026-08-25

### Fixed

- **The panel could load before the workbench was serving.** The extension
  opened the webview as soon as the CLI printed its listening banner, but a
  bound port is not a served response — the first request can arrive before
  the pipeline is ready, and the panel then shows an error page for a
  workbench that came up fine a moment later. It now polls the URL until it
  answers, and reports a clear failure if it never does.
- **The workbench opened in a browser window as well as in the editor.** The
  extension hosts a webview and always did, but it started the CLI without
  `--no-browser` — and the CLI opens a browser on startup by default, which is
  right for someone running `bowire` in a terminal and wrong here. The
  operator got two windows, and the one they did not ask for took focus.

## [0.1.2] — 2026-08-25

### Fixed

- **The CLI-resolution diagram rendered as raw text on the Marketplace.** It
  was a Mermaid flowchart, which GitHub renders and the VS Code Marketplace
  does not — so the extension's own listing showed twenty lines of
  `flowchart TD` source to anyone reading the description. Replaced with a
  table, which reads the same in both places and, for a chain this linear,
  arguably better.

## [0.1.1] — 2026-08-25

### Changed

- **README: the requirements section no longer reads as a precondition.** It
  said "Bowire itself, version 2.0 or newer" above three install commands,
  which made a manual install look mandatory — while the section right below
  it already documented that the extension fetches and manages a CLI when it
  finds none (#590). Both were accurate; together they misled. The requirement
  is stated as what it is, with the manual install offered as the option you
  take when CI already pins a version.
