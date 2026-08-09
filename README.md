# Lean.Brokerages.Bitbank

[QuantConnect LEAN](https://github.com/QuantConnect/Lean) から日本の暗号資産取引所 [bitbank](https://bitbank.cc) に接続する、**スタンドアロンのブローカレッジプラグイン**。

LEAN 本体の改変・再ビルドは不要。公式 NuGet パッケージ / 公式 Docker イメージ（`quantconnect/lean`)の上に、この DLL とデータ行を足すだけで動きます。

## 機能

- **ライブトレーディング**: 現物(spot)の成行・指値・逆指値注文、post_only、残高同期、JPY 口座通貨
- **ライブデータフィード**: ティッカー・約定・板(depth)のリアルタイム購読(`IDataQueueHandler`)
- **ヒストリカルデータ**: ローソク足 API 経由の履歴取得(1min / 1hour / 1day)
- **対応ペア**: 公式サイト掲載の JPY 建て 44 ペア
- 依存パッケージゼロ(Socket.IO 4 / PubNub とも必要最小プロトコルを自前実装)

実装の設計判断・アーキテクチャは [docs/DESIGN.md](docs/DESIGN.md)、API キー設定は [docs/SETUP.md](docs/SETUP.md) を参照。

## 必要環境

- .NET SDK 10(DLL のビルドに使用)
- Docker(公式 LEAN イメージでのバックテスト/ライブ実行に使用)
- bitbank の API キー(ライブのみ。バックテストは不要)

## クイックスタート(公式 LEAN イメージでバックテスト)

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
| `CandleDownloader` | ローソク足の一括取得(Lean データ形式) | なし |

## テスト

```bash
dotnet test QuantConnect.BitbankBrokerage.Tests   # 29 tests、ネットワーク不要
```

## 制限事項(v1)

- 現物のみ(信用取引 API は未対応)
- Second / Tick 解像度の履歴・Quote 履歴は非対応(ライブの板購読は対応)
- bitbank にはテストネットが無いため、ライブ検証は本番口座 + 最小ロットで行うこと

## ライセンス

Apache License 2.0(LEAN 本体と同じ)。本ソフトウェアの利用による損失について作者は責任を負いません。自動売買は自己責任で、必ず少額から検証してください。
