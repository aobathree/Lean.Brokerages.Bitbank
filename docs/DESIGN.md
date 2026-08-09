> **Note**: この文書は Lean フォーク(aobathree/Lean `jp-broker-bitbank`)での開発時に書かれたもの。Launcher の直接実行・run-e2e.sh 等の記述はフォーク環境が前提です。プラグイン単体での使い方は [README](../README.md) を参照。

# Lean × bitbank コネクター設計文書

**Status:** v1.1(2026-08-08)— P2(Lean 本体基盤)実装済み
**対象 Lean リポジトリ:** `/Users/aobathree/Lean`(fork: `aobathree/Lean`、実装ブランチ: **`jp-broker-bitbank`**)
**成果物:** `QuantConnect.Brokerages.Bitbank`(設計文書・検証ツールは本ディレクトリ `QuantConnect.BitbankBrokerage/` に同梱)

> **実装状況**: Lean 側基盤(`Market.Bitbank` = id 44、`BrokerageName.Bitbank`、`BitbankBrokerageModel`、`BitbankFeeModel`、`BitbankOrderProperties`、symbol-properties 44 ペア、market-hours、config.json の `live-bitbank` 環境)と、コネクター本体(`QuantConnect.BitbankBrokerage` プロジェクト: REST + HMAC 認証、PubNub プライベートストリーム、Socket.IO データフィード、板管理、GetHistory、Factory、単体テスト 29 件)を `jp-broker-bitbank` ブランチに実装済み。
>
> **結合テスト結果(2026-08-08、本番口座・実機検証済み)**:
> - `AssetsCheck`: 認証・署名・残高取得 OK(JPY + 5 資産)
> - `StreamCheck`: PubNub プライベートストリーム 60 秒接続維持 OK
> - `OrderSmokeTest`: 最小ロット(BTC 0.0001)の発注 → `spot_order_new` 受信 → 取消 → `CANCELED_UNFILLED` 受信 → REST 最終確認まで**全段階合格**(order_id 59592963728)。取消イベントが REST 応答より先に到着するレースを実環境で観測し、`BrokerageConcurrentMessageHandler` の必要性を裏付け
> - **Lean 本体 E2E 合格**(2026-08-08、`BitbankE2ETestAlgorithm` @ live-bitbank 環境): OnData 受信(92 ティック)→ アルゴリズム API 発注(BrokerId 59593138216)→ 90 秒後取消 → ストリーム経由 Canceled 確認 → 自動終了まで全段階成功。初回実行でデータ到達が 7.5 分遅延する不具合を検出 → 原因は購読管理のチャネル共有により Quote(depth)room join がスキップされていたバグで、tick type ごとのチャネル名に修正済み(コミット 311b48376)。**修正後の 2 回目 E2E で効果を実証**: 購読から約 1 秒で OnData 到達(depth_whole 即時受信)、テスト全体が約 95 秒で自動完走(BrokerId 59593289172)。transactions チャネルの初回受信は購読の約 50 秒後で、「約定はまばら・板は常時更新」という Quote 併用設計の妥当性も実地で確認
> - 残り: 24h 安定稼働試験(P5)のみ
>
> **設計変更(v1.2)**: コネクタープロジェクトは開発効率のため Lean フォーク内(`Lean/QuantConnect.BitbankBrokerage/`)に配置した(Launcher から ProjectReference、ビルドで DLL が出力ディレクトリに自動配置される)。将来 upstream に PR する場合や独立配布する場合は、そのまま別リポジトリ + NuGet パッケージに切り出せる構成。また、Socket.IO は外部 SDK ではなく Engine.IO v4 プロトコルの必要最小サブセットを `WebSocketClientWrapper` 上に直接実装し(`BitbankSocketIoClient`)、PubNub も SDK ではなく subscribe v2 ワイヤプロトコル(HTTP ロングポーリング)で実装した(`BitbankPrivateStreamClient`)。依存パッケージゼロで、再接続・トークンリフレッシュを自前制御できる。

---

## 1. 目的とスコープ

QuantConnect の Lean エンジンから、日本の暗号資産取引所 **bitbank**(bitbank.cc)に接続し、以下を可能にする。

- **ライブトレーディング**: 現物(spot)の成行・指値・逆指値注文の発注/取消、残高・建玉の同期
- **ライブデータフィード**: ティッカー・約定・板情報のリアルタイム購読(`IDataQueueHandler`)
- **ヒストリカルデータ**: ローソク足 API 経由の履歴取得(`GetHistory` / `BrokerageHistoryProvider`)

### スコープ外(v1)

- ~~信用取引(margin)— bitbank は margin API を持つが、v1 は現物のみ~~ → **v1.3(2026-08-09)で対応済み**。`bitbank-account-type: margin` で有効化。`position_side` は反対建玉からの自動判定(買い=ショート決済 or ロング新規、売りは逆)+ `BitbankOrderProperties.PositionSide` での明示指定。建玉は `GET /user/margin/positions` → `GetAccountHoldings()`、`margin_position_update` / `margin_payable_update` / `margin_notice_update` ストリームを処理、`BitbankBrokerageModel` は `AccountType.Margin` でレバレッジ 2 倍 + `SecurityMarginModel`。ドテン(1 注文での決済+新規)は API 制約により拒否。**実機検証済み(2026-08-09、`tools/MarginSmokeTest` で新規建て→建玉照会→決済の全経路合格)**: btc_jpy 信用 taker 手数料は 0.1%/片道で、新規建て時は fill の手数料 0(建玉の unrealized_fee に繰り延べ)・決済 fill に新規+決済分を合算徴収、`profit_loss` は手数料・金利控除後。ストリーム `margin_position_update` は REST と異なる短縮フィールド名(open / locked / unrealized_fee / unrealized_interest)を使う
- 入出金操作(deposit / withdrawal)
- bitbank 固有の `take_profit` / `stop_loss` / `losscut` 注文タイプ

---

## 2. 参照アーキテクチャ

QuantConnect 公式のブローカレッジ構成([Lean.Brokerages.Template](https://github.com/QuantConnect/Lean.Brokerages.Template)、[Lean.Brokerages.Binance](https://github.com/QuantConnect/Lean.Brokerages.Binance))に倣い、**Lean 本体とは別のプラグインパッケージ**として実装する。Lean の `Composer` は `[InheritedExport]` 属性付きの `IBrokerageFactory` / `IDataQueueHandler` 実装を DLL スキャンで自動発見するため、ビルド成果物を Launcher の出力ディレクトリ(または `plugin-directory`)に配置し、config.json で型名を指定するだけで組み込める。

```
┌─────────────────────────── Lean Engine ───────────────────────────┐
│  BrokerageTransactionHandler      LiveTradingDataFeed             │
│        │ PlaceOrder/Cancel              │ Subscribe               │
│        ▼                                ▼                         │
│  ┌──────────────────────────────────────────────────────┐         │
│  │   BitbankBrokerage : BaseWebsocketsBrokerage,        │         │
│  │                      IDataQueueHandler               │         │
│  └──────┬──────────────────┬──────────────────┬─────────┘         │
└─────────┼──────────────────┼──────────────────┼───────────────────┘
          │ REST (HMAC)      │ PubNub           │ Socket.IO 4
          ▼                  ▼                  ▼
   api.bitbank.cc/v1   private stream     stream.bitbank.cc
   (注文・残高・履歴)   (注文/約定/資産      (ticker, transactions,
                        イベント)           depth_whole/diff)
```

**重要な設計判断**: bitbank は 3 つの異なるトランスポートを要求する。

| 用途 | トランスポート | 備考 |
|---|---|---|
| 注文・残高・履歴 | REST(HMAC-SHA256 署名) | `https://api.bitbank.cc/v1`、公開系は `https://public.bitbank.cc` |
| 注文イベント・約定・資産更新 | **PubNub** | `GET /v1/user/subscribe` でチャネル名とトークンを取得 |
| 市場データ(ticker/約定/板) | **Socket.IO 4.x** | `wss://stream.bitbank.cc`。生の WebSocket ではないため Lean 標準の `WebSocketClientWrapper` は使えない |

Socket.IO / PubNub とも、Lean の `IWebSocket` インターフェース(9 メンバーの小さな抽象)にアダプターを被せて `BaseWebsocketsBrokerage` の再接続・再購読フローに乗せる。

---

## 3. プロジェクト構成

```
QuantConnect.BitbankBrokerage/          # ← 現在は Lean フォーク内の本ディレクトリ
├── DESIGN.md                              ← 本書
├── QuantConnect.BitbankBrokerage/
│   ├── BitbankBrokerage.cs                # IBrokerage 本体(発注・取消・残高)
│   ├── BitbankBrokerage.Messaging.cs      # partial: PubNub/Socket.IO メッセージ処理
│   ├── BitbankBrokerage.DataQueueHandler.cs # partial: IDataQueueHandler 実装
│   ├── BitbankBrokerage.History.cs        # partial: GetHistory(ローソク足)
│   ├── BitbankBrokerageFactory.cs         # IBrokerageFactory
│   ├── Api/
│   │   ├── BitbankRestApiClient.cs        # REST クライアント + HMAC 署名
│   │   └── BitbankAuthenticator.cs        # ACCESS-REQUEST-TIME 方式の署名生成
│   ├── Streaming/
│   │   ├── BitbankSocketIoClient.cs       # Socket.IO 4 ラッパー(IWebSocket 適合)
│   │   └── BitbankPubNubClient.cs         # PubNub ラッパー(トークン更新込み)
│   ├── Messages/                          # REST/stream の DTO(success/data エンベロープ)
│   └── BitbankOrderBook.cs                # depth_whole/diff の順序付け(DefaultOrderBook 利用)
├── QuantConnect.BitbankBrokerage.Tests/
│   └── ...                                # 単体・結合テスト
└── bitbank.json                           # モジュール仕様(CLI 用、任意)
```

### Lean 本体側への追加(fork or PR)

外部プラグインだけでは完結しない部分。Lean リポジトリ側に最小限の追加を行う。

| ファイル | 追加内容 |
|---|---|
| `Common/Market.cs` | `public const string Bitbank = "bitbank";` + `Tuple.Create(Bitbank, 44)`(※本体を改変したくない場合は、Factory 起動時に `Market.Add("bitbank", 44)` を呼ぶ方式でも可) |
| `Common/Brokerages/BrokerageName.cs` | `Bitbank` を追加 |
| `Common/Brokerages/BitbankBrokerageModel.cs` | 新規(§7) |
| `Common/Orders/Fees/BitbankFeeModel.cs` | 新規(§8) |
| `Common/Orders/BitbankOrderProperties.cs` | 新規: `PostOnly` プロパティ(`BinanceOrderProperties` と同型) |
| `Data/symbol-properties/symbol-properties-database.csv` | bitbank の各ペア行(§5) |
| `Data/market-hours/market-hours-database.json` | `"Crypto-bitbank-[*]"` 24/365 エントリ(UTC、coinbase の項をコピー) |
| `Launcher/config.json` | 認証キーと `live-bitbank` 環境(§10) |

---

## 4. クラス設計

### 4.1 BitbankBrokerage

```csharp
[BrokerageFactory(typeof(BitbankBrokerageFactory))]
public partial class BitbankBrokerage : BaseWebsocketsBrokerage, IDataQueueHandler
```

`Brokerage` 基底の必須メンバーの実装方針:

| メンバー | 実装 |
|---|---|
| `Connect()` | REST 疎通確認(`/spot/status`)→ `GET /user/subscribe` → PubNub 購読開始 → Socket.IO 接続 |
| `IsConnected` | Socket.IO と PubNub 両方の接続状態の AND |
| `PlaceOrder(Order)` | `POST /user/spot/order`。`BrokerageConcurrentMessageHandler.WithLockedStream` 内で実行(§4.4)。成功時 `order.BrokerId` に `order_id` を格納し `Submitted` イベント発火 |
| `UpdateOrder(Order)` | **常に false**(bitbank に amend API が無い)。`BitbankBrokerageModel.CanUpdateOrder` も false を返し、アルゴリズム側には cancel + 再発注を促す |
| `CancelOrder(Order)` | `POST /user/spot/cancel_order` |
| `GetOpenOrders()` | `GET /user/spot/active_orders`(全ペア分。pair 指定なしで全件取得) |
| `GetAccountHoldings()` | 現物 cash account のため **空リスト**を返す(Binance/Coinbase と同様、残高は CashBalance で表現) |
| `GetCashBalance()` | `GET /user/assets` → 資産ごとに `CashAmount(onhand_amount, asset.ToUpper())` |
| `AccountBaseCurrency` | **`Currencies.JPY`**(§6 参照 — USD 中心の既存実装との最大の相違点) |
| `GetHistory(HistoryRequest)` | ローソク足 API(§9) |

### 4.2 REST クライアントと認証

`BitbankRestApiClient` に署名処理を集約する。認証は **time-window 方式**(nonce 方式はプロセス間衝突リスクがあるため不採用):

- ヘッダー: `ACCESS-KEY` / `ACCESS-REQUEST-TIME`(UNIX ms)/ `ACCESS-TIME-WINDOW`(既定 5000ms)/ `ACCESS-SIGNATURE`
- 署名 = HMAC-SHA256(hex)。メッセージは
  - GET: `{REQUEST-TIME}{TIME-WINDOW}` + `/v1{path}?{query}`(**`/v1` を含むフルパス**)
  - POST: `{REQUEST-TIME}{TIME-WINDOW}` + 生 JSON ボディ
- レスポンスは常に `{"success": 0|1, "data": {...}}` エンベロープ。`success: 0` の場合 `data.code` を `errors.md` のコード表(§11)にマップして `BrokerageMessageEvent` を発火

**レートリミッター**(Lean の `RateGate` を使用):

| 区分 | 上限 | 適用先 |
|---|---|---|
| 参照系(GET) | 10 req/s | assets, active_orders, order 照会 |
| 更新系(POST) | 6 req/s | order, cancel_order |

### 4.3 プライベートストリーム(PubNub)

注文ステータス・約定・資産変動はすべて PubNub 経由で受信する。

1. 署名付き `GET /v1/user/subscribe` → `{ pubnub_channel, pubnub_token }` を取得
2. PubNub SDK(NuGet: `Pubnub`)を subscribeKey `sub-c-ecebae8e-dd60-11e6-b6b1-02ee2ddab7fe`、`userId = pubnub_channel` で初期化し、`setToken(pubnub_token)` → チャネル購読
3. **トークン失効対応**: PubNub の access-denied ステータスを受けたら `/user/subscribe` を再実行してトークンを差し替え(自動リフレッシュループ)

処理するメッセージタイプと Lean イベントへのマッピング:

| bitbank メッセージ | Lean 側の処理 |
|---|---|
| `spot_order_new` | `OrderStatus.Submitted` の `OrderEvent` |
| `spot_order`(status 遷移) | `UNFILLED→Submitted`, `PARTIALLY_FILLED→PartiallyFilled`, `FULLY_FILLED→Filled`, `CANCELED_*→Canceled`, `REJECTED→Invalid` |
| `spot_trade` | 約定明細。`maker_taker` フラグと手数料額(`fee_amount_base/quote`)を `OrderEvent.OrderFee` に反映 |
| `spot_order_invalidation` | 資産不足によるエンジン取消 → `Canceled` + 警告メッセージ |
| `asset_update` | `AccountEvent`(残高変動通知) |

`spot_trade` と `spot_order` の両方が届くため、**約定数量の集計は `spot_order` の `executed_amount` を正とし、`spot_trade` は手数料と maker/taker 判定に使う**(二重計上防止)。

### 4.4 注文イベントの競合対策

REST の発注応答より先に PubNub の約定通知が届くレースが起こり得る。Lean 標準の `BrokerageConcurrentMessageHandler<T>` を使い:

- PubNub コールバックは `HandleNewMessage(msg)` に流す
- `PlaceOrder` / `CancelOrder` の REST 呼び出しは `WithLockedStream(...)` で包む

これにより `BrokerId` 未設定の状態で約定イベントを処理してしまう事故を防ぐ。

### 4.5 マーケットデータ(Socket.IO)+ IDataQueueHandler

- `EventBasedDataQueueHandlerSubscriptionManager` で `(Symbol, TickType)` の購読を参照カウント管理
- Socket.IO の room 購読(`join-room` emit):

| Lean TickType | bitbank チャネル | 生成する BaseData |
|---|---|---|
| Trade | `transactions_{pair}` | `Tick(TickType.Trade)`(price, amount, side) |
| Quote | `depth_whole_{pair}` + `depth_diff_{pair}` | `DefaultOrderBook` を更新し `BestBidAskUpdated` で `Tick(TickType.Quote)` |

- **板の整合性**: `depth_whole`(200 レベルのスナップショット、シーケンス id 付き)受信で再ベースライン、`depth_diff` はシーケンス id がスナップショットより大きいもののみ適用。amount `"0"` はレベル削除
- ティックは `IDataAggregator`(`AggregationManager`)の `Update()` に流し、Lean 側で任意の Resolution に集約させる
- Socket.IO 切断時は `BaseWebsocketsBrokerage` の再接続 → `Subscribe(GetSubscribed())` フローで全 room を再購読

### 4.6 BitbankBrokerageFactory

```csharp
public class BitbankBrokerageFactory : BrokerageFactory
```

- `BrokerageData`: `bitbank-api-key`, `bitbank-api-secret`, `bitbank-rest-url`, `bitbank-public-url`, `bitbank-websocket-url` を `Config.Get` から供給
- `GetBrokerageModel(orderProvider)` → `new BitbankBrokerageModel()`
- `CreateBrokerage(job, algorithm)` → 集約マネージャーを Composer から解決しつつ `BitbankBrokerage` を生成
- Lean 本体を改変しない運用の場合、ここで `Market.Add("bitbank", 44)` を実行

---

## 5. シンボルマッピング

**カスタムマッパー不要**の設計とする。`SymbolPropertiesDatabaseSymbolMapper(Market.Bitbank)` は symbol-properties DB の `market_ticker` 列で双方向マッピングを行うため、CSV に bitbank のペアコードを載せれば足りる。

CSV 行の例(値は起動時に `GET /spot/pairs` で検証する。`price_digits` → `minimum_price_variation`、`amount_digits` → `lot_size`、`unit_amount` → `minimum_order_size`):

```csv
market,symbol,type,description,quote_currency,contract_multiplier,minimum_price_variation,lot_size,market_ticker,minimum_order_size
bitbank,BTCJPY,crypto,Bitcoin-Japanese Yen,JPY,1,1,0.0001,btc_jpy,0.0001
bitbank,ETHJPY,crypto,Ethereum-Japanese Yen,JPY,1,1,0.0001,eth_jpy,0.0001
bitbank,XRPJPY,crypto,Ripple-Japanese Yen,JPY,1,0.001,0.0001,xrp_jpy,0.0001
```

- Lean シンボル: `Symbol.Create("BTCJPY", SecurityType.Crypto, Market.Bitbank)`
- **対応ペア(v1): 公式サイト掲載の JPY 建て 44 ペア**(出典: [取扱ペアおよび注文単位](https://bitbank.cc/guide/pair/)、2026-08-08 取得):
  `btc, xrp, ltc, eth, mona, bcc, xlm, qtum, bat, omg, xym, link, boba, enj, pol, dot, doge, astr, ada, avax, axs, flr, sand, ape, gala, chz, oas, mana, grt, render, bnb, arb, op, dai, klay, imx, mask, sol, cyber, trx, lpt, atom, sui, sky`(各 `_jpy`)
- 注意: API の `/spot/pairs` は 47 の JPY ペアを `is_enabled: true` で返すが、うち `mkr_jpy`、`rndr_jpy`(RENDER に改称)、`matic_jpy`(POL に改称)の 3 つは公式サイトの取扱一覧に無いレガシーティッカーのため**除外**する
- 数値仕様は API を正とする: `price_digits` → `minimum_price_variation`、`amount_digits` → `lot_size`、`unit_amount` → `minimum_order_size`
- CSV 生成は手作業でなく **公式 44 ペアのリストと `/spot/pairs` を結合して CSV 行を生成するスクリプト**(ToolBox 相当)を用意し、tick size 変更に追随できるようにする

---

## 6. 口座通貨 = JPY の取り扱い

既存のクリプトブローカレッジは USD 前提のため、ここが bitbank 固有の要注意点。

- `Brokerage.AccountBaseCurrency = Currencies.JPY` を設定。`BrokerageSetupHandler` がこれを `algorithm.AccountCurrency` に反映する
- `CashBook` の通貨換算は `*JPY` ペアの存在に依存する。上記 CSV に JPY 建てペアを揃えることで、BTC/ETH 等の保有分の JPY 換算レートが解決可能になる
- 手数料通貨: 買い注文の手数料はベース通貨、売り注文はクオート通貨(JPY)で発生する(bitbank の `fee_amount_base` / `fee_amount_quote` に対応)

---

## 7. BitbankBrokerageModel

`CoinbaseBrokerageModel` を雛形とする(現物 cash-only という性質が最も近い)。

```csharp
public class BitbankBrokerageModel : DefaultBrokerageModel
```

| オーバーライド | 内容 |
|---|---|
| `DefaultMarkets` | `SecurityType.Crypto → Market.Bitbank` |
| ctor | `AccountType.Margin` は throw(v1 は現物のみ) |
| `GetLeverage` | `1m` |
| `CanSubmitOrder` | `SecurityType.Crypto` のみ許可。対応注文タイプ: `Market, Limit, StopMarket, StopLimit`。`IsValidOrderSize` で `unit_amount` 未満を拒否。市場注文は `market_max_amount`、指値は `limit_max_amount` 超過を拒否 |
| `CanUpdateOrder` | **false 固定**(amend API なし → cancel-replace を強制) |
| `GetFeeModel` | `new BitbankFeeModel()` |
| `GetBenchmark` | `Symbol.Create("BTCJPY", SecurityType.Crypto, Market.Bitbank)` |

`StopMarket` / `StopLimit` は bitbank の `stop` / `stop_limit`(`trigger_price` 必須)にマップ。ペアごとの `stop_order` / `stop_stop_order` 停止フラグが立っている場合は `CanSubmitOrder` で拒否する。

---

## 8. BitbankFeeModel

- 既定値: **maker -0.02%(-0.0002m)/ taker 0.12%(0.0012m)** — 主要ペアの標準料率
- 注意: 料率はペアごと・キャンペーンで変動する(2026-08 時点で btc_jpy は maker 0% / taker 0.10%)。そのため:
  - コンストラクタで maker/taker を注入可能にする(`BinanceFeeModel` と同型)
  - ブローカレッジ接続時に `GET /spot/pairs` の `maker_fee_rate_quote` / `taker_fee_rate_quote` を読んで実料率を使う(ライブでは PubNub `spot_trade` の実手数料が最終的な正)
- maker 判定は Lean の慣用句 `order.Type == OrderType.Limit && (props.PostOnly || !order.IsMarketable)` を踏襲
- `OrderFee` は負値(maker リベート)を許容するため、負の手数料もそのまま表現できる

---

## 9. ヒストリカルデータ(GetHistory)

`GET https://public.bitbank.cc/{pair}/candlestick/{candle_type}/{date}` を使用。

| Lean Resolution | candle_type | date パラメータ |
|---|---|---|
| Minute | `1min` | `YYYYMMDD`(日単位でページング) |
| Hour | `1hour` | `YYYYMMDD` |
| Daily | `1day` | `YYYY`(年単位でページング) |

- レスポンスの `ohlcv` 配列 `[open, high, low, close, volume, unixtime_ms]` → `TradeBar`
- Second / Tick 解像度、および `TickType.Quote` の履歴は非対応(null を返し警告メッセージ)
- config の `history-provider: ["BrokerageHistoryProvider", "SubscriptionDataReaderHistoryProvider"]` により、ブローカレッジ履歴 → ローカルデータの順でフォールバック

---

## 10. 設定(Launcher/config.json)

```jsonc
// トップレベルに追加
"bitbank-rest-url": "https://api.bitbank.cc",
"bitbank-public-url": "https://public.bitbank.cc",
"bitbank-websocket-url": "wss://stream.bitbank.cc",
"bitbank-api-key": "",
"bitbank-api-secret": "",

// environments に追加
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

### API キーの管理

API キー・シークレットは平文でのコミット・保存を禁止し、環境ごとに以下のシークレットストアから起動時に注入する。

| 環境 | 保管場所 | 注入方法 |
|---|---|---|
| ローカル(開発・検証) | **1Password** | 1Password CLI(`op`)で実行時に解決。config.json には値を書かない |
| AWS(本番運用) | **AWS Systems Manager パラメータストア**(SecureString、KMS 暗号化) | 起動スクリプトまたはエントリポイントで `ssm get-parameter --with-decryption` により取得し環境変数へ |

Lean の `Config` は環境変数によるオーバーライドに対応していないため、注入は次のいずれかで行う:

1. **起動ラッパー方式(推奨)**: config.json をテンプレート(`config.template.json`)として管理し、起動スクリプトがシークレットストアから取得した値で `bitbank-api-key` / `bitbank-api-secret` を埋めた一時 config.json を生成して Launcher に渡す。生成物は tmpfs 等に置き、ディスク永続化やログ出力をしない
2. **Factory 内フォールバック方式**: `BitbankBrokerageFactory` で `Config.Get("bitbank-api-key")` が空の場合に環境変数 `BITBANK_API_KEY` / `BITBANK_API_SECRET` を参照する。この場合、ローカルは `op run --env-file=.env.1password -- dotnet QuantConnect.Lean.Launcher.dll`(`.env.1password` には `BITBANK_API_KEY="op://<vault>/<item>/api-key"` 形式の参照のみを記載)、AWS は ECS タスク定義 / EC2 起動時に SSM パラメータ(例: `/lean/bitbank/api-key`, `/lean/bitbank/api-secret`)を環境変数へマップして起動する

いずれの方式でも:

- リポジトリには `config.template.json`(キー空欄)と `env.1password.sample`(op:// 参照のテンプレート)だけをコミットし、`.env*`(実値やボールト固有の op:// 参照を含む)は `.gitignore` に登録する
- AWS 側の SSM パラメータは SecureString + 専用 KMS キーで暗号化し、Lean 実行ロール(ECS タスクロール等)にのみ `ssm:GetParameter` / `kms:Decrypt` を許可する
- ログ・例外メッセージ・`BrokerageMessageEvent` にキーやシークレットを含めない(REST クライアントの署名処理はシークレットをフィールドに保持せず、可能な限り `HMACSHA256` インスタンス生成後に参照を手放す)
- テスト用と本番用でキーを分離し(§12)、SSM 側もパス階層(`/lean/bitbank/prod/...`, `/lean/bitbank/test/...`)で分ける

### デプロイ方式

1. **開発中**: 本プロジェクトを `dotnet build` し、出力 DLL を `Lean/Launcher/bin/Debug/` にコピー(post-build event)。Composer が自動発見
2. **配布**: `.nupkg` を作り Lean の `LocalPackages/` に配置する方式(Lean 公式の外部ブローカレッジ開発フロー)

---

## 11. エラーハンドリング

bitbank のエラーコード(`{"success":0,"data":{"code":N}}`)の主なマッピング:

| コード | 意味 | Lean 側の扱い |
|---|---|---|
| 20001–20005 | 認証・署名エラー | `BrokerageMessageType.Error`、接続中断 |
| 20033–20035 | request-time / time-window 不正 | 時刻同期警告を出して 1 回リトライ |
| 10009 | レート制限超過 | RateGate 待機後リトライ(最大 3 回、指数バックオフ) |
| 40001–40021 | 数量・価格・パラメータ不正 | `OrderStatus.Invalid` |
| 50009 / 50026 / 50027 | 注文なし / 取消済 / 約定済 | Cancel 時は成功扱いに正規化(冪等性) |
| 50061–50062, 60001 | 残高不足 | `OrderStatus.Invalid` + `Message` イベント |
| 70011 | システム高負荷 | 警告 + バックオフ・リトライ |
| HTTP 429 | レート制限 | 10009 と同様 |

その他: `GET /spot/status` の取引ステータス(ペアごとの `status: NORMAL/BUSY/VERY_BUSY/HALT`)を接続時に確認し、`HALT` のペアへの発注は事前拒否する。

---

## 12. テスト計画

| レイヤー | 内容 |
|---|---|
| 単体 | 署名生成(既知ベクトルとの一致)、シンボルマッピング往復、depth_diff シーケンス適用ロジック、エラーコード → OrderStatus マッピング、DTO デシリアライズ |
| 結合(要 API キー) | Lean 標準の `Tests/Brokerages/BrokerageTests.cs` を継承した `BitbankBrokerageTests`: PlaceOrder→Fill→CashBalance 反映、Cancel、GetOpenOrders 復元。最小ロット(BTC 0.0001)で実施 |
| データフィード | `transactions_{pair}` / depth 購読の長時間安定試験、切断→再接続→再購読の検証 |
| E2E | `live-bitbank` 環境 + 最小額アルゴリズムで Launcher を起動し、発注〜約定〜ポートフォリオ反映を確認 |

bitbank にはテストネットが無いため、結合テスト以降は**本番口座 + 最小ロット**で行う。誤発注防止のため、テスト用 API キーは発注上限の小さい専用キーを使う。

---

## 13. 実装フェーズ

| フェーズ | 内容 | 完了条件 |
|---|---|---|
| **P1: 基盤** | プロジェクト雛形、REST クライアント + 署名、DTO、レートリミッター | `/user/assets` が取得できる単体テスト green |
| **P2: シンボル/モデル** | Market 登録、symbol-properties CSV 生成スクリプト、BrokerageModel、FeeModel、market-hours | マッピング往復テスト green |
| **P3: 注文系** | PlaceOrder / CancelOrder / GetOpenOrders / GetCashBalance、PubNub 受信、OrderEvent 変換、ConcurrentMessageHandler | 本番最小ロットで発注→約定→取消が Lean に正しく反映 |
| **P4: データフィード** | Socket.IO クライアント、IDataQueueHandler、板管理、Tick 生成 | ライブ Tick がアルゴリズムの OnData に届く |
| **P5: 履歴・仕上げ** | GetHistory、エラーハンドリング網羅、再接続耐久試験、E2E | live-bitbank 環境での 24h 安定稼働 |

---

## 14. 主要リスクと対策

| リスク | 対策 |
|---|---|
| Socket.IO 4 / PubNub の C# 依存追加(Lean 公式ブローカレッジに前例が少ない) | `SocketIOClient`(NuGet)と `Pubnub` 公式 SDK を採用。どちらも `IWebSocket` アダプター越しに隔離し、差し替え可能にする。PubNub が不安定な場合のフォールバックとして `active_orders` の REST ポーリング(2s 間隔、レート制限内)を実装 |
| PubNub トークン失効 | access-denied 検知 → `/user/subscribe` 再実行の自動リフレッシュ(§4.3) |
| 手数料率の変動(キャンペーン等) | 起動時に `/spot/pairs` から実料率を取得。モデルはあくまで見積り、確定値は `spot_trade` イベントの実額 |
| JPY 口座通貨に起因する換算エラー | 全対応ペアを symbol-properties に登録し、起動時に CashBook 換算解決を検証するチェックを入れる |
| 注文履歴が API で約 3 ヶ月しか遡れない | Lean 側の永続化(結果ファイル)を正とし、リカバリ時は `active_orders` + 直近 `trade_history` で復元 |
| **既知の upstream 脆弱性**(NuGet 監査 NU1902〜NU1904): ① DotNetZip 1.16.0(High、zip 展開時のパストラバーサル [GHSA-xhg6-9j5j-w4vf](https://github.com/advisories/GHSA-xhg6-9j5j-w4vf)、Lean 本体 `Compression` が直接参照)② System.Drawing.Common 4.7.0(Critical、画像処理 RCE [GHSA-rxg9-xrhp-64gj](https://github.com/advisories/GHSA-rxg9-xrhp-64gj)、`System.Windows.Extensions 4.7.0` 経由の推移的依存)③ Launcher のみで検出される WCF 系 3 パッケージ(いずれも 4.4.0): System.Net.Http.WinHttpHandler(High [GHSA-6xh7-4v2w-36q6](https://github.com/advisories/GHSA-6xh7-4v2w-36q6))、System.Private.ServiceModel / System.ServiceModel.Primitives(High [GHSA-jc8g-xhw5-6x46](https://github.com/advisories/GHSA-jc8g-xhw5-6x46)、Medium [GHSA-p9wx-v264-q34p](https://github.com/advisories/GHSA-p9wx-v264-q34p)) | いずれも **Lean 本体(upstream)由来**で、本コネクターの追加依存はゼロ(Socket.IO / PubNub を自前実装にした理由の一つ)。攻撃条件は「悪意ある zip / 画像 / WCF 通信の処理」であり、ライブ取引経路(REST / ストリーム)はいずれも扱わない(WCF 系は Windows 向け機能で macOS/Linux の実行パスに乗らない)ため実質的リスクは低い。**方針: フォーク側では修正せず upstream の課題として扱う**(独自差し替えは upstream 追従時の衝突源になる。DotNetZip はメンテ終了で修正版が存在せず、根本対応は upstream のライブラリ置換待ち)。AWS 本番デプロイで監査要件がある場合のみ、System.Drawing.Common を安全版(≥ 4.7.2)へ明示ピン留めする 1 行コミットを別途積む。`NoWarn` による警告抑制は行わない |
| **ビルド時の大量の CA 系コード分析警告**(CA1063 Dispose パターン、CA1031 汎用例外 catch、CA1200 cref 表記など、Engine / ToolBox / Research / Launcher で数百件) | **Lean 本体が .NET コード分析アナライザーを有効にしてビルドされるために表示される upstream の既存警告**であり、エラーではなくビルド成否・実行時動作に影響しない(upstream の master をビルドしても同様に出る)。本コネクターのプロジェクト(`QuantConnect.BitbankBrokerage` / `.Tests`)は `EnableNETAnalyzers=false` としており、**コネクター由来の警告はゼロ**。方針: フォーク側では対応しない(修正は広範囲の upstream コード変更になり追従時の衝突源になる)。CI 等でログノイズが問題になる場合は、ビルドログのフィルタリング(`-clp:ErrorsOnly` や grep)で対処し、コード側の抑制は行わない |

### ビルド警告の OS 依存性に関する補足

**ビルド警告は OS 非依存**である。CA 系警告は Roslyn アナライザー(.NET SDK 同梱)による静的解析、NU190x 系警告はパッケージ復元時の nuget.org 脆弱性 DB 照合で生成され、いずれもビルドホストの OS(macOS / Windows 11 / Linux)に関係なく同一の警告が出る。警告の有無・件数を左右するのは OS ではなく次の 3 点:

1. **.NET SDK のバージョン** — 新しい SDK ほどルールが増え警告は増える方向(NuGet 監査自体が .NET 8 SDK 以降で既定有効化された機能)
2. **プロジェクト設定** — `EnableNETAnalyzers` / `NoWarn` 等(本コネクターが警告ゼロなのはこのため)
3. **フィード到達性** — オフライン復元やプライベートフィードでは脆弱性 DB を取得できず NU190x が「見えなくなる」ことがある(安全になったわけではない点に注意)

一方、**実行時のリスク特性は OS 依存**であり、方向は逆になる。`System.Drawing.Common`(Windows GDI+)や WCF 系 3 パッケージ(`System.Net.Http.WinHttpHandler` 等)は Windows 向け機能のため、macOS / Linux では実行パスに乗らないが **Windows では実際に動き得る**。したがって上記リスク表の「実行パスに乗らないため実質的リスクは低い」という評価は macOS / Linux(AWS の Linux コンテナ運用を含む)を前提としたものであり、**将来 Windows で運用する場合はこの前提を再評価すること**。

---

## 付録 A: bitbank API リファレンス要約

- ドキュメント: https://github.com/bitbankinc/bitbank-api-docs
- 公式クライアント(署名実装の参照用): [node-bitbankcc](https://github.com/bitbankinc/node-bitbankcc), [python-bitbankcc](https://github.com/bitbankinc/python-bitbankcc)
- 注文ステータス: `INACTIVE / UNFILLED / PARTIALLY_FILLED / FULLY_FILLED / CANCELED_UNFILLED / CANCELED_PARTIALLY_FILLED / REJECTED`
- 注文タイプ: `limit / market / stop / stop_limit`(+ margin 系)。`post_only`(limit のみ)、`trigger_price`(stop 系必須)
- レート制限: 参照 10 req/s、更新 6 req/s(超過で HTTP 429 / code 10009)

## 付録 B: Lean 側参照ファイル

| 用途 | パス |
|---|---|
| IBrokerage | `Common/Interfaces/IBrokerage.cs` |
| Brokerage 基底 | `Brokerages/Brokerage.cs` |
| BaseWebsocketsBrokerage | `Brokerages/BaseWebsocketsBrokerage.cs` |
| IDataQueueHandler | `Common/Interfaces/IDataQueueHandler.cs` |
| 購読管理 | `Common/Data/EventBasedDataQueueHandlerSubscriptionManager.cs` |
| 板管理 | `Brokerages/DefaultOrderBook.cs` |
| 競合対策 | `Brokerages/BrokerageConcurrentMessageHandler.cs` |
| シンボルマッパー | `Brokerages/SymbolPropertiesDatabaseSymbolMapper.cs` |
| 手数料の雛形 | `Common/Orders/Fees/CoinbaseFeeModel.cs`, `BinanceFeeModel.cs` |
| モデルの雛形 | `Common/Brokerages/CoinbaseBrokerageModel.cs` |
| ファクトリの雛形 | `Brokerages/Paper/PaperBrokerageFactory.cs` |
| テスト基底 | `Tests/Brokerages/BrokerageTests.cs` |
