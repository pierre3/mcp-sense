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

## Trying a large spec

McpSense reads a URL as happily as a file, so a large public spec needs no download:

```bash
mcpsense dump-tools \
  https://raw.githubusercontent.com/github/rest-api-description/main/descriptions/api.github.com/api.github.com.json
```

GitHub's description has over a thousand operations, so this lands in meta-tool mode and prints the
47 groups it found. It is the case the mode exists for: advertising those operations directly
produces a tool list far larger than a model can read.
