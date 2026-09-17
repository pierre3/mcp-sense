# サンプル

*This document in [English](README.md).*

`bookshop.yml` は、単体で完結する小さな OpenAPI 定義です。McpSense が扱う必要のある要素を
ひととおり含むように書いてあります。対応する全ての位置に置かれたパラメータ、リクエストボディ、
スキーマ間の `$ref`、2 種類の認証方式、認証を明示的に外した操作、非推奨の操作などです。
3 つのタグに 13 オペレーションあり、公開の仕方を切り替えて試すには十分で、
かつ読むのが面倒になるほど大きくはありません。

`servers` の URL には何も待ち受けていないので、ツールを呼び出すと接続に失敗します。
McpSense が spec をどう解釈するかを見るためのものです。応答まで見たい場合は実在の API を指してください。

## spec の解釈を確認する

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

`--detail` を付けると各オペレーションのパラメータとボディが展開されます。
`--json` なら同じ内容を `jq` に流せる形で出力します。

## MCP クライアントが受け取るツールを見る

```bash
mcpsense dump-tools samples/bookshop.yml --tool placeOrder --schema
```

`placeOrder` が一番見どころのある操作です。必須のヘッダーパラメータに加えて、
2 つの別スキーマを参照するボディを持ちます。出力されたスキーマに `$ref` は残っていません。
クライアントが spec を持たなくても済むよう、McpSense が展開しているからです。

## meta-tool モードに切り替える

13 オペレーションは既定の閾値を下回るので、標準では全オペレーションがそのまま公開されます。
閾値を下げると、もう一方のモードを確認できます。

```bash
mcpsense dump-tools samples/bookshop.yml --threshold 5
```

```text
mode:       MetaTool
operations: 13
advertised: 3
groups:     (untagged) (1), admin (4), books (3), orders (5)
```

13 個ではなく 3 個のツールが公開され、タグのグループが目次になります。
`--mode meta` なら件数に関係なく同じ状態に、`--mode direct` なら逆に固定できます。

## 公開する範囲を絞る

```bash
# カタログの参照のみ
mcpsense dump-tools samples/bookshop.yml --tag books --deny "add*,remove*,replace*"
```

```text
mode:       Direct
operations: 3
advertised: 3
groups:     books (3)
```

`--tag` は先頭のタグだけでなく、その操作が持つどのタグとも一致します。
そのため `[admin, books]` を持つ `addBook` は `--tag books` に引っかかるので、名前で除外しています。

## サーバーとして動かす

```bash
mcpsense mcp samples/bookshop.yml --bearer-token "$TOKEN"
```

このコマンドを MCP クライアントに登録します。登録方法は[メインの README](../README_ja.md) を参照してください。

## GitHub API を MCP サーバーとして使う

`bookshop.yml` は意図的に小さくしてあります。対する GitHub の REST API は **47 タグ・1,239 オペレーション**
という反対側の極端な例で、誰でも試せます。以下はその手順です。

### 1. spec を取得する

```bash
curl -sL -o api.github.com.json \
  https://raw.githubusercontent.com/github/rest-api-description/main/descriptions/api.github.com/api.github.com.json
```

McpSense は URL をそのまま読めますが、ローカルに置く価値はあります。13 MB あり、
MCP クライアントはセッションごとにサーバーを起動し直すためです。

### 2. どう解釈されるかを見る

```bash
mcpsense dump-tools ./api.github.com.json
```

```text
mode:       MetaTool
operations: 1239
advertised: 3
groups:     actions (199), activity (34), agent-tasks (5), agents (30), apps (37), billing (13), ...
```

既定の閾値 30 をはるかに超えるので meta-tool モードになります。1,239 個ではなく 3 個のツールが公開され、
タグが目次になります。そのまま公開するとツール一覧は約 1.8 MB になり、多くのコンテキストウィンドウに収まりません。

### 3. トークンを用意する

GitHub は personal access token をベアラートークンとして受け付けます。
[fine-grained token](https://github.com/settings/personal-access-tokens) を作成し、モデルに触らせたい
リポジトリと権限だけを付与してください。**トークンにできることは、そのままツール呼び出しにできること**です。
[classic token](https://github.com/settings/tokens) でも同様に動きます。

他に設定は要りません。ベース URL は spec の `servers`(`https://api.github.com`)から取得します。
GitHub は `User-Agent` の無いリクエストを拒否しますが、McpSense は `--header` で上書きしない限り
`mcpsense/<version>` を送ります。

### 4. MCP クライアントに登録する

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

バージョンヘッダーは任意です。将来の API バージョンで応答が変わらないよう、GitHub が固定を推奨しています。
トークンはクライアントの設定ファイルに平文で置かれるので、権限を絞る理由がもう一つあります。

### 5. モデルからはこう見える

meta-tool モードでは、モデルは 3 ステップでオペレーションに辿り着きます。
オペレーション名は公開されていないので、まず検索から始まります。

```text
search_operations   { "query": "list repositories for the authenticated user", "tag": "repos" }
  -> repos_list-for-authenticated-user             (GET /user/repos)
     repos_list-invitations-for-authenticated-user (GET /user/repository_invitations)
     repos_list-for-user                           (GET /users/{username}/repos)
     ... 10 件

describe_operation  { "operation": "repos_list-for-authenticated-user" }
  -> GET /user/repos、説明文、引数の JSON Schema

call_operation      { "operation": "repos_list-for-authenticated-user",
                      "arguments": { "sort": "updated", "per_page": 2 } }
  -> [{"id":1366969568,"name":"mcp-sense","full_name":"pierre3/mcp-sense", ...
```

検索は既定で語彙ベースなので、AI を設定しなくても動きます。
`--ai-embedding-model text-embedding-3-small` を付けると意味ベースの検索に切り替わり、
依頼の言い回しと spec の語彙が食い違う場合に効きます。

### 6. 必要な範囲だけに絞る

meta-tool モードのおかげで 1,239 オペレーションでも破綻しませんが、issue と pull request しか扱わない
用途なら、範囲を絞って直接公開するほうが快適です。

```bash
mcpsense dump-tools ./api.github.com.json --tag issues,pulls --allow "*list*,*get*" --mode direct
```

```text
mode:       Direct
operations: 39
advertised: 39
groups:     issues (25), pulls (14)
```

39 個なら多くのクライアントが問題なく扱える量で、検索を挟まずにモデルから見えます。
`--allow` で書き込み系を除外しているので、読み取り専用トークンと二重の歯止めになります。
`mcp` にも同じオプションを渡せば、その範囲でサーバーが起動します。

### 7. クライアント無しで動作確認する

サーバーは stdio 上の JSON-RPC なので、プローブ用のファイルがあれば設定を確認できます。
GitHub は未認証でも 1 時間に 60 リクエストを許すため、トークンを用意する前に疎通だけ確認できます。

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

`sleep` を挟んで **stdin を開いたままにする**のが要点です。ファイルを流し込んで即 EOF にすると、
応答が flush される前にシャットダウンが始まり、stdout が空になります。
実際の MCP クライアントは stdin を保持するので、手で叩くときだけの注意点です。
