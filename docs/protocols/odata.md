---
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
