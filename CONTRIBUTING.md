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
./scripts/pack-local.sh
```

## License

By contributing, you agree that your contributions will be licensed under the [Apache 2.0 License](LICENSE).
