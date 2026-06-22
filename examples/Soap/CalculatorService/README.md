# SOAP — Calculator sample

A minimal SOAP 1.1 service exposing `Add`, `Subtract`, `Multiply` and
`Divide` over hand-rolled XML — no WCF or SoapCore dependency. Lets
you exercise the Bowire SOAP plugin end-to-end without standing up a
real SOAP backend.

## Run

```pwsh
dotnet run --project examples/Soap/CalculatorService
```

The service listens on `http://localhost:5180/Calculator.asmx`.

## Connect from Bowire

1. Start Bowire (`dotnet run --project src/Kuestenlogik.Bowire.Tool` or
   the standalone `bowire` tool).
2. In the workbench, pick the **SOAP** protocol tab.
3. Paste `http://localhost:5180/Calculator.asmx` into the server URL
   field — Bowire appends `?wsdl` on discovery.
4. The plugin parses the WSDL, lists four operations under the
   `Calculator` service. Invoke `Add` with body:

   ```xml
   <a>3</a><b>4</b>
   ```

   …and you should get `7` back.

5. `Divide` with `b=0` round-trips a SOAP `Fault` so you can see the
   plugin's fault-handling path light up.
