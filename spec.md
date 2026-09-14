# McpSense — OpenAPI → MCP 動的プロキシ(.NET) 実装プラン

## Context

`pierre3/line-openapi-dotnet` で確立した「OpenAPI spec → (Kiota codegen +) MCPツールのファサード」というパターンを、LINE以外の任意のAPIに汎用化する。specを1オペレーション=1ツールに変換するところまでは難しくないため、それを実際のAPIに対して使おうとしたときに出てくる以下2点を軸に据えることで合意した。

1. **大規模spec対応の質** — タグベースの自動グルーピング + 埋め込みベクトルによるセマンティック検索を組み込み、数百オペレーション規模のspecでもLLMのツール一覧を肥大化させずに使えるようにする。
2. **Microsoft.Extensions.AIによるツール説明文の自動改善** — 任意の`IChatClient`を使い、ツール名・説明・パラメータ説明をLLM向けに分かりやすく書き換える機能を提供する。**この機能はオプトイン**とする(ユーザー確認済み)。デフォルトはAI不要で完全動作し、設定で`IChatClient`を渡すと有効化される。

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
  transport: stdio(Claude Desktop/Code向け)。Streamable HTTPはM6で削除(下記参照)

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

プロジェクト名: **McpSense**(2026-09-12確定。GitHub/NuGetで同名衝突なしを確認済み)

## マイルストーン

- **M0 スキャフォールド**(2026-09-12 完了): ソリューション構成、`Directory.Build.props`(.NET 10)、MITライセンス、CI雛形
- **M1 spec解析パイプライン**(2026-09-12 完了): `Microsoft.OpenApi`(3.10.2)の`OpenApiDocument.LoadAsync` → `ReadResult`でOpenAPI 3.0/3.1をパースし`OperationDescriptor`モデルを構築。CLIに`dump-operations`(text / `--detail` / `--json` / `--tag`)を用意。`line-openapi-dotnet`の8 specすべて(計110オペレーション)で検証済み。GitHub/Stripeの大規模specはM3のグルーピング検証と併せて実施する
- **M2 素朴な動的MCPサーバー**(2026-09-12 完了): 1:1でツール化する最小経路をstdioで実現。`ToolCatalogBuilder`(InputSchema生成・`$ref`インライン展開)、`ApiDispatcher`(HTTP組み立て・静的認証付与)、`ToolCatalogHandlers`(低レベルハンドラ)。CLIに`mcp`と`dump-tools`を追加。initialize / tools/list / tools/call を実HTTP込みでstdio越しに検証済み
- **M3 差別化1: グルーピング/検索**(2026-09-12 完了): `ToolGroupingEngine`(タグベースグルーピング・閾値・allowlist/denylist)、meta-toolモード(`search_operations` / `describe_operation` / `call_operation`)、`LexicalOperationSearch`(Core・AI不要の既定)と`EmbeddingOperationSearch`(McpSense.Ai・オプトイン)。GitHub spec(**1,229オペレーション**・47タグ)で検証し、`tools/list`ペイロードを **1,885,129バイト → 3,153バイト(約598分の1)** に削減。`--header`も追加(User-Agent必須なAPI対応)
- **M4 差別化2: AI説明改善(オプトイン)**(2026-09-12 完了): `DescriptionEnhancer`(名前・説明・パラメータ説明をバッチ書き換え)、`FileDistributedCache`によるプロセス跨ぎのキャッシュ、CLIのOpenAI互換エンドポイント対応(`--ai-model`等)、埋め込み検索のCLI配線(`--ai-embedding-model`)。OpenAI互換のスタブサーバーで、AI有効/無効の差分と**2回目の起動でモデルへのリクエストが0件**であることを実測確認
- **M5 認証・HTTPトランスポート・配布**(2026-09-12 完了): APIキー/Bearer/任意ヘッダに加え、**上流APIへのOAuth 2.0 Authorization Code Flow + PKCE**(`mcpsense login`、ループバックリダイレクト、`state`検証、リフレッシュトークンによる自動更新、DPAPI暗号化したファイル保管)。`dotnet tool install -g`配布を実機確認済み。Streamable HTTPもこの時点では実装したが、M6で削除した。
  - **注意**: 当初の記述にあった「JWT検証・スコープ強制」は McpSense が**リソースサーバー**になる側(MCPクライアント → McpSense)の話で、上流OAuthとは別物。ユーザー確認のうえ**上流クライアント側のみ**を実装した。
- **M6 ドキュメント/公開**(2026-09-13 完了、ただし公開操作を除く): `samples/bookshop.yml`(13オペレーション・3タグ、全パラメータ位置・`$ref`・2認証方式・非推奨・security打ち消しを含む)と`samples/README.md`/`README_ja.md`、`CHANGELOG.md`/`CHANGELOG_ja.md`、タグ駆動のリリースワークフロー(`release.yml`、NuGet Trusted Publishing)。READMEからsamplesとCHANGELOGへ導線を追加。
  - **残: NuGetへの実公開**。長期APIキーを使わないTrusted Publishing方式のため、nuget.org側でポリシー登録(Owner: pierre3 / Repo: mcp-sense / Workflow: release.yml / Environment: nuget)が必要。タグ`v*`のpushで発火する。

## 検証方法

- 各マイルストーンで対象specに対しCLIから起動し、Claude Desktop/Code(既にこのユーザーが使っている環境)をMCPクライアントとして接続して実際にツール一覧・呼び出しを確認
- M3ではオペレーション数の異なる複数spec(小規模/中規模/GitHub級の大規模)でツール一覧のトークン数と検索精度を比較
- M4ではAI有効/無効の両パスでdescriptionの差分を確認し、キャッシュがヒットして2回目以降LLM呼び出しが発生しないことをログで確認
- ユニットテストは`OperationModelBuilder`(spec→モデル変換の正確性)と`ToolGroupingEngine`(閾値・グルーピングロジック)を中心に

## 未確定事項

- **Streamable HTTPをいつ戻すか**(M6で削除)。リモート公開する必要が出てきたら、リソースサーバー側の認証とセットで再導入する。
- NuGetへの実公開のタイミング(M6)。

## 確定済み(当初の未確定事項より)

- **リポジトリの配置場所**: `C:\pierre3\mcp-sense`(2026-09-12確定)
- **パッケージ選定の訂正**: 当初案の`Microsoft.OpenApi.Readers`は`2.0.0-preview9`で更新が止まっており使用しない。リーダーは2.x以降`Microsoft.OpenApi`本体に統合済みで、こちらは`3.10.2`が安定版(Microsoft LearnのAPIリファレンスで確認)。
- **MCP C# SDK**: `2.2.0`が安定版。`McpServerOptions.Handlers`経由で`McpServerHandlers.ListToolsHandler` / `CallToolHandler`を設定する設計が現行SDKでも有効であることを確認済み。
- **YAMLリーダーは別パッケージ**(M1で判明): `Microsoft.OpenApi`単体はJSONのみ。YAMLには`Microsoft.OpenApi.YamlReader`と`settings.AddYamlReader()`の明示登録が必要。
- **バリデーションエラーは非致命として扱う**(M1で判明): リーダーの検証ルールは厳しく、LINEの本番specでもdiscriminatorルールで3件エラーが出る。McpSenseは任意APIのプロキシなので提供元より厳格であってはならない。ドキュメントが生成できなかった場合のみ失敗とする。
- **言語規約**: ソースコメントは英語。公開ドキュメントは英語を正とし、日本語版を別ファイル(`README.md` / `README_ja.md`)で用意する。
- **`$ref`のインライン展開**(M2で解決): `OpenApiWriterSettings.InlineLocalReferences = true`で足りる。自己参照スキーマでも停止する。MCPクライアントはspecを持たないため必須の処理。
- **stdoutはMCPのプロトコルチャネル**(M2で判明): `mcp`コマンドではログを含めstdoutに書いてはならない。全ログをstderrへ寄せてある。
- **ベースURLは文字列連結で繋ぐ**(M2で判明): OpenAPIのパステンプレートは`/`始まりのため、`new Uri(base, path)`だとサーバーURL側のパス(`https://example.com/v2`の`/v2`)が消える。
- **検索は語彙ベースを既定にする**(M3の設計変更): 当初案は`IEmbeddingGenerator`前提だったが、meta-toolモードの発動条件はspecのサイズであってAIの有無ではない。埋め込み専用だと大規模specがAI必須になり「デフォルトはAI不要で完全動作」と衝突するため、Coreに`IOperationSearch`と語彙ベース既定実装を置き、埋め込みは`McpSense.Ai`のオプトイン差し替えとした。
- **meta-toolの引数名は`operation`**(M3): カタログのキーは正規化・重複解消済みのツール名で、specの`operationId`と一致するとは限らないため、`operationId`という名前は誤解を招く。
- **`User-Agent`を既定送信する**(M3で判明): `HttpClient`は何も送らず、GitHubはヘッダ無しを403で拒否する。`--header`で任意の静的ヘッダを指定可能。
- **キャッシュはファイルベース必須**(M4で判明): `MemoryDistributedCache`ではCLIプロセスが実行ごとに終了するため毎回LLMを叩き、「起動のたびにLLMを叩かない」が成立しない。`FileDistributedCache`を実装した。キャッシュキーはリクエスト内容から導出されるため有効期限は不要。
- **`ChatClientBuilder`/`UseDistributedCache`は実装パッケージ側**(M4で判明): Abstractionsには無い。`McpSense.Ai`をAbstractionsのみに保つため、キャッシュ装飾とプロバイダ生成は合成ルート(`McpSense.Tool`)に置いた。
- **上流OAuthとリソースサーバー側OAuthは別物**(M5で整理): 「JWT検証・スコープ強制」は後者、「トークン自動更新」は前者。M5では前者のみ実装。`login`を別コマンドにしたのは、stdioサーバーがstdin/stdoutをプロトコルに占有されており同意画面を出すチャネルが無いため。PKCEと`state`検証は必須(パブリッククライアントで秘密を持てないため)。リフレッシュは直列化する(ローテーションするプロバイダで並行実行するとログインごと無効化される)。
- **Streamable HTTPは削除した**(M6の判断、2026-09-13): 通信方式は公開できて初めて価値があり、リモート公開の利点(ブラウザ版クライアント・チーム共有・大規模specの起動コスト償却)はすべて外部から繋げることが前提。手元でしか使えないHTTPはstdioに何も足さない。一方「動くが公開してはいけない」状態は、使う人からは動いて見えるため罠になる。したがって**stdioのみでリリースし、HTTPはリソースサーバー側の認証とセットで再導入する**。なお再導入時は、認証だけでなく「接続したユーザーの呼び出しを誰の権限で行うか」(現状は`McpSenseServerOptions`が上流認証情報を1組しか持たない)の設計も必要。
