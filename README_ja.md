# McpSense

OpenAPI の定義ファイルから MCP サーバーを起動するツールです。変換用のコードを書く必要はありません。

McpSense は OpenAPI 3.0/3.1 のドキュメントを実行時に読み込み、各オペレーションを
[Model Context Protocol](https://modelcontextprotocol.io) のツールとして公開します。Claude Desktop や
Claude Code などの MCP クライアントから、その API を直接呼び出せます。コード生成は行わないため、
生成物をリポジトリに置く必要もありません。

*This document in [English](README.md).*

> **プレビュー版です。** 大規模な spec への対応、AI による説明文の改善、トークン自動更新付きの OAuth まで
> 一通り動作しますが、コマンドラインは今後変わる可能性があります。

## 特徴

spec を 1 オペレーション = 1 ツールに変換するだけなら単純です。McpSense は、それを実際の API に
使おうとしたときに出てくる 2 つの問題に対応しています。

**大規模な spec への対応。** 数百のオペレーションを持つ spec では、ツール一覧だけでモデルのコンテキストを
消費します。McpSense は閾値を超えると meta-tool モードに切り替わり、`search_operations` /
`describe_operation` / `call_operation` の 3 つだけを公開します。spec のタグを目次として使うため、
全オペレーションを渡さずに API の構成を伝えられます。

GitHub の REST API を例にすると、**1,229 オペレーション、ツール一覧は JSON で 1.8 MB** になり、多くの
コンテキストウィンドウに収まりません。meta-tool モードでは **3.1 KB**、約 600 分の 1 です。あわせて
47 個のタググループを提示します。

検索は AI を設定しなくても動くため、大規模な spec もそのまま扱えます。埋め込みモデルを設定すると意味ベースの
検索に切り替わります。類似度の計算はインメモリで行うため、外部のベクトルデータベースは要りません。対象を
明示的に絞る allowlist / denylist も使えます。

**モデル向けの説明文。** spec のツール名や説明は、簡潔すぎたり、表記が揺れていたり、モデルが知らない前提を
含んでいたりします。GitHub の spec には `activity_list-repos-starred-by-authenticated-user` のような名前が
並びます。McpSense は任意の `IChatClient` を使って、名前・説明・パラメータの説明を書き換えられます。応答は
ディスクにキャッシュするため、spec が変わらなければ 2 回目以降の起動でモデルを呼びません。

この機能はオプトインで、McpSense は AI 無しでも一通り動作します。モデルを指定しなければ spec の記述を
そのまま使います。AI 関連の依存は別アセンブリに分けてあるため、使わないビルドが依存を抱えることもありません。
モデルがエラーを返した場合や応答が解析できない場合も、spec の記述が残ってサーバーは起動します。

## 動作要件

- [.NET 10 SDK](https://dotnet.microsoft.com/download) 以降

## 使い始める

`mcpsense` コマンドを NuGet からインストールします。

```bash
dotnet tool install --global McpSense.Tool --prerelease
```

McpSense 自体を開発する場合は、リポジトリをクローンして `dotnet build McpSense.slnx` し、
`dotnet run --project src/McpSense.Tool -- <command>` で実行してください。

[`samples/`](samples/) に、要点を一通り含む小さな spec と、各モードを試すためのコマンドを置いてあります。

まず spec を読ませて、抽出されるオペレーションを確認します。

```bash
mcpsense dump-operations https://example.com/openapi.yaml
```

引数はローカルのファイルパスまたは URL で、JSON・YAML のどちらでも読めます。

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

`mcpsense mcp` は stdio で MCP を話します。Claude Desktop や Claude Code が使う形式です。

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

30 オペレーション(`--threshold` で変更可)までは、1 オペレーションが 1 ツールになります。path・query・
header・cookie のパラメータはトップレベルの引数になり、リクエストボディは `body` という 1 つの引数で
渡します。

これを超えると、McpSense は代わりに次の 3 つのツールを公開します。

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

`dump-tools <spec>` — MCP クライアントが実際に受け取るツールを表示する。`dump-operations` の 1 段あとの
状態にあたり、引数の形が想定と違うときの確認に使う。

| オプション | 説明 |
| --- | --- |
| `--schema` | 各ツールの入力スキーマを全文表示する。 |
| `--tool <name>` | 指定した名前のツールだけを表示する。 |
| *(下記の選択オプションも使える)* | |

`mcp <spec>` — spec を stdio で公開する。

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

AI オプション(`dump-tools` / `mcp` で共通)。モデルを指定するまでは使われません。

| オプション | 説明 |
| --- | --- |
| `--ai-model <name>` | 名前・説明・パラメータの説明を書き換えるチャットモデル。 |
| `--ai-embedding-model <name>` | 意味検索に使う埋め込みモデル。未指定なら語彙検索のまま。 |
| `--ai-endpoint <url>` | OpenAI 互換の任意のエンドポイント。既定は OpenAI。 |
| `--ai-api-key <key>` | 未指定なら環境変数 `OPENAI_API_KEY` を使う。 |
| `--ai-cache <dir>` | モデル応答のキャッシュ先。既定はユーザーごとのディレクトリ。 |
| `--ai-batch-size <n>` | 1 リクエストあたりのオペレーション数。既定は 10。 |
| `--ai-keep-names` | 説明だけ書き換え、名前は spec のまま残す。 |

選択オプション(`dump-operations` / `dump-tools` / `mcp` で共通)。

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
  -> DescriptionEnhancer      [オプトイン] IChatClient で名前・説明・パラメータの説明を書き換える。
                              バッチ処理し、ディスクにキャッシュする
  -> ToolCatalog              クライアントへ公開するツール群(ローカル $ref はインライン展開済み)

CallTool -> 呼び出しプランを引く -> HttpRequestMessage を組み立てる
         -> 設定から認証情報を付与して送信する(モデルには渡らない)
         -> レスポンスを CallToolResult へ変換する
```

認証情報は起動時に一度渡され、リクエストの送信時にサーバー側で付与されます。ツールのスキーマには現れず、
モデルにも渡りません。

### OAuth はサーバー起動前にログインする

OAuth が必要な API では、`mcpsense login` で事前に認可を済ませます。

```bash
mcpsense login \
  --oauth-authorization-endpoint https://provider.example/authorize \
  --oauth-token-endpoint https://provider.example/token \
  --oauth-client-id your-client-id \
  --oauth-scope "read write"
```

ブラウザを開き、ループバックポートでリダイレクトを受け取ってトークンを保存します。以降はサーバーが期限に
応じて自動で更新するため、手作業は不要です。

ログインを別コマンドにしているのは、stdio の MCP サーバーでは stdin と stdout をプロトコルが使っており、
同意画面を表示する経路が無いためです。未ログインのまま呼び出された場合は、`mcpsense login` の実行を促す
エラーを返します。

PKCE は必須にしています。McpSense はユーザーのマシン上で動くパブリッククライアントで、クライアント
シークレットを安全に持てません。PKCE が無いと、ループバック上で観測された認可コードを同じマシンの別の
プロセスに引き換えられる可能性があります。保存したトークンは Windows では DPAPI で暗号化し、その他の OS では
所有者のみ読み書きできる権限で保存します。

### AI は必須にしない

AI の処理はすべてオプトインで、必須の経路には入れていません。モデルがエラーを返した場合、タイムアウトした
場合、応答を解析できなかった場合も、該当するオペレーションは spec の記述を保ったまま起動が続きます。

書き換えはバッチ処理で、既定は 1 リクエストあたり 10 オペレーションです。1,000 オペレーションの spec でも
約 100 リクエストで済みます。応答はディスクにキャッシュするため、2 回目以降の起動ではモデルを呼びません。
メモリキャッシュでは目的を果たせません。CLI は実行のたびにプロセスが終了するため、毎回モデルを呼ぶことに
なります。

### 検索は AI 無しで動く

meta-tool モードに切り替わる条件は spec のサイズであり、AI モデルの設定有無とは関係ありません。検索が埋め込み
前提だと大規模な spec がすべて AI 必須になり、AI 無しで動作するという前提が成り立たなくなります。そのため
既定の索引は語彙ベースにしてあります。識別子を単語に分割し、出現頻度の低い語を一般的な語より重く扱い、
オペレーション名での一致を説明文での一致より上位に置きます。呼び出し側が API 本来の語彙に近い言葉を使って
いれば、目的のオペレーションに辿り着けます。

`IEmbeddingGenerator` を設定すると意味ベースの検索に切り替わり、spec と言い回しが異なるクエリでも
見つかります。埋め込みは起動時に 1 回、1 リクエストにまとめて生成します。

### スキーマは自己完結させる

MCP クライアントは OpenAPI ドキュメントを持たないため、`{"$ref": "#/components/schemas/Pet"}` を含む
ツールスキーマは解決できません。McpSense はツールを公開する前にローカル参照をすべてインライン展開します。
自己参照を含むスキーマにも対応しています。

### spec はそのまま受け取る

実際に運用されている spec が、OpenAPI の検証には引っかかるものの実用上は問題ない、ということは珍しく
ありません。API の提供元が公開している spec でも起こります。提供元より厳格ではプロキシとして使えないため、
McpSense はドキュメントをまったく生成できなかった場合のみエラーとします。検証の指摘は非致命の問題として
報告したうえで、オペレーションはそのまま公開します。

## ライセンス

[MIT](LICENSE)
