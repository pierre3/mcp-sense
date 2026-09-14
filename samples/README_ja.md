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

## 大きな spec を試す

McpSense はファイルと同じように URL も読めるので、大きな公開 spec をダウンロードする必要はありません。

```bash
mcpsense dump-tools \
  https://raw.githubusercontent.com/github/rest-api-description/main/descriptions/api.github.com/api.github.com.json
```

GitHub の定義は 1,000 を超えるオペレーションを持つため、meta-tool モードになり、
見つかった 47 個のグループが表示されます。このモードが存在する理由そのものの例です。
これらをそのまま公開すると、ツール一覧がモデルの読める量をはるかに超えてしまいます。
