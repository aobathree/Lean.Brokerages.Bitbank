#!/usr/bin/env python3
"""lean init 直後のワークスペースを bitbank 用に仕立てる。

docs/LEAN-CLI.md の手順 3・4・5・7 を 1 コマンドにまとめたもの:

  3. database-update-frequency の無効化(と engine-image の設定)
  4. イメージから symbol-properties / market-hours / 日足データをワークスペースへ
  5. lean CLI のモジュール定義に BitbankBrokerage を追加
  7. lean.json の environments に live-bitbank を挿入

これらは lean init では絶対に作られない。lean init は QuantConnect/Lean 本家の
master.zip を落として Launcher/config.json をそのまま写すだけで(URL は
lean/commands/init.py にハードコード)、本家に無いブローカレッジは環境定義にも
モジュール一覧にも現れないため。カスタムイメージを作っても変わらない
(lean init は Docker を起動しない)。

使い方:

  cd <lean CLI ワークスペース>          # lean.json のあるディレクトリ
  lean init --language python
  python <このリポジトリ>/deploy/lean-cli/setup-workspace.py

**lean CLI が入っている Python で実行すること**(手順 5 が lean の
site-packages を書き換えるため)。冪等なので何度実行してもよい。
"""
import argparse
import json
import shutil
import subprocess
import sys
from pathlib import Path

IMAGE = "lean-bitbank:cli"

# lean.json の "environments": { 直下に挿入する。docs/LEAN-CLI.md 手順 7 と同一。
# bitbank-api-key / bitbank-api-secret は書かない(環境変数フォールバックを使う)。
LIVE_ENVIRONMENT = """\
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
"""

# イメージ内の Data から、ワークスペースの data/ が必要とするものを取り出す。
# CLI はワークスペースの data/ を /Lean/Data にマウントで「被せる」ため、
# イメージ側に定義があってもワークスペースに無いと
# "Unable to locate exchange hours for Crypto-bitbank-BTCJPY" で落ちる。
COPY_SCRIPT = (
    "cp /Lean/Data/symbol-properties/symbol-properties-database.csv /out/symbol-properties/ && "
    "cp /Lean/Data/market-hours/market-hours-database.json /out/market-hours/ && "
    "mkdir -p /out/crypto/bitbank && cp -r /Lean/Data/crypto/bitbank/daily /out/crypto/bitbank/"
)


def run(cmd, **kwargs):
    """コマンドを実行して CompletedProcess を返す。失敗しても例外にしない"""
    return subprocess.run(cmd, capture_output=True, text=True, **kwargs)


def lean_config_get(key):
    """lean config get の値。未設定なら None(未設定時は exit code 1 になる)"""
    result = run([shutil.which("lean") or "lean", "config", "get", key])
    return result.stdout.strip() if result.returncode == 0 else None


def lean_config_set(key, value):
    result = run([shutil.which("lean") or "lean", "config", "set", key, value])
    if result.returncode != 0:
        sys.exit(f"ERROR: lean config set {key} に失敗しました\n{result.stderr}")


def configure_cli():
    """手順 3。データ自動更新の無効化と engine-image の設定"""
    # 無効化しないと CLI が 1 日 1 回 upstream からデータ定義を落として
    # 手順 4 で入れた bitbank 定義を消す(落とし穴 1)
    freq = lean_config_get("database-update-frequency")
    if freq == "-":
        print("config: database-update-frequency は既に無効です")
    else:
        lean_config_set("database-update-frequency", "-")
        print("config: database-update-frequency を無効にしました(upstream の上書きを防ぐ)")

    # engine-image はユーザー単位のグローバル設定。他のカスタムイメージを使って
    # いる人の設定を黙って奪わないよう、別の値が入っていたら触らず案内だけ出す
    current = lean_config_get("engine-image")
    if current == IMAGE:
        print(f"config: engine-image は既に {IMAGE} です")
    elif current is None:
        lean_config_set("engine-image", IMAGE)
        print(f"config: engine-image を {IMAGE} にしました")
    else:
        print(f"config: engine-image は {current} のままにしました(グローバル設定のため上書きしません)")
        print(f"        bitbank を既定にするなら: lean config set engine-image {IMAGE}")
        print(f"        1 回だけ使うなら:         lean backtest <project> --image {IMAGE}")


def install_data(workspace):
    """手順 4。イメージからデータ定義と日足をワークスペースへコピー"""
    data_dir = workspace / "data"
    for sub in ("symbol-properties", "market-hours"):
        if not (data_dir / sub).is_dir():
            sys.exit(f"ERROR: {data_dir / sub} がありません。先に `lean init` を実行してください")

    if run(["docker", "image", "inspect", IMAGE]).returncode != 0:
        sys.exit(f"ERROR: イメージ {IMAGE} がありません。docs/LEAN-CLI.md 手順 1 を先に実行してください")

    result = run([
        "docker", "run", "--rm", "--entrypoint", "/bin/sh",
        "-v", f"{data_dir}:/out", IMAGE, "-c", COPY_SCRIPT,
    ])
    if result.returncode != 0:
        sys.exit(f"ERROR: データのコピーに失敗しました\n{result.stderr}")

    daily = data_dir / "crypto" / "bitbank" / "daily"
    count = len(list(daily.glob("*.zip"))) if daily.is_dir() else 0
    print(f"data: 定義をマージし、日足 {count} ペアを配置しました")


def patch_modules():
    """手順 5。lean CLI のモジュール一覧に BitbankBrokerage を追加"""
    script = Path(__file__).with_name("patch-lean-cli-modules.py")
    if not script.is_file():
        print(f"WARNING: {script.name} が見つかりません。ライブ運用するなら手動で実行してください")
        return
    # 自分と同じインタプリタで実行する。lean CLI の site-packages を書き換えるため、
    # 別の Python だと違う lean を書き換えてしまう
    result = run([sys.executable, str(script)])
    print(result.stdout.strip() or result.stderr.strip())
    if result.returncode != 0:
        print("WARNING: モジュールパッチに失敗しました。ライブ運用時に `lean live deploy` が失敗します")


def patch_lean_config(workspace, account_type):
    """手順 7。environments に live-bitbank を挿入し、口座種別を書く"""
    path = workspace / "lean.json"
    if not path.is_file():
        sys.exit(f"ERROR: {path} がありません。先に `lean init` を実行してください")

    lines = path.read_text(encoding="utf-8").splitlines(keepends=True)

    if any('"live-bitbank"' in line for line in lines):
        print("lean.json: live-bitbank は既にあります(再生成したい場合は手で削除してから実行)")
    else:
        index = next((i for i, line in enumerate(lines)
                      if line.lstrip().startswith('"environments"')), None)
        if index is None:
            sys.exit("ERROR: lean.json に environments が見つかりません")
        lines.insert(index + 1, LIVE_ENVIRONMENT)
        print("lean.json: live-bitbank を environments へ挿入しました")

    # 既存行があれば値だけ差し替える(書式を保つため、丸ごと再シリアライズはしない)
    key = "bitbank-account-type"
    entry = f'    "{key}": "{account_type}",\n'
    index = next((i for i, line in enumerate(lines)
                  if line.lstrip().startswith(f'"{key}"')), None)
    if index is None:
        lines.insert(1, entry)      # 先頭の { の直後
        action = "追加"
    else:
        lines[index] = entry
        action = "更新"

    path.write_text("".join(lines), encoding="utf-8")
    print(f"lean.json: {key}={account_type} ({action})")
    if account_type == "cash":
        print("        → 現物モードです。信用取引をするなら --account-type margin を付けて実行し直してください")


def main():
    parser = argparse.ArgumentParser(description="lean CLI ワークスペースを bitbank 用に設定する")
    parser.add_argument("--workspace", type=Path, default=Path.cwd(),
                        help="lean.json のあるディレクトリ(既定: カレントディレクトリ)")
    parser.add_argument("--account-type", choices=["cash", "margin"], default="cash",
                        help="現物 or 信用。信用取引をするときだけ margin を指定する(既定: cash)")
    parser.add_argument("--skip-data", action="store_true", help="手順 4(データ配置)を飛ばす")
    parser.add_argument("--skip-modules", action="store_true", help="手順 5(モジュールパッチ)を飛ばす")
    args = parser.parse_args()

    workspace = args.workspace.resolve()
    if not (workspace / "lean.json").is_file():
        sys.exit(f"ERROR: {workspace / 'lean.json'} がありません。先に `lean init` を実行してください")

    configure_cli()
    if args.skip_data:
        print("data: スキップしました (--skip-data)")
    else:
        install_data(workspace)
    if args.skip_modules:
        print("modules: スキップしました (--skip-modules)")
    else:
        patch_modules()
    patch_lean_config(workspace, args.account_type)

    print()
    print("完了。次の確認:")
    print("  lean project-create --language python BitbankSmaCrossExample")
    print("  # examples/bitbank_sma_cross.py を BitbankSmaCrossExample/main.py へコピー")
    print("  lean backtest BitbankSmaCrossExample")
    print()
    print("ライブは API キーが要ります。docs/LEAN-CLI.md 手順 8 と SETUP.md を参照してください。")


if __name__ == "__main__":
    main()
