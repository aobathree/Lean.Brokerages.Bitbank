> **Note**: この文書は Lean フォーク(aobathree/Lean `jp-broker-bitbank`)での開発時に書かれたもの。Launcher の直接実行・run-e2e.sh 等の記述はフォーク環境が前提です。プラグイン単体での使い方は [README](../README.md) を参照。

# bitbank API キー設定ガイド

Lean × bitbank コネクターの API キー設定手順。**キーの実値は 1Password(ローカル)/ AWS SSM パラメータストア(AWS)にのみ置き、ファイルや config.json には書かない**。

コネクターは次の優先順位で認証情報を解決する(`BitbankBrokerageFactory.GetCredential`):

1. `config.json` の `bitbank-api-key` / `bitbank-api-secret`(通常は空のまま)
2. 環境変数 `BITBANK_API_KEY` / `BITBANK_API_SECRET` ← **こちらを使う**

---

## 1. bitbank 側: API キーの発行

1. [bitbank](https://app.bitbank.cc/) にログイン → 右上メニュー → **API** → **API キーを発行**
2. 権限は次のとおり設定する:

   | 権限 | 設定 | 理由 |
   |---|---|---|
   | 参照(残高・注文の照会) | ✅ 有効 | GetCashBalance / GetOpenOrders / private stream に必要 |
   | 取引(注文の発注・取消) | ✅ 有効 | PlaceOrder / CancelOrder に必要 |
   | **出金** | ❌ **無効** | コネクターは出金 API を一切使わない。キー漏洩時の被害を限定する |

3. **信用取引を使う場合**: bitbank 側で信用取引の利用審査を完了しておく(未完了だと発注時にエラー 50058)。API キーの「取引」権限で信用注文も発注できる。コネクター側は config `bitbank-account-type: "margin"` で有効化(README「信用取引」節参照)
4. 可能であれば **IP アドレス制限**を設定する(自宅/オフィスの固定 IP、AWS は NAT Gateway の EIP)
5. **テスト用と本番用で別のキーを発行**する(bitbank にはテストネットが無いため、結合テストも本番口座で行う。テスト用キーは事故切り分けと失効運用のために分離する)
6. 表示された API キーとシークレットをその場で 1Password に保存する(シークレットは再表示不可)

## 2. ローカル: 1Password

### 2.1 アイテムの作成

1Password アプリで:

- Vault: `Private`(任意。チーム利用なら専用 Vault を推奨)
- アイテム名: `bitbank-api-test`(本番用は `bitbank-api-prod`)
- カテゴリ: API Credential(または Login)
- フィールド:
  - `api-key` = 発行された API キー
  - `api-secret` = 発行されたシークレット(フィールド種別を「パスワード」にする)

CLI で作る場合(値はプロンプトで貼り付け):

```bash
op item create --category "API Credential" --vault Private --title bitbank-api-test \
  api-key[text]="$(read -s -p 'API Key: ' k; echo $k)" \
  api-secret[password]="$(read -s -p 'API Secret: ' s; echo $s)"
```

### 2.2 CLI 連携の有効化(初回のみ)

1Password アプリ → 設定 → **開発者** → **1Password CLI と統合** を ON(`op` コマンドの認証がアプリの生体認証経由になる)。

動作確認:

```bash
op read "op://Private/bitbank-api-test/api-key"
```

### 2.3 `.env.1password`(ローカル専用、git 管理外)

op:// 参照は実値(シークレット)ではないが、**ボールト名・アイテム ID という環境固有のメタデータを含む**ため、リポジトリにはテンプレート [env.1password.sample](../QuantConnect.BitbankBrokerage/env.1password.sample) のみをコミットする。実ファイルは各マシンでサンプルからコピーして作る(`.env*` は .gitignore 済み):

```bash
cp QuantConnect.BitbankBrokerage/env.1password.sample QuantConnect.BitbankBrokerage/.env.1password
```

作成した `.env.1password` の op:// 参照を自分の環境に合わせて編集する(アイテム ID は `op item list --vault <vault>`、フィールド ID は `op item get <item> --format json` で確認。API Credential カテゴリのフィールド ID は `username` / `credential`):

```bash
BITBANK_API_KEY="op://<vault>/<item-id>/username"
BITBANK_API_SECRET="op://<vault>/<item-id>/credential"
```

macOS / Windows など複数マシンで作業する場合は、マシンごとにこの手順で作成する(git pull では同期されない・されてはいけない)。

### 2.4 起動方法

`op run` が op:// 参照を実行時に解決し、**子プロセスの環境変数としてだけ**注入する(ディスクに書かれない):

```bash
cd Launcher/bin/Debug   # Lean リポジトリのルートから
op run --env-file=QuantConnect.BitbankBrokerage/.env.1password -- \
  dotnet QuantConnect.Lean.Launcher.dll --environment live-bitbank
```

疎通確認だけしたい場合(残高取得のワンショット):

```bash
op run --env-file=QuantConnect.BitbankBrokerage/.env.1password -- \
  dotnet run --project QuantConnect.BitbankBrokerage/tools/AssetsCheck
```

## 3. AWS: SSM パラメータストア

### 3.1 パラメータの登録

パス階層で環境を分離する(`prod` / `test`):

```bash
aws ssm put-parameter --name /lean/bitbank/prod/api-key \
  --type SecureString --key-id alias/lean-bitbank --value '<API_KEY>'

aws ssm put-parameter --name /lean/bitbank/prod/api-secret \
  --type SecureString --key-id alias/lean-bitbank --value '<API_SECRET>'
```

- `--key-id` は専用の KMS キー(`alias/lean-bitbank`)を推奨。省略時はアカウント既定の `aws/ssm` キーが使われる
- シェル履歴に値を残したくない場合は `--value file:///dev/stdin` で標準入力から渡す

### 3.2 IAM(実行ロールに最小権限)

Lean を動かす ECS タスクロール / EC2 インスタンスロールにのみ:

```json
{
  "Version": "2012-10-17",
  "Statement": [
    {
      "Effect": "Allow",
      "Action": "ssm:GetParameter",
      "Resource": "arn:aws:ssm:*:<ACCOUNT_ID>:parameter/lean/bitbank/prod/*"
    },
    {
      "Effect": "Allow",
      "Action": "kms:Decrypt",
      "Resource": "arn:aws:kms:*:<ACCOUNT_ID>:key/<KEY_ID>"
    }
  ]
}
```

### 3.3 環境変数へのマップ

**ECS(推奨)** — タスク定義の `secrets` で直接マップ(コンテナ環境変数になる):

```json
"secrets": [
  { "name": "BITBANK_API_KEY",    "valueFrom": "arn:aws:ssm:ap-northeast-1:<ACCOUNT_ID>:parameter/lean/bitbank/prod/api-key" },
  { "name": "BITBANK_API_SECRET", "valueFrom": "arn:aws:ssm:ap-northeast-1:<ACCOUNT_ID>:parameter/lean/bitbank/prod/api-secret" }
]
```

**EC2 / systemd** — 起動スクリプトで取得して export:

```bash
export BITBANK_API_KEY=$(aws ssm get-parameter --with-decryption \
  --name /lean/bitbank/prod/api-key --query Parameter.Value --output text)
export BITBANK_API_SECRET=$(aws ssm get-parameter --with-decryption \
  --name /lean/bitbank/prod/api-secret --query Parameter.Value --output text)
exec dotnet QuantConnect.Lean.Launcher.dll --environment live-bitbank
```

## 4. 運用ルール

- `config.json` の `bitbank-api-key` / `bitbank-api-secret` は**常に空のまま**にする(環境変数フォールバックが働く)
- 実値を含むファイル(`.env` など)を作った場合は必ず [.gitignore](../.gitignore) 対象にする。本リポジトリでは `.env*` を**すべて** ignore 済みで、コミット対象はテンプレート `env.1password.sample` のみ(§2.3)
- キーのローテーション: bitbank で新キー発行 → 1Password / SSM の値を差し替え → プロセス再起動 → 旧キーを bitbank 側で削除
- ログ・例外にキーが出ないことは実装側で担保済み(署名処理はヘッダー生成時のみシークレットを使用)

## 5. 動作確認チェックリスト

```bash
# 1) op が参照を解決できるか
op read "op://Private/bitbank-api-test/api-key" | head -c 8; echo "..."

# 2) 環境変数が子プロセスに渡るか
op run --env-file=QuantConnect.BitbankBrokerage/.env.1password -- printenv BITBANK_API_KEY | head -c 8; echo "..."
```

3) 残高取得(結合テスト第 1 段階)は、キー登録完了後に `/user/assets` のワンショット実行で確認する(§2.4)。

## 6. 結合テストツール

すべて `op run` 経由で実行する(§2.4 と同じ方式)。

| ツール | 内容 | リスク |
|---|---|---|
| `tools/AssetsCheck`(パスはリポジトリルートから `QuantConnect.BitbankBrokerage/tools/...`) | 残高・アクティブ注文・private stream 認証情報の取得 | なし(参照のみ) |
| `tools/StreamCheck` | PubNub プライベートストリームを購読し受信メッセージを表示(既定 60 秒) | なし(参照のみ) |
| `tools/OrderSmokeTest` | **実注文**の最小ロットライフサイクステスト: post_only 指値買い(市場価格の 50%、約定しない)→ ストリームで確認 → 即取消 | 最小(`--yes` 必須、約 500 円相当の指値が数秒間板に載る) |
| `tools/MarginSmokeTest` | **信用取引の実注文**テスト: ①position_side=long 付き指値の発注→取消(約定しない)②最小ロット(0.0001 BTC)成行でロング新規建て → `/user/margin/positions` で建玉確認 → 成行決済 → 実現損益・手数料をレポート | 小(`--yes` 必須。②は往復 taker 手数料+スプレッドで数円〜数十円。`--cancel-only` で②をスキップ可。要: 信用取引の利用審査完了) |

```bash
# ストリーム受信確認(60 秒監視。実行中に bitbank アプリで注文操作をすると spot_order イベントが流れる)
op run --env-file=QuantConnect.BitbankBrokerage/.env.1password -- dotnet run --project QuantConnect.BitbankBrokerage/tools/StreamCheck

# 注文ライフサイクル確認(実注文を伴うため --yes と Enter 確認が必要)
op run --env-file=QuantConnect.BitbankBrokerage/.env.1password -- dotnet run --project QuantConnect.BitbankBrokerage/tools/OrderSmokeTest -- --yes
```

`OrderSmokeTest` の合格条件: 発注 → `spot_order_new`(UNFILLED)受信 → 取消 → `CANCELED_UNFILLED` 受信 → REST 最終確認、の全段階が通ること。これが green なら Lean 本体(`live-bitbank` 環境)での E2E に進める。

## 7. Lean 本体 E2E テスト

Lean エンジン全体を通した最終確認。テストアルゴリズム `BitbankE2ETestAlgorithm`(`Lean/Algorithm.CSharp/`)が以下を自動実行する:

1. BTCJPY の秒足ライブデータを購読(OnData 到達をログで確認)
2. 最初の価格受信で最小ロット(0.0001 BTC)の **post_only 指値買いを市場価格の 50%** で発注(約定不可能)
3. 90 秒後に取消し、エンジン経由で Canceled が確認できたら**自動終了**(タイムアウト 300 秒)

```bash
./QuantConnect.BitbankBrokerage/run-e2e.sh
```

(内部で Launcher をビルドし、`op run` 経由で `--environment live-bitbank --algorithm-type-name BitbankE2ETestAlgorithm` を起動する。実注文を伴うため実行は手動。中断は Ctrl+C、注文が残った場合は bitbank アプリから手動取消できる)

合格条件: ログに `OnData #N` のハートビート、`E2E OnOrderEvent` の Submitted → Canceled 遷移、最後に `E2E: SUCCESS` が出て自動終了すること。これが green なら P5(24h 安定稼働試験)を残すのみ。
