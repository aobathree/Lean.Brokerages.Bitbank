# Lean CLI で使う(カスタムイメージ)

[Lean CLI](https://www.lean.io/docs/v2/lean-cli/key-concepts/getting-started)(`lean` コマンド)の通常ワークフロー — `lean backtest` / `lean live deploy` — を、bitbank プラグイン入りのカスタム Docker イメージで使うための手順。一度セットアップすれば、**普段どおり `lean` と打つだけ**で bitbank 対応の LEAN が動きます。

README の[クイックスタート](../README.md#クイックスタート公式-lean-イメージでバックテスト)は raw `docker run` で完結する最小構成です。プロジェクト管理・バックテスト結果の保存・ライブ運用まで含めた日常の開発には、この Lean CLI 構成を推奨します。

> **注意: ローカル実行専用です。** 本手順で使えるのは lean CLI の**ローカル実行系コマンド**(`lean backtest` / `lean live deploy` — ローカルの Docker でコンテナが動く)だけです。QuantConnect クラウドで実行する `lean cloud push` / `lean cloud backtest` / `lean cloud live` は、QC 側のサーバーに本プラグイン(DLL・データ定義)を持ち込めないため**動きません**。`lean login`(QuantConnect アカウント)はワークスペース作成とサンプルデータ取得のために使うだけで、実行はすべてローカルで完結します。

## 仕組み(前提知識)

- Lean CLI はコンテナ起動時に entrypoint を自前のスクリプトで上書きし、イメージ内 `/Lean/Launcher/bin/Debug` のバイナリで LEAN を実行する。カスタムイメージの要件は「このパスに必要な DLL があること」だけなので、**公式イメージにプラグイン DLL を 1 個足せばよい**(LEAN 本体の改変・再ビルドは不要)
- C# アルゴリズムはコンテナ内でビルドされ、CLI が csproj の NuGet 参照を `/Lean/Launcher/bin/Debug/*.dll` への参照に書き換える。プラグイン DLL も含まれるため `BitbankOrderProperties` 等がそのまま使える(ローカル IDE では赤線が出るがコンテナ内ビルドは通る)。Python は `AddReference("QuantConnect.BitbankBrokerage")` で同じ DLL をロードする
- CLI はワークスペースの `data/` を `/Lean/Data` にマウントする。そのため bitbank のデータ定義(symbol-properties / market-hours)は**イメージ内だけでなくワークスペースにも**必要(手順 4)
- API キーはイメージにも設定ファイルにも置かず、`--extra-docker-config` でコンテナ環境変数として注入する(プラグインの Factory に環境変数 `BITBANK_API_KEY` / `BITBANK_API_SECRET` フォールバックあり)

## このフォルダ(deploy/lean-cli)の内容

| ファイル | 用途 |
|---|---|
| `Dockerfile.cli` | カスタムイメージ `lean-bitbank:cli` のビルド定義(ベース: `quantconnect/lean:latest`) |
| `Dockerfile.cli.dockerignore` | ビルドコンテキスト絞り込み |
| `patch-lean-cli-modules.py` | lean CLI のモジュール定義に `BitbankBrokerage` を追加するパッチ(落とし穴 2) |
| `run-live.ps1` | Windows 用ライブ起動スクリプト(PowerShell 5.1 の引用符問題対策込み) |

## 手順

### 0. 前提ソフトウェア

- **Docker**(Windows は Docker Desktop + WSL2)
- **.NET SDK 10**(`dotnet --list-sdks` で 10.x を確認)
- **Python 3.8+ と lean CLI**: `pip install lean`(または `pipx install lean`)
- **QuantConnect アカウント**(無料。`lean login` で使用)
- ライブのみ: bitbank API キーと、安全に注入するための **1Password + op CLI**(推奨。[SETUP.md](SETUP.md) 参照)

### 1. clone・プラグインビルド・日足データ取得・イメージビルド

```bash
git clone https://github.com/aobathree/Lean.Brokerages.Bitbank.git
cd Lean.Brokerages.Bitbank
dotnet build QuantConnect.BitbankBrokerage
dotnet run --project QuantConnect.BitbankBrokerage/tools/CandleDownloader -- --from 2018
docker build -f deploy/lean-cli/Dockerfile.cli -t lean-bitbank:cli .
```

(PowerShell も同じコマンドで可。パス区切りはそのままで動きます)

- **`CandleDownloader` は省略できません。** 日足 zip は `Data/crypto/` が `.gitignore`
  されているためリポジトリに含まれず、クローン直後は存在しません。先に取得しないと
  `docker build` が
  `cp: cannot stat '/tmp/bitbank/Data/crypto/bitbank/daily': No such file or directory`
  で失敗します(公開 API なので API キーは不要)
- ベースの `quantconnect/lean:latest` は初回 pull に時間がかかる(圧縮で数 GB、展開後 40GB 超)
- イメージには、プラグイン DLL の配置に加えて symbol-properties / market-hours への bitbank 行マージと、上で取得した日足データの配置まで焼き込まれる

### 2. lean CLI ワークスペース作成

リポジトリの**外**の任意の場所に作ります:

```bash
mkdir -p ~/bitbank/lean-cli && cd ~/bitbank/lean-cli
lean init --language python
```

(PowerShell: `mkdir $HOME\bitbank\lean-cli; cd $HOME\bitbank\lean-cli` 以下同じ。認証を求められたら `lean login`)

### 3. CLI 設定: カスタムイメージ + データ自動更新の無効化

```bash
lean config set engine-image lean-bitbank:cli
lean config set database-update-frequency "-"
```

- `engine-image` はユーザー単位のグローバル設定。以後どのワークスペースでも `lean` と打つだけでカスタムイメージが使われる(コマンド毎の `--image lean-bitbank:cli` でも可)
- `database-update-frequency "-"` を**先に**設定しないと、CLI が 1 日 1 回 upstream からデータ定義をダウンロードして手順 4 の bitbank 定義を消す(落とし穴 1)
- カスタムイメージ使用時に表示される pull 警告は無視してよい(`--no-update` で抑制可)

### 4. ワークスペースへ bitbank データを配置

イメージに焼き込み済みのマージ結果と日足データを、ワークスペースの `data/` へコピーします。

bash:

```bash
cd ~/bitbank/lean-cli
docker run --rm --entrypoint /bin/sh -v "$PWD/data:/out" lean-bitbank:cli -c \
  'cp /Lean/Data/symbol-properties/symbol-properties-database.csv /out/symbol-properties/ &&
   cp /Lean/Data/market-hours/market-hours-database.json /out/market-hours/ &&
   mkdir -p /out/crypto/bitbank && cp -r /Lean/Data/crypto/bitbank/daily /out/crypto/bitbank/'
```

PowerShell:

```powershell
cd $HOME\bitbank\lean-cli
docker run --rm --entrypoint /bin/sh -v "${PWD}\data:/out" lean-bitbank:cli -c 'cp /Lean/Data/symbol-properties/symbol-properties-database.csv /out/symbol-properties/ && cp /Lean/Data/market-hours/market-hours-database.json /out/market-hours/ && mkdir -p /out/crypto/bitbank && cp -r /Lean/Data/crypto/bitbank/daily /out/crypto/bitbank/'
```

日足データの銘柄追加・更新はリポジトリ側で `CandleDownloader` を実行して再コピー(公開 API、キー不要):

```bash
dotnet run --project QuantConnect.BitbankBrokerage/tools/CandleDownloader -- --from 2018
```

### 5. lean CLI のモジュール定義パッチ(ライブ運用する場合のみ必須)

`lean live deploy` はブローカレッジ名を CLI 内蔵のモジュール一覧から解決するため、パッチなしだと `BitbankBrokerage` が解決できず失敗します(落とし穴 2)。**lean CLI が入っている Python** で実行:

```bash
python deploy/lean-cli/patch-lean-cli-modules.py
```

- pipx の場合(Windows 例): `& "$(pipx environment --value PIPX_LOCAL_VENVS)\lean\Scripts\python.exe" deploy\lean-cli\patch-lean-cli-modules.py`
- パッチはモジュール定義ファイルを読み取り専用にして CDN の日次上書きも防ぐ。**lean CLI をアップグレードしたら再実行**

### 6. バックテストで動作確認

サンプルアルゴリズム(BTC/JPY ゴールデンクロス)をプロジェクト化して実行します。プロジェクト名はクラス名と一致させる必要があります:

```bash
cd ~/bitbank/lean-cli
lean project-create --language python "BitbankSmaCrossExample"
cp <リポジトリ>/examples/bitbank_sma_cross.py BitbankSmaCrossExample/main.py
lean backtest "BitbankSmaCrossExample"
```

(PowerShell: `Copy-Item <リポジトリ>\examples\bitbank_sma_cross.py .\BitbankSmaCrossExample\main.py`)

`STATISTICS::` ブロック(Total Orders / Net Profit / Total Fees ¥...)が出れば成功。ここまでで バックテスト環境は完成です。

#### `Failed data requests` に quote が並ぶのは正常

末尾の `DATA USAGE::` に次のような数字が出ますが、異常ではありません。

```
DATA USAGE:: Failed data requests 5
DATA USAGE:: Failed data requests percentage 62%
```

失敗しているのは `crypto/bitbank/daily/btcjpy_quote.zip` です(バックテスト出力の
`failed-data-requests-*.txt` で確認できます)。LEAN は Crypto に対して TradeBar と
QuoteBar の**両方**を購読しますが、bitbank の公開 API はローソク足(OHLCV)しか
返さず板の履歴を提供しないため、quote ファイルは存在しません。

影響はありません。ログに `ERROR` は出ず、LEAN は quote が無ければ trade の価格を
使います。売買結果も統計も変わりません。

**合成データで埋めないこと。** 他取引所(binance 等)の価格に為替レートを掛けて
quote を作る案は成立しません。借りてくる側も `_trade` で板情報を持たず、結局
スプレッドを発明することになります。取引所間の価格差(日本の取引所は数%乖離する)
も掛け算では埋まりません。指標の見栄えのためにバックテストの土台を崩す取引です。

スプレッドやスリッページをバックテストに織り込みたい場合は、`BitbankFeeModel` と
LEAN のスリッページモデルで表現してください。板データを持たない取引所を扱う
プラグインとしては、そちらが正しい設計です。

### 7. ライブ環境の設定

ワークスペースの `lean.json` の `"environments": {` 直下に以下を挿入:

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
},
```

`bitbank-api-key` / `bitbank-api-secret` は**書かない**(環境変数フォールバックを使う)。信用取引を使う場合は lean.json のトップレベルに `"bitbank-account-type": "margin"` を追加(既定は現物)。

キーなしでの経路確認(実注文なし):

```bash
lean live deploy "<プロジェクト名>" --environment live-bitbank
```

`bitbank API error 20003 (HTTP 200): API key not found` で停止すれば、キー以外の全経路が正常です。

### 8. ライブ起動(実注文が発生し得ます)

API キーは 1Password の op:// 参照で注入します([SETUP.md](SETUP.md) §2 の手順でアイテムを作成し、`.env.1password` を用意)。

Windows:

```powershell
op run --env-file="<.env.1password のパス>" -- pwsh -NoProfile -File <リポジトリ>\deploy\lean-cli\run-live.ps1 -Project "<プロジェクト名>"
```

(pwsh 7 が無ければ `powershell -NoProfile -File ...` でも可。スクリプトが 5.1 の引用符問題を自動処理します)

macOS / Linux:

```bash
op run --env-file=<.env.1password のパス> -- sh -c \
  'lean live deploy "<プロジェクト名>" --environment live-bitbank \
   --extra-docker-config "{\"environment\": {\"BITBANK_API_KEY\": \"$BITBANK_API_KEY\", \"BITBANK_API_SECRET\": \"$BITBANK_API_SECRET\"}}"'
```

- 常駐運用は `run-live.ps1 -Detach`(または `--detach`)。ログ確認は `lean logs --live`、停止は `lean live stop "<プロジェクト名>"`
- 停止してもオープン注文・建玉は bitbank 側にそのまま残ります
- 初回は `tools/OrderSmokeTest`([SETUP.md](SETUP.md) §6)で疎通確認してからのライブ投入を推奨

## 落とし穴まとめ

1. **CLI のデータベース自動更新**: 既定で 1 日 1 回、upstream から market-hours / symbol-properties をダウンロードして workspace の `data/` を上書き → bitbank 定義が消え「Unable to locate exchange hours for Crypto-bitbank-BTCJPY」で落ちる。`lean config set database-update-frequency "-"` で無効化(手順 3)
2. **モジュール解決**: `--environment` 指定でもブローカレッジ名は CLI 内蔵モジュール一覧(`site-packages/lean/modules-*.json`、CDN から日次上書き)から解決される。未知の名前だと `argument of type 'bool' is not a container or iterable` 等で落ちる → `patch-lean-cli-modules.py` で定義追加 + read-only 化(手順 5)
3. **IDE の赤線(C#)**: ローカル補完は NuGet 版 LEAN 参照のためプラグイン型にエラー表示が出るが、コンテナ内ビルドでは csproj がイメージ内 DLL 参照に書き換えられるので問題ない。Python は `AddReference("QuantConnect.BitbankBrokerage")` を main.py 冒頭に書く(examples 参照)
4. **コンソール表示の途切れ**: `lean backtest` / `lean live deploy` のコンソール出力は途中で途切れることがあるが、実行自体は継続している。結果・合否は `<プロジェクト>/backtests/<日時>/log.txt`(ライブは `live/<日時>/log.txt`)で確認する
5. **PowerShell の引用符**: PowerShell 5.1 / pwsh 7.2 以前は native コマンド引数内の `"` をエスケープしないため、`--extra-docker-config` の JSON が「Expecting property name enclosed in double quotes」で壊れる。`run-live.ps1` はバージョン判定して自動対処済み。手打ちする場合は pwsh 7.3+ を使うこと
6. **アーキテクチャ**: `quantconnect/lean:latest` はマルチアーキ(Apple Silicon = arm64 / x86_64 = amd64)。プラグイン DLL は IL なのでどちらでも動く
7. **プロジェクト名 = クラス名**: `lean project-create` したプロジェクトは、main.py / Main.cs のアルゴリズムクラス名がプロジェクト名と一致している必要がある

## 更新時の運用

- **プラグイン更新時**: `git pull` → `dotnet build QuantConnect.BitbankBrokerage` → `docker build ...`(手順 1)→ データ定義が変わった場合は手順 4 を再実行
- **lean CLI アップグレード時**: 手順 5 のパッチを再実行
