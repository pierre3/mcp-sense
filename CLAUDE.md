# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

@docs/SESSION-HANDOFF.md

> 引継ぎの運用: セッション終了時に `/handoff` で `docs/SESSION-HANDOFF.md` へ一時状態(作業中断点・未確定判断・as-of の状態)を保存し、次セッションでこの import 経由で自動読み込みして再開する。消化したら `/handoff-clear` で空テンプレートへ戻す。このファイルは Git 追跡対象外のローカル専用で、存在しない環境では import が空になり `/handoff` が自動生成する。
>
> **このファイル(CLAUDE.md)には恒久的な文脈のみを書く。** 一時的な引継ぎは上記へ回すこと。

## 現状

**M0 〜 M6 完了(NuGet への実公開を除く)。** 機能もドキュメントもひととおり揃っている — spec 解析、meta-tool モード、AI 説明改善、OAuth(Authorization Code + PKCE・自動更新)、stdio トランスポート、`dotnet tool` 配布、`samples/`、CHANGELOG、リリースワークフロー。

**残作業は NuGet への実公開のみ。** `release.yml` はタグ `v*` の push で発火するが、NuGet Trusted Publishing のポリシー登録(nuget.org 側、Owner: pierre3 / Repo: mcp-sense / Workflow: release.yml / Environment: nuget)が未了。**公開は外向きの不可逆操作なのでユーザーが行う。**

作業前に必ず `spec.md` を読むこと。以下はそこから抽出した「複数ファイルを読まないと分からない」レベルの恒久的文脈であり、詳細な経緯は `spec.md` 本文にある。

## プロジェクト概要

**McpSense** — 任意の OpenAPI spec を読み込んで動的に MCP サーバーを立てる .NET ツール。`pierre3/line-openapi-dotnet` で確立した「OpenAPI spec → MCP ツールのファサード」パターンを LINE 以外に汎用化したもの。

素朴な「spec → MCP」変換そのものは難しくないため、**以下 2 軸が製品の中核**であり、ここを削ると存在意義が無くなる:

1. **大規模 spec 対応** — タグベースの自動グルーピング + 閾値超過時の meta-tool モード + 埋め込みベクトルによるセマンティック検索。GitHub spec(500+ オペレーション)級でも LLM のツール一覧を肥大化させない。
2. **AI によるツール説明文の自動改善** — `IChatClient` でツール名・説明・パラメータ説明を LLM 向けに書き換える。**必ずオプトイン**(ユーザー確認済みの決定事項)。未設定時は spec 原文を素通しし、AI 無しで完全動作すること。この「デフォルトは AI 不要」制約を壊す変更をしてはならない。

## アーキテクチャ

起動時に spec を解析してツールカタログを構築し、呼び出し時にそれを引いて HTTP リクエストへ変換する、という 2 フェーズ構成:

```
OpenApiDocument (Microsoft.OpenApi.Readers)
  → OperationModelBuilder   … IReadOnlyList<OperationDescriptor>
                               (operationId, method+pathテンプレート, param配置(path/query/header/body),
                                requestBody の JSON Schema, securityRequirement, tags)
  → ToolGroupingEngine      … タグベースでグループ化(spec 既存情報なのでコスト0)。
                               閾値(既定30・設定可)超過で meta-tool モードへ切替:
                                 search_operations / describe_operation / call_operation
                               検索は IEmbeddingGenerator で summary+description+path を埋め込み、
                               インメモリのコサイン類似度で解決(外部ベクトル DB を導入しない)。
                               allowlist/denylist はエスケープハッチとして併存。
  → DescriptionEnhancer     … [オプトイン] IChatClient で説明文を書き換え。
                               ChatClientBuilder().UseDistributedCache() で起動毎の LLM 呼び出しを回避。
  → ToolCatalog             … Dictionary<string, ToolInvocationPlan> + 生成済み Tool 一覧

CallToolHandler → ToolInvocationPlan → HttpRequestMessage 組み立て
  → IHttpClientFactory 経由で送信(認証ヘッダは起動時 config から付与し、LLM には渡さない)
  → CallToolResult へ変換(エラー時 isError: true)
```

### 重要な設計制約

- **MCP ツールは低レベルハンドラで公開する。** `AIFunctionFactory` / `McpServerTool.Create` などの高レベル API は C# デリゲートのコンパイル時パラメータからリフレクションでスキーマを生成する設計のため、形が実行時にしか決まらない本ツールでは使えない(ModelContextProtocol C# SDK のドキュメントで確認済み)。`McpServerHandlers.ListToolsHandler` / `CallToolHandler` を自前実装し、生の `Tool`(Name/Description/InputSchema: JsonElement)を返すのが正しい実装。これを「高レベル API で書き直す」方向のリファクタは誤り。
- **Microsoft.Extensions.AI 依存は `McpSense.Ai` に隔離する。** `Core` / `Server` がこれに依存してはならない(AI オプトイン制約の実装上の裏付け)。
- **トランスポートは stdio のみ。** Streamable HTTP は一度実装したが 2026-09-13 に削除した(下記「M6 の判断」参照)。

- **ソースコードのコメント・XML doc は英語**(`.cs` / `.csproj` / `Directory.Build.props` / CI yml / `.gitignore` 等を含む)。
- **公開ドキュメントは英語を正、日本語は別ファイル**。`README.md` / `README_ja.md`(作成済み。片方だけ直さず必ず対で更新する)、`CHANGELOG.md` / `CHANGELOG_ja.md`(M6 で作成)。`README.md` は `Directory.Build.props` から全パッケージに同梱されるので、内容が古いと NuGet 上の表示も古くなる。README の「Project status」表はマイルストーン完了のたびに更新すること。
- `CLAUDE.md` と `spec.md` は公開ドキュメントではなく内部の作業文脈なので日本語のままでよい。

## M6 の判断 — Streamable HTTP を削除した

2026-09-13、ユーザーの判断で `--http` / `--http-url` と `ModelContextProtocol.AspNetCore` 依存を削除した。**理由を残しておかないと、いつか「せっかく実装したのに」と戻されるので注意。**

- **通信方式は、公開できて初めて価値がある。** リモート公開のメリット(ブラウザ版クライアントから使える、チームで共有できる、大規模 spec の起動コストを 1 回で償却できる)はすべて「外から繋げること」が前提。手元でしか使えない HTTP は stdio に対して何も足さない。
- **「動くが公開してはいけない」は最悪の状態。** 使う人からは動いて見えるので、注意書きを読まなければ普通に公開してしまう。中途半端に残すより、無いほうが安全。
- **戻すときは認証とセット。** 必要なのは 2 段階で、2 段目のほうが重い:
  1. リソースサーバー化(JWKS による JWT 検証、audience・スコープ確認、保護リソースメタデータ)。ASP.NET Core の JWT Bearer 認証に乗るので M5 程度。
  2. **上流の認証情報をどうするか。** 現状 `McpSenseServerOptions` はサーバー全体で 1 組しか持たないため、1 だけ実装しても接続した全員が同じアカウントとして上流 API を叩く。ユーザーごとに分けるなら接続元トークンの引き渡しが要り、設計の見直しを伴う。
- 削除したコードは git 履歴にある(`McpCommand` の `WebApplication` 分岐と `MapMcp()`)。実装量は 10 行程度なので、書き直すコストは低い。

## M5 の設計判断

- **spec.md の M5 記述は 2 種類の OAuth を混同していた。** 「JWT 検証・スコープ強制」は McpSense が**リソースサーバー**になる側(MCP クライアント → McpSense)、「トークン自動更新」は**OAuth クライアント**になる側(McpSense → 上流 API)の話。**実装したのは後者のみ**(2026-09-12 にユーザー確認済み)。前者は Streamable HTTP ごと M6 で削除した。
- **`login` は別コマンドでなければならない。** stdio サーバーは stdin/stdout をプロトコルに占有されており、同意画面を見せるチャネルが無い。サーバーは保存済みトークンの読み取りと更新のみを行い、未ログインなら `mcpsense login` を促すツールエラーを返す。**サーバー内で対話フローを開始する実装にしてはならない。**
- **PKCE は必須**(オプションにしない)。ユーザーのマシン上で動くパブリッククライアントなので秘密を持てず、ループバック上の認可コードを同一マシンの別プロセスに引き換えられうる。`state` 検証も同様に必須。
- **リフレッシュは `SemaphoreSlim` で直列化する。** 同時実行のツール呼び出しが同じリフレッシュトークンを並行して引き換えると、ローテーションするプロバイダではログインごと無効化される。
- **リフレッシュ応答が `refresh_token` を省略したら以前の値を引き継ぐ。** 省略は「今のものを使い続けろ」の意味で、捨てると長期ログインが一回限りになる。
- **トークンの保管キーはスコープを含む。** スコープ違いで同じトークンを共有すると、付与された範囲と異なる権限で呼ぶことになる。
- **トランスポートは stdio 固定。** `McpCommand` は `Host.CreateApplicationBuilder()` + `WithStdioServerTransport()` のみ。

- **キャッシュはファイルベースでなければならない。** `UseDistributedCache` に `MemoryDistributedCache` を渡すと、CLI プロセスは実行ごとに終了するため毎回 LLM を叩く。spec.md の「起動のたびにLLMを叩かない」が成立しないので、`McpSense.Tool/FileDistributedCache.cs` を実装した。キャッシュキーはリクエスト(= オペレーション原文を含む)から導出されるため、spec を編集すれば自動的に別キーになる。**有効期限は持たせていない**(持たせる必要がない)。
- **`ChatClientBuilder` / `UseDistributedCache` は `Microsoft.Extensions.AI`(実装パッケージ)にあり、Abstractions には無い。** `McpSense.Ai` を Abstractions のみに保つ制約を守るため、キャッシュ装飾と OpenAI クライアント生成は合成ルートである `McpSense.Tool`(`AiOptions.cs`)で行う。**`McpSense.Ai` に実装パッケージやプロバイダを足してはならない。**
- **AI の失敗は致命にしない。** モデルのエラー・タイムアウト・パース不能な応答はすべて警告ログのみで、該当オペレーションは spec 原文を保つ。`DescriptionEnhancer` の catch を「握りつぶし」と見て例外を投げるように変更してはならない。これは「AI 無しで完全動作する」制約の実行時の担保。
- **書き換えはバッチ処理する。** 1 オペレーション 1 リクエストだと GitHub spec で 1,229 リクエストになる。既定 10 件/リクエスト。
- **モデルが返した名前は正規化と一意化を必ず通す。** カタログは名前をキーにしており、モデルは同じ名前を 2 回返すことがある。

## M3 の設計判断(spec.md からの意図的な逸脱)

- **検索は Core の語彙ベースが既定、埋め込みは `McpSense.Ai` のオプトイン上位互換。** spec.md は検索を `IEmbeddingGenerator` 前提で書いていたが、meta-tool モードの発動条件は **spec のサイズ**であって AI の有無ではない。検索が埋め込み専用だと大規模 spec が AI 必須になり、「デフォルトは AI 不要で完全動作」という中核制約と衝突する。そこで `IOperationSearch` を Core に置き、`LexicalOperationSearch`(識別子の単語分割 + 希少語重み + 名前 > 説明文の重み付け)を既定に、`EmbeddingOperationSearch` を差し替え可能にした。**この構造を崩して Core を AI 依存にしてはならない。**
- **meta-tool の引数名は `operationId` ではなく `operation`。** カタログのキーは正規化・重複解消済みのツール名であり、spec の `operationId` と一致するとは限らないため。
- **タググループはツールとして公開しない。** グループは「目次」であり、`search_operations` の絞り込みと server instructions での提示に使う。
- **meta-tool モードではオペレーション名を直接呼べない。** モデルに知らされていない名前であり、通してしまうと 2 つのモードの挙動が静かに食い違う。`TryGet`(公開済み)と `TryGetOperation`(全件)を分けてあるのはこのため。

## M1 / M2 / M3 で判明した実装上の事実

実 spec と実クライアントに当てて初めて分かったもの。spec.md の初版には無い。

- **YAML リーダーは別パッケージ**。`Microsoft.OpenApi` 単体は JSON しか読めず、YAML には `Microsoft.OpenApi.YamlReader` と `settings.AddYamlReader()` の明示登録が要る。未登録だと `Format 'yaml' is not supported` で落ちる。公開 spec は YAML が多いので必須依存。リーダー設定は `OpenApiSpecLoader` の 1 箇所に集約してあり、テストも同じ経路を通す。
- **バリデーションエラーは致命扱いにしない**。リーダーの検証ルールは厳しく、LINE の本番 spec ですら discriminator ルールで 3 件エラーになる。McpSense は任意の API をプロキシする道具なので、API 提供元より厳格だと使い物にならない。`OpenApiSpecLoader` はドキュメントが生成できなかった場合のみ例外を投げ、検証結果は `Problems`(非致命)として返す。
- **path パラメータの `required` 欠落はリーダーが弾く**。`OperationModelBuilder` 側の「path はつねに required」正規化は、プログラムで組んだ `OpenApiDocument` を渡す経路でのみ効く。
- **`$ref` のインライン化は writer 設定 1 つで済む**(M2 で解決)。`SerializeAsync` に `new OpenApiWriterSettings { InlineLocalReferences = true }` を渡す。自己参照スキーマでも停止し、LINE messaging-api の全 73 ツールで `$ref` が 0 件・最大スキーマ 5,611 文字に収まることを確認済み。MCP クライアントは spec を持たないので、この展開は必須。
- **stdout は MCP のプロトコルチャネル**。`mcp` コマンドではログを含め stdout に何も書いてはならない。`builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace)` で全ログを stderr に寄せてある。CLI のエラー出力も `Console.Error` を使うこと。
- **ベース URL は Uri 解決ではなく文字列連結で繋ぐ**。OpenAPI のパステンプレートは必ず `/` 始まりで、`new Uri(baseAddress, "/pets")` はベース URL 側のパスを捨てる。`servers: https://example.com/v2` の `/v2` が消えるため、`ApiDispatcher.BuildRequestUri` は authority + basePath + path を連結している(回帰テストあり)。
- **`CallToolRequestParams.Arguments` は `IDictionary`**(`IReadOnlyDictionary` ではない)。
- **`User-Agent` は既定で送る**(M3 で判明)。`HttpClient` は何も送らず、GitHub はヘッダ無しのリクエストを 403 で拒否する。`--header` で任意の静的ヘッダを付与でき、未指定なら `mcpsense/<version>` を送る。
- **GitHub spec の実測値**(M3): 1,229 オペレーション / 47 タグ、解析 2 秒。`tools/list` ペイロードは direct 1,885,129 バイト → meta 3,153 バイト(約 598 分の 1)。README の数値はこれが根拠。再測するなら `--mode direct` と `--mode meta` で stdio プローブを流して `tail -n +2 | wc -c`。
- **stdio サーバーの手動プローブでは stdin を開いたままにする**。JSON をパイプして即 EOF にすると、応答が flush される前にシャットダウンが走り stdout が空になる。`{ cat probe.jsonl; sleep 6; } | McpSense.Tool.exe mcp <spec>` のようにすること。実 MCP クライアントは stdin を保持するのでこの問題は起きない。

## プロジェクト構成と依存の向き

```
src/McpSense.Core    spec解析・グルーピング     → Microsoft.OpenApi
src/McpSense.Ai      説明改善・埋め込み検索     → Microsoft.Extensions.AI.Abstractions + Core
src/McpSense.Server  ハンドラ・dispatcher・認証(Authentication/ に OAuth) → ModelContextProtocol
                     + Hosting + Http + Core
src/McpSense.Tool    dotnet global tool (mcpsense) → Cocona + Microsoft.Extensions.AI(.OpenAI)
                     + Caching.Abstractions + Core/Ai/Server
tests/McpSense.Core.Tests  tests/McpSense.Server.Tests  tests/McpSense.Ai.Tests   (xunit)
```

**依存の向きが設計の要**: `Server` は `Ai` を参照しない。両者を束ねるのは `Tool` だけで、これが「AI はオプトインで、未設定でも完全動作する」制約の実装上の担保になっている。`Server` から `Ai` への参照を足す変更は、この制約を壊すので入れてはならない。

外部パッケージのバージョンは `Directory.Build.props` の MSBuild プロパティで一元管理する(各 `.csproj` に直書きしない)。

## 開発コマンド

```powershell
dotnet build McpSense.slnx --configuration Release
dotnet test  McpSense.slnx --configuration Release --no-build
dotnet test  McpSense.slnx --filter "FullyQualifiedName~OperationModelBuilderTests"  # 単一テスト/クラス
dotnet run --project src/McpSense.Tool -- version
dotnet run --project src/McpSense.Tool -- dump-operations <spec> [--detail] [--json] [--tag t]
dotnet run --project src/McpSense.Tool -- dump-tools <spec> [--schema] [--tool n] [--tag t]
dotnet run --project src/McpSense.Tool -- mcp <spec> [--base-url u] [--bearer-token t]
dotnet list  McpSense.slnx package --vulnerable --include-transitive                 # 脆弱性サマリ
```

CI の実質のゲートは restore 時の NuGet 監査。ローカルで CI と同じ厳しさを再現するには:

```powershell
dotnet restore McpSense.slnx -p:NuGetAuditMode=all -warnaserror:NU1901,NU1902,NU1903,NU1904
```

(`dotnet list --vulnerable` は exit 0 を返すためゲートにならない。サマリ表示専用。)

## 開発ツール(導入済み)

`.claude/settings.json` で有効化(プロジェクトスコープ):

- **`mcp-server-dev` プラグイン** — `build-mcp-server` スキル。ツール設計・認証(OAuth)・プロトコル版・サーバー capability の指針が入っている。M2(最小 MCP サーバー)/ M5(OAuth 2.0 + PKCE)で参照すること。言語非依存の設計指針であり、C# SDK の API リファレンスではない。
- **`csharp-lsp` プラグイン** + `csharp-ls` グローバルツール(0.27.0、導入済み) — `.cs` のコード補完・診断。

ドキュメント参照は MCP 経由で行い、記憶で書かない。2 つとも接続済み:

- **Context7 MCP** — `/modelcontextprotocol/csharp-sdk` に公式 C# SDK が揃っている(618 スニペット)。**低レベルハンドラ API(`ListToolsHandler` / `CallToolHandler` / 生の `Tool`)の確認は必ずここを引くこと。**
- **Microsoft Learn MCP** — `Microsoft.OpenApi`、Microsoft.Extensions.AI(`IChatClient` / `IEmbeddingGenerator` / `UseDistributedCache`)、`IHttpClientFactory` の一次情報。`microsoft_docs_search` で当たりを付け、`microsoft_code_sample_search` で実コード、足りなければ `microsoft_docs_fetch` で全文。

**パッケージ選定の注意**: spec 読み取りは `Microsoft.OpenApi`(3.10.2)の `OpenApiDocument.LoadAsync` → `ReadResult` を使う。**`Microsoft.OpenApi.Readers` は使わない** — 2.x でリーダーが本体へ統合された結果 `2.0.0-preview9` で更新が止まっており、名前から素直に選ぶと死んだパッケージを掴む。spec.md の初版はこの旧名で書かれていた(訂正済み)。

**Kiota は導入しない。** `line-openapi-dotnet` は Kiota コード生成が中核だったが、McpSense は spec を実行時に解析する動的プロキシであり codegen を一切行わない。兄弟リポジトリからのパターン流用でここを取り違えないこと。`docfx` は M6 のドキュメント整備時に `.config/dotnet-tools.json` へ追加する。

## 踏襲する規約(`line-openapi-dotnet` 由来)

- .NET 10 単一ターゲット(`net10.0`)、`Nullable=enable`、**`ImplicitUsings=disable`**(using は明示的に書く)、`GenerateDocumentationFile=true`。これらは `Directory.Build.props` で一元管理する。
- MIT ライセンス、バイリンガルドキュメント(`README.md` / `README_ja.md`)、`CHANGELOG.md`。
- 決定的ビルド・SourceLink・snupkg シンボルパッケージを有効化。

## テストの重点

`OperationModelBuilder`(spec → モデル変換の正確性)と `ToolGroupingEngine`(閾値・グルーピングロジック)をユニットテストの中心に置く。ユニットテストのフィクスチャは小さな手書き spec とし、1 テスト 1 ルールに絞る(実 spec は `dump-operations` での検証に回す)。

フィクスチャは**補間なしの生文字列** `"""..."""` で書くこと。`$"""` にすると `{petId}` や `{ description: ok }` のような YAML の波かっこが補間として解釈されて壊れる。共通の preamble は文字列連結で足す。

動作確認は各マイルストーンで実 spec を CLI から起動し、Claude Desktop/Code を MCP クライアントとして接続して行う。ローカルの実 spec は `C:\pierre3\line-openapi-dotnet\openapi\*.yml`(8 ファイル・計 110 オペレーション)が使える。M4 では AI 有効/無効の両パスで description の差分と、2 回目以降キャッシュがヒットして LLM を叩かないことをログで確認する。

## 決定済み / 未確定

- **確定**: プロジェクト名 `McpSense`(2026-09-12。GitHub/NuGet で衝突なし確認済み — 蒸し返さないこと)。AI 機能はオプトイン。
- **未確定**: M1 の検証に使う実 spec の選定(Stripe/GitHub 公開 spec へのアクセス方法を含む)。
