# Plugin compatibility

A Bowire installation is a [Tool host (`bowire`)](../../README.md) plus zero or more protocol / UI / auth plugins loaded out of `~/.bowire/plugins/`. Plugin DLLs and the Tool host are built independently, often by different maintainers, and ship on independent release cadences. This page describes the contract that keeps them interoperable.

## The contract in one line

> A plugin built against `Kuestenlogik.Bowire X.Y.Z` runs in any Bowire Tool whose Bowire library version is at least `X.Y.Z` and shares the same major (`X`).

That's it. Everything below is the detail.

## Why plugins each have their own version

The Bowire repo and every plugin repo (`Kuestenlogik.Bowire.Protocol.*`, `Kuestenlogik.Bowire.Extension.*`, future `Kuestenlogik.Bowire.Auth.*`) ship as separate NuGet packages with **independent SemVer numbers**. AMQP is `0.2.x` because the plugin's own API is still settling; Kafka is `1.0.x` because the public surface is stable; Bowire core is `1.5.x`. We deliberately keep these on separate tracks:

* Plugin maturity tracks the plugin's own API, not the host. A pre-stable plugin (`0.x`) is honest about "the way you wire this might change"; pinning it to the host version would lie.
* A plugin bug-fix should not require a Bowire host release. AMQP `v0.2.1` (queue discovery) shipped without touching Bowire core — and that's the point.
* Community plugins set their own pace. We can't (and won't) demand that a third-party `Bowire.Protocol.MyOddBroker` follows our version cadence.

## How the binding actually works

Every plugin csproj pins the Bowire library it builds against:

```xml
<PackageReference Include="Kuestenlogik.Bowire" Version="1.5.1" />
```

That `Version` is **the minimum host Bowire that can load the plugin**. NuGet resolves it on the consumer side (the host installs `bowire` and the plugin and pulls a Bowire library `>= 1.5.1` in); the Tool host then loads the plugin DLL through `BowirePluginLoadContext`, which links it against the library version already present in the host process.

That gives us three concrete failure / success modes:

| Plugin built against | Tool Bowire library | Result |
|---|---|---|
| Bowire **older or equal** to host | any 1.x ≥ plugin's pin | ✅ loads; uses APIs the host has |
| Bowire **newer** than host (same major) | host < plugin's pin | ❌ `TypeLoadException` / `MissingMethodException` on load — the plugin references types the host doesn't carry |
| Bowire across a major boundary (1.x ↔ 2.x) | major mismatch | ❌ ABI changed; plugin needs a rebuild |

In other words: **newer host, older plugin → fine. Older host, newer plugin → broken. Major bump in either direction → rebuild required.**

The SemVer contract on the Bowire side that makes this work: inside any 1.x release line, the plugin-facing surfaces (`IBowireProtocol`, `IBowireUiExtension`, `IBowireCliCommand`, the future `IBowireAuthProvider`) stay binary-compatible. We only break those at a major bump.

## Compatibility matrix (first-party plugins)

| Plugin | Latest release | Built against Bowire | Works with Bowire |
|---|---|---|---|
| [`Bowire.Protocol.Amqp`](https://github.com/Kuestenlogik/Bowire.Protocol.Amqp)           | v1.0.0 | 1.6.0 | 1.6.0 ≤ x < 2.0 |
| [`Bowire.Protocol.TacticalApi`](https://github.com/Kuestenlogik/Bowire.Protocol.TacticalApi) | v1.0.0-rc.1 | 1.5.1 | 1.5.1 ≤ x < 2.0 |
| [`Bowire.Protocol.Kafka`](https://github.com/Kuestenlogik/Bowire.Protocol.Kafka)         | v1.0.3 | 1.5.0 | 1.5.0 ≤ x < 2.0 |
| [`Bowire.Protocol.Akka`](https://github.com/Kuestenlogik/Bowire.Protocol.Akka)           | v1.0.3 | 1.5.0 | 1.5.0 ≤ x < 2.0 |
| [`Bowire.Protocol.Dis`](https://github.com/Kuestenlogik/Bowire.Protocol.Dis)             | v1.0.3 | 1.5.0 | 1.5.0 ≤ x < 2.0 |
| [`Bowire.Protocol.Udp`](https://github.com/Kuestenlogik/Bowire.Protocol.Udp)             | v1.0.3 | 1.5.0 | 1.5.0 ≤ x < 2.0 |
| [`Bowire.Protocol.Surgewave`](https://github.com/Kuestenlogik/Bowire.Protocol.Surgewave) | (no release yet — gated on the `Kuestenlogik.Surgewave.Client` SDK going public) | — | — |

Note: each row's "Built against" reflects the Bowire-library version the *currently released* NuGet package was compiled with — not the HEAD `Directory.Packages.props` pin. The HEAD of every sibling-repo has already been bumped to Bowire 1.6.0; that change takes effect on the next plugin release, not retroactively for the rows above.

"Built against" is the `Kuestenlogik.Bowire` `PackageReference` the plugin was compiled with — the floor of what the consuming host must carry. "Works with" applies the SemVer contract: floor is the build-against version; ceiling is the next major.

When Bowire 2.0 happens, every plugin in this matrix grows a parallel 2.x release line and the ceiling on the 1.x row tightens to the last patch we tested.

## Convention for community plugins

We can't centralise compatibility data for plugins outside this repo, but if you maintain a third-party Bowire plugin, two conventions keep things readable for your users:

1. **Pin the floor.** Your csproj's `<PackageReference Include="Kuestenlogik.Bowire" Version="X.Y.Z" />` is your compatibility floor. Treat that as a public commitment, not just a build-time setting.
2. **Put a Bowire-compatibility line at the top of your README.** Recommended phrasing — drop into your README under the badges:

   ```markdown
   ![Bowire](https://img.shields.io/badge/Bowire-%E2%89%A5%201.5.1%2C%20%3C%202.0-006B9F)
   ```

   or as plain prose:

   > Compatible with **Bowire ≥ 1.5.1, < 2.0**. Built against Bowire `1.5.1` — the next major (2.0) will require a rebuild.

That covers the same information first-party plugins put in this table. If you'd like your plugin listed in the table above, open a pull request against this file.

## When does the contract actually get tested?

* **First-party plugins**: every plugin repo's CI smokes a `dotnet add package` of the floor Bowire version into the sample to verify it still resolves and binds at runtime. The same matrix above is the test plan.
* **Community plugins**: we can't test what we don't see. The convention is best-effort. If a Bowire release breaks a plugin we know about, we'll call it out in the release notes; otherwise, the SemVer contract above is what you can rely on.

If a plugin won't load and the matrix says it should, that's a Bowire bug — open an issue with the `BindingFailureException` stack and the two version numbers (Tool, plugin DLL).
