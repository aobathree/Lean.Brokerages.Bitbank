**夏休みの自由工作: QuantConnect LEAN の bitbank コネクター**

アルゴトレードエンジン **QuantConnect LEAN** から bitbank に接続するスタンドアロンのブローカレッジプラグインを作りました。LEAN 本体のフォークや再ビルドは不要で、公式 Docker イメージに DLL を1個足すだけで動きます。
🔗 https://github.com/aobathree/Lean.Brokerages.Bitbank (Apache 2.0)

**できること**
・バックテスト: bitbank の実ローソク足(公開API・キー不要で取得)+ bitbank の手数料モデルで検証。JPY建て口座に対応
・ライブ: 現物の成行/指値/post_only、信用取引(2倍レバ・ロング/ショート、position_side 自動判定)、WebSocketリアルタイムフィード
・対応ペア: JPY建て44ペア
・依存パッケージゼロ(Socket.IO / PubNub も必要最小を自前実装)

**セットアップ**

```
# 1) プラグインをビルドし、日足データを取得してカスタムイメージを作る
git clone https://github.com/aobathree/Lean.Brokerages.Bitbank.git
cd Lean.Brokerages.Bitbank
dotnet build QuantConnect.BitbankBrokerage
dotnet run --project QuantConnect.BitbankBrokerage/tools/CandleDownloader -- --from 2018
docker build -f deploy/lean-cli/Dockerfile.cli -t lean-bitbank:cli .

# 2) lean CLI ワークスペースを作って設定スクリプトを流す
mkdir -p ~/bitbank/lean-cli && cd ~/bitbank/lean-cli
lean init --language python
python3 ~/Lean.Brokerages.Bitbank/deploy/lean-cli/setup-workspace.py

# 3) (任意) Jupyter で bitbank シンボルを使うなら research 用イメージも作る
cd ~/Lean.Brokerages.Bitbank
docker build -f deploy/lean-cli/Dockerfile.research -t lean-bitbank:research .
lean config set research-image lean-bitbank:research
```

1) の `CandleDownloader` は飛ばさないでください。日足 zip はリポジトリに含めていないので、取得前に `docker build` すると `cp: cannot stat '.../Data/crypto/bitbank/daily'` で失敗します。公開 API なので API キーは不要です。

2) の `setup-workspace.py` が、`lean init` では入らないものをまとめて入れます。

・ワークスペースの `data/` への bitbank データ定義 — 無いと `Unable to locate exchange hours for Crypto-bitbank-BTCJPY` で落ちます
・CLI のデータ自動更新の無効化 — 放っておくと翌日に上の定義が消えます
・ライブ用のモジュール定義パッチと `lean.json` の `live-bitbank` 環境

`lean init` は QuantConnect/Lean 本家の設定ファイルをそのまま写すだけなので、外部プラグインの定義は構造上どうやっても入りません。そこを埋めるスクリプトです。冪等なので何度流しても大丈夫。中身の手順は [docs/LEAN-CLI.md](https://github.com/aobathree/Lean.Brokerages.Bitbank/blob/main/docs/LEAN-CLI.md) に全部書いてあります。

これで以後は `lean backtest` / `lean live deploy` と打つだけです。

> ⚠️ **lean CLI が入っている Python で実行してください**(モジュール定義パッチが lean の site-packages を書き換えるため)。pipx なら:
> `"$(pipx environment --value PIPX_LOCAL_VENVS)/lean/bin/python" ~/Lean.Brokerages.Bitbank/deploy/lean-cli/setup-workspace.py`
> Windows は PowerShell 用の手順が docs にあります。

**動作環境**
・Windows / macOS / Linux(スクリプトは Python です)
・Apple Silicon もネイティブ動作(公式イメージが arm64 対応)
・⚠️ **ローカル実行専用**です(ローカルの Docker で動かす構成)。QuantConnect クラウド(`lean cloud` 系)ではカスタムブローカレッジを持ち込めないため動きません
・対応する lean CLI コマンドは **`lean backtest` / `lean live deploy` / `lean research`**(research は上記 3 のイメージが必要。ノートブックでは `market="bitbank"` を使う前に `BitbankBrokerageModel()` を一度呼んでください)。**`lean report` / `lean optimize` は非対応**です — LEAN がバックテスト結果を読み直す処理はプラグインの市場登録が届かない箇所で、本体を改変しない方針では原理的に埋まりません

**そのほか**

サンプル(BTC/JPY ゴールデンクロス)同梱、`dotnet test` 48本、実口座での発注→取消 E2E も確認済みです。bitbank にはテストネットが無いので、最小ロットで安全に疎通確認するツール(OrderSmokeTest 等)も入れてあります。

サンプルのバックテストで `Failed data requests` が 6割ほど出ますが正常です。LEAN は Crypto に板情報も要求しますが、bitbank の公開APIはローソク足しか返さないためです。売買結果には影響しません(docs に説明あり)。

⚠️ APIキーは出金権限オフ推奨・環境変数注入(1Password/SSM の手順あり)。自動売買は自己責任で、必ず少額からどうぞ。

質問・不具合報告は GitHub Issues かこのスレッドへ 🙏
