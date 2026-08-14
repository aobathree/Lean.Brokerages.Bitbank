# 既知の制限と将来対応すべき事項

2026-08-14 の macOS(Apple Silicon / lean CLI 1.0.227 / Docker Desktop)での
手順検証で確定した事実と、そこから導かれる対応事項の記録。憶測と実測を区別して書く。

## lean CLI コマンド対応状況(実測)

`lean config set engine-image lean-bitbank:cli` 済みのワークスペースでの結果。

| コマンド | 結果 | 原因 |
|---|---|---|
| `lean backtest` | ✅ 動く | 基準値どおり(19 注文 / Net Profit 63.627% / OrderListHash 一致) |
| `lean live deploy` | ✅ 動く | 2026-08-10 に Windows で実機確認済み |
| `lean report` | ❌ 失敗 | DLL が `/Lean/Report/bin/Debug` に無い(下記 1) |
| `lean research` | ❌ bitbank 利用不可 | そもそも別イメージ `quantconnect/research:latest` を使う(下記 2) |
| `lean optimize` | ❌ 失敗 | **bitbank 無関係**。Apple Silicon 上の stock LEAN の一般問題(下記 3) |

背景: 公式イメージには用途別の bin ディレクトリが 3 つある(`Launcher` / `Report` /
`Optimizer.Launcher`)。`Dockerfile.cli` は `/Lean/Launcher/bin/Debug/` にしか DLL を
置いていないため、Launcher 以外の working_dir で動くコマンドではプラグインの
`[ModuleInitializer]` が走らず、market `bitbank`(= 44)が未登録のままになる。

lean CLI 側の根拠(1.0.227 のソース):

- `commands/report.py:211` — `working_dir = "/Lean/Report/bin/Debug"`
- `commands/optimize.py:336` — `working_dir = "/Lean/Optimizer.Launcher/bin/Debug"`
- `commands/research.py` — `research-image`(既定 `quantconnect/research:latest`)を使用。
  `engine-image` 設定は効かない(`constants.py:69`, `cli_config_manager.py:49,54`)

## 1. `lean report` 対応(修正は容易な見込み)

失敗の実エラー:

```
SecurityIdentifier.TryParseProperties(): Error parsing SecurityIdentifier: 'BTCJPY 3EF',
Exception: System.ArgumentOutOfRangeException: The specified market wasn't found in the
markets lookup. Requested: 44. You can add markets by calling QuantConnect.Market.Add(string,int)
```

バックテスト結果 JSON 内の注文の SecurityIdentifier をデコードする段階で market 44 を
解決できず異常終了する。

**対応案(未検証)**: `Dockerfile.cli` に COPY を 1 行足す。

```dockerfile
COPY QuantConnect.BitbankBrokerage/bin/Debug/net10.0/QuantConnect.BitbankBrokerage.dll /Lean/Report/bin/Debug/
```

Report の Composer が working_dir の DLL を拾って `[ModuleInitializer]` が走れば直る
見込みだが、**再ビルド後に `lean report` を実行して確認するまで確定ではない**。

## 2. `lean research` 対応(「DLL 1 個」では収まらない)

`lean research` は `engine-image` ではなく `research-image` を使う仕様のため、
現在のカスタムイメージ(`quantconnect/lean:latest` ベース)は経路上に存在しない。

実測(`lean research` が起動した実コンテナ内で、notebook と同じ初期化後に確認):

- Jupyter Lab 自体は正常に起動する
- `AddReference("QuantConnect.BitbankBrokerage")` → `FileNotFoundException`
- `SecurityIdentifier.Parse("BTCJPY 3EF")` → market 44 未登録エラー

つまり research 環境では bitbank のシンボルに一切触れない。

**対応案(未検証)**: 2 枚目のカスタムイメージを作る。research イメージも
`/Lean/{Launcher,Report,Optimizer.Launcher}` の同一レイアウトを持つことは確認済みなので、
`FROM quantconnect/research:latest` + 既存と同じ COPY 群で成立する見込み。利用者には
`lean config set research-image lean-bitbank:research` を案内する。

**留意**: 「公式 Docker イメージに DLL を 1 個足すだけ」という売り文句は
backtest / live deploy の範囲では正確だが、research まで含めると成り立たない。
対応するかどうかに関わらず、告知・README で対応コマンドの範囲を明示すること(下記 4)。

## 3. `lean optimize` は Apple Silicon では bitbank 以前の問題

実エラー:

```
rosetta error: failed to open elf at /lib64/ld-linux-x86-64.so.2
```

最適化器が起動する子バックテストプロセスが x86_64 バイナリとして exec され、arm64
コンテナ内で即死する。**bitbank と無関係のデフォルトプロジェクト(SPY)でも同一の失敗を
確認済み**で、stock LEAN + Apple Silicon の一般問題。

さらに悪いことに、全子プロセスが死んでも lean CLI は `Successfully optimized` と表示して
正常終了する(ログには `Result was not reached` / `Got null/empty backtest result`)。

**対応事項**:

- amd64(Windows / Intel Linux)では子プロセスが起動するため、その先で
  `/Lean/Optimizer.Launcher/bin/Debug` に DLL が無いことが問題になるかは**未検証**。
  Windows 機で `lean optimize` を実行して確認する
- 予防的に `Dockerfile.cli` へ `/Lean/Optimizer.Launcher/bin/Debug/` への COPY も
  足しておくのは低コスト(Report と同時に)
- arm64 の rosetta 問題は本リポジトリでは直せない。upstream(QuantConnect/Lean)の
  既知 issue か確認し、docs に「Apple Silicon では optimize 不可(LEAN 側の制約)」と
  記載する

## 4. announcement.md の含意の修正(投稿済み文面の訂正)

検証の結果、**明示的な虚偽は無い**。特に「これで以後は `lean backtest` /
`lean live deploy` と打つだけです」(38 行目)は対応コマンドを 2 つに限定しており正確。

問題は 47 行目の「⚠️ ローカル実行専用です(…)QuantConnect クラウドでは動きません」が
**唯一の制限として提示されている**こと。読み手は「ローカルなら lean CLI のコマンドは
一通り使える」と受け取るが、実際には `lean research` はローカルコマンドなのに使えない。
嘘ではなく、誤った含意。

**対応**: 47 行目の直後に対応範囲を 1 行追記する(全面書き直しは不要)。文面は
上記 1〜3 の対応がどこまで済んだかで変わるため、**修正を先に済ませてから確定させる**。

暫定文案(report / optimize の Dockerfile 修正が済んだ場合):

```
・lean CLI のローカルコマンドのうち対応は backtest / live deploy / report です。research は
  別イメージ(research-image)を使う仕様のため非対応、optimize は Apple Silicon では LEAN 側の
  制約で動きません(Windows / Intel は検証中)
```

Discord 投稿済みの文面も同趣旨で追記編集する。

## 5. docs/LEAN-CLI.md への追記候補

- **手順 2 の詰まりポイント**: 保存済み認証が失効していると `lean init` が
  `Error: Credentials are invalid` / `lean whoami` が `Hash doesn't match UID` で止まる。
  `lean login` のやり直しで解消(トークンは quantconnect.com の Settings → API Access)。
  現状 docs に記載が無い
- 手順 5(モジュール定義パッチ)は今回の macOS 検証では実行できておらず**未検証**。
  ライブ運用の検証を macOS で行う際に合わせて確認する

## 検証環境の記録

- macOS(Apple Silicon)、Docker Desktop、lean CLI 1.0.227(pipx)
- ワークスペース: `~/bitbank/lean-cli-maccheck`(検証用に新規作成。既存の
  `~/bitbank/lean-cli` とは別)
- イメージ: `lean-bitbank:cli`(2026-08-14 再ビルド、DLL 71,168 バイト)
- 基準値: 19 注文 / Net Profit 63.627% / OrderListHash `6cd85622f2c63806941f2196b5511fca`
