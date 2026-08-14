# 既知の制限と将来対応すべき事項

2026-08-14 の macOS(Apple Silicon / lean CLI 1.0.227 / Docker Desktop)での
手順検証で確定した事実と、そこから導かれる対応事項の記録。憶測と実測を区別して書く。

## lean CLI コマンド対応状況(実測)

`lean config set engine-image lean-bitbank:cli` 済みのワークスペースでの結果。

| コマンド | 結果 | 原因 |
|---|---|---|
| `lean backtest` | ✅ 動く | 基準値どおり(19 注文 / Net Profit 63.627% / OrderListHash 一致) |
| `lean live deploy` | ✅ 動く | 2026-08-10 に Windows で実機確認済み |
| `lean report` | ❌ **恒久的に不可**(stock LEAN) | DLL 追加でも直らないことを Windows で実測(下記 1) |
| `lean research` | ✅ **対応済み** | 2 枚目のイメージ `lean-bitbank:research` で成立を実測(下記 2) |
| `lean optimize` | ❌ **恒久的に不可**(stock LEAN) | 親プロセスの SID パースで死ぬことを Windows amd64 で実測(下記 3) |

背景(2026-08-14 Windows 実測で確定した一般則): `[ModuleInitializer]` は
「モジュール内の型への**最初のアクセス**」でしか走らない。DLL がロードされるだけでは
発火しない(Composer の `Assembly.Load` + リフレクション列挙もメンバアクセスではない)。
したがって

- **アルゴリズム/ノートブックのコードを実行する経路**(backtest 子プロセス / live /
  research)は、ユーザーコードがプラグインの型に触れるので動く
- **結果を読むだけの経路**(report / optimize の親プロセス)は型に一切触れないため
  発火せず、market 44 を解決できない。**DLL をどの bin ディレクトリに置いても直らない**。
  解は `Common/Market.cs` への焼き込み(= LEAN フォーク)のみで、本リポジトリの方針外

同一機構は kabuSTATION トラック(market 45)でも 2026-08-14 に確認済み。

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

**対応案は不成立と実測で確定(2026-08-14 Windows)**。`lean-bitbank:cli` に
`COPY ... /Lean/Report/bin/Debug/` を足したテストイメージで `lean report` を実行した
ところ、Composer は DLL をロードする(Skipping トレース無し)が `[ModuleInitializer]` は
発火せず、まったく同じ market 44 エラーで abort した。Report プロセスはプラグインの
型に触れないため、DLL の配置場所をどう変えても直らない。

**stock LEAN では対応不可として docs に明記する**(結論)。

参考(開発者自身の回避策): `aobathree/Lean` の `jp-lean-patches` ブランチは
`Common/Market.cs` に bitbank 44 / kabustation 45 を焼き込んでおり、そこからビルドした
エンジンなら DLL 無しでレポートが生成できる。bitbank のバックテストに対して
`lean report --image lean-cli/engine:jpfork` で exit 0 / report.html 969.9 KB を実測済み。
コミュニティには案内しない(フォーク前提になり本リポジトリの売りと矛盾するため)。

## 2. `lean research` 対応(「DLL 1 個」では収まらない)

`lean research` は `engine-image` ではなく `research-image` を使う仕様のため、
現在のカスタムイメージ(`quantconnect/lean:latest` ベース)は経路上に存在しない。

実測(`lean research` が起動した実コンテナ内で、notebook と同じ初期化後に確認):

- Jupyter Lab 自体は正常に起動する
- `AddReference("QuantConnect.BitbankBrokerage")` → `FileNotFoundException`
- `SecurityIdentifier.Parse("BTCJPY 3EF")` → market 44 未登録エラー

つまり research 環境では bitbank のシンボルに一切触れない。

**対応済み・成立を実測(2026-08-14 Windows)**。`deploy/lean-cli/Dockerfile.research`
(`FROM quantconnect/research:latest` + DLL 1 個の COPY)を追加し、
`lean-bitbank:research` をビルド。コンテナ内でヘッドレス検証:

```
AddReference("QuantConnect.BitbankBrokerage")
from QuantConnect.Brokerages.Bitbank import BitbankBrokerageModel   # ← これだけでは不十分
model = BitbankBrokerageModel()                                     # ← ここで発火する
Market.Encode("bitbank")            # → 44
SecurityIdentifier.Parse("BTCJPY 3EF")  # → 成功
```

**落とし穴(実測で発見)**: `AddReference` + `import` だけでは market が登録されない
(`Market.Encode("bitbank")` が None を返す)。pythonnet の import は型オブジェクトの
取得(リフレクション)であってメンバアクセスではないため。ノートブックでは
`market="bitbank"` を使う**前に** `BitbankBrokerageModel()` を一度インスタンス化する
こと。docs のノートブック例に必ずこの 1 行を入れる。

利用者への案内: `lean config set research-image lean-bitbank:research`。

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

**Windows amd64 で実測済み(2026-08-14)**: ダミーパラメータのグリッド(2 セット)で
`lean optimize --image lean-bitbank:cli` を実行。結果は

- **子バックテストは 2 本とも完走**(Sharpe 0.43 の統計まで出力)。子はアルゴリズムを
  実行するので `AddReference` 経由で market が登録される
- その直後、**親の Optimizer.Launcher が結果内の SID `'BTCJPY 3EF'` をパースする段階で
  market 44 未解決 → Unhandled exception、exit 1**

つまり amd64 では rosetta 問題の先にある本質が report と同一だと確定した。
親プロセスは型に触れないため、**`/Lean/Optimizer.Launcher/bin/Debug/` への予防的 COPY は
無意味**(report で実測済みの同一機構)。stock LEAN では対応不可として docs に明記する。

- arm64 の rosetta 問題は本リポジトリでは直せない。upstream(QuantConnect/Lean)の
  既知 issue か確認し、docs に「Apple Silicon では optimize は子プロセス起動段階で不可
  (LEAN 側の制約)」と記載する
- 参考: フォークエンジン(`jp-lean-patches`)では optimize が完走する(kabuSTATION
  トラックで 25 バックテストのスイープ実績、2026-08-14)

## 4. announcement.md の含意の修正(投稿済み文面の訂正)

検証の結果、**明示的な虚偽は無い**。特に「これで以後は `lean backtest` /
`lean live deploy` と打つだけです」(38 行目)は対応コマンドを 2 つに限定しており正確。

問題は 47 行目の「⚠️ ローカル実行専用です(…)QuantConnect クラウドでは動きません」が
**唯一の制限として提示されている**こと。読み手は「ローカルなら lean CLI のコマンドは
一通り使える」と受け取るが、実際には `lean research` はローカルコマンドなのに使えない。
嘘ではなく、誤った含意。

**対応**: 47 行目の直後に対応範囲を 1 行追記する(全面書き直しは不要)。
1〜3 の実測が済んだので文面確定(2026-08-14、announcement.md に反映済み):

```
・対応する lean CLI コマンド: lean backtest / lean live deploy / lean research(research は
  2 枚目のイメージが必要、docs 参照)。lean report / lean optimize は非対応 — LEAN が
  バックテスト結果を読み直す処理はプラグインの市場登録が届かない箇所で、本体を
  改変しない方針では原理的に埋まりません
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
