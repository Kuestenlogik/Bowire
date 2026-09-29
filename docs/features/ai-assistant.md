---
title: AI assistant
summary: 'The workbench assistant knows the method in front of you, its schema and your recent calls. It runs against a local model (Ollama, LM Studio), your own key for Anthropic, OpenAI or OpenRouter, or an MCP host you already run. Prompts never pass through Küstenlogik.'
---

# AI assistant

The workbench carries an assistant in the right-hand drawer. <kbd>Ctrl</kbd>/<kbd>Cmd</kbd>+<kbd>Shift</kbd>+<kbd>A</kbd> opens and closes it. It sees what you see: the selected method, its schema, the last response and the recent calls. It can look things up itself:

| Tool | What it does |
|---|---|
| `bowire_list_services` | The services and methods discovery found |
| `bowire_describe_method` | One method's request and response shape |
| `bowire_recent_history` | The last calls and their outcome |
| `bowire_open_method` | Opens a method in the workbench |
| `bowire_invoke` | Calls a method — only after you switch off *observe-only* in the drawer's warning bar; that holds for the session and every call is audited |

Ask it what a method does, what a request body should look like, or why the last call failed.

Without a model the drawer is not empty: rule-based hints still point out an empty gRPC response, fields the schema does not declare, or a call you keep repeating.

## Pick a model

Everything goes through one choice: **Settings → AI**, or the same three settings on the command line or in configuration.

| Provider | `ProviderId` | Endpoint | Key | Where prompts go |
|---|---|---|---|---|
| Ollama | `ollama` (default) | `http://localhost:11434` | — | nowhere, it stays on the machine |
| LM Studio | `lmstudio` | `http://localhost:1234` | — | nowhere, it stays on the machine |
| Anthropic | `anthropic` | SDK default | yours | api.anthropic.com |
| OpenAI | `openai` | `https://api.openai.com/v1` | yours | api.openai.com, or the proxy / Azure deployment you name |
| OpenRouter | `openrouter` | `https://openrouter.ai/api/v1` | yours | openrouter.ai |
| MCP host | `mcp` | an MCP URL, or `stdio:<command>` | — | the MCP host you run |

A running Ollama or LM Studio is found on its own. The settings page then offers its models. Settings saved on that page win over flags and configuration, since they are the most recent explicit choice.

A key stays on the machine that runs Bowire. Bowire calls the provider directly, and Küstenlogik never sees the key, the prompts or the responses.

### The CLI

```bash
ollama pull llama3.2:3b
bowire --url https://api.example.com --ai-provider ollama --ai-model llama3.2:3b
```

With a key:

```bash
export Bowire__Ai__ApiKey=<your key>
bowire --url https://api.example.com --ai-provider anthropic --ai-model claude-opus-4-7
```

`--ai-provider`, `--ai-endpoint` and `--ai-model` map to `Bowire:Ai:ProviderId`, `Endpoint` and `Model`. There is no flag for the key, so it cannot end up in shell history or a process list.

### The container

The same settings as environment variables. Inside the container `localhost` is the container itself, so a model server on the host is `host.docker.internal`:

```bash
docker run --rm -p 5080:5080 \
  -e Bowire__Ai__ProviderId=ollama \
  -e Bowire__Ai__Endpoint=http://host.docker.internal:11434 \
  -e Bowire__Ai__Model=llama3.2:3b \
  --add-host host.docker.internal:host-gateway \
  kuestenlogik/bowire:latest --url https://api.example.com
```

### An embedded host

The assistant is its own package. Ollama and LM Studio come with it, and each cloud provider is one more package:

```bash
dotnet add package Kuestenlogik.Bowire.Ai
dotnet add package Kuestenlogik.Bowire.Ai.Anthropic   # or .OpenAi (OpenAI + OpenRouter), .Mcp
```

```csharp
builder.Services.AddBowireAi(builder.Configuration);   // reads Bowire:Ai
builder.Services.AddBowireAiAnthropic();

var app = builder.Build();
app.MapBowire();
app.MapBowireAiEndpoints();
```

If the host already registers an `IChatClient` before `AddBowireAi`, the assistant uses that client. The Settings page then shows the provider as host-managed.

### VS Code

The extension drives a `bowire` CLI. Pick the provider under **Settings → AI** in its panel.

## More

- The [AI integration design](../architecture/ai-integration.md) covers the three model-access modes, which features work on a small local model, and the privacy stance.
- The security drawer uses the same model for AI-assisted findings — see [security testing](../architecture/security-testing.md).
