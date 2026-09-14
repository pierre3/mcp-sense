# McpSense

Turn any OpenAPI description into an MCP server, without writing a line of glue code.

McpSense reads an OpenAPI 3.0/3.1 document at run time and exposes its operations as
[Model Context Protocol](https://modelcontextprotocol.io) tools, so an MCP client such as Claude
Desktop or Claude Code can call the API directly. Nothing is code-generated and nothing is
committed: point it at a spec and it serves.

*Read this in [日本語](README_ja.md).*

> **Status: preview.** Everything described below runs today — specs far too large to expose one
> tool per operation, AI-assisted descriptions, and OAuth with automatic token refresh. The command
> line may still change between preview releases.

## What it is built around

Turning a spec into one tool per operation is the easy part. McpSense is built around the two
problems that show up once you try to use that on a real API.

**Large specs stay usable.** A spec with hundreds of operations produces a tool list that swamps
the model's context before any work begins. Past a configurable threshold McpSense switches to a
meta-tool mode — `search_operations`, `describe_operation`, `call_operation` — and uses the tags the
spec already carries as a table of contents, so the model can see what territory exists without
being handed every operation.

GitHub's REST API is the extreme case: **1,229 operations, whose tool list is 1.8 MB of JSON**, more
than most context windows hold. Through the meta-tools the same API is advertised in **3.1 KB**, a
600-fold reduction, with its 47 groups listed up front.

Search works without any AI configured, so a large spec stays usable out of the box; configuring an
embedding generator upgrades it to semantic matching. Similarity search runs in memory, so no
external vector database is involved. Explicit allowlists and denylists remain available as an
escape hatch.

**Descriptions written for a model, not for a developer.** Tool names and descriptions taken
verbatim from a spec are often terse, inconsistent, or assume context the model does not have —
GitHub's own spec offers names like `activity_list-repos-starred-by-authenticated-user`. McpSense
can rewrite names, descriptions and parameter hints through any `IChatClient`. Responses are cached
on disk, so a second start over an unchanged spec sends nothing to the model at all.

That second feature is **opt-in**. McpSense is designed to work completely without AI: name no
model and the spec's own text is passed through unchanged. The AI dependency is confined to a
separate assembly so that builds which never enable it do not carry it, and if the model errors or
answers with nonsense, the spec's own text is kept and the server still starts.

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download) or later

## Getting started

Install the `mcpsense` command from NuGet:

```bash
dotnet tool install --global McpSense.Tool --prerelease
```

To work on McpSense itself, clone the repository, run `dotnet build McpSense.slnx`, and use
`dotnet run --project src/McpSense.Tool -- <command>` instead.

[`samples/`](samples/) has a small spec written to exercise the parts that matter, with commands for
trying each mode against it.

Then inspect a spec to see the operations McpSense extracts from it:

```bash
mcpsense dump-operations https://example.com/openapi.yaml
```

The argument is a local file path or a URL, in JSON or YAML.

```text
spec:       ./openapi/liff.yml
version:    OpenApi3_0
title:      LIFF server API
operations: 4
tags:       1

getAllLIFFApps  GET    /liff/v1/apps
                tags=liff  params=0  body=-  auth=Bearer
addLIFFApp      POST   /liff/v1/apps
                tags=liff  params=0  body=application/json  auth=Bearer
deleteLIFFApp   DELETE /liff/v1/apps/{liffId}
                tags=liff  params=1  body=-  auth=Bearer
updateLIFFApp   PUT    /liff/v1/apps/{liffId}
                tags=liff  params=1  body=application/json  auth=Bearer
```

### Serving a spec

`mcpsense mcp` speaks MCP over stdio, which is what Claude Desktop and Claude Code expect:

```bash
mcpsense mcp ./openapi.yaml --bearer-token "$API_TOKEN"
```

Register it with an MCP client:

```json
{
  "mcpServers": {
    "example-api": {
      "command": "mcpsense",
      "args": ["mcp", "/path/to/openapi.yaml", "--bearer-token", "..."]
    }
  }
}
```

Up to 30 operations (configurable with `--threshold`), every operation becomes one tool. Path,
query, header and cookie parameters appear as top-level arguments; the request body arrives as a
single `body` argument.

Beyond that, McpSense advertises three tools instead, and the model works through them:

```text
search_operations   { "query": "list the emojis github supports" }
  -> emojis_get  (GET /emojis)  "Get emojis"
describe_operation  { "operation": "emojis_get" }
  -> the operation's JSON Schema
call_operation      { "operation": "emojis_get", "arguments": {} }
  -> the API's response
```

Force either shape with `--mode direct` or `--mode meta`.

### Command reference

`dump-operations <spec>` — print what the parser extracted from a spec.

| Option | Description |
| --- | --- |
| `--detail` | Expand each operation's parameters and request body. |
| `--json` | Emit machine-readable JSON, including each parameter and body schema. |
| `--tag <name>` | Only show operations carrying this tag. |

`dump-tools <spec>` — print the tools an MCP client would receive, one stage further down the
pipeline. Useful when a model calls a tool with the wrong shape of arguments.

| Option | Description |
| --- | --- |
| `--schema` | Print each tool's full input schema. |
| `--tool <name>` | Only show the tool with this name. |
| *(plus every selection option below)* | |

`mcp <spec>` — serve the spec over stdio.

| Option | Description |
| --- | --- |
| `--base-url <url>` | Base URL of the API. Defaults to the spec's first `servers` entry. |
| `--bearer-token <token>` | Sent as `Authorization: Bearer ...` on every request. |
| `--api-key-header <name>` / `--api-key <value>` | Sent as a header on every request. |
| `--header "Name: Value"` | Any other header, on every request. Repeatable. A `User-Agent` of `mcpsense/<version>` is sent unless you override it. |

`login` — authorize McpSense against an API with OAuth 2.0 and store the tokens. Pass the same
`--oauth-*` options to `mcp` afterwards so it finds them.

| Option | Description |
| --- | --- |
| `--oauth-authorization-endpoint <url>` | Where the user approves access. |
| `--oauth-token-endpoint <url>` | Where codes and refresh tokens are exchanged. |
| `--oauth-client-id <id>` | Client identifier registered with the provider. |
| `--oauth-client-secret <secret>` | Only for providers requiring a confidential client. |
| `--oauth-scope <scopes>` | Scopes to request, comma or space separated. |
| `--oauth-redirect-port <port>` | Fixed loopback port, for providers that require an exact redirect URI. |
| `--token-dir <dir>` | Where token files are stored. Defaults to a per-user directory. |
| `--no-browser` | Print the authorization URL instead of opening a browser. |

AI options, shared by `dump-tools` and `mcp`. Everything stays off until a model is named:

| Option | Description |
| --- | --- |
| `--ai-model <name>` | Chat model used to rewrite names, descriptions and parameter hints. |
| `--ai-embedding-model <name>` | Embedding model for semantic search. Search stays lexical without it. |
| `--ai-endpoint <url>` | Any OpenAI-compatible endpoint. Defaults to OpenAI. |
| `--ai-api-key <key>` | Falls back to the `OPENAI_API_KEY` environment variable. |
| `--ai-cache <dir>` | Where cached model responses live. Defaults to a per-user directory. |
| `--ai-batch-size <n>` | Operations per rewrite request. Defaults to 10. |
| `--ai-keep-names` | Rewrite descriptions but leave names as the spec had them. |

Selection options, shared by `dump-operations`, `dump-tools` and `mcp`:

| Option | Description |
| --- | --- |
| `--mode <auto\|direct\|meta>` | Force the presentation. Defaults to `auto`. |
| `--threshold <n>` | Operation count above which `auto` switches to meta-tools. Defaults to 30. |
| `--tag <names>` | Only include operations carrying these tags (comma-separated). |
| `--allow <patterns>` | Only include operations whose name matches (comma-separated, `*` allowed). |
| `--deny <patterns>` | Exclude operations whose name matches (comma-separated, `*` allowed). |

## How it works

At startup the spec is read once and turned into a catalog; at call time the catalog is consulted
to build an HTTP request.

```text
OpenAPI spec (URL or file)
  -> OpenApiSpecLoader        reads JSON/YAML, reports validation findings without rejecting the document
  -> OperationModelBuilder    operationId, method + path template, parameter placement,
                              request body schema, security requirements, tags
  -> ToolGroupingEngine       tag-based grouping; meta-tool mode past the threshold;
                              lexical search by default, embeddings when configured
  -> DescriptionEnhancer      [opt-in] rewrites names, descriptions and parameter hints through an
                              IChatClient, batched and cached on disk
  -> ToolCatalog              the tools served to the client, with local $refs inlined

CallTool -> look up the invocation plan -> build an HttpRequestMessage
         -> send it, adding credentials from configuration (never exposed to the model)
         -> convert the response into a CallToolResult
```

Credentials are supplied once at startup and attached server-side. They are never placed in a tool
schema and never reach the model.

### Consent is obtained before the server starts

An API that needs OAuth is authorized with a separate `mcpsense login` step:

```bash
mcpsense login \
  --oauth-authorization-endpoint https://provider.example/authorize \
  --oauth-token-endpoint https://provider.example/token \
  --oauth-client-id your-client-id \
  --oauth-scope "read write"
```

That opens a browser, catches the redirect on a loopback port, and stores the tokens. Afterwards the
server refreshes them on its own as they expire, and never needs a person again.

It has to work this way round. An MCP server speaking stdio has its stdin and stdout taken by the
protocol, so there is no channel on which to show anybody a consent page. A server that discovered
mid-call that it needed consent could only fail — which is exactly what McpSense does, with a
message saying to run `mcpsense login`.

The flow uses PKCE and is not optional about it. McpSense runs on the user's machine, so it is a
public client with no secret worth the name; without the proof key, an authorization code observed
on the loopback interface could be redeemed by anything else on that machine. Stored tokens are
encrypted with DPAPI on Windows and written owner-only elsewhere.

### AI never becomes a dependency

The AI stages are opt-in, and nothing about them is allowed to become load-bearing. If the model
errors, times out, or returns something that will not parse, the affected operations keep the
spec's own wording and startup continues — an outage at a model provider must not stop a server
whose whole point is that it works without one.

Rewrites are batched (ten operations per request by default) so a thousand-operation spec costs
about a hundred requests rather than a thousand, and cached on disk so the second start costs
nothing. Caching a response in memory would not help here: a CLI process exits between runs, so
every start would pay again.

### Search works without AI

Meta-tool mode is triggered by how large a spec is, not by whether an AI model was configured. If
search needed embeddings, every large spec would need AI — which would defeat the point of working
fully without it. So the default index is lexical: it splits identifiers into words, weights rare
terms above common ones, and ranks a match in an operation's name above one in its prose. That
finds an operation when the caller uses roughly the API's own vocabulary.

Configuring an `IEmbeddingGenerator` swaps in semantic search, which also finds operations whose
wording differs from the query. Every operation is embedded once at startup, in a single batched
call.

### Schemas are self-contained

An MCP client never sees the OpenAPI document, so a tool schema containing
`{"$ref": "#/components/schemas/Pet"}` would be meaningless to it. Every local reference is inlined
before a tool is advertised, including in specs whose schemas refer to themselves.

### Specs are taken as they are

Real-world specs routinely fail strict OpenAPI validation while remaining perfectly usable — even
first-party specs published by API vendors do. A proxy that is stricter than the vendor is not much
use, so McpSense fails only when no document can be produced at all. Validation findings are
reported as non-fatal problems and the operations are served regardless.

## Project status

| Milestone | State |
| --- | --- |
| M0 Scaffolding | Done |
| M1 Spec parsing pipeline and `dump-operations` | Done |
| M2 Dynamic MCP server over stdio, one tool per operation | Done |
| M3 Tag grouping, meta-tool mode, search | Done |
| M4 Opt-in AI description enhancement | Done |
| M5 Authentication (API key, Bearer, OAuth 2.0 + PKCE) and distribution | Done |
| M6 Documentation, sample specs, NuGet release | Done |

Released versions are listed in the [changelog](CHANGELOG.md).

## Contributing

Issues and pull requests are welcome. Please keep source comments in English; user-facing
documentation is written in English first, with a Japanese counterpart in a separate file.

## License

[MIT](LICENSE)
