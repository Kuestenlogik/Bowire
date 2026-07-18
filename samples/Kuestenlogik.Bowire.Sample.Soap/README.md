# Kuestenlogik.Bowire.Sample.Soap

A minimal SOAP 1.1 Calculator (`Add` / `Subtract` / `Multiply` / `Divide`
at `/Calculator.asmx`, WSDL at `/Calculator.asmx?wsdl`) demonstrating
**both** ways Bowire meets a SOAP service, from one project:

- **Embedded** — the workbench is mounted at `/bowire`, and the bundled
  `soap-catalogue.json` seeds the Sources rail with this host's WSDL. The
  SOAP plugin parses the contract and surfaces the four operations.
- **Separate** — it is a real SOAP endpoint, so point an external
  workbench or the CLI at it.

The wire is hand-rolled XML over HTTP — no SoapCore/WCF dependency, so
the sample stays portable across .NET LTS boundaries.

## Run

```pwsh
dotnet run --project samples/Kuestenlogik.Bowire.Sample.Soap
```

- Embedded workbench: <http://localhost:5180/bowire> — `Calculator` is
  already in the Sources rail.
- As a separate target:

  ```pwsh
  bowire --url soap@http://localhost:5180/Calculator.asmx?wsdl
  ```
