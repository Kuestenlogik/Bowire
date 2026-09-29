---
title: Service scaffolding
summary: 'Describe a service in one sentence and get a schema (OpenAPI or proto3), a runnable C# stub, a workbench collection and a smoke test. The AI assistant writes only the spec; the files come from checked-in templates, so the same spec gives the same code with any model or none.'
---

# Service scaffolding

"REST CRUD for User with email (required) + role" is enough to start a service. Bowire turns the sentence into a small **spec**: one entity, its fields and a protocol. From the spec it generates:

| File | What it is |
|---|---|
| `openapi.yaml` or `<service>.proto` | The contract: list, get, create, update, delete |
| `Program.cs`, `<Service>.csproj` (+ `<Service>Service.cs` for gRPC) | A runnable C# stub that keeps its data in memory — replace the store, keep the routes |
| `bowire/<entities>.collection.json` | A workbench collection: every operation, plus an unknown id and a create without its required fields |
| `bowire/<entities>.smoke-test.json` | A smoke test for `bowire test` |
| `bowire-scaffold.json` | The spec itself, to edit and generate again |
| `README.md` | How to run and try it |

The stub runs with `dotnet run`, and the smoke test passes against it as generated.

## The model writes the spec, never the code

The assistant only reads the sentence into a spec. The files come from templates checked in to `Kuestenlogik.Bowire.Scaffold`, which is why a 3B local model is enough and a different model changes at most the field list, never the shape of the code. The same spec gives the same bytes on every machine.

Which model is asked:

1. The configured provider, when it is local (Ollama, LM Studio).
2. When the configured provider is a cloud one, a local Ollama or LM Studio that answers first, and the cloud provider only when the local one fails. `Bowire:Ai:Scaffold:PreferLocal=false` asks the configured provider only.
3. When no model gives a usable spec — none configured, none reachable, or an answer that is not a spec — a deterministic parser reads the sentence. The dialog and the CLI say which one wrote the spec, and why the others did not.

The parser understands the shape above: the protocol from `rest` / `grpc`, the entity from the word after `for`, the fields from the list after `with`, split on commas, `+` and `and`. A type word next to a field sets its type (`total: double`, `paid bool`, `year int`), and `required`, `(required)` or a trailing `*` makes it required. German works too: `für`, `mit`, `und`, `Pflicht`.

## In the workbench

**Sources → + Scaffold a service**, or **Scaffold a service…** in the command palette.

1. **Describe** — the sentence and, optionally, the protocol. **Propose** asks the assistant; **Read without AI** asks the parser.
2. **Spec** — the spec as JSON. Fix what the model got wrong here, before a file exists.
3. **Files** — every generated file, each one editable.

**Add to workspace** writes the files to `scaffold/<Service>/` in the workspace folder, uploads the schema the way **Upload schema files** does — so the service shows up in discovery at once — and adds the collection. The requests that need an id read it from the environment variable `<entity>Id`: create one, copy its id into the variable, and get, update and delete follow it.

## On the command line

```bash
bowire scaffold "REST CRUD for User with email (required) + role" -o users
cd users && dotnet run
bowire test bowire/users.smoke-test.json
```

| Option | |
|---|---|
| `--protocol`, `-p` | `rest` or `grpc`; wins over the sentence |
| `--ai` | Let the assistant write the spec — local model first, then the configured provider (`Bowire__Ai__*`, or what Settings → AI saved). Without it the parser reads the sentence. |
| `--print-spec` | Print the spec and stop |
| `--spec <file>` | Generate from a spec file instead of a sentence |
| `--base-url` | Where the stub listens; default `http://localhost:5000` |
| `--out`, `-o` | The folder; default `./<Service>` |
| `--force` | Write into a folder that is not empty |

`--print-spec`, an edit, then `--spec` is the command-line version of the dialog's spec step.

## The spec

```json
{
  "entity": "User",
  "protocol": "rest",
  "fields": [
    { "name": "email", "type": "string", "required": true },
    { "name": "role", "type": "string" }
  ],
  "service": "Users",
  "baseUrl": "http://localhost:5000"
}
```

| Field | |
|---|---|
| `entity` | Singular; brought into PascalCase |
| `protocol` | `rest` (OpenAPI 3 + minimal API) or `grpc` (proto3 + Grpc.AspNetCore with server reflection) |
| `fields` | Up to 40; camelCase on the wire. Types: `string`, `int`, `long`, `double`, `bool`, `datetime`, `uuid`. A field called `id` is dropped — every entity gets one. |
| `service` | Default: the entity's plural |
| `baseUrl` | Default `http://localhost:5000` |

In the proto, `datetime` and `uuid` are strings (ISO 8601, UUID), and every scalar other than a string is `optional`, so a required number can be told apart from a zero.

## Not yet

C# is the only stub language; REST and gRPC the only protocols. More languages and protocols are templates next to the existing ones.
