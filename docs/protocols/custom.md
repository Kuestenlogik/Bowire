---
summary: 'You can extend Bowire with your own protocol plugins by implementing IBowireProtocol.'
---

# Custom Protocols

You can extend Bowire with your own protocol plugins by implementing `IBowireProtocol`. Bowire auto-discovers any class that implements the interface inside a NuGet package marked with `<PackageType>BowirePlugin</PackageType>`.

There are two ways to start a plugin project:

- [Quickstart with `dotnet new bowire-plugin`](#quickstart-with-dotnet-new-bowire-plugin) — a template-based scaffolder that produces a fully-wired project (csproj, `IBowireProtocol` stub, xunit tests, optional CI, optional duplex channel, optional transport preset). Recommended for greenfield plugins.
- [Manual setup](#manual-setup) — add packages and implement the interface by hand. Use this when integrating into an existing solution that can't accept a full template output.

## Quickstart with `dotnet new bowire-plugin`

The [Kuestenlogik.Bowire.Templates](https://github.com/Kuestenlogik/Bowire.Templates) NuGet package ships a `dotnet new` template:

```bash
dotnet new install Kuestenlogik.Bowire.Templates

dotnet new bowire-plugin \
    -n Contoso.Bowire.Protocol.Foo \
    --DisplayName "Foo Protocol" \
    --ProtocolId "foo" \
    --Preset rest

cd Contoso.Bowire.Protocol.Foo
dotnet test                  # passes out of the box
dotnet pack -c Release
```

Key parameters:

| Parameter                | What it does                                                                                       |
|--------------------------|----------------------------------------------------------------------------------------------------|
| `--DisplayName`          | Human-readable protocol name shown on the Bowire UI tab.                                          |
| `--ProtocolId`           | Short identifier used internally (e.g. `"grpc"`, `"mqtt"`).                                        |
| `--Preset`               | `none` / `rest` / `mqtt` / `websocket` / `grpc` / `signalr` — pre-fills `DiscoverAsync` + `InvokeAsync` for a transport. |
| `--IncludeIntegrationTests` | Adds a second test project that hosts the plugin in an ASP.NET Core `TestServer` and hits the real Bowire HTTP API.                |
| `--IncludeDuplexChannel` | Adds a working `IBowireChannel` echo demo for bidirectional protocols.                            |
| `--ProjectOnly`          | Emits only `src/` + `tests/` (no solution / build-props) for drop-in to an existing monorepo.      |

See the [template docs](https://github.com/Kuestenlogik/Bowire.Templates/blob/main/docs/BowirePluginTemplate.md) for the full parameter list, the scaffold layout, and publishing instructions.

## Manual setup

If you'd rather wire everything up by hand — or you're adding the plugin to an existing repository that already provides its own `Directory.Build.props` / `.slnx` — the minimum is four steps.

### 1. Create a class library

```bash
dotnet new classlib -n Contoso.Bowire.Protocol.Foo
cd Contoso.Bowire.Protocol.Foo
dotnet add package Kuestenlogik.Bowire
```

### 2. Mark the project as a Bowire plugin

Bowire's installer and the in-app marketplace filter NuGet packages by `<PackageType>`. Add this to your csproj:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>

    <PackageId>Contoso.Bowire.Protocol.Foo</PackageId>
    <PackageType>BowirePlugin</PackageType>
    <PackageTags>bowire bowire-plugin foo</PackageTags>
    <IsPackable>true</IsPackable>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Kuestenlogik.Bowire" Version="0.1.*" />
  </ItemGroup>
</Project>
```

### 3. Implement `IBowireProtocol`

A minimal implementation only needs the four interface methods plus the three metadata properties. `OpenChannelAsync` can return `null` for protocols that don't support interactive duplex — unary and server-streaming calls still work.

```csharp
using System.Runtime.CompilerServices;
using Kuestenlogik.Bowire;
using Kuestenlogik.Bowire.Models;

namespace Contoso.Bowire.Protocol.Foo;

public sealed class FooProtocol : IBowireProtocol
{
    public string Name => "Foo Protocol";
    public string Id => "foo";
    public string IconSvg =>
        """<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" width="16" height="16"><circle cx="12" cy="12" r="10"/></svg>""";

    public void Initialize(IServiceProvider? serviceProvider) { }

    public Task<List<BowireServiceInfo>> DiscoverAsync(
        string serverUrl, bool showInternalServices, CancellationToken ct = default)
    {
        var echo = new BowireMethodInfo(
            Name: "Echo",
            FullName: "DemoService/Echo",
            ClientStreaming: false,
            ServerStreaming: false,
            InputType:  new BowireMessageInfo("EchoRequest",  "DemoService.EchoRequest",  []),
            OutputType: new BowireMessageInfo("EchoResponse", "DemoService.EchoResponse", []),
            MethodType: "Unary");

        var service = new BowireServiceInfo(
            Name: "DemoService",
            Package: Id,
            Methods: [echo]);

        return Task.FromResult<List<BowireServiceInfo>>([service]);
    }

    public Task<InvokeResult> InvokeAsync(
        string serverUrl, string service, string method,
        List<string> jsonMessages, bool showInternalServices,
        Dictionary<string, string>? metadata = null, CancellationToken ct = default)
    {
        // Call the target and wrap the response.
        return Task.FromResult(new InvokeResult(
            Response: jsonMessages.Count > 0 ? jsonMessages[0] : "{}",
            DurationMs: 0,
            Status: "OK",
            Metadata: new Dictionary<string, string>(StringComparer.Ordinal)));
    }

    public async IAsyncEnumerable<string> InvokeStreamAsync(
        string serverUrl, string service, string method,
        List<string> jsonMessages, bool showInternalServices,
        Dictionary<string, string>? metadata = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // Yield JSON strings for server-streaming / duplex calls.
        await Task.CompletedTask.ConfigureAwait(false);
        yield break;
    }

    public Task<IBowireChannel?> OpenChannelAsync(
        string serverUrl, string service, string method,
        bool showInternalServices, Dictionary<string, string>? metadata = null,
        CancellationToken ct = default)
    {
        // Return an IBowireChannel for interactive duplex/client-streaming
        // protocols. null means unary + server-streaming only.
        return Task.FromResult<IBowireChannel?>(null);
    }
}
```

### 4. Pack and install

```bash
dotnet pack -c Release
bowire plugin install Contoso.Bowire.Protocol.Foo
```

Or, during development, reference the local package directly:

```bash
dotnet add package Contoso.Bowire.Protocol.Foo --source ./bin/Release
```

### 5. Make it discoverable

Two small things help others find your plugin:

- **NuGet `<PackageTags>`**: include `bowire` and `bowire-plugin` so the plugin shows up in Bowire's in-app marketplace search and in nuget.org's tag-based discovery.

  ```xml
  <PackageTags>bowire bowire-plugin foo-protocol</PackageTags>
  ```

- **GitHub repo topics**: tag the source repo with `bowire-plugin` (and ideally `bowire` + `dotnet`). The official protocol plugins under `Kuestenlogik/Bowire.Protocol.*` use the same convention, so a [topic search for `bowire-plugin`](https://github.com/topics/bowire-plugin) lists all of them next to yours.

## Interface reference

### `IBowireProtocol`

```csharp
public interface IBowireProtocol
{
    string Name { get; }           // Protocol name shown in UI tabs
    string Id { get; }             // Short identifier (e.g., "myproto")
    string IconSvg { get; }        // SVG icon for the protocol tab

    IReadOnlyList<BowirePluginSetting> Settings => [];  // optional

    void Initialize(IServiceProvider? serviceProvider) { }

    Task<List<BowireServiceInfo>> DiscoverAsync(
        string serverUrl, bool showInternalServices, CancellationToken ct = default);

    Task<InvokeResult> InvokeAsync(
        string serverUrl, string service, string method,
        List<string> jsonMessages, bool showInternalServices,
        Dictionary<string, string>? metadata = null, CancellationToken ct = default);

    IAsyncEnumerable<string> InvokeStreamAsync(
        string serverUrl, string service, string method,
        List<string> jsonMessages, bool showInternalServices,
        Dictionary<string, string>? metadata = null, CancellationToken ct = default);

    Task<IBowireChannel?> OpenChannelAsync(
        string serverUrl, string service, string method,
        bool showInternalServices, Dictionary<string, string>? metadata = null,
        CancellationToken ct = default);
}
```

### Method semantics

- **`Initialize`** — called once during `MapBowire()` with the application's `IServiceProvider`. Use it to resolve services you need for embedded-mode discovery (e.g. `EndpointDataSource`). In standalone mode, `serviceProvider` is `null`.
- **`DiscoverAsync`** — return a list of `BowireServiceInfo` objects for the services and methods your protocol exposes. Each service contains methods with input/output schemas that Bowire's UI renders as forms and JSON templates.
- **`InvokeAsync`** — handle unary and client-streaming calls. `jsonMessages` carries one message for unary, or multiple for client-streaming. Return an `InvokeResult` with body, status, and metadata.
- **`InvokeStreamAsync`** — handle server-streaming and duplex calls. Yield JSON strings as messages arrive from the target service.
- **`OpenChannelAsync`** — return an `IBowireChannel` for interactive duplex/client-streaming. Return `null` if your protocol does not support interactive channels.

### `IBowireChannel`

For duplex support, implement `IBowireChannel`:

```csharp
public interface IBowireChannel : IAsyncDisposable
{
    string Id { get; }
    bool IsClientStreaming { get; }
    bool IsServerStreaming { get; }
    int SentCount { get; }
    bool IsClosed { get; }
    long ElapsedMs { get; }

    Task<bool> SendAsync(string jsonMessage, CancellationToken ct = default);
    Task CloseAsync(CancellationToken ct = default);
    IAsyncEnumerable<string> ReadResponsesAsync(CancellationToken ct = default);
}
```

The `--IncludeDuplexChannel true` flag on `dotnet new bowire-plugin` scaffolds a working `System.Threading.Channels`-backed echo implementation as a starting point.

See also: [Plugin System](../features/plugin-system.md), [Plugin Architecture](../architecture/plugin-architecture.md)
