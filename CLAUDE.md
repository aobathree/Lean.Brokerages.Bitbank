# Lean.Brokerages.Bitbank

bitbank.cc(暗号通貨)の LEAN ブローカレッジプラグイン。設計は `docs/DESIGN.md`、
セットアップは `docs/SETUP.md`、CLI 運用は `docs/LEAN-CLI.md`。

## このリポジトリは公開物であることが最優先

コミュニティ向けに公開しており、**「LEAN 本体のフォークや再ビルドは不要、公式 Docker
イメージに DLL を 1 個足すだけ」が売り**。導入の敷居を下げることが目的なので、
以下を絶対に崩さないこと。

- `Common/Market.cs` など LEAN 本体を書き換える方式に戻さない。市場は
  `BitbankMarket.cs` の `[ModuleInitializer]` が自己登録する(`bitbank` = **44**)
- ベースイメージは `quantconnect/lean:latest` のまま。`lean-bitbank:cli` を
  フォークビルドの上に積み替えない
- 依存パッケージゼロを維持(Socket.IO / PubNub も必要最小を自前実装)

`aobathree/Lean` に `jp-broker-bitbank` という廃止済みブランチがある。bitbank を
LEAN 本体に埋め込む初期方式(4,837 行追加)で、この独立リポジトリ方式に移行して捨てた。
**参考にしない。**

## 検証は stock LEAN 上で行う

日本株トラックでは `:jpfork`(フォークビルド)系イメージを使っているが、
**bitbank の検証は `lean-bitbank:cli` で行う**。コミュニティ利用者と同じ土俵で
問題を踏めるようにするため。

グローバル `engine-image` は日本株側の `lean-jquants:jpfork` になっており、
そこに bitbank の DLL は入っていない。ワークスペース
`D:\bitbank\lean-cli` の `backtest.ps1` / `live.ps1` が
`--image lean-bitbank:cli` を明示指定する。

## 未解決: BitbankBrokerageModel を import できない

2026-08-14 時点で `D:\bitbank\lean-cli` のバックテストが失敗する。

```
ERROR:: cannot import name 'BitbankBrokerageModel' from
        'QuantConnect.Brokerages.Bitbank' (unknown location)
  at main.py: line 11
```

切り分け済みの事実:

- `lean-bitbank:cli` でも `lean-jp:cli` でも**同一のエラー**。イメージ選択の問題ではない
- 両イメージの `QuantConnect.BitbankBrokerage.dll` は同一(59,392 バイト、2026-08-10)
- `main.py` は**成功していた 2026-08-10 の実行時スナップショットとバイト単位で同一**
  (当時 19 注文 / Net Profit 59.043% / ERROR なし)
- `AddReference` は例外を出さず、その次の `from ... import` で型が見つからない

アルゴリズムもイメージも当時のままなので、環境側で何かが変わっている。
アセンブリのロードか型の公開側を追うのが次の一手。

## market id

bitbank 44 / kabuSTATION 45 / GMOCoin 46 で埋まっている。`Market.Add` は識別子の
重複も例外にするため、新規プラグインは 47 以降を使う。

## リポジトリ運用

- 日足 zip を**コミットしている**(姉妹の kabuSTATION リポジトリは逆にコミットしない方針。
  混同しないこと)
- 秘密情報は 1Password。`op run --env-file=QuantConnect.BitbankBrokerage/.env.1password -- <command>`
  で注入し、コミット対象は sample のみ。このリポジトリの `.env.1password` が
  全プロジェクト共通パターンの参照実装
