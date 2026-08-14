# Lean.Brokerages.Bitbank

[QuantConnect LEAN](https://github.com/QuantConnect/Lean) から日本の暗号資産取引所 [bitbank](https://bitbank.cc) に接続する、**スタンドアロンのブローカレッジプラグイン**。

LEAN 本体の改変・再ビルドは不要。公式 NuGet パッケージ / 公式 Docker イメージ（`quantconnect/lean`)の上に、この DLL とデータ行を足すだけで動きます。

## 機能

- **ライブトレーディング**: 現物(spot)の成行・指値・逆指値注文、post_only、残高同期、JPY 口座通貨
- **信用取引(margin)**: ロング/ショート両建玉、最大レバレッジ 2 倍、建玉同期、position_side の自動判定([下記](#信用取引margin))
- **ライブデータフィード**: ティッカー・約定・板(depth)のリアルタイム購読(`IDataQueueHandler`)
- **ヒストリカルデータ**: ローソク足 API 経由の履歴取得(1min / 1hour / 1day)
- **対応ペア**: 公式サイト掲載の JPY 建て 44 ペア
- 依存パッケージゼロ(Socket.IO 4 / PubNub とも必要最小プロトコルを自前実装)

実装の設計判断・アーキテクチャは [docs/DESIGN.md](docs/DESIGN.md)、API キー設定は [docs/SETUP.md](docs/SETUP.md) を参照。

## 必要環境

- .NET SDK 10(DLL のビルドに使用。`dotnet --list-sdks` で 10.x があるか確認。無ければ `winget install Microsoft.DotNet.SDK.10`)
- Docker(公式 LEAN イメージでのバックテスト/ライブ実行に使用)
- bitbank の API キー(ライブのみ。バックテストは不要)

## クイックスタート(公式 LEAN イメージでバックテスト)

bash(macOS / Linux / Git Bash)の場合。**Windows PowerShell の場合は[次節](#クイックスタートwindows-powershell)へ。**

```bash
git clone https://github.com/aobathree/Lean.Brokerages.Bitbank.git
cd Lean.Brokerages.Bitbank

# 1) プラグイン DLL をビルド(LEAN 本体はビルドしない。公式 NuGet 参照のみ)
dotnet build QuantConnect.BitbankBrokerage

# 2) 日足データを取得(公開 API、キー不要。Data/crypto/bitbank/daily/ に保存)
dotnet run --project QuantConnect.BitbankBrokerage/tools/CandleDownloader -- --from 2018

# 3) 公式イメージの symbol-properties / market-hours に bitbank 行をマージ
docker pull quantconnect/lean:latest
mkdir -p /tmp/lean-data/symbol-properties /tmp/lean-data/market-hours
C=$(docker create quantconnect/lean:latest)
docker cp $C:/Lean/Data/symbol-properties/symbol-properties-database.csv /tmp/lean-data/symbol-properties/
docker cp $C:/Lean/Data/market-hours/market-hours-database.json /tmp/lean-data/market-hours/
docker rm $C
scripts/install-bitbank-data.sh /tmp/lean-data

# 4) サンプルアルゴリズム(Python、BTC/JPY のゴールデンクロス)をバックテスト
docker run --rm \
  -v $PWD/QuantConnect.BitbankBrokerage/bin/Debug/net10.0:/plugin:ro \
  -v /tmp/lean-data/symbol-properties/symbol-properties-database.csv:/Lean/Data/symbol-properties/symbol-properties-database.csv:ro \
  -v /tmp/lean-data/market-hours/market-hours-database.json:/Lean/Data/market-hours/market-hours-database.json:ro \
  -v $PWD/Data/crypto/bitbank:/Lean/Data/crypto/bitbank:ro \
  -v $PWD/examples:/Algo:ro \
  --entrypoint /bin/sh quantconnect/lean:latest -c \
  'cp /plugin/QuantConnect.BitbankBrokerage.dll /Lean/Launcher/bin/Debug/ &&
   cd /Lean/Launcher/bin/Debug &&
   dotnet QuantConnect.Lean.Launcher.dll \
     --algorithm-type-name BitbankSmaCrossExample \
     --algorithm-language Python \
     --algorithm-location /Algo/bitbank_sma_cross.py'
```

最後に `STATISTICS::` ブロック(Total Orders / Net Profit / Total Fees ¥...)が出れば成功です。

### `Total Orders 0` で完走したときは日足データが見えていません

ステップ 2) を飛ばした場合、**エラーは出ずに 0 注文で正常終了します**。`-v $PWD/Data/crypto/bitbank:...` のマウント元が存在しないと、Docker が空ディレクトリを勝手に作ってマウントするためです。次の兆候で判別できます。

```
JPY: ¥     1000000.00 @       1.00 = ¥1000000
BTC: ₿           0.00 @       0.00 = ¥0     ← 換算レートが 0.00
...
DATA USAGE:: Failed data requests percentage 100%
```

- **CashBook の BTC 換算レートが `0.00`**(正常なら `@ 5338537.00` のような実勢値)。ログの早い位置に出るので最初の手がかりになります
- `Failed data requests percentage` が **100%**(正常時も quote 分で 62% 程度は失敗します。理由は [docs/LEAN-CLI.md](docs/LEAN-CLI.md) 手順 6 参照)
- `Algorithm finished warming up.` が出ないままウォームアップ中に終了する

`ls Data/crypto/bitbank/daily`(zip が 8 個)を確認し、無ければステップ 2) の `CandleDownloader` を実行してください。

## クイックスタート(Windows PowerShell)

ステップ 1)〜2)(`dotnet build` / `dotnet run`)は上と同じです。ステップ 3)〜4)を以下に読み替えます。マージスクリプト(POSIX sh + python3)は LEAN コンテナ内で実行するため、Git Bash や Python のホスト側インストールは不要です。

```powershell
# 3) 公式イメージの symbol-properties / market-hours に bitbank 行をマージ
#    (抽出とマージをコンテナ内でまとめて実行)
docker pull quantconnect/lean:latest
$base = "$env:TEMP\lean-data"
New-Item -ItemType Directory -Force "$base\symbol-properties", "$base\market-hours" | Out-Null
docker run --rm -v "${PWD}:/repo:ro" -v "${base}:/out" --entrypoint /bin/sh quantconnect/lean:latest -c 'cp /Lean/Data/symbol-properties/symbol-properties-database.csv /out/symbol-properties/ && cp /Lean/Data/market-hours/market-hours-database.json /out/market-hours/ && /repo/scripts/install-bitbank-data.sh /out'

# 4) サンプルアルゴリズム(Python、BTC/JPY のゴールデンクロス)をバックテスト
docker run --rm `
  -v "${PWD}\QuantConnect.BitbankBrokerage\bin\Debug\net10.0:/plugin:ro" `
  -v "${base}\symbol-properties\symbol-properties-database.csv:/Lean/Data/symbol-properties/symbol-properties-database.csv:ro" `
  -v "${base}\market-hours\market-hours-database.json:/Lean/Data/market-hours/market-hours-database.json:ro" `
  -v "${PWD}\Data\crypto\bitbank:/Lean/Data/crypto/bitbank:ro" `
  -v "${PWD}\examples:/Algo:ro" `
  --entrypoint /bin/sh quantconnect/lean:latest -c 'cp /plugin/QuantConnect.BitbankBrokerage.dll /Lean/Launcher/bin/Debug/ && cd /Lean/Launcher/bin/Debug && dotnet QuantConnect.Lean.Launcher.dll --algorithm-type-name BitbankSmaCrossExample --algorithm-language Python --algorithm-location /Algo/bitbank_sma_cross.py'
```

注意: `scripts/install-bitbank-data.sh` は LF 改行必須です(`.gitattributes` で強制済み)。2026-08 以前にクローンしたリポジトリで `/bin/sh: not found` エラーが出る場合は、`git pull` 後に再クローンするか `git checkout -- scripts/` で改行を正規化してください。

`Total Orders 0` で終わった場合は[上記の節](#total-orders-0-で完走したときは日足データが見えていません)を参照してください(PowerShell では `dir Data\crypto\bitbank\daily` で確認)。

## Lean CLI での使い方(推奨)

普段 [Lean CLI](https://www.lean.io/docs/v2/lean-cli/key-concepts/getting-started)(`lean` コマンド)で開発している場合は、プラグイン入りカスタムイメージを一度作って `lean config set engine-image lean-bitbank:cli` しておけば、以後は**普段どおり `lean backtest` / `lean live deploy` と打つだけ**で bitbank 対応 LEAN が使えます:

```bash
dotnet build QuantConnect.BitbankBrokerage
dotnet run --project QuantConnect.BitbankBrokerage/tools/CandleDownloader -- --from 2018
docker build -f deploy/lean-cli/Dockerfile.cli -t lean-bitbank:cli .
lean config set engine-image lean-bitbank:cli
```

`CandleDownloader`(上の 2 行目)は省略できません。日足 zip はリポジトリに含まれないため、先に取得しないと `docker build` が `cp: cannot stat '.../Data/crypto/bitbank/daily'` で失敗します。

ワークスペースへのデータ配置・ライブ運用に必要な CLI パッチなど、完全な手順は [docs/LEAN-CLI.md](docs/LEAN-CLI.md) を参照してください。

> **注意**: 本プラグインは**ローカル実行専用**です(ローカルの Docker でバックテスト/ライブを実行)。QuantConnect クラウド(`lean cloud push` / `lean cloud backtest` / `lean cloud live`)では QC 側サーバーに本プラグインが存在しないため動きません。

## アルゴリズムからの使い方

Python:

```python
from AlgorithmImports import *
from clr import AddReference
AddReference("QuantConnect.BitbankBrokerage")
from QuantConnect.Brokerages.Bitbank import BitbankBrokerageModel

class MyAlgorithm(QCAlgorithm):
    def initialize(self):
        self.set_account_currency("JPY")
        self.set_cash(1_000_000)
        self.set_brokerage_model(BitbankBrokerageModel())   # bitbank 市場の登録も兼ねる
        self.btc = self.add_crypto("BTCJPY", Resolution.DAILY, "bitbank").symbol
```

C# も同様に `SetBrokerageModel(new BitbankBrokerageModel())`(`using QuantConnect.Brokerages.Bitbank;`)。

- 市場登録は DLL ロード時に自動で行われます(`Market.Add("bitbank", 44)` 相当。id は config `bitbank-market-id` で変更可)
- post_only 指値は `BitbankOrderProperties { PostOnly = true }` を注文プロパティに指定
- 注文の amend は不可(bitbank API 仕様)。cancel + 再発注してください

## 信用取引(margin)

bitbank の信用取引(最大レバレッジ 2 倍、ロング/ショート)に対応しています。

```python
class MyMarginAlgorithm(QCAlgorithm):
    def initialize(self):
        self.set_account_currency("JPY")
        self.set_cash(1_000_000)
        self.set_brokerage_model(BitbankBrokerageModel(AccountType.MARGIN))  # レバレッジ 2 倍・ショート可
        self.btc = self.add_crypto("BTCJPY", Resolution.DAILY, "bitbank").symbol

    def on_data(self, data):
        if not self.portfolio.invested:
            self.set_holdings(self.btc, -0.5)  # ショート建て
```

ライブでは config(または環境変数経由の `BrokerageData`)に **`"bitbank-account-type": "margin"`** を追加してください(既定は `"cash"` = 現物)。

- **position_side の自動判定**: bitbank の信用注文は建玉サイド(`position_side`)必須ですが、コネクターが自動判定します — 反対側の建玉があれば決済(買い=ショート決済、売り=ロング決済)、無ければ新規建て
- **明示指定**: `BitbankOrderProperties { PositionSide = BitbankPositionSide.Short }` で強制できます(例: ロング建玉を残したままショートを新規建て)
- **ドテン(決済+新規を 1 注文)は不可**: bitbank API の制約により、反対建玉の量を超える注文は拒否されます。2 注文に分割するか `PositionSide` を明示してください
- 建玉は `GetAccountHoldings()`(`GET /v1/user/margin/positions`)として LEAN に同期されます
- マージンコール / ロスカット / 追証の通知はプライベートストリーム(`margin_notice_update` 等)経由で警告メッセージとして届きます
- **建玉金利 0.04%/日**(日本時間 0 時徴収)はバックテストの `BitbankFeeModel` ではモデル化されません(ライブでは現金同期で反映)
- **手数料の徴収タイミング**(2026-08-09 実機確認): 新規建て約定の手数料は 0 で建玉の `unrealized_fee` に繰り延べられ、決済約定時に新規+決済分がまとめて徴収されます(決済 fill の手数料に合算)。約定イベントの `profit_loss` は手数料・金利控除後の実現損益です
- 信用取引には bitbank 側の**利用審査**の完了が必要です(未完了はエラー 50058)

## ライブトレーディング

config.json の `environments` に追加:

```jsonc
"live-bitbank": {
  "live-mode": true,
  "live-mode-brokerage": "BitbankBrokerage",
  "data-queue-handler": [ "BitbankBrokerage" ],
  "setup-handler": "QuantConnect.Lean.Engine.Setup.BrokerageSetupHandler",
  "result-handler": "QuantConnect.Lean.Engine.Results.LiveTradingResultHandler",
  "data-feed-handler": "QuantConnect.Lean.Engine.DataFeeds.LiveTradingDataFeed",
  "real-time-handler": "QuantConnect.Lean.Engine.RealTime.LiveTradingRealTimeHandler",
  "transaction-handler": "QuantConnect.Lean.Engine.TransactionHandlers.BrokerageTransactionHandler",
  "history-provider": [ "BrokerageHistoryProvider", "SubscriptionDataReaderHistoryProvider" ]
}
```

API キーは環境変数 `BITBANK_API_KEY` / `BITBANK_API_SECRET` で注入します(config の `bitbank-api-key` / `bitbank-api-secret` でも可だが、平文保存は非推奨)。キー発行の権限設定(出金権限は必ず無効に)・1Password / AWS SSM を使った安全な注入方法は [docs/SETUP.md](docs/SETUP.md) 参照。

ライブ前の疎通確認ツール(すべて `QuantConnect.BitbankBrokerage/tools/`):

| ツール | 内容 | 実注文 |
|---|---|---|
| `AssetsCheck` | 認証・残高・アクティブ注文の取得 | なし |
| `StreamCheck` | プライベートストリーム(PubNub)購読テスト | なし |
| `OrderSmokeTest` | 最小ロット指値の発注→取消ライフサイクル(`--yes` 必須) | **あり**(約定しない価格) |
| `MarginSmokeTest` | 信用取引: position_side 付き発注→取消 + 最小ロットのロング新規建て→建玉確認→決済(`--yes` 必須、`--cancel-only` で建玉なし) | **あり**(往復で数円〜数十円のコスト) |
| `CandleDownloader` | ローソク足の一括取得(Lean データ形式) | なし |

## テスト

```bash
dotnet test QuantConnect.BitbankBrokerage.Tests   # 48 tests、ネットワーク不要
```

## 制限事項

- **ローカル実行専用**: QuantConnect クラウド(`lean cloud` 系コマンド、quantconnect.com 上でのバックテスト/ライブ)では動作しない。クラウド側にカスタムブローカレッジを持ち込む仕組みが無いため
- 信用取引: ドテン(反対建玉の決済と新規建てを 1 注文で)は不可(API 制約、分割が必要)。bitbank 固有の `take_profit` / `stop_loss` / `losscut` 注文タイプは未対応。建玉金利はバックテストで未モデル化
- Second / Tick 解像度の履歴・Quote 履歴は非対応(ライブの板購読は対応)
- bitbank にはテストネットが無いため、ライブ検証は本番口座 + 最小ロットで行うこと

## ライセンス

Apache License 2.0(LEAN 本体と同じ)。本ソフトウェアの利用による損失について作者は責任を負いません。自動売買は自己責任で、必ず少額から検証してください。
