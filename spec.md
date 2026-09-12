# McpSense — OpenAPI → MCP 動的プロキシ(.NET) 実装プラン

## Context

`pierre3/line-openapi-dotnet` で確立した「OpenAPI spec → (Kiota codegen +) MCPツールのファサード」というパターンを、LINE以外の任意のAPIに汎用化する。当初は素朴な「specを読んで動的にMCPサーバーを立てる」ツールを想定していたが、競合調査の結果、この単純な形は既に複数の実装(Python `mcp-openapi-proxy` は★154・活発にメンテ中/ .NETにも `ZeroMcp.Relay` という直接競合が存在、ただしv0.1.1の早期段階)があり、大手APIゲートウェイベンダーもこの機能を標準機能として取り込みつつある。単純な再実装は差別化価値が低いと判断し、以下2点を軸に据えることで合意した。

1. **大規模spec対応の質** — 既存ツールは手動whitelistどまり(`mcp-openapi-proxy`)か未解決(`ZeroMcp.Relay`)。タグベースの自動グルーピング + 埋め込みベクトルによるセマンティック検索を組み込み、数百オペレーション規模のspecでもLLMのツール一覧を肥大化させずに使えるようにする。
2. **Microsoft.Extensions.AIによるツール説明文の自動改善** — 既存ツールはspecの`summary`/`description`をそのまま流用するだけ。任意の`IChatClient`を使い、ツール名・説明・パラメータ説明をLLM向けに分かりやすく書き換える機能を提供する(調査した範囲でこれをやっているツールは無い)。**この機能はオプトイン**とする(ユーザー確認済み)。デフォルトはAI不要で完全動作し、設定で`IChatClient`を渡すと有効化される。

対象: .NET 10、MITライセンス、`line-openapi-dotnet`と同様のリポジトリ構成・言語(C#)・バイリンガルドキュメント(README.md / README_ja.md)を踏襲する。

## アーキテクチャ

```
起動時:
  OpenAPI spec(URL/ファイル) --[Microsoft.OpenApi]--> OpenApiDocument
    --> OperationModelBuilder --> IReadOnlyList<OperationDescriptor>
          (name/operationId, method+pathテンプレート, param配置(path/query/header/body),
           requestBodyのJSON Schema, securityRequirement, tags)
    --> ToolGroupingEngine
          - タグベースでグループ化(specに既にある情報、コスト0)
          - 閾値(既定30、設定可)を超える場合は "meta-tool" モードへ切替:
              search_operations(query) / describe_operation(operationId) / call_operation(operationId, args)
              search は IEmbeddingGenerator<string, Embedding<float>> でオペレーションの
              summary+description+pathを埋め込み、インメモリでコサイン類似度検索(外部ベクトルDB不要)
          - 明示的allowlist/denylist設定も引き続きサポート(エスケープハッチ)
    --> [オプトイン] DescriptionEnhancer
          - IChatClient.GetResponseAsync でツール説明・パラメータ説明を書き換え
          - ChatClientBuilder().UseDistributedCache(cache) で結果をキャッシュし、
            起動のたびにLLMを叩かない(コスト・レイテンシ対策)
          - 未設定なら素通し(spec原文をそのまま使用)
    --> ToolCatalog (Dictionary<string, ToolInvocationPlan> + 生成済み Tool 一覧)

MCPサーバー起動:
  McpServerHandlers.ListToolsHandler / CallToolHandler を自前実装して ToolCatalog を公開
  ( ※ AIFunctionFactory / McpServerTool.Create の高レベルAPIはC#デリゲートの
     コンパイル時パラメータからリフレクションでスキーマ生成する設計のため、
     実行時にしか形が決まらない今回のケースには使えない。ModelContextProtocol
     C# SDK のドキュメントで確認済み — 低レベルハンドラ経由で生の Tool
     (Name/Description/InputSchema: JsonElement) を返す実装が正しい選択 )
  transport: stdio(Claude Desktop/Code向け)、Streamable HTTP(リモート/コンテナ向け)の両対応

呼び出し時:
  CallToolHandler --> ToolInvocationPlan引き --> HttpRequestMessage組み立て(path/query/header/body振り分け)
    --> IHttpClientFactory経由で送信 --> 認証ヘッダは起動時configから自動付与(LLMには渡さない)
    --> レスポンスをCallToolResultに変換(エラー時 isError: true)
```

## リポジトリ構成(line-openapi-dotnetを踏襲)

```
/src
  /McpSense.Core        … spec解析、OperationModelBuilder、ToolGroupingEngine
  /McpSense.Ai          … DescriptionEnhancer、埋め込み検索(オプトイン、Microsoft.Extensions.AI依存はここに隔離)
  /McpSense.Server      … McpServerHandlers実装、HTTP dispatcher、認証注入
  /McpSense.Tool        … dotnet global tool (CLI + `mcp`サブコマンドでサーバー起動)
/tests
  /McpSense.Core.Tests  … spec解析・グルーピングのユニットテスト
  /McpSense.Server.Tests… ツール一覧生成・dispatchの統合テスト
/samples                 … 実specでの動作例(後述)
/docs, README.md, README_ja.md, CHANGELOG.md, LICENSE(MIT), Directory.Build.props, .slnx
```

プロジェクト名: **McpSense**(2026-09-12確定。GitHub/NuGetで同名衝突なしを確認済み。当初候補の`Mcpify`は`abdebek/MCPify`と衝突したため不採用)

## マイルストーン

- **M0 スキャフォールド**(2026-09-12 完了): ソリューション構成、`Directory.Build.props`(.NET 10)、MITライセンス、CI雛形
- **M1 spec解析パイプライン**: `Microsoft.OpenApi`(3.10.2)の`OpenApiDocument.LoadAsync` → `ReadResult`でOpenAPI 3.0/3.1をパースし`OperationDescriptor`モデルを構築。CLIで`dump-operations`的なデバッグコマンドを用意し、実specで検証(Stripe/GitHubの公開spec、および`line-openapi-dotnet`自身のspecでドッグフーディング)
- **M2 素朴な動的MCPサーバー**: 1:1でツール化する最小経路をstdioで通し、Claude Desktop/Code上で動作確認
- **M3 差別化1: グルーピング/検索**: タグベースグルーピング実装 → 閾値超過時のmeta-toolモード実装 → `IEmbeddingGenerator`による埋め込み検索。GitHub spec(500+オペレーション)のような大規模specで検証
- **M4 差別化2: AI説明改善(オプトイン)**: `IChatClient`抽象での書き換えパイプライン、`UseDistributedCache`によるキャッシュ、未設定時のフォールバック確認
- **M5 認証・HTTPトランスポート・配布**: APIキー/Bearerに加え、**OAuth 2.0 Authorization Code Flow + PKCE**(JWT検証・スコープ強制・トークン自動更新を含む)まで対応し、認証面で`MCPify`に見劣りしない水準にする。Streamable HTTPモード、`dotnet tool install -g`配布
- **M6 ドキュメント/比較表**: `mcp-openapi-proxy`/`ZeroMcp.Relay`/`MCPify`との比較表(大規模spec対応・AI説明改善・認証方式の3軸)を明記したREADME、サンプルspec集、CI

## 検証方法

- 各マイルストーンで対象specに対しCLIから起動し、Claude Desktop/Code(既にこのユーザーが使っている環境)をMCPクライアントとして接続して実際にツール一覧・呼び出しを確認
- M3ではオペレーション数の異なる複数spec(小規模/中規模/GitHub級の大規模)でツール一覧のトークン数と検索精度を比較
- M4ではAI有効/無効の両パスでdescriptionの差分を確認し、キャッシュがヒットして2回目以降LLM呼び出しが発生しないことをログで確認
- ユニットテストは`OperationModelBuilder`(spec→モデル変換の正確性)と`ToolGroupingEngine`(閾値・グルーピングロジック)を中心に

## 未確定事項(実装着手前に確定)

- M1で検証に使う実specの具体的な選定(Stripe/GitHub公開specへのアクセス方法含む)

## 確定済み(当初の未確定事項より)

- **リポジトリの配置場所**: `C:\pierre3\mcp-sense`(2026-09-12確定)
- **パッケージ選定の訂正**: 当初案の`Microsoft.OpenApi.Readers`は`2.0.0-preview9`で更新が止まっており使用しない。リーダーは2.x以降`Microsoft.OpenApi`本体に統合済みで、こちらは`3.10.2`が安定版(Microsoft LearnのAPIリファレンスで確認)。
- **MCP C# SDK**: `2.2.0`が安定版。`McpServerOptions.Handlers`経由で`McpServerHandlers.ListToolsHandler` / `CallToolHandler`を設定する設計が現行SDKでも有効であることを確認済み。