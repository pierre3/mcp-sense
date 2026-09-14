# McpSense

任意の OpenAPI 定義を、グルーコードを 1 行も書かずに MCP サーバーへ変えます。

McpSense は OpenAPI 3.0/3.1 のドキュメントを実行時に読み込み、そのオペレーションを
[Model Context Protocol](https://modelcontextprotocol.io) のツールとして公開します。Claude Desktop や
Claude Code などの MCP クライアントから、その API を直接呼び出せるようになります。コード生成は行わず、
生成物をコミットすることもありません。spec を指すだけで動きます。

*This document in [English](README.md).*

> **状態: プレビュー。** 以下に書かれていることは全て現時点で動作します — 1 オペレーション = 1 ツールでは
> 到底収まらない大規模 spec、AI による説明改善、トークン自動更新付きの OAuth。NuGet への公開はまだなので、
> ソースからインストールしてください。

## 何を軸にしているか

spec を 1 オペレーション = 1 ツールに変換するところまでは難しくありません。McpSense は、
それを実際の API に対して使おうとしたときに出てくる 2 つの問題を軸に据えています。

**大規模 spec でも使えること。** 数百オペレーションある spec は、作業を始める前にツール一覧だけでモデルの
コンテキストを圧迫します。McpSense は設定可能な閾値を超えると meta-tool モード —
`search_operations` / `describe_operation` / `call_operation` — に切り替え、spec が元々持っているタグを
目次として使います。全オペレーションを渡さずに、どんな領域があるかをモデルに示せます。

GitHub の REST API が極端な例です。**1,229 オペレーション、そのツール一覧は JSON で 1.8 MB** — 大半の
コンテキストウィンドウに収まりません。meta-tool 経由なら同じ API が **3.1 KB** で公開され、約 600 分の 1 に
なります。47 のグループも冒頭で提示されます。

検索は AI 未設定でも動くため、大規模 spec もそのまま使えます。埋め込みジェネレータを設定すると意味ベースの
検索にアップグレードされます。類似度検索はインメモリで完結するため、外部のベクトル DB は不要です。明示的な
allowlist / denylist もエスケープハッチとして利用できます。

**開発者向けではなく、モデル向けの説明文。** spec からそのまま持ってきたツール名や説明は、簡潔すぎたり、
表記が揺れていたり、モデルが持っていない前提を要求したりしがちです。GitHub の spec には
`activity_list-repos-starred-by-authenticated-user` のような名前が並びます。McpSense は任意の
`IChatClient` を使って名前・説明・パラメータの補足を書き換えられます。レスポンスはディスクにキャッシュ
されるため、spec が変わらなければ 2 回目の起動でモデルへのリクエストは 1 件も発生しません。

後者の機能は**オプトイン**です。McpSense は AI 抜きで完全に動作するよう設計されています。モデルを指定
しなければ spec の原文がそのまま使われます。AI への依存は別アセンブリに隔離してあり、この機能を使わない
ビルドが依存を抱え込むことはありません。モデルがエラーを返したり意味不明な応答をした場合も、spec の原文が
保たれてサーバーは起動します。

## 動作要件

- [.NET 10 SDK](https://dotnet.microsoft.com/download) 以降

## 使い始める

McpSense はまだ NuGet に公開していないため、ソースからビルドしてインストールしてください。

```bash
git clone https://github.com/pierre3/mcp-sense.git
cd mcp-sense
dotnet pack src/McpSense.Tool --configuration Release -o ./artifacts
dotnet tool install --global --add-source ./artifacts McpSense.Tool --version 0.1.0-preview
```

これで `mcpsense` コマンドが入ります。McpSense 自体を開発する場合は `dotnet build McpSense.slnx` して
`dotnet run --project src/McpSense.Tool -- <command>` で実行してください。

[`samples/`](samples/) に、要点を一通り含む小さな spec と、各モードを試すためのコマンドを置いてあります。

spec を読ませて、McpSense が抽出するオペレーションを確認します。

```bash
mcpsense dump-operations https://example.com/openapi.yaml
```

引数はローカルのファイルパスまたは URL で、JSON・YAML のどちらでも構いません。

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

### spec を公開する

`mcpsense mcp` は stdio 越しに MCP を話します。Claude Desktop や Claude Code が期待する形式です。

```bash
mcpsense mcp ./openapi.yaml --bearer-token "$API_TOKEN"
```

MCP クライアントへは次のように登録します。

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

30 オペレーション(`--threshold` で変更可)までは 1 オペレーションが 1 ツールになります。path・query・header・
cookie のパラメータはトップレベルの引数として現れ、リクエストボディは `body` という 1 つの引数で渡します。

それを超えると、McpSense は代わりに 3 つのツールを公開し、モデルはこれらを経由して作業します。

```text
search_operations   { "query": "list the emojis github supports" }
  -> emojis_get  (GET /emojis)  "Get emojis"
describe_operation  { "operation": "emojis_get" }
  -> そのオペレーションの JSON Schema
call_operation      { "operation": "emojis_get", "arguments": {} }
  -> API のレスポンス
```

`--mode direct` / `--mode meta` でどちらかに固定できます。

### コマンド一覧

`dump-operations <spec>` — パーサが spec から抽出した内容を表示する。

| オプション | 説明 |
| --- | --- |
| `--detail` | 各オペレーションのパラメータとリクエストボディを展開して表示する。 |
| `--json` | 機械可読な JSON を出力する。パラメータとボディのスキーマを含む。 |
| `--tag <name>` | 指定したタグを持つオペレーションだけを表示する。 |

`dump-tools <spec>` — MCP クライアントが実際に受け取るツールを表示する(パイプラインの 1 段下)。
モデルが誤った形の引数でツールを呼ぶときの調査に使う。

| オプション | 説明 |
| --- | --- |
| `--schema` | 各ツールの入力スキーマを全文表示する。 |
| `--tool <name>` | 指定した名前のツールだけを表示する。 |
| *(下記の選択オプションも使える)* | |

`mcp <spec>` — spec を stdio 越しに公開する。

| オプション | 説明 |
| --- | --- |
| `--base-url <url>` | API のベース URL。既定は spec の最初の `servers` エントリ。 |
| `--bearer-token <token>` | 全リクエストに `Authorization: Bearer ...` として付与する。 |
| `--api-key-header <name>` / `--api-key <value>` | 全リクエストにヘッダとして付与する。 |
| `--header "Name: Value"` | その他の任意のヘッダを全リクエストに付与する。複数指定可。上書きしない限り `User-Agent: mcpsense/<version>` を送る。 |

`login` — OAuth 2.0 で API への認可を取得し、トークンを保存する。取得後は同じ `--oauth-*` オプションを
`mcp` にも渡すことで、保存済みトークンが使われる。

| オプション | 説明 |
| --- | --- |
| `--oauth-authorization-endpoint <url>` | ユーザーが認可する画面の URL。 |
| `--oauth-token-endpoint <url>` | 認可コードとリフレッシュトークンを交換する URL。 |
| `--oauth-client-id <id>` | プロバイダに登録したクライアント識別子。 |
| `--oauth-client-secret <secret>` | 機密クライアントを要求するプロバイダ向け。 |
| `--oauth-scope <scopes>` | 要求するスコープ。カンマまたは空白区切り。 |
| `--oauth-redirect-port <port>` | ループバックの固定ポート。リダイレクト URI の完全一致を要求するプロバイダ向け。 |
| `--token-dir <dir>` | トークンファイルの保存先。既定はユーザーごとのディレクトリ。 |
| `--no-browser` | ブラウザを開かず認可 URL を表示するだけにする。 |

AI オプション(`dump-tools` / `mcp` で共通)。モデルを指定するまで一切動きません:

| オプション | 説明 |
| --- | --- |
| `--ai-model <name>` | 名前・説明・パラメータの補足を書き換えるチャットモデル。 |
| `--ai-embedding-model <name>` | 意味検索に使う埋め込みモデル。未指定なら語彙検索のまま。 |
| `--ai-endpoint <url>` | OpenAI 互換の任意のエンドポイント。既定は OpenAI。 |
| `--ai-api-key <key>` | 未指定なら環境変数 `OPENAI_API_KEY` を使う。 |
| `--ai-cache <dir>` | モデル応答のキャッシュ先。既定はユーザーごとのディレクトリ。 |
| `--ai-batch-size <n>` | 1 リクエストあたりのオペレーション数。既定は 10。 |
| `--ai-keep-names` | 説明だけ書き換え、名前は spec のまま残す。 |

選択オプション(`dump-operations` / `dump-tools` / `mcp` で共通):

| オプション | 説明 |
| --- | --- |
| `--mode <auto\|direct\|meta>` | 公開形式を固定する。既定は `auto`。 |
| `--threshold <n>` | `auto` が meta-tool へ切り替わるオペレーション数。既定は 30。 |
| `--tag <names>` | 指定したタグを持つオペレーションだけを対象にする(カンマ区切り)。 |
| `--allow <patterns>` | 名前が一致するオペレーションだけを対象にする(カンマ区切り、`*` 可)。 |
| `--deny <patterns>` | 名前が一致するオペレーションを除外する(カンマ区切り、`*` 可)。 |

## 仕組み

起動時に spec を 1 度読んでカタログを構築し、呼び出し時にそのカタログを引いて HTTP リクエストを組み立てます。

```text
OpenAPI spec (URL またはファイル)
  -> OpenApiSpecLoader        JSON/YAML を読む。検証の指摘は報告するが、それでドキュメントを拒否はしない
  -> OperationModelBuilder    operationId、メソッド + パステンプレート、パラメータの配置、
                              リクエストボディのスキーマ、セキュリティ要件、タグ
  -> ToolGroupingEngine       タグベースのグルーピング。閾値超過で meta-tool モードへ。
                              既定は語彙検索、設定すれば埋め込み検索
  -> DescriptionEnhancer      [オプトイン] IChatClient で名前・説明・パラメータの補足を書き換える。
                              バッチ処理し、ディスクにキャッシュする
  -> ToolCatalog              クライアントへ公開するツール群(ローカル $ref はインライン展開済み)

CallTool -> 呼び出しプランを引く -> HttpRequestMessage を組み立てる
         -> 設定から認証情報を付与して送信する(モデルには一切渡らない)
         -> レスポンスを CallToolResult へ変換する
```

認証情報は起動時に一度渡され、サーバー側で付与されます。ツールのスキーマに現れることはなく、モデルに
届くこともありません。

### 同意はサーバー起動前に取得する

OAuth が必要な API へは、`mcpsense login` という別ステップで認可します。

```bash
mcpsense login \
  --oauth-authorization-endpoint https://provider.example/authorize \
  --oauth-token-endpoint https://provider.example/token \
  --oauth-client-id your-client-id \
  --oauth-scope "read write"
```

ブラウザを開き、ループバックポートでリダイレクトを受け取り、トークンを保存します。以降サーバーは期限切れに
応じて自動で更新するので、二度と人手を必要としません。

この順序でなければ成立しません。stdio で話す MCP サーバーは stdin と stdout をプロトコルに占有されており、
同意画面を誰かに見せるチャネルがありません。呼び出しの途中で同意が必要だと分かっても失敗させるしかなく、
McpSense はまさにそうします — `mcpsense login` を実行するよう促すメッセージを返します。

フローは PKCE を必須としています。McpSense はユーザーのマシン上で動くパブリッククライアントで、秘密と呼べる
ものを持てません。証明鍵が無ければ、ループバックインターフェース上で観測された認可コードを同じマシンの別の
プロセスが引き換えられてしまいます。保存したトークンは Windows では DPAPI で暗号化し、それ以外の OS では
所有者のみ読み書き可能な権限で書き込みます。

### AI は依存にならない

AI のステージはオプトインであり、どこも必須経路にはしていません。モデルがエラーを返しても、タイムアウト
しても、パースできない応答を返しても、該当オペレーションは spec の原文を保ったまま起動が続きます。AI 無しで
動くことが存在意義のサーバーが、モデル提供元の障害で止まってはいけません。

書き換えはバッチ処理(既定で 1 リクエストあたり 10 オペレーション)なので、1,000 オペレーションの spec でも
約 100 リクエストで済みます。さらにディスクにキャッシュするため 2 回目の起動はゼロコストです。メモリ
キャッシュでは意味がありません。CLI プロセスは実行ごとに終了するため、毎回モデルに課金が発生してしまいます。

### 検索は AI 無しで動く

meta-tool モードの発動条件は spec のサイズであって、AI モデルが設定されているかどうかではありません。検索が
埋め込み必須だと、すべての大規模 spec が AI 必須になり、「AI 無しで完全に動作する」という前提が崩れます。
そこで既定のインデックスは語彙ベースにしてあります。識別子を単語に分割し、希少な語を一般的な語より重く扱い、
オペレーション名での一致を説明文での一致より上位に置きます。これで、呼び出し側が API 本来の語彙に近い言葉を
使っていれば目的のオペレーションに辿り着けます。

`IEmbeddingGenerator` を設定すると意味ベースの検索に差し替わり、クエリと表現が異なるオペレーションも
見つかるようになります。全オペレーションは起動時に 1 回、バッチ 1 リクエストで埋め込まれます。

### スキーマは自己完結させる

MCP クライアントは OpenAPI ドキュメントを一切見ないため、`{"$ref": "#/components/schemas/Pet"}` を含む
ツールスキーマはクライアントにとって無意味です。McpSense はツールを公開する前にローカル参照をすべて
インライン展開します。自己参照するスキーマを含む spec でも同様です。

### spec はあるがままに受け取る

実運用の spec は、厳格な OpenAPI バリデーションに引っかかりながら実用上は何の問題もない、ということが
珍しくありません。API ベンダー自身が公開している一次 spec ですらそうです。提供元より厳格なプロキシは
使い物にならないため、McpSense はドキュメントをまったく生成できなかった場合にのみ失敗とします。検証の
指摘は非致命の問題として報告したうえで、オペレーションはそのまま公開します。

## プロジェクトの状況

| マイルストーン | 状態 |
| --- | --- |
| M0 スキャフォールド | 完了 |
| M1 spec 解析パイプラインと `dump-operations` | 完了 |
| M2 stdio での動的 MCP サーバー(1 オペレーション = 1 ツール) | 完了 |
| M3 タグによるグルーピング、meta-tool モード、検索 | 完了 |
| M4 オプトインの AI 説明改善 | 完了 |
| M5 認証(API キー、Bearer、OAuth 2.0 + PKCE)と配布 | 完了 |
| M6 ドキュメント、サンプル spec、NuGet 公開 | 次 |

リリースの内容は[変更履歴](CHANGELOG_ja.md)にあります。

## コントリビュート

Issue と Pull Request を歓迎します。ソースコードのコメントは英語でお願いします。利用者向けドキュメントは
英語を正とし、日本語版は別ファイルで用意します。

## ライセンス

[MIT](LICENSE)
