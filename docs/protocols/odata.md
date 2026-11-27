---
title: OData
summary: 'Bowire discovers OData services by fetching the $metadata endpoint and parsing the EDMX/CSDL document.'
---

# OData v4 Protocol

Bowire discovers OData services by fetching the `$metadata` endpoint and parsing the EDMX/CSDL document. Entity sets become services, CRUD operations become methods.

## Setup

```bash
dotnet add package Kuestenlogik.Bowire.Protocol.OData
```

### Standalone

```bash
bowire --url http://localhost:5020/odata/$metadata
```

## Discovery

Each entity set in the EDM model becomes a service with five methods:

| Method | HTTP Verb | Path | Description |
|--------|-----------|------|-------------|
| GET | GET | `/{EntitySet}` | Query with $filter, $select, $expand |
| GET_BY_KEY | GET | `/{EntitySet}({key})` | Get by primary key |
| POST | POST | `/{EntitySet}` | Create new entity |
| PATCH | PATCH | `/{EntitySet}({key})` | Update entity |
| DELETE | DELETE | `/{EntitySet}({key})` | Delete entity |

### Functions and actions

Every function and action the model binds to an entity type, or to a collection of it, is one more method of that entity set, under its own name. An unbound function or action import is a method of one more service named after the entity container.

| Kind | HTTP Verb | Path |
|------|-----------|------|
| Function on one entity | GET | `/{EntitySet}({key})/{Ns.Name}(p=v,…)` |
| Function on the set | GET | `/{EntitySet}/{Ns.Name}(p=v,…)` |
| Action on one entity | POST | `/{EntitySet}({key})/{Ns.Name}` |
| Action on the set | POST | `/{EntitySet}/{Ns.Name}` |
| Function import | GET | `/{Name}(p=v,…)` |
| Action import | POST | `/{Name}` |

The input carries the entity's key (for an operation on one entity) and the operation's parameters. A function writes its parameters into the URL as OData literals: strings and enum values quoted, numbers, booleans, GUIDs and dates as they are, a structured value as a JSON parameter alias (`p=@p&@p={…}`). An action sends its parameters as the JSON body. Overloads of one name are told apart by their parameter names.

A service that does not serve `$metadata` is described by uploading the model (`.edmx`, `.csdl`: the `$metadata` response saved to a file) to the workspace's [schema files](../features/schema-files.md). The entity sets are listed from it under the URL the workbench is pointed at, and a call learns each set's key from it, so a string key is quoted as `People('ada')`.

## Input Fields

Entity type properties are reflected as input fields with their EDM types mapped to Bowire field types (string, int64, double, bool).

## Query Parameters

For GET requests, use OData system query options in the request body:

```json
{
  "$filter": "Price gt 50",
  "$select": "Name,Price",
  "$orderby": "Name"
}
```

For key-based operations, include the key:

```json
{
  "key": "42"
}
```
