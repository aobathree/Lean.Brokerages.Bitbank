**夏休みの自由工作: QuantConnect LEAN の bitbank コネクター**

アルゴトレードエンジン **QuantConnect LEAN** から bitbank に接続するスタンドアロンのブローカレッジプラグインを作りました。LEAN 本体のフォークや再ビルドはせず、公式 Docker イメージにプラグイン DLL を追加して使う構成です。
🔗 https://github.com/aobathree/Lean.Brokerages.Bitbank (Apache 2.0)

**できること**
・バックテスト: bitbank の実ローソク足(公開API・キー不要で取得)+ bitbank の手数料モデルで検証。JPY建て口座に対応
・ライブ: 現物の成行/指値/post_only、信用取引(2倍レバ・ロング/ショート、position_side 自動判定)、WebSocketリアルタイムフィード
・対応ペア: JPY建て44ペア
・依存パッケージゼロ(Socket.IO / PubNub も必要最小を自前実装)

**対応範囲と制限**
・対応する lean CLI コマンドは `lean backtest` / `lean live deploy` / `lean research`(research は専用イメージを別途ビルド)
・**`lean report` / `lean optimize` と QuantConnect クラウド(`lean cloud` 系)は非対応**です。LEAN の仕様上、本体を改変しない方針では届かない箇所があるためで、理由は docs に書いてあります
・Windows / macOS / Linux(Apple Silicon 対応)、ローカルの Docker で動かす構成です

**セットアップ**

具体的手順はリポジトリのドキュメント([docs/LEAN-CLI.md](https://github.com/aobathree/Lean.Brokerages.Bitbank/blob/main/docs/LEAN-CLI.md))を参照ください。

**そのほか**

サンプル(BTC/JPY ゴールデンクロス)同梱、`dotnet test` 48本、実口座での発注→取消 E2E も確認済みです。bitbank にはテストネットが無いので、最小ロットで疎通確認するツール(OrderSmokeTest 等)も入れてあります。

⚠️ APIキーは出金権限オフ推奨・環境変数注入(1Password/SSM の手順あり)。自動売買は自己責任で、必ず少額からどうぞ。

質問・不具合報告は GitHub Issues かこのスレッドへ 🙏
