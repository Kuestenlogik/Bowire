---
title: Build a UI extension
summary: 'Implement IBowireUiExtension to mount a viewer / editor against a semantic kind — a map for coordinates, a player for audio bytes, a chart for time-series, &c.'
---

# Build a UI extension

A **UI extension** is a widget that mounts on top of a payload field once that field carries a known **semantic kind** (`coordinate.wgs84`, `image.bytes`, `audio.opus`, `time-series`, …). Where a protocol plugin carries opaque bytes, an extension recognises what the bytes mean and renders the right visualisation. Reach for this seam to ship a new viewer (response-side, read-only) or editor (request-side, interactive) for a payload semantic.

The widget itself is a JS bundle; the descriptor below is the C# side that tells the workbench which bundle to load, which kinds it claims, and which roles it can fill.

## The interface

`IBowireUiExtension` lives in `src/Kuestenlogik.Bowire/Semantics/Extensions/IBowireUiExtension.cs`. The public surface:

```csharp
public interface IBowireUiExtension
{
    string Id { get; }
    string BowireApiRange { get; }
    IReadOnlyList<string> Kinds { get; }
    ExtensionCapabilities Capabilities { get; }
    string BundleResourceName { get; }
    string? StylesResourceName { get; }
    IReadOnlyList<string> AdditionalAssetNames => [];
}
```

What each member does:

- **`Id`** — stable extension identifier, conventionally `{vendor}.{name}` (e.g. `"kuestenlogik.maplibre"`). The JS-side `window.BowireExtensions.register({ id })` call uses the same string so the workbench can pair the server descriptor with the JS registration.
- **`BowireApiRange`** — semver range the extension declares compatibility with (e.g. `"1.x"`). The workbench compares this against its own Bowire API version on load; a mismatch leaves the extension visible-but-disabled with a "needs Bowire {x}.x" badge instead of mounting it.
- **`Kinds`** — semantic kinds the extension claims (e.g. `["coordinate.wgs84"]`). One extension can claim several kinds in the same registration.
- **`Capabilities`** — bitmask of `Viewer` (response-pane mount) and / or `Editor` (request-pane mount). Defined in `src/Kuestenlogik.Bowire/Semantics/Extensions/ExtensionCapabilities.cs`:

  ```csharp
  [Flags]
  public enum ExtensionCapabilities
  {
      None = 0,
      Viewer = 1 << 0,
      Editor = 1 << 1,
  }
  ```

  `None` is a reserved sentinel — never register with it. Declare `Viewer | Editor` to fill both roles.

- **`BundleResourceName`** — embedded-resource name of the JS bundle that calls `window.BowireExtensions.register({...})`. Resolved against the declaring assembly via `Assembly.GetManifestResourceStream` and served at `/api/ui/extensions/{Id}/{Name}`.
- **`StylesResourceName`** — optional embedded-resource name of a stylesheet shipped alongside the bundle. `null` when the extension renders without dedicated CSS.
- **`AdditionalAssetNames`** — extra asset names served under `/api/ui/extensions/{Id}/{name}`. Ship vendor libraries (e.g. a map renderer's JS), glyph PBFs, sprite atlases, or LICENSE files the bundle dynamic-loads at runtime. The asset endpoint only resolves request paths whose leaf filename appears in this list — anything else is rejected with 404, so the endpoint never serves arbitrary files out of the plugin assembly.

## Minimal working example

The MapLibre extension (`src/Kuestenlogik.Bowire.Map/Semantics/Extensions/MapLibreExtension.cs`) is the canonical in-repo template and the dogfood proof that the same shape third parties use is the shape Bowire's own widgets use:

```csharp
using Kuestenlogik.Bowire.Semantics.Extensions;

namespace Kuestenlogik.Bowire.Semantics.Extensions;

[BowireExtension]
public sealed class MapLibreExtension : IBowireUiExtension
{
    public string Id => "kuestenlogik.maplibre";
    public string BowireApiRange => "1.x";
    public IReadOnlyList<string> Kinds { get; } = ["coordinate.wgs84"];
    public ExtensionCapabilities Capabilities
        => ExtensionCapabilities.Viewer | ExtensionCapabilities.Editor;

    public string BundleResourceName => "wwwroot/js/widgets/map.js";
    public string? StylesResourceName => "wwwroot/maplibre/maplibre-gl.css";

    public IReadOnlyList<string> AdditionalAssetNames { get; } =
    [
        "wwwroot/maplibre/maplibre-gl.js",
        "wwwroot/maplibre/LICENSE",
    ];
}
```

The `wwwroot/js/widgets/map.js` bundle is shipped as an `<EmbeddedResource>` on the `Kuestenlogik.Bowire.Map` assembly. At first mount, the workbench's extension loader dynamic-imports it from `/api/ui/extensions/kuestenlogik.maplibre/map.js`; the bundle then calls `window.BowireExtensions.register({...})` to declare its mount / unmount callbacks.

## The `[BowireExtension]` attribute

Tagging the class with `[BowireExtension]` (defined in `src/Kuestenlogik.Bowire/Semantics/Extensions/BowireExtensionAttribute.cs`) is what opts the type into the assembly scan. The attribute carries no metadata — the implementation supplies id / capabilities / resource names through `IBowireUiExtension` itself. The attribute is `AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)`, so it sits exactly once on each extension class.

A single NuGet can ship multiple `[BowireExtension]` types when they belong together (e.g. a MIL-symbol package shipping both a detector and a viewer extension) — each one is independently instantiated and registered.

## Registration

Auto-discovery only. Drop the `[BowireExtension]`-tagged class into a `Kuestenlogik.Bowire.*` assembly with a parameterless constructor; Core scans every loaded `Kuestenlogik.Bowire*` assembly at startup, picks every type carrying the attribute that implements `IBowireUiExtension`, instantiates it via `Activator.CreateInstance`, and surfaces it through `/api/ui/extensions`. The JS loader iterates that endpoint at workbench boot to know which bundles to fetch and which kinds each claims.

There is no `services.AddBowireUiExtension<T>()` helper — the attribute + interface combo is the only registration path, deliberately, to keep the contract narrow in v1.0.

## Register a custom detector

The same `[BowireExtension]` marker + assembly-scan mechanism covers the server-side detector seam. Implement `IBowireFieldDetector` (`src/Kuestenlogik.Bowire/Semantics/Detectors/IBowireFieldDetector.cs`), tag the class, and drop the assembly next to the host — Core registers the detector as a singleton on `IBowireFieldDetector` alongside the built-ins. No `services.AddSingleton<IBowireFieldDetector, MyDetector>()` needed; hosts that still hand-register a detector continue to work, and the sweep de-dupes by concrete type so the same detector never fires twice.

The `IBowireFieldDetector` contract:

```csharp
public interface IBowireFieldDetector
{
    string Id { get; }
    IEnumerable<DetectionResult> Detect(in DetectionContext ctx);
}
```

The `Wgs84CoordinateDetector` in `src/Kuestenlogik.Bowire/Semantics/Detectors/Wgs84CoordinateDetector.cs` is the in-repo reference — it carries the marker so its shape is exactly the one third parties ship:

```csharp
[BowireExtension]
public sealed partial class Wgs84CoordinateDetector : IBowireFieldDetector
{
    public string Id => "kuestenlogik.wgs84-coordinate";

    public IEnumerable<DetectionResult> Detect(in DetectionContext ctx)
    {
        // walk ctx.Frame, propose one or more DetectionResult per match
    }
}
```

Detector authors ship a parameterless constructor (the assembly sweep calls `Activator.CreateInstance` on the type) and keep the implementation stateless so the shared `IFrameProber` singleton can invoke it concurrently across streaming clients. Everything else — `AnnotationKey`, `AnnotationSource.Auto` routing, resolver priority — stays inside the framework.

The `BowireOptions.DisableBuiltInDetectors` flag still opts Core's five built-ins out even though they carry the marker (the sweep filters them by assembly identity); third-party detectors in sibling packages remain unaffected.

## See also

- <xref:Kuestenlogik.Bowire.Semantics.Extensions.IBowireUiExtension> — auto-generated interface reference.
- <xref:Kuestenlogik.Bowire.Semantics.Extensions.ExtensionCapabilities> — the viewer / editor bitmask.
- [UI extensions feature page](../features/extensions.md) — install paths + how activation works against the semantics framework.
- [Frame semantics framework](../architecture/frame-semantics-framework.md) — the detector → annotation → extension pipeline + the full `window.BowireExtensions.register({...})` contract (the `ctx` surface, mount / unmount semantics, what changes between API majors).
