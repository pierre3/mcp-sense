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

---

## GitHub API を MCP サーバーとして使う

`bookshop.yml` は意図的に小さくしてあります。一方 GitHub の REST API は **47 タグ・1,239 オペレーション**
と桁違いに大きく、しかも誰でも試せます。以下はその手順です。

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
[classic token](https://github.com/settings/tokens) でも同様に動きます。トークンを手で更新したくない
場合は、代わりに OAuth で認証してください。後述の
[GitHub を OAuth で認証する](#github-を-oauth-で認証する) を参照。

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

## GitHub を OAuth で認証する

personal access token は手軽ですが、期限切れのたびに手で更新が必要です。長く使うなら、
GitHub の **OAuth 2.0 認可コードフロー(PKCE)** で認証すると、以降はトークンが自動で更新されます。

ブラウザでの承認は `mcpsense login` で一度だけ行います。サーバー(`mcpsense mcp`)は
stdin/stdout をプロトコルに使うためブラウザを開けず、保存済みのトークンを読んで期限切れ時に
裏で更新するだけです。

### 1. OAuth App を登録する

classic OAuth App は API では作れないので、ブラウザで作成します。
**Settings → Developer settings → OAuth Apps → New OAuth App**。

| 項目 | 値 |
| --- | --- |
| Application name | 任意 |
| Homepage URL | 任意 |
| **Authorization callback URL** | `http://127.0.0.1:8765/`(末尾スラッシュ含む) |
| Enable Device Flow | チェックしない |

登録したら **Client ID** をコピーし、**Generate a new client secret** でシークレットを生成します
(一度しか表示されません)。コールバックのポート(ここでは `8765`)は `--oauth-redirect-port` と
一致させてください。

### 2. シークレットを設定ファイルの外に置く

Client ID は秘密ではないので `.mcp.json` に直書きで構いませんが、シークレットは書かず、
MCP クライアントが読める環境変数に入れます。

```powershell
[Environment]::SetEnvironmentVariable("GH_OAUTH_SECRET", "<secret>", "User")
```

環境変数は、設定した時点で起動済みのプロセスには反映されません。設定後はシェルを、
のちほど MCP クライアントも再起動してください。

### 3. 一度だけ認可する

```bash
mcpsense login \
  --oauth-authorization-endpoint https://github.com/login/oauth/authorize \
  --oauth-token-endpoint https://github.com/login/oauth/access_token \
  --oauth-client-id <client-id> \
  --oauth-client-secret "$GH_OAUTH_SECRET" \
  --oauth-scope repo \
  --oauth-redirect-port 8765
```

ブラウザで `repo` スコープを承認します(`repo` はプライベートリポジトリにも届きます。公開だけなら
絞ってください)。成功すると次が表示されます。

```text
Authorized. Tokens stored.
The access token expires at <time>Z and will be refreshed automatically.
```

ブラウザに出る「タブを閉じてよい」という画面は、トークン交換が終わる前の表示です。完了したかは
上のコンソール出力で確認してください。認可コードは使い捨てで短命なので、途中で止まったら
やり直せば大丈夫です。

### 4. サーバー側の設定

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

トークンは **認可エンドポイント・Client ID・スコープ** から作ったキーで保存されます。この 3 つが
`login` と `mcp` で一致していないと、サーバーはトークンを見つけられません。トークンエンドポイントと
シークレットはキーには含まれませんが、更新に必要なので同じく渡します。

## AI 機能を有効にする

ここまでは AI なしで動きます。チャットモデルを指定すると説明が書き換えられ、埋め込みモデルを
指定すると検索が字句ベースから意味ベースに変わります。[メインの README](../README_ja.md) にある
2 つの `--ai-*` 機能を、実際に動くバックエンドで試します。

### OpenAI 互換エンドポイントを選ぶ

> **GitHub Models は 2026-07-30 に終了しました。** `models.github.ai` はどのリクエストにも
> `text/plain` の `OK` を返すだけで、バックエンドには使えません。GitHub は代替として
> Azure AI Foundry か GitHub Copilot を挙げています。

ここでは OpenAI 互換エンドポイントを持つ **Azure AI Foundry** を使います。チャットモデルと
埋め込みモデルをデプロイしてください(この手順では `gpt-4.1-mini` と `text-embedding-3-small`)。
Azure の v1 エンドポイントは次の形式です。

```text
https://<resource>.services.ai.azure.com/openai/v1/responses
```

McpSense に渡すのは **末尾の `/responses` を除いたベース URL** です。`/chat/completions` と
`/embeddings` は McpSense が付けます。

```text
https://<resource>.services.ai.azure.com/openai/v1
```

API キーは OAuth シークレットと同様、環境変数に入れておきます。

```powershell
[Environment]::SetEnvironmentVariable("AZURE_AI_KEY", "<key>", "User")
```

### フラグを足す

`--ai-*` オプションは `mcp` でも `dump-tools` でも、認証フラグと並べて指定できます。

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

`dump-tools` は `--ai-model` / `--ai-endpoint` / `--ai-api-key` を受け付けますが、
`--ai-embedding-model` は受け付けません。埋め込みは `mcp` の `search_operations` でのみ使います。

### 説明の書き換え

同じタグに `--ai-model` ありとなしで `dump-tools` を流すと違いが分かります。
GitHub の `rate-limit` 操作が分かりやすい例です。

```text
before  rate-limit_get  (GET /rate_limit)
        | Get rate limit status for the authenticated user
        | > [!NOTE] Accessing this endpoint does not count against your REST API rate limit.
        | ... カテゴリごとの注記と docs リンクで約 20 行

after   get_rate_limit_status  (GET /rate_limit)
        | Get rate limit status for the authenticated user
        | Retrieve the current rate limit status ... does not count against your rate limit.
```

変わるのは 2 点です。名前が動詞で始まる `snake_case` に整い(`rate-limit_get` →
`get_rate_limit_status`)、約 20 行のドキュメント断片が数文に縮みます。モデルのツール選択を
いちばん助けるのは分かりやすい名前で、これがこの機能の狙いです。

書き換わるのは操作名・操作の説明・**URL** パラメータの説明(path / query / header。例えば
`owner`、`repo`、issue 番号)です。リクエストボディの各フィールドは対象外で、`create_issue` の
ような書き込みでは `title` / `body` / `labels` の説明は spec のまま(Markdown 込み)残ります。
型・`required`・`enum`・パラメータ名も変わりません。

### セマンティック検索

埋め込みモデルを指定すると、`search_operations` が意味でマッチするようになり、操作名と語が
重ならないクエリでも見つかります。

| クエリ | 1 位ヒット |
| --- | --- |
| stop watching a repository so I no longer get notifications | `delete_repo_subscription` |
| invite a person to collaborate with write access | `add_repo_collaborator` |
| merge a pull request | `merge_pull_request` |

弱いのは言語をまたいだ並び順です。同じ内容を日本語で聞くと、意図した操作が語の似た別の操作の
下に埋もれることがあります。**英語 + `--tag` での絞り込み** を併せると安定します。

### 初回のコスト

`--ai-model` 付きの初回起動では全操作を書き換えるため、GitHub 仕様では約 124 回(10 操作ずつ)の
リクエストが出ます。2 回目以降はディスクキャッシュを読むだけで、何も送りません。`--tag` / `--allow` /
`--deny` は書き換えの **後** に効くので、絞り込んでも初回のコストは変わりません。
キャッシュは既定で `%LOCALAPPDATA%\mcpsense\ai-cache\` に保存され、操作のテキストをキーにするので、
spec を編集すれば自動的に別のキーになります。
