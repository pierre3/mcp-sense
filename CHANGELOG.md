# Changelog

*Read this in [日本語](CHANGELOG_ja.md).*

This project follows [Semantic Versioning](https://semver.org/spec/v2.0.0.html). Until 1.0.0 the
public API may change between preview releases.

## [Unreleased]

Nothing yet.

## [0.1.0-preview] - 2026-09-14

First preview, published to NuGet as
[`McpSense.Tool`](https://www.nuget.org/packages/McpSense.Tool). Everything below works and is
covered by tests, but the command line is not stable yet.

### Serving a spec

- Read OpenAPI 3.0 and 3.1 descriptions, JSON or YAML, from a local path or a URL.
- Serve them as MCP tools over stdio (`mcpsense mcp`), which is what Claude Desktop and Claude Code expect.
- One tool per operation: path, query, header and cookie parameters become top-level arguments and
  the request body arrives as a single `body` argument.
- Local `$ref`s are inlined into each tool's schema, including in specs whose schemas refer to
  themselves, so a client never needs the spec.
- Upstream error responses are passed through unchanged, because the API's own message is what the
  model needs to act on.

### Large specs

- Past a configurable threshold (`--threshold`, default 30), advertise three tools —
  `search_operations`, `describe_operation`, `call_operation` — instead of one per operation, with
  the spec's own tags as a table of contents. `--mode` forces either shape.
- Search runs without any AI configured: the default index splits identifiers into words, weights
  rare terms above common ones, and ranks name matches above prose matches.
- Narrow what is exposed with `--tag`, `--allow` and `--deny` (wildcards allowed; deny wins).

### Opt-in AI

- Rewrite tool names, descriptions and parameter hints through any `IChatClient` (`--ai-model`),
  batched to keep the request count down.
- Responses are cached on disk, so a second start over an unchanged spec sends nothing to the model.
- Semantic search through any `IEmbeddingGenerator` (`--ai-embedding-model`), embedded once at
  startup in a single batched call.
- A model error, timeout or unparseable reply is never fatal: the affected operations keep the
  spec's own text and the server still starts.

### Authentication

- Static credentials: `--bearer-token`, `--api-key-header` / `--api-key`, and arbitrary headers via
  `--header`. A `User-Agent` is sent unless overridden, because some APIs reject requests without one.
- OAuth 2.0 authorization code flow with PKCE (`mcpsense login`), with a loopback redirect, `state`
  validation, and automatic token refresh afterwards. Refreshes are serialised so concurrent tool
  calls cannot redeem the same refresh token twice.
- Tokens are stored per provider, client and scope; encrypted with DPAPI on Windows and written
  owner-only elsewhere.
- Credentials never appear in a tool schema or an argument. They are attached server-side, just
  before the request is sent.

### Deliberately left out

- **Streamable HTTP.** A transport only pays off if the server can be exposed, and exposing it
  safely needs an answer to who may connect — and, beyond that, to whose credentials a connecting
  user's calls should run under. Shipping a transport that works but must not be exposed would be a
  trap, so it waits until the authentication comes with it.
- **The libraries as packages.** Only `McpSense.Tool` is published. The tool bundles
  `McpSense.Core`, `McpSense.Ai` and `McpSense.Server` inside itself and declares no NuGet
  dependencies, so installing the CLI needs nothing else. Publishing them separately would commit
  to a public API before anyone has asked for one, and while packing can be turned on later, a
  published package cannot be withdrawn.

[Unreleased]: https://github.com/pierre3/mcp-sense/compare/v0.1.0-preview...HEAD
[0.1.0-preview]: https://github.com/pierre3/mcp-sense/releases/tag/v0.1.0-preview
