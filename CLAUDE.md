# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

@docs/SESSION-HANDOFF.md

> 引継ぎの運用: セッション終了時に `/handoff` で `docs/SESSION-HANDOFF.md` へ一時状態(作業中断点・未確定判断・as-of の状態)を保存し、次セッションでこの import 経由で自動読み込みして再開する。消化したら `/handoff-clear` で空テンプレートへ戻す。このファイルは Git 追跡対象外のローカル専用で、存在しない環境では import が空になり `/handoff` が自動生成する。
>
> **このファイル(CLAUDE.md)には恒久的な文脈のみを書く。** 一時的な引継ぎは上記へ回すこと。

## 現状

**M0(スキャフォールド)完了。M1(spec 解析パイプライン)が次の作業。** ソリューションは restore / build / test が通る状態だが、`src/` の 3 ライブラリは中身が空で、実装は M1 以降で入る。`tests/` の `ScaffoldSmokeTests` は配管確認用のダミーなので、実テストを書いたら削除してよい。

作業前に必ず `spec.md` を読むこと。以下はそこから抽出した「複数ファイルを読まないと分からない」レベルの恒久的文脈であり、詳細な根拠(競合調査の結果など)は `spec.md` 本文にある。

## プロジェクト概要

**McpSense** — 任意の OpenAPI spec を読み込んで動的に MCP サーバーを立てる .NET ツール。`pierre3/line-openapi-dotnet` で確立した「OpenAPI spec → MCP ツールのファサード」パターンを LINE 以外に汎用化したもの。

素朴な「spec → MCP」変換は既に競合が複数存在する(Python `mcp-openapi-proxy`、.NET `ZeroMcp.Relay`、`MCPify`)ため、**差別化の 2 軸が製品の中核**であり、ここを削ると存在意義が無くなる:

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
- トランスポートは stdio(Claude Desktop/Code 向け)と Streamable HTTP(リモート/コンテナ向け)の両対応。

## プロジェクト構成と依存の向き

```
src/McpSense.Core    spec解析・グルーピング     → Microsoft.OpenApi
src/McpSense.Ai      説明改善・埋め込み検索     → Microsoft.Extensions.AI.Abstractions + Core
src/McpSense.Server  ハンドラ・dispatcher・認証 → ModelContextProtocol + Hosting + Core
src/McpSense.Tool    dotnet global tool (mcpsense) → Cocona + Core/Ai/Server
tests/McpSense.Core.Tests  tests/McpSense.Server.Tests   (xunit)
```

**依存の向きが設計の要**: `Server` は `Ai` を参照しない。両者を束ねるのは `Tool` だけで、これが「AI はオプトインで、未設定でも完全動作する」制約の実装上の担保になっている。`Server` から `Ai` への参照を足す変更は、この制約を壊すので入れてはならない。

外部パッケージのバージョンは `Directory.Build.props` の MSBuild プロパティで一元管理する(各 `.csproj` に直書きしない)。

## 開発コマンド

```powershell
dotnet build McpSense.slnx --configuration Release
dotnet test  McpSense.slnx --configuration Release --no-build
dotnet test  McpSense.slnx --filter "FullyQualifiedName~OperationModelBuilderTests"  # 単一テスト/クラス
dotnet run --project src/McpSense.Tool -- version                                    # CLI の動作確認
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

`OperationModelBuilder`(spec → モデル変換の正確性)と `ToolGroupingEngine`(閾値・グルーピングロジック)をユニットテストの中心に置く。動作確認は各マイルストーンで実 spec を CLI から起動し、Claude Desktop/Code を MCP クライアントとして接続して行う。M4 では AI 有効/無効の両パスで description の差分と、2 回目以降キャッシュがヒットして LLM を叩かないことをログで確認する。

## 決定済み / 未確定

- **確定**: プロジェクト名 `McpSense`(2026-09-12。GitHub/NuGet で衝突なし確認済み。当初候補 `Mcpify` は `abdebek/MCPify` と衝突したため不採用 — 蒸し返さないこと)。AI 機能はオプトイン。
- **未確定**: M1 の検証に使う実 spec の選定(Stripe/GitHub 公開 spec へのアクセス方法を含む)。
