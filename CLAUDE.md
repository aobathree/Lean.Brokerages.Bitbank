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

## 解決済み: import 失敗はイメージ内の DLL が古かった (2026-08-14)

`cannot import name 'BitbankBrokerageModel' from 'QuantConnect.Brokerages.Bitbank'
(unknown location)` でバックテストが起動しなかった。原因は **`lean-bitbank:cli` に
焼かれた DLL が古いまま**だったこと。ソースは 2026-08-09 22:59 の `5fb14fc`
(信用取引対応) 以降変わっていないのに、イメージ内は 59,392 バイト、現行ソースの
ビルドは 71,168 バイトだった。当時成功していたバックテストは、いまは存在しない
`lean-bitbank:cli-test` タグで走っていた。

対処は再ビルドだけ。ソース・アルゴリズム・lean CLI はいずれも無関係だった。
再ビルド後は 2026-08-10 と同じ 19 注文 / Net Profit 59.043% に戻った(その直後に
サンプル側のタイムゾーンを直したので、現在の照合基準は **19 注文 /
Net Profit 63.627%**。下の節を参照)。

```powershell
dotnet build QuantConnect.BitbankBrokerage
docker build -f deploy/lean-cli/Dockerfile.cli -t lean-bitbank:cli .
```

**教訓: プラグインを直したら、それを載せた「すべて」のイメージを積み直す。**
`lean-bitbank:cli` の上に kabuSTATION を重ねた `lean-jp:cli` があるため、
下段だけ古いと上段も壊れる。この依存関係は自動化されていない。

切り分けの決め手は、同じ `lean-jp:cli` (bitbank と kabuSTATION の DLL が両方入る)
で kabuSTATION 側の import が通ったこと。イメージでもエンジンのビルドでも
lean CLI のバージョンでもなく、bitbank の DLL 固有だと確定できた。

## サンプルの照合基準と、直した 2 点 (2026-08-14)

`examples/bitbank_sma_cross.py` の現在の期待値は **19 注文 / Net Profit 63.627% /
Alpha 0.035 / Beta 0.479**。イメージやプラグインを変えたときはこれと突き合わせる。

直した内容は 2 つ。どちらも売買ロジックには触れていない。

**ベンチマークは関数で渡す。** `set_benchmark(symbol)` はバックテストで解像度が
Hour 固定の内部購読を作る(`UniverseSelection.AddPendingInternalDataFeeds`)が、
bitbank は日足しか持たないため必ず失敗し、`Alpha` / `Beta` / `Treynor Ratio` が
すべて 0 になっていた。関数を渡せば購読自体が作られない。

```python
self.set_benchmark(lambda _: self.securities[self.btc].price)
```

**タイムゾーンを UTC にする。** `Crypto-bitbank-[*]` の market hours は
`dataTimeZone` / `exchangeTimeZone` とも UTC で 24 時間市場。既定の
America/New_York のままだとアルゴリズム時刻がバーからずれ、equity curve の
日次サンプリングが歪む。**この変更で損益が動く**(59.043% → 63.627%)。動くのが
正しく、24 時間 UTC 市場を NY 時間で刻んでいた歪みが取れた結果。

## `Failed data requests` に quote が並ぶのは仕様

上記の修正後も `btcjpy_quote.zip` の要求が失敗し続ける(62% 程度)。LEAN は Crypto に
TradeBar と QuoteBar の両方を購読するが、bitbank の公開 API はローソク足しか返さず
板の履歴を提供しないため quote ファイルは存在しない。`ERROR` は出ず、LEAN は quote が
無ければ trade の価格を使うので売買結果に影響しない。詳細は `docs/LEAN-CLI.md` 手順 6。

**合成データで埋めない。** 他取引所の価格に為替レートを掛けて quote を作る案は
2026-08-14 に検討して却下した。借りてくる側(binance のサンプル)も `_trade` で板情報を
持たず、期間も 2018 年の 5 日分しかなく、USDJPY もサンプルに存在しない。仮に揃っても
取引所間の価格差は掛け算で埋まらない。`SubscriptionManager.AvailableDataTypes` から
Quote を外して購読を抑える手もあるが、`SecurityType.Crypto` 全体に効くうえライブでも
板購読が消えるため採らない。

## market id

bitbank 44 / kabuSTATION 45 / GMOCoin 46 で埋まっている。`Market.Add` は識別子の
重複も例外にするため、新規プラグインは 47 以降を使う。

## リポジトリ運用

- 日足 zip は**コミットしていない**。`.gitignore` の `Data/crypto/` で除外しており、
  クローン直後は `Data/crypto/bitbank/daily/` が存在しない。取得は
  `dotnet run --project QuantConnect.BitbankBrokerage/tools/CandleDownloader -- --from 2018`
  (公開 API、キー不要)。`Dockerfile.cli` はこの zip を COPY するので、**取得前に
  `docker build` すると `cp: cannot stat .../daily` で失敗する**。
  `docs/LEAN-CLI.md` 手順 1 と `README.md` クイックスタート手順 2 に組み込み済み
- 秘密情報は 1Password。`op run --env-file=QuantConnect.BitbankBrokerage/.env.1password -- <command>`
  で注入し、コミット対象は sample のみ。このリポジトリの `.env.1password` が
  全プロジェクト共通パターンの参照実装
