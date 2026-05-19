# Draft: GitHub issue for asyncapi/net-sdk

**Use this when filing https://github.com/asyncapi/net-sdk/issues — paste
the title and the body below, replace `<repo>` with your own repro link
or attach the .yaml if you prefer.**

Once the issue is open, drop the URL into the ROADMAP entry under
"AsyncAPI as a discovery source" so future work tracks against it.

---

## Title

`StringEnumDeserializer crashes when scalar matches an implicit YAML type (Decimal/Integer) — asyncapi: 3.0.0, bindings.mqtt.qos: 2`

## Body

### What I'm seeing

Reading a minimal AsyncAPI 3 document through `IAsyncApiDocumentReader.ReadAsync` throws when:

- the `asyncapi:` or `info.version:` value is written unquoted (`asyncapi: 3.0.0`)
- any binding block contains a numeric scalar that maps to a typed property (`bindings.mqtt.qos: 2`)

In all cases the inner exception is `System.FormatException` from `Decimal.Parse`, called from
`Neuroglia.Serialization.Yaml.StringEnumDeserializer` after YamlDotNet's
`ScalarNodeDeserializer` was asked for an `expectedType` of `Decimal`.

Quoting the value (`asyncapi: '3.0.0'`, `qos: '2'`) makes the document parse fine — so the
data is well-formed AsyncAPI, the deserialiser just trips on YAML's implicit-type resolver.

### Minimal repro

```yaml
asyncapi: 3.0.0
info:
  title: Repro
  version: 1.0.0
channels:
  hello:
    address: 'hello'
```

```csharp
using Neuroglia.AsyncApi.IO;
using Microsoft.Extensions.DependencyInjection;

var sp = new ServiceCollection().AddAsyncApiIO().BuildServiceProvider();
var reader = sp.GetRequiredService<IAsyncApiDocumentReader>();
using var stream = File.OpenRead("repro.yaml");
var doc = await reader.ReadAsync(stream, default);  // throws
```

### Inner stack trace

```
System.Number.ThrowFormatException
System.Decimal.Parse(String, IFormatProvider)
YamlDotNet ScalarNodeDeserializer.Deserialize
Neuroglia.Serialization.Yaml.StringEnumDeserializer.Deserialize
YamlDotNet NodeValueDeserializer.DeserializeValue
... (down to AsyncApiDocumentReader.ReadAsync line 52)
```

### Environment

- `Neuroglia.AsyncApi.Core` 3.0.6
- `Neuroglia.AsyncApi.IO` 3.0.6
- .NET 10 (`net8.0` target asset selected)
- YamlDotNet (whatever Neuroglia.Serialization.YamlDotNet 4.20.0 pulls in)
- Windows 11

### What I expected

Per the AsyncAPI 3 spec the version strings are unconstrained, and standard tooling
(Spectral, the official VS Code extension, the AsyncAPI playground) all accept the
unquoted form. The reader should treat them as strings without involving the
StringEnumDeserializer at all.

### What I think is happening

`StringEnumDeserializer` looks like it's being installed for *every* scalar in the
document (not just ones whose `expectedType` is actually an enum), and inside it
delegates back to `ScalarNodeDeserializer` with the *property*'s declared CLR type.
When the property is `string` or `int` but YAML resolved the scalar to a different
implicit type first, the conversion lands on `Decimal.Parse` and blows up.

### Workaround I'm using

Documentation note that authors should quote scalars that look numeric:

```yaml
asyncapi: '3.0.0'
info:
  version: '1.2.3'
```

Happy to put together a PR if useful — needs guidance on whether the fix should be
in `StringEnumDeserializer` (skip when expectedType isn't an enum) or earlier in
the resolver chain.
