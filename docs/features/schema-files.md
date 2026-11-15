---
title: Schema files
summary: 'Upload the description of an API that does not hand one out itself: a gRPC descriptor set, GraphQL SDL, an OpenAPI, AsyncAPI or OpenRPC document, a WSDL, an OData EDMX model. Each format is read by the plugin that speaks its protocol.'
---

# Schema files

Bowire normally asks a server what it offers: gRPC reflection, GraphQL introspection, `?wsdl`, `$metadata`, `rpc.discover`, an OpenAPI document under a well-known path. Production servers often switch these off. If you have the description as a file, upload it, and the workbench lists and calls the server from the file.

## Uploading

Open a workspace's **Sources** and drop the file on **Upload schema files**, or click it to pick one. The line under the title lists what this host reads. It depends on the protocol plugins that are loaded:

| Format | Extensions | Read by | What a call takes from it |
|---|---|---|---|
| gRPC descriptor set | `.protoset`, `.pb`, `.desc`, `.binpb` | gRPC | The message layouts |
| Protobuf source | `.proto` | core | Nothing: it gives names only, see [gRPC](../protocols/grpc.md) |
| GraphQL SDL | `.graphql`, `.graphqls`, `.gql` | GraphQL | Variable types and the selection set |
| WSDL 1.1 | `.wsdl` | SOAP | SOAPAction and target namespace |
| OData EDMX | `.edmx`, `.csdl` | OData | The key of each entity set |
| OpenRPC | `.json` | JSON-RPC | The parameter list for the form |
| AsyncAPI 2.x / 3.0 | `.yaml`, `.yml`, `.json` | AsyncAPI | Channels, servers and bindings |
| OpenAPI / Swagger | `.json`, `.yaml`, `.yml` | REST | Paths, parameters and body schemas |

A file is stored in the workspace, under `schemas/` in its [storage location](../architecture/storage-locations.md), as the bytes you uploaded. It is there after a restart, and in a git-native workspace it is a file a reviewer can read.

The toast says what was read: `1 GraphQL SDL imported — 2 service(s)`. A file that cannot be read is refused, with the reader's own reason, and is not stored.

## How a file's format is decided

The server decides, not the browser:

1. **By extension**, when one format declares it. `orders.wsdl` goes to the SOAP plugin. If the file is not a WSDL, the SOAP plugin says what it found instead.
2. **By content**, when several formats share the extension. `.json` and `.yaml` hold OpenAPI, AsyncAPI and OpenRPC. Bowire looks for the top-level `asyncapi` or `openrpc` key; a document with neither is stored as OpenAPI.
3. **By content**, when no format declares the extension. A WSDL saved as `service.xml` or SDL saved as `schema.txt` still finds its reader.

A file that no loaded plugin reads is refused with the list of what the host does read.

## Where a call goes

An uploaded description is listed under the URL the workbench is pointed at, and a call goes to that URL. Two formats name their own address:

- A **WSDL** with a `soap:address` is addressed there.
- An **AsyncAPI** document is listed under its file name. Its calls go to the servers and bindings the document declares, as they do for a document behind a URL.

When a server also describes itself, the uploaded description wins for the services both name. Uploading one says "describe it this way".

## From the command line

```bash
# Standalone: endpoints are at the root.
curl -X POST "http://localhost:5080/api/schema/upload?name=harbor.graphql" \
     --data-binary @./harbor.graphql

# Embedded: prefix the mount point of MapBowire (default /bowire).
curl -X POST "http://localhost:5001/bowire/api/schema/upload?name=harbor.graphql" \
     --data-binary @./harbor.graphql
```

`name` is the file name: the document is stored under it, and its extension is what the format is read from. The answer names the format and what it described:

```json
{ "kind": "graphql-sdl", "displayName": "GraphQL SDL", "plugin": "graphql", "imported": 2, "services": ["Query", "Mutation"] }
```

`GET /api/schema/kinds` lists what the host imports. `DELETE /api/schema/upload` removes every uploaded file of the workspace.

`bowire discover`, `call` and `test` take a file of any of these formats with `--schema` instead of reading the workspace, and decide its format the same way:

```bash
bowire discover --schema ./harbor.graphql
bowire call Query/ship --url https://api.example.com/graphql --schema ./harbor.graphql -d '{"id":"101"}'
```

`list`, `describe` and `scan` are gRPC commands and take descriptor sets only; see [CLI mode](cli-mode.md).

## For plugin authors

A protocol plugin imports a format by implementing `IBowireUploadedSchemaReader` beside `IBowireProtocol`:

```csharp
public sealed class MyProtocol : IBowireProtocol, IBowireUploadedSchemaReader
{
    public IReadOnlyList<BowireUploadedSchemaKind> UploadedSchemaKinds { get; } =
        [new("my-idl", "My IDL", [".myidl"])];

    // Asked only when the file name does not settle the format.
    public bool LooksLikeUploadedSchema(string kind, ReadOnlyMemory<byte> document)
        => BowireUploadedSchemas.HasTopLevelKey(document, "myidl");

    // Throw FormatException with a reason for a document that is not one.
    public IReadOnlyList<BowireServiceInfo> ReadUploadedSchema(
        string kind, ReadOnlyMemory<byte> document, string sourceName)
        => MyIdlParser.Parse(document);
}
```

Bowire calls `ReadUploadedSchema` at upload, to refuse a broken file, and again when discovery lists what is stored. The workbench, `bowire discover` and the MCP tool therefore agree without the plugin doing anything in `DiscoverAsync`.

Leave `OriginUrl` unset on the returned services unless the document names its own address. Bowire then fills in the URL being discovered.

When a call needs the document again, read it back with `BowireUploadedSchemas.GetAll("my-idl")`. Each entry has an `Id` that changes with the content; cache what you parse against it.
