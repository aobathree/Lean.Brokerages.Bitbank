# Lean CLI ライブ起動 (Windows / PowerShell 5.1+ / pwsh 7)。
# op run (1Password CLI) 経由で実行する前提 (BITBANK_API_KEY/SECRET が環境変数として注入済み):
#   cd <lean-cli ワークスペース>
#   op run --env-file="<.env.1password のパス>" -- pwsh -NoProfile -File <このファイル> -Project <プロジェクト名>
# --extra-docker-config の JSON を ConvertTo-Json で組み立てるので引用符地獄を回避できる。
param(
    [Parameter(Mandatory = $true)][string]$Project,
    [string]$Environment = "live-bitbank",
    [switch]$Detach
)

if (-not $env:BITBANK_API_KEY -or -not $env:BITBANK_API_SECRET) {
    Write-Error "BITBANK_API_KEY / BITBANK_API_SECRET が未設定。op run 経由で実行すること"
    exit 1
}

$envMap = @{
    BITBANK_API_KEY    = $env:BITBANK_API_KEY
    BITBANK_API_SECRET = $env:BITBANK_API_SECRET
}
# アルゴリズムに渡したい追加の環境変数があればここに足す(例: 検証ガード)

$cfg = @{ environment = $envMap } | ConvertTo-Json -Compress

# Windows PowerShell 5.1 / pwsh 7.2 以前は native コマンドへ渡す引数内の
# 二重引用符をエスケープしないため、lean 側で JSON が壊れる。手動で \" にする
if ($PSVersionTable.PSVersion -lt [version]'7.3') {
    $cfg = $cfg -replace '"', '\"'
}

$extra = @()
if ($Detach) { $extra += "--detach" }

lean live deploy $Project --environment $Environment --extra-docker-config $cfg @extra
exit $LASTEXITCODE
