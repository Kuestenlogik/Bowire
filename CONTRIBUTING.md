# Contributing to Bowire

Thank you for your interest in contributing to Bowire!

## Getting Started

1. Fork the repository
2. Clone your fork: `git clone https://github.com/YOUR-USERNAME/Bowire.git`
3. Create a branch: `git checkout -b feature/my-feature`
4. Install .NET 10 SDK
5. Build: `dotnet build Kuestenlogik.Bowire.slnx`
6. Test: `dotnet test Kuestenlogik.Bowire.slnx`

## Development Workflow

### Branch Naming
- `feature/description` -- New features
- `fix/description` -- Bug fixes
- `docs/description` -- Documentation
- `refactor/description` -- Code improvements

### Commit Messages
Follow conventional commits:
- `feat: add new feature`
- `fix: resolve bug`
- `docs: update documentation`
- `refactor: improve code structure`
- `test: add tests`
- `chore: maintenance tasks`

### Pull Requests
1. Ensure all tests pass: `dotnet test Kuestenlogik.Bowire.slnx`
2. Ensure zero build warnings: `dotnet build Kuestenlogik.Bowire.slnx -c Release`
3. Add tests for new features
4. Update documentation if needed

### Code Style
- Follow existing patterns in the codebase
- One class/interface per file, named by content
- Use `<summary>` XML docs on all public members
- Target .NET 10 and C# 14

## Project Structure

```
src/
  Kuestenlogik.Bowire/           Main library (NuGet package)
tests/
  Kuestenlogik.Bowire.Tests/     Unit and integration tests
docs/                  Documentation
scripts/               Build and packaging scripts
```

## Frontend fragments

The workbench UI is vanilla JavaScript. `wwwroot/bowire.js` is **generated** —
MSBuild concatenates the per-feature fragments under
`src/Kuestenlogik.Bowire/wwwroot/js/` into one IIFE, in the order the
`<BowireJsFragment>` items are listed in the csproj. Edit the fragments; never
the bundle.

Two things bite newcomers:

- **Build the project you are going to run.** Every consumer
  (`Kuestenlogik.Bowire.Tool`, each `samples/*`) keeps its own copy of
  `Kuestenlogik.Bowire.dll` with the bundle embedded. Building the main project
  alone leaves the old copy in place, and the browser serves the old bundle.

- **`el(tag, attrs, children…)` sets attributes, not DOM properties.** For an
  HTML *boolean* attribute (`disabled`, `checked`, `selected`, `readonly`,
  `required`, `hidden`, …) presence is what counts, so `disabled: false` used to
  disable the control. `el()` now drops `false` for those attributes, but the
  rule to remember is that they are attributes: `spellcheck`, `draggable`,
  `contenteditable` and every `aria-*` are *enumerated*, and there `false`
  correctly becomes the string `"false"`. When you need real property semantics
  — marking an `<option>` selected, for one — set the property after
  construction: `var o = el('option', {...}); o.selected = true;`

JS tests live beside the C# ones and run separately:

```bash
npm run test:js
```

## Running Tests

```bash
dotnet test Kuestenlogik.Bowire.slnx -v normal
```

## Local NuGet Package

To test the NuGet package locally:

```powershell
.\scripts\pack-local.ps1
```

Or on Linux/macOS:

```bash
./scripts/ci/pack-local.sh
```

## License

By contributing, you agree that your contributions will be licensed under the [Apache 2.0 License](LICENSE).
