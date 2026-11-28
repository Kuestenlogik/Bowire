---
# The release's `#` heading. A sentence about this delivery, not the version.
title: <fill in before the tag>
version: 2.11.0
---

<One-sentence frame for what 2.11 is about. Replaces this placeholder
before the tag.>

## Highlights

<!-- Add a section per landed feature as the work merges. Pattern —
note the example heading is indented so the placeholder detector in
release.yml (which counts lines matching `^### `) does not mistake this
template for a filled-in body:

  ### <headline> (#issue)
  <2-4 sentences>
-->

### Streams and channels typed in by hand (#995)

The freeform form offered a method type for every protocol but always made a unary call. A server-streaming call typed in by hand now follows its stream frame by frame, and a client-streaming or duplex call opens a channel: connect, send each message, disconnect. Neither needs the method to have been discovered first.

Compose gains a form for every protocol plugin that describes one. SOAP, SignalR, NATS, Pulsar and Socket.IO do so far. You pick a call kind (produce or subscribe, invoke, stream or channel, publish, request or JetStream consume) and fill in the fields that kind needs, such as topic, hub method, subject, SOAPAction or namespace. Bowire assembles the call from them. The plugin describes its form on the server through the new `IBowireManualEntry` contract, so a plugin in its own repository adds a Compose form without workbench code. The freeform form now offers each protocol the method types its plugin names, instead of a list kept in the workbench.

A sidecar plugin describes its form the same way, as a `manualEntry` object in its `initialize` reply (#1003).

### Schema files for every protocol that has one (#994)

The schema drop zone knew two formats: a `.proto`, and everything else as OpenAPI. It now takes the description of any server that does not hand one out itself. GraphQL SDL (`.graphql`, `.graphqls`, `.gql`) lists the operations of a server that refuses introspection, and a call is built from it with typed variables and a real selection set. A WSDL gives a SOAP call its SOAPAction and namespace without `?wsdl`. An OData EDMX model, an OpenRPC document and an AsyncAPI document list a service that serves no `$metadata`, answers no `rpc.discover`, or whose AsyncAPI document is not reachable under a URL.

One route, `POST /api/schema/upload`, takes any of them and decides the format: by extension where one format declares it, by content for `.json` and `.yaml`, which OpenAPI, AsyncAPI and OpenRPC share, and for a file saved under another extension. The drop zone lists what the loaded plugins read, and a file none of them reads is refused with that list. `GET /api/schema/kinds` returns it.

A plugin imports a format by implementing `IBowireUploadedSchemaReader`: it declares the format and its extensions and reads a document into services. The Schema files page in the documentation describes it.

The command line follows (#1018). `--schema` on `bowire discover`, `call` and `test` takes every format a loaded plugin imports and decides it the way the upload route does, so `--schema events.yaml` with an AsyncAPI document is no longer read as OpenAPI and `--schema schema.graphql` is no longer an unknown extension. A call that names a schema of another protocol goes to the plugin that reads it without `--protocol`. A file no plugin reads, or one its plugin cannot read, stops the command with exit code 2 and the reason.

### gRPC servers without reflection, from the workbench (#991)

A gRPC server with reflection switched off could be listed in the workbench from a `.proto` and not called: a `.proto` gives names, and a call needs the compiled message layouts. The schema drop zone now takes the compiled descriptor set (`protoc --descriptor_set_out=api.protoset --include_imports`; `.protoset`, `.pb`, `.desc` or `.binpb`). Its services appear under the server's URL, and unary calls, streams and channels go through it. The set is stored per workspace beside the other schema files.

The set answers for the services it describes. A call to any other service still asks its server, so one workspace can hold servers with and without reflection. A set compiled without `--include_imports` is refused at upload with the name of the missing file. Uploading a `.proto` now says that it gives names only and points at the descriptor set.

On the command line, `--schema api.protoset` does the same for `discover`, `list`, `describe` and `call`, and `bowire test` gains `--schema`, so a test run can reach a server without reflection. `bowire scan` takes `--schema` for descriptor sets too, more than one if needed (#1016).

### An embedded workbench reads the host's gRPC contract in-process (#992)

An embedded Bowire used to map the host's gRPC reflection service and read the host's own services back over the wire. It now reads the descriptors compiled into the host for every service mapped with `MapGrpcService<T>()`: discovery makes no network call to the host, and unary calls, streams and channels are marshalled from the same descriptors. A host that only calls `AddBowire()` and `MapBowire()` needs nothing else.

`AddBowireGrpc` names what the mapped services do not cover: `Descriptors` for generated code whose service is not mapped here, and `DescriptorSet` / `DescriptorSetFile` for a contract that lives in another process.

### An embedded workbench reads the host's OpenAPI document (#993)

Embedded REST discovery used to walk ApiExplorer and reflect over the body types, which gives the shape the CLR types have. A host that registers an OpenAPI generator (`AddOpenApi()`, Swashbuckle, NSwag) is now discovered from the document it generates, read in-process: property names after the host's JSON naming policy, schemas after its transformers, examples and descriptions. The host does not have to publish the document for this.

It needs the OpenAPI adapter package that matches the generator (`Kuestenlogik.Bowire.Protocol.Rest.OpenApi2` for ASP.NET Core's `AddOpenApi()`). A host without a generator or without an adapter is discovered from ApiExplorer as before. With the document as the source, an untagged endpoint is listed under the generator's default tag instead of `Default`, and an operation without an `operationId` is named from its verb and path.

### The security scan sends forged tokens on the WebSocket upgrade (#963)

The scan's API2 checks sent a JWT re-headed to `alg:none`, and one with a corrupted signature, to HTTP endpoints only; on a WebSocket it tried a handshake without any credential, never with a forged one. A socket that decodes the bearer token without verifying it passed, while the HTTP routes of the same server were reported. With `--auth-header` carrying a JWT and the anonymous upgrade refused, the WebSocket check now opens the upgrade with both forgeries, and either one getting through is a finding (`BWR-OWASP-API2-WS-ALG-NONE`, `BWR-OWASP-API2-WS-SIG`). It still connects and closes without sending a frame.

### Data sets drive flows and workspace runs (#946)

`bowire test --data rows.csv` ran a test collection once per row; a flow file or a `--workspace` run refused the flag. A flow now runs once per row as well, the row's columns over its environment and `{{iteration}}` / `{{iterationCount}}` bound, and every step result names its iteration and the row's source line, so a failing row says which row it was in the console and in the HTML, JUnit and SARIF reports. With `--workspace` or `--workspace-id` every flow runs once per row. `--fail-fast` stops after the first failing iteration.

### Yaak exports import like Insomnia and Bruno collections (#939)

A Yaak export (*Export Data* in the app, `yaak export` on the CLI) imports through the same entry point as the other formats: the workbench's **Import files…**, `bowire import` and the `bowire.import` MCP tool. Each workspace becomes a collection with its folders; a request carries the headers and the authentication it inherits from its folders and its workspace, as Yaak sends it; `${[ variable ]}` becomes `{{variable}}`, and the UUID and timestamp functions their Bowire counterparts. Sub-environments are laid over their parents and the base environment. gRPC and WebSocket requests, multipart and file bodies and response-chaining functions are named in the import report. The importer is tested against exports the Yaak CLI wrote. See [Migrating from Yaak](../migration/yaak.md).

### Translations can say "one" and "many" the way their language does (#688)

The translation layer only substituted `{name}` placeholders, so a sentence whose form depends on a number read wrong at one ("1 certificate(s)") and could not be written at all in a language with more than two forms. Catalogue strings can now use ICU `plural` and `select` blocks: `{count, plural, one {# certificate} other {# certificates}}`. The workbench picks the branch by the locale's own plural rules, `#` is the number, and a string without such a block works as before. The locale-parity test and `bowire docs translations --check` hold every translation to its language's categories, and `--check` now also compares placeholders. The first strings use it: the certificate and descriptor-set counts, the import summary and the remove-all-assertions confirmation.

### Pre-request and post-response scripts run in `bowire test` (#672)

A method's scripts ran in the workbench only: an exported test collection left them out, and `bowire test` had no engine to run them, so CI sent a different request than the one tested. The export now carries `preScript` and `postScript`, and `bowire test` runs them with the same `ctx`: the pre-request script changes the request before it goes out, the post-response script checks the response, `ctx.env.set` and `ctx.vars.captured` carry values through the run, and a failing script fails its test with its message. The server side runs the workbench's own script runtime in a sandboxed JavaScript engine (Jint) with limits on time, memory and statements from `Bowire:Scripting:*`, so a runaway script fails its test instead of the run. The engine is the new optional package `Kuestenlogik.Bowire.Scripting`, which the `bowire` tool ships and core does not reference. See [Pre-request and post-response scripts](../features/scripts.md).

### Code export: the HTTPSnippet set, and snippets for gRPC and the brokers (#677)

The Code tab offered four or five languages per protocol and nothing for the brokers. It now offers the HTTPSnippet set for REST, OData, GraphQL, JSON-RPC and MCP — curl, fetch, axios, requests, httpx, HttpClient, RestSharp, Go, Java HttpClient and OkHttp, Kotlin, PHP, Ruby, Rust, Swift, PowerShell, HTTPie and wget — and, where no snippet library reaches, gRPC in C#, Go, Python, Node and Java, WebSocket, SignalR, SSE, MQTT, NATS, Socket.IO and Pulsar clients. Each snippet sends what the workbench sends: JSON-RPC and MCP carry their envelope, broker snippets their topic, subject, QoS or JetStream publish, gRPC snippets the method's streaming shape. Secrets stay `{{secret.*}}` references in every snippet (until now only the CLI export held them back), and a token fetched at request time is a comment, not a header. The targets are one registry; a plugin adds its own through `window.bowireCodeExport.register`. See [Export / Import](../features/export-import.md#code-export).

### Scaffolding: stubs in Go, Node, Python and Rust, and GraphQL (#754)

`bowire scaffold` wrote C# stubs only, for REST and gRPC. GraphQL joins as a protocol, schema first (`schema.graphql`, with stubs on Hot Chocolate, graphql-js and graphql-core), and REST and gRPC stubs now come in Go, Node, Python and Rust as well — REST on `net/http`, `node:http` and `http.server` with no dependency at all, and on axum; gRPC on grpc-go, @grpc/grpc-js, grpcio and tonic, each with server reflection and none needing protoc on the machine — chosen with `--language`, the dialog's language picker, `"language"` in the spec or the sentence (`… in Go`). Each answers like the C# one, and `scripts/scaffold/verify.mjs` builds and starts every stub and runs its generated smoke test against it.

### MCP Apps: a tool's interactive view, hosted and traced (#810)

A tool whose `_meta.ui.resourceUri` names a `ui://` resource comes with an interactive view, an [MCP App](https://github.com/modelcontextprotocol/ext-apps). After the call, the workbench shows that view above the result and logs every message between the view and the workbench. The view runs in a sandbox, under the Content-Security-Policy its resource declares, and it can reach no network that the resource does not name. When the view calls a tool or reads a resource, the call goes through `/api/invoke` with the same rights and audit as any other. Bowire announces the `io.modelcontextprotocol/ui` extension, so a server that offers a tool with and without a view offers the view.

### Bowire's MCP tools answer with apps (#811)

An MCP Apps host now shows two of Bowire's tools as UI. Claude, VS Code Copilot and the Bowire workbench are such hosts. `bowire.test.run` shows every test or flow step with its checks; a failed test can be re-run from there. `bowire.discover` lists the services and methods, and each method can be invoked from the list. Both apps are single HTML documents that load nothing from the network. A client that does not declare the extension sees the same tools and JSON as before. `bowire.test.run` now also answers with the run step by step (`report`) and takes `test`, to run one test of a collection again.

## Fixes along the way

- **gRPC servers written with tonic (Rust) can be called (#1069).** Bowire asked their reflection service for a file named like the service; tonic answers an unknown file by failing the stream, so every call failed with `file '…' not found`. Only file names are asked for by name now.

- **gRPC servers behind Node's `@grpc/reflection` can be called (#1068).** Bowire listed their services but every call failed with `Service '…' not found. Available: []`: the descriptors that library serves use relative type names, declare no dependencies, put `google.protobuf.Empty` in a file named `google_protobuf.proto` and leave proto3 `optional` unflagged. Bowire now repairs them before building, and a descriptor that still cannot be built is named in the error.

- **No 404s at start for absent packages (#1065).** A host without the Help or ManualEntry package logged two 404s in the browser console on every start, because the page probed both to find out. The page now reads which packages the host serves from `__BOWIRE_CONFIG__.features` (an endpoint contribution's new optional `FeatureId`) and asks only those.

- **Scripts run in the workbench again (#1062).** Since the Content-Security-Policy of #625, no pre-request or post-response script ran in the workbench: the runtime compiled each script with `new Function`, the policy refused it, and the error showed only in the script console. Scripts now run in QuickJS compiled to WebAssembly, in a heap of their own with no reach into the page, its storage or the network, under the `Bowire:Scripting:*` time and memory limits. The page's policy gains `'wasm-unsafe-eval'` and still refuses `'unsafe-eval'`. The tab, the collection runner and recording replay all run through it. `Bowire:ContentSecurityPolicy` now reaches the `bowire` tool's workbench as well; a custom policy must allow `'wasm-unsafe-eval'` for scripts to run.

- **The workbench's collection runner runs an item's scripts (#1060).** A request saved to a collection with a pre-request or post-response script ran them from its tab but not as part of its collection. The runner now runs both through the same runtime: the pre-request script changes the request before it goes out (headers, body, query), a failing one stops the item before the call, and a failing post-response script fails the item.
- **A gRPC server without reflection is reported as such (#1063).** A server that answers `Unimplemented` while the reflection request is still being written made the call fail, now and then, with a bare `Cancelled`. The server's own answer is now read and reported.

### Imported requests run (#1055)

A request imported from Postman, Insomnia, Bruno, Yaak or a `.http` file is saved with its URL as the service, and the REST plugin looked that URL up as an OpenAPI operation: running the collection failed with "No OpenAPI document found" for every request. An item whose service is a URL and whose method is an HTTP verb is now sent as it was imported, with its headers and body, to the URL with the environment's variables filled in.

### A clean start leaves the browser console clean (#1054)

Without a `.bowire/project.json` above the host's working directory, the workbench's boot probe got a `404` from `/api/project`, and the browser logged it as a failed request on every start. "No project manifest" now answers `200 {"found": false}`; a malformed manifest still answers `400`.

### OData functions and actions can be called (#529)

Discovery listed five CRUD methods per entity set and nothing else, although the plugin's description promised functions and actions. Every function and action bound to an entity type or its collection is now a method of the entity set, and unbound function and action imports form one more service named after the container. A function is called as a GET with its parameters in the URL segment, quoted by their EDM type; an action as a POST with its parameters as the body; an entity-bound one behind the entity's key.

### A REST step in `bowire test` reaches the server once (#1053)

`bowire test` discovered before every call, and REST's discovery tries the URL as an OpenAPI document. Every REST step therefore sent a GET to its own URL ahead of the call, a `POST /orders` step included, and a data-driven run did so for every row. A plugin now says whether its invoke needs discovery first (`IBowireProtocol.InvokeNeedsDiscovery`, default yes); REST does not, and a plugin that does is discovered once per server URL for the run.

### Insomnia imports, checked against collections Insomnia wrote (#940)

The importers were tested with files written from the formats' specifications. Run against the collections of Insomnia's and Bruno's own end-to-end tests, the Bruno importers held, and the Insomnia importer did not in three places. A Socket.IO request was imported as an HTTP GET to the Socket.IO server; it is now left out and named in the report, as gRPC and WebSocket requests are. A collection of only such requests was refused with "no request collection found"; the refusal now says what the collection holds and to add the server as a source. A global environment or a mock server export was called a design document. The report line for a `{% response %}` tag reads as a sentence.

### A host that leaves a shared http sidecar takes only its own channels (#1014)

One http sidecar can serve several Bowire hosts, but nothing on the wire told them apart, and the contract did not say what a host's `shutdown` meant there. With the Node and Go SDKs the first host to leave closed every other host's channels and tore down the plugin under them. A host now names itself with a `Bowire-Session` header on every request and on its SSE stream. Over http, `shutdown` ends that session: its channels and streams, nothing else. A session also ends when its SSE stream closes, so a host that crashed leaves nothing open. Notifications go only to the session that owns the stream or channel, and the plugin's `shutdown` hook runs when the sidecar process stops, not when a host leaves. The four SDKs follow the rule from their next release.

### A compiled sidecar ships one manifest for every platform (#990, #986)

On Windows a manifest's `executable` is now also looked up in the plugin directory with each `PATHEXT` extension, so `"executable": "echo"` starts the shipped `echo.exe` instead of the first `echo.exe` on `PATH`. A Go or Rust sidecar names its binary once and runs on Linux, macOS and Windows.

The .NET plugin loader also leaves sidecar folders to the sidecar loader, so a start no longer logs `ManifestMissing` for every installed sidecar (#986).

### A discovered SOAP call carries its SOAPAction and namespace (#1000)

Discovery read an operation's SOAPAction only into its description and did not read the target namespace at all, so a call to a discovered operation went out with an empty SOAPAction and an unqualified body element. The SOAP plugin now takes both from the WSDL. For a document-style operation the namespace is that of the input element, for an rpc-style one it is that of `soap:body`. A call made without discovering first, from the CLI, a flow or a recording, loads the WSDL once itself. Metadata the user enters still wins.

### A discovered NATS request waits for its reply (#1001)

NATS discovery gave the subscribe, publish and request of a subject one name, the subject itself, and a bare subject runs as a publish. A discovered request therefore never waited for a reply. Each operation now has its own name, such as `orders.created/request`, so the sidebar also tells the three apart. JetStream and Services methods reach their stream or service route through any caller, including MCP tools and benchmarks. A request saved with the bare subject runs as it did.

### A client-streaming SignalR channel to a remote hub gets its answer (#1002)

SignalR can only look a hub method up in its own host. For a remote hub, or a call typed in by hand, it opened every channel as duplex, and a client-streaming hub method refused that. `/api/channel/open` now takes the `methodType` the caller opens the channel for and hands it to the plugin as the reserved `__bowireMethodType__` marker. The workbench sends it from every channel it opens, and SignalR's hand-entry form offers client streaming as a call kind of its own.

### A SOAP call entered by hand reaches the service (#996)

The freeform form sent its Metadata tab for ad-hoc REST only, so a SOAP call typed in by hand, against a service with no WSDL, went out without its `soap_action` and `target_namespace`. The metadata now reaches every protocol's invoke.

### An embedded workbench no longer keeps a disposed host alive (#989)

The plugin update check kept the `HttpClient` that `AddHttpClient` registers for the default name, and with it the host's root service provider, so a host that embeds the workbench was never collected after it was disposed. The check now owns its client, which also recycles its connections every two minutes so a DNS change reaches it. A test holds that no singleton Bowire registers takes an `HttpClient` from DI.

## Breaking changes

<!-- Each change has been on a back-compat ramp through the prior minor
and is removed in this release. Add a section per breaking change, with
the migration path. -->

### `MapBowire()` no longer maps the gRPC reflection endpoints (#992)

Up to 2.10 every host that embedded Bowire with the gRPC plugin exposed `grpc.reflection.v1` and `grpc.reflection.v1alpha` as a side effect. They are no longer mapped. The embedded workbench does not need them.

If clients outside the process (grpcurl, a standalone Bowire pointed at the host) rely on them, opt in: `builder.Services.AddBowireGrpc(grpc => grpc.MapReflectionService = true)`, or call `app.MapGrpcReflectionService()` yourself. Do one, not both.

### `bowire scan --grpc-descriptor-set` is now `--schema` (#1016)

The scan named its descriptor set with an option of its own. It takes `--schema <file>` like the other commands, and `--grpc-descriptor-set` is removed from `scan`. Replace the option name; the file and what the scan does with it are unchanged.

### `bowire list` and `bowire describe` take descriptor sets only with `--schema` (#1018)

Both are gRPC commands. They accepted an OpenAPI document named with `--schema` and did not use it; they now refuse every format but a descriptor set, with exit code 2. `bowire discover --schema` lists the others. A descriptor set named with `--schema` on a host without the gRPC plugin is refused instead of passed on unread.

### `BowireOptions.ProtoSources` and `ProtoSource` are removed (#992)

They fed a `.proto` through a name-only parser: the services were listed and could not be called. Replace them with the descriptors: nothing at all when the host maps the service, otherwise `AddBowireGrpc(grpc => grpc.Descriptors.Add(WeatherReflection.Descriptor))` or `grpc.DescriptorSetFile = "api.protoset"`.

## Acknowledgements

<!-- Optional. Names of contributors who exercised rc / reported. -->
