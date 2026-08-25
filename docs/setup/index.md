---
title: Deployment modes
summary: 'Where Bowire can run, and how to install it for each.'
---

# Deployment

Bowire is one workbench with several places to put it. Every mode supports every protocol plugin — the choice is about how Bowire reaches the target API and who starts it, not about what it can talk to.

| Mode | How it reaches the API | Use case |
|------|------------------------|----------|
| [Standalone](standalone.md) | CLI / browser UI pointing at any remote URL | Testing third-party APIs, QA sessions, CI one-liners |
| [In your editor](../integrations/vscode.md) | The standalone CLI, hosted in a VS Code panel | Working on a service with the workbench next to the code |
| [Embedded](embedded.md) | Mounted at `/bowire` inside your ASP.NET app | Dev-time browser UI for your own service |
| [Container](docker.md) | Sidecar next to a non-.NET service | Teams running Go / Rust / Python / Node services |

## Standalone

The CLI and the browser UI in one executable. It ships with every protocol plugin built in and points at any URL you pass:

```bash
bowire --url https://your-server
```

Best when the target service isn't yours or you don't want to modify it. Also works offline against a schema file (`.proto`, OpenAPI, GraphQL SDL) when no server is reachable.

**Installing it.** Four routes, all producing the same `bowire` on your `PATH`:

```bash
winget install KuestenLogik.Bowire        # Windows
choco install bowire                      # Windows (Chocolatey)
dotnet tool install -g Kuestenlogik.Bowire.Tool   # any platform with the .NET SDK
```

Or take a native installer or a self-contained archive from the [Downloads page](https://bowire.io/downloads.html): MSI for Windows, DEB for Debian / Ubuntu, RPM for Fedora / RHEL / SUSE, and zip / tarball bundles for anywhere you want an xcopy deployment. The self-contained bundles carry their own runtime, so they need no .NET installed at all. (A Homebrew tap is still pending.)

See [Standalone tool](standalone.md) for the CLI command set, how to restrict loaded plugins, and how another program can start Bowire and learn which port it bound.

## In your editor

The [VS Code extension](../integrations/vscode.md) opens the workbench in an editor panel beside your code.

```
ext install kuestenlogik.bowire-vscode
```

It does not bundle Bowire — it drives one. The extension uses a CLI you configured, one your repository pins in a tool manifest, or one on your `PATH`, and offers to fetch a verified copy when it finds none. That matters because it means the workbench in your editor, the `bowire` in your terminal and the one in CI are the same binary reading the same collections.

Collections, environments and recordings stay plain JSON on disk, so a repo that opts into [project-scoped storage](../architecture/storage-locations.md#the-project-opt-in) keeps them beside its code and they commit, diff and review like any other file.

## Embedded

Add Bowire directly to an ASP.NET application. The discovery pipeline reuses the host's service provider and endpoint metadata, so every protocol plugin you have installed works automatically.

```bash
dotnet add package Kuestenlogik.Bowire
```

```csharp
app.MapBowire();
```

Best when you own the service and want a zero-config UI available during development.

See [Embedded mode](embedded.md) for configuration options, custom authentication, and per-plugin settings.

## Container

Bowire is published as a multi-arch (`linux/amd64` + `linux/arm64`) OCI image on every tagged release:

```bash
docker pull ghcr.io/kuestenlogik/bowire:latest
```

Best as a sidecar next to a service that isn't .NET, or anywhere you'd rather not install anything on the host.

See [Containers / OCI](docker.md) for the registries, tag policy and a compose example.

## Requirements

- **Nothing at all** for the native installers and the self-contained archives — they carry their own runtime.
- **.NET 10 SDK** for `dotnet tool install`, for the embedded package, and for a repository that pins Bowire in a tool manifest.
- **Any modern browser** for the UI.
- **For gRPC targets:** the server must expose Server Reflection, **or** you drop a `.proto` file into Bowire.

## Next

- [User Guide](../ui-guide/index.md) &mdash; once Bowire is running, how to drive the UI
- [Protocol Guides](../protocols/index.md) &mdash; per-protocol behaviour and setup
- [Features](../features/index.md) &mdash; workflows (recording, flows, performance, environments, &hellip;)
- [Storage locations](../architecture/storage-locations.md) &mdash; where your collections and recordings end up
