#!/usr/bin/env python3
"""lean CLI のモジュール定義に BitbankBrokerage を追加する。

lean CLI は `lean live deploy --environment ...` でもブローカレッジ名を
既知モジュール一覧(site-packages/lean/modules-*.json、CDN から日次更新)から
解決するため、カスタムブローカレッジは定義を追加しないと
"Failed to resolve module" (または解決フォールバック中の
"argument of type 'bool' is not a container or iterable")で落ちる。

このスクリプトは:
1. インストール済み lean CLI の modules-*.json を見つけて BitbankBrokerage を挿入
2. CDN の日次ダウンロードによる上書きを防ぐためファイルを読み取り専用にする
   (lean/models/__init__.py は書き込み失敗を握りつぶして既存ファイルを使う)

lean CLI が入っている Python で実行すること(idempotent)。
lean CLI をアップグレードしたら再実行すること。
"""
import json
import stat
import sys
from glob import glob
from pathlib import Path

BITBANK_MODULE = {
    "configurations": [
        {
            "filters": [{"condition": {
                "dependent-config-id": "type",
                "pattern": "brokerage",
                "type": "exact-match"}}],
            "id": "live-mode-brokerage",
            "type": "info",
            "value": "BitbankBrokerage",
        },
        {
            "filters": [{"condition": {
                "dependent-config-id": "type",
                "pattern": "data-queue-handler",
                "type": "exact-match"}}],
            "id": "data-queue-handler",
            "type": "info",
            "value": "[\"BitbankBrokerage\"]",
        },
        {
            "filters": [{"condition": {
                "dependent-config-id": "type",
                "pattern": "history-provider",
                "type": "exact-match"}}],
            "id": "history-provider",
            "type": "info",
            "value": "[\"BrokerageHistoryProvider\"]",
        },
    ],
    "display-id": "bitbank",
    "id": "BitbankBrokerage",
    # API キーの configurations は意図的に定義しない:
    # BitbankBrokerageFactory が環境変数 BITBANK_API_KEY/SECRET に
    # フォールバックするので、CLI にプロンプトさせる必要がない
    "live-cash-balance-state": "not-supported",
    "live-holdings-state": "not-supported",
    "platform": ["local", "cli"],
    "type": ["brokerage", "data-queue-handler", "history-provider"],
}


def find_modules_file() -> Path:
    import lean
    candidates = glob(str(Path(lean.__file__).parent / "modules-*.json"))
    if not candidates:
        sys.exit("modules-*.json が見つからない (lean CLI は導入済みか?)")
    return Path(sorted(candidates)[-1])


def main() -> None:
    path = find_modules_file()
    path.chmod(path.stat().st_mode | stat.S_IWUSR)
    data = json.loads(path.read_text(encoding="utf-8"))
    modules = data["modules"]

    modules[:] = [m for m in modules if m.get("id") != "BitbankBrokerage"]
    modules.append(BITBANK_MODULE)

    path.write_text(json.dumps(data, ensure_ascii=False, indent=4), encoding="utf-8")
    path.chmod(stat.S_IRUSR | stat.S_IRGRP | stat.S_IROTH)
    print(f"patched: {path} (read-only にして CDN 上書きを防止)")


if __name__ == "__main__":
    main()
