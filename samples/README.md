# Samples

*Read this in [日本語](README_ja.md).*

`bookshop.yml` is a small self-contained OpenAPI description, written to exercise the parts of a
spec McpSense has to handle: parameters in every supported location, a request body, `$ref` between
schemas, two security schemes, an operation that opts out of authentication, and a deprecated one.
It has 13 operations across 3 tags — enough to switch between presentation modes without being large
enough to be tedious.

Nothing listens at the `servers` URL, so tool calls will fail to connect. Use it to see what
McpSense makes of a spec; point at a real API when you want responses.

## Inspecting the spec

```bash
mcpsense dump-operations samples/bookshop.yml
```

```text
spec:       samples/bookshop.yml
version:    OpenApi3_0
title:      Bookshop API
operations: 13
tags:       3
```

Add `--detail` to expand each operation's parameters and body, or `--json` for the same data in a
form you can pipe into `jq`.

## Seeing the tools an MCP client receives

```bash
mcpsense dump-tools samples/bookshop.yml --tool placeOrder --schema
```

`placeOrder` is the interesting one: it takes a required header parameter alongside a body whose
schema refers to two others. The printed schema has no `$ref` left in it — that is the inlining
McpSense does so a client never needs the spec.

## Switching to meta-tool mode

With 13 operations the spec stays under the default threshold, so every operation is advertised
directly. Lower the threshold to see the other mode:

```bash
mcpsense dump-tools samples/bookshop.yml --threshold 5
```

```text
mode:       MetaTool
operations: 13
advertised: 3
groups:     (untagged) (1), admin (4), books (3), orders (5)
```

Three tools are advertised instead of thirteen, and the tag groups become the table of contents.
`--mode meta` forces the same thing regardless of size, and `--mode direct` forces the opposite.

## Narrowing what is exposed

```bash
# the catalogue, read-only
mcpsense dump-tools samples/bookshop.yml --tag books --deny "add*,remove*,replace*"
```

```text
mode:       Direct
operations: 3
advertised: 3
groups:     books (3)
```

`--tag` matches any tag an operation carries, not just its first one, so `addBook` — tagged
`[admin, books]` — is caught by `--tag books` and has to be excluded by name.

## Serving it

```bash
mcpsense mcp samples/bookshop.yml --bearer-token "$TOKEN"
```

Then register that command with an MCP client, as described in the [main README](../README.md).

## Serving GitHub's API

`bookshop.yml` is deliberately small. GitHub's REST API is the opposite case — **1,239 operations
across 47 tags** — and anyone can point McpSense at it, so the rest of this page walks it through end
to end.

### 1. Get the spec

```bash
curl -sL -o api.github.com.json \
  https://raw.githubusercontent.com/github/rest-api-description/main/descriptions/api.github.com/api.github.com.json
```

McpSense reads that URL directly too, but keeping the file locally is worth the step: it is 13 MB,
and an MCP client starts the server again for every session.

### 2. See what it becomes

```bash
mcpsense dump-tools ./api.github.com.json
```

```text
mode:       MetaTool
operations: 1239
advertised: 3
groups:     actions (199), activity (34), agent-tasks (5), agents (30), apps (37), billing (13), ...
```

Well past the default threshold of 30, so meta-tool mode: three tools instead of 1,239, with the
tags as the table of contents. Advertising the operations directly would hand the model a tool list
of around 1.8 MB, more than most context windows hold.

### 3. Create a token

GitHub accepts a personal access token as a bearer token. Create a
[fine-grained token](https://github.com/settings/personal-access-tokens) and grant it only the
repositories and permissions you want the model to reach — whatever the token can do, a tool call
can do. A [classic token](https://github.com/settings/tokens) works the same way. To avoid rotating
a token by hand, authenticate with OAuth instead — see
[Authenticating GitHub with OAuth](#authenticating-github-with-oauth) below.

Nothing else needs configuring: the base URL comes from the spec's `servers` entry
(`https://api.github.com`), and GitHub rejects requests without a `User-Agent`, which McpSense sends
as `mcpsense/<version>` unless `--header` overrides it.

### 4. Register it with an MCP client

```json
{
  "mcpServers": {
    "github": {
      "command": "mcpsense",
      "args": [
        "mcp",
        "C:/path/to/api.github.com.json",
        "--bearer-token", "github_pat_...",
        "--header", "X-GitHub-Api-Version: 2022-11-28"
      ]
    }
  }
}
```

The version header is optional; GitHub recommends pinning it so a future API version cannot change
responses underneath you. The token sits in the client's configuration file in plain text, which is
the other reason to scope it narrowly.

### 5. What the model does with it

In meta-tool mode the model reaches an operation in three steps. Operation names are not advertised,
so it has to search first:

```text
search_operations   { "query": "list repositories for the authenticated user", "tag": "repos" }
  -> repos_list-for-authenticated-user             (GET /user/repos)
     repos_list-invitations-for-authenticated-user (GET /user/repository_invitations)
     repos_list-for-user                           (GET /users/{username}/repos)
     ... 10 matches

describe_operation  { "operation": "repos_list-for-authenticated-user" }
  -> GET /user/repos, its description, and the JSON Schema of its arguments

call_operation      { "operation": "repos_list-for-authenticated-user",
                      "arguments": { "sort": "updated", "per_page": 2 } }
  -> [{"id":1366969568,"name":"mcp-sense","full_name":"pierre3/mcp-sense", ...
```

Search is lexical by default, so this works with no AI configured. Adding
`--ai-embedding-model text-embedding-3-small` switches it to semantic matching, which helps when the
wording of the request and the wording of the spec have little in common.

### 6. Narrow it to the part you need

Meta-tool mode keeps 1,239 operations affordable, but a session that only reviews issues and pull
requests does better with a smaller, directly advertised surface:

```bash
mcpsense dump-tools ./api.github.com.json --tag issues,pulls --allow "*list*,*get*" --mode direct
```

```text
mode:       Direct
operations: 39
advertised: 39
groups:     issues (25), pulls (14)
```

39 tools is under most clients' comfort limit, and the model sees them without searching. `--allow`
leaves out everything that writes, which is a useful belt alongside a read-only token. Pass the same
options to `mcp` to serve that selection.

### 7. Checking it without a client

The server speaks JSON-RPC over stdio, so a probe file is enough to test a configuration. GitHub
allows 60 unauthenticated requests an hour — enough to confirm the plumbing before a token is
involved:

```bash
cat > probe.jsonl <<'EOF'
{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"probe","version":"1.0"}}}
{"jsonrpc":"2.0","method":"notifications/initialized"}
{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"rate-limit_get","arguments":{}}}
EOF

{ cat probe.jsonl; sleep 8; } | mcpsense mcp ./api.github.com.json --tag rate-limit --mode direct
```

```text
{"result":{"content":[{"type":"text","text":"{\"resources\":{\"core\":{\"limit\":60,\"remaining\":56, ...
```

Keep stdin open, as the `sleep` does. Piping the file in and closing it immediately lets shutdown
start before the responses are flushed, and stdout comes back empty. A real client holds stdin open,
so this only affects hand-run probes.

## Authenticating GitHub with OAuth

A personal access token is the quickest way in, but it has to be rotated by hand when it expires. For
a longer-lived setup McpSense can authenticate through GitHub's **OAuth 2.0 authorization code flow
with PKCE** instead, and refresh the access token on its own from then on.

Consent happens once, in a browser, through `mcpsense login`. The server (`mcpsense mcp`) never opens
a browser — its stdin and stdout belong to the protocol — so it only reads the stored token and
refreshes it in the background as it expires.

### 1. Register an OAuth App

GitHub's classic OAuth Apps cannot be created through the API, so do it in the browser:
**Settings → Developer settings → OAuth Apps → New OAuth App**.

| Field | Value |
| --- | --- |
| Application name | anything |
| Homepage URL | anything |
| **Authorization callback URL** | `http://127.0.0.1:8765/` (trailing slash included) |
| Enable Device Flow | leave unchecked |

Register it, then copy the **Client ID** and **Generate a new client secret** (shown only once). The
callback port — `8765` here — has to match `--oauth-redirect-port` exactly.

### 2. Keep the secret out of the config file

The client ID is not sensitive and can sit in `.mcp.json`; the secret should not. Put it in an
environment variable the MCP client will inherit:

```powershell
[Environment]::SetEnvironmentVariable("GH_OAUTH_SECRET", "<secret>", "User")
```

A persistent user environment variable is not visible to processes already running, so restart the
shell — and later the MCP client — after setting it.

### 3. Authorize once

```bash
mcpsense login \
  --oauth-authorization-endpoint https://github.com/login/oauth/authorize \
  --oauth-token-endpoint https://github.com/login/oauth/access_token \
  --oauth-client-id <client-id> \
  --oauth-client-secret "$GH_OAUTH_SECRET" \
  --oauth-scope repo \
  --oauth-redirect-port 8765
```

Approve the `repo` scope in the browser (`repo` reaches private repositories; narrow it if you only
need public ones). Success looks like this:

```text
Authorized. Tokens stored.
The access token expires at <time>Z and will be refreshed automatically.
```

The browser's "you can close this tab" page shows up *before* the token exchange finishes — the
console line above is what actually confirms it. The authorization code is single-use and
short-lived, so if a run stalls, just run it again.

### 4. Point the server at the stored token

```json
{
  "mcpServers": {
    "github": {
      "command": "mcpsense",
      "args": [
        "mcp",
        "C:/path/to/api.github.com.json",
        "--oauth-authorization-endpoint", "https://github.com/login/oauth/authorize",
        "--oauth-token-endpoint", "https://github.com/login/oauth/access_token",
        "--oauth-client-id", "<client-id>",
        "--oauth-client-secret", "${GH_OAUTH_SECRET}",
        "--oauth-scope", "repo",
        "--header", "X-GitHub-Api-Version: 2022-11-28"
      ]
    }
  }
}
```

The token is stored under a key derived from the **authorization endpoint, client ID and scope**, so
those three have to be identical between `login` and `mcp` or the server will not find it. The token
endpoint and client secret are not part of that key but are needed to refresh, so pass them as well.

## Enabling the AI features against a real backend

Everything above works with no AI configured. Naming a chat model turns on the description rewrite,
and naming an embedding model turns search from lexical into semantic — the two `--ai-*` features
from the [main README](../README.md), shown here against a working backend.

### Pick an OpenAI-compatible endpoint

> **GitHub Models retired on 2026-07-30.** `models.github.ai` now returns a `text/plain` `OK` to
> every request and cannot serve as a backend. GitHub points to Azure AI Foundry or GitHub Copilot
> instead.

The example below uses **Azure AI Foundry**, which exposes an OpenAI-compatible endpoint. Deploy a
chat model and an embedding model — this walkthrough used `gpt-4.1-mini` and
`text-embedding-3-small`. Azure hands out a v1 endpoint of the form:

```text
https://<resource>.services.ai.azure.com/openai/v1/responses
```

Give McpSense the base URL **without the trailing `/responses`**; it appends `/chat/completions` and
`/embeddings` itself:

```text
https://<resource>.services.ai.azure.com/openai/v1
```

Keep the API key in an environment variable, as with the OAuth secret:

```powershell
[Environment]::SetEnvironmentVariable("AZURE_AI_KEY", "<key>", "User")
```

### Add the flags

The `--ai-*` options join the auth flags on either `mcp` or `dump-tools`:

```json
"args": [
  "mcp",
  "C:/path/to/api.github.com.json",
  "--bearer-token", "github_pat_...",
  "--header", "X-GitHub-Api-Version: 2022-11-28",
  "--ai-model", "gpt-4.1-mini",
  "--ai-embedding-model", "text-embedding-3-small",
  "--ai-endpoint", "https://<resource>.services.ai.azure.com/openai/v1",
  "--ai-api-key", "${AZURE_AI_KEY}"
]
```

`dump-tools` accepts `--ai-model`, `--ai-endpoint` and `--ai-api-key` but not `--ai-embedding-model`
— embeddings only apply to `search_operations`, which `mcp` serves.

### What the rewrite does

Run `dump-tools` with and without `--ai-model` against the same tag to see the difference. GitHub's
`rate-limit` operation is a compact example:

```text
before  rate-limit_get  (GET /rate_limit)
        | Get rate limit status for the authenticated user
        | > [!NOTE] Accessing this endpoint does not count against your REST API rate limit.
        | ... ~20 lines of per-category notes and doc links

after   get_rate_limit_status  (GET /rate_limit)
        | Get rate limit status for the authenticated user
        | Retrieve the current rate limit status ... does not count against your rate limit.
```

Two things change: the name is normalised to a verb-first `snake_case` (`rate-limit_get` →
`get_rate_limit_status`), and ~20 lines of doc fragments compress to a couple of sentences. Better
names are what most improve the model's tool choice, which is the point of the feature.

The rewrite reaches operation names, operation descriptions and **URL** parameter hints (path, query
and header — e.g. `owner`, `repo`, an issue number). It does **not** touch request-body fields: for a
write like `create_issue` the `title` / `body` / `labels` descriptions stay as the spec wrote them,
Markdown and all. Types, `required`, `enum` and the parameter names themselves are never changed
either.

### What semantic search does

With an embedding model configured, `search_operations` matches on meaning, so a query that shares no
words with the operation still finds it:

| query | top hit |
| --- | --- |
| stop watching a repository so I no longer get notifications | `delete_repo_subscription` |
| invite a person to collaborate with write access | `add_repo_collaborator` |
| merge a pull request | `merge_pull_request` |

Its weak spot is cross-lingual ranking: the same request in Japanese can bury the intended operation
below newer, similarly-worded ones. English phrasing plus a `--tag` scope is the reliable
combination.

### Cost of the first run

The first start with `--ai-model` rewrites every operation, so GitHub's spec costs about 124 batched
requests (ten operations each); later starts read the disk cache and send nothing. `--tag`,
`--allow` and `--deny` are applied *after* the rewrite, so filtering does not lower that first-run
cost. The cache lives in `%LOCALAPPDATA%\mcpsense\ai-cache\` by default and is keyed on the operation
text, so editing the spec re-keys it automatically.
