# ── Aether 虚拟币链·远程部署脚本（在服务器上执行）──────────────────────────────────
# 由 .github/workflows/deploy-ledger.yml 经 SSH 调起：
#   powershell -NoProfile -ExecutionPolicy Bypass -File C:/Aether/ledger-tmp/ledger-remote-deploy.ps1
# 前提（一次性）：已以管理员运行 scripts/ledger-setup-server.ps1（AetherLedgerNode 服务 + env 注册表）。
# 流程：preflight → 干净解包 → 停节点 → robocopy /MIR 三目录 → 起节点（等 Running）→ RPC 探针。
# 链数据（ledger.chain + 节点日志）在 DataDir——三部署目录之外，重部署不动链。
# ⚠ 节点重启语义：读取 + 全量重放恢复（单号/nonce 序列续排）；交易所桥轮询自动重连（交易所不用动）。
# 兼容 Windows PowerShell 5.1（无 ?? / 三元 / &&）。

[CmdletBinding()]
param(
    [string]$NodeService = 'AetherLedgerNode',
    [string]$NodeDir = 'C:\Aether\LedgerNode',
    [string]$WalletDir = 'C:\Aether\LedgerWallet',
    [string]$VerifyDir = 'C:\Aether\LedgerVerify',
    [string]$StagingDir = 'C:\Aether\ledger-tmp',
    [string]$TarName = 'ledger.tar.gz',
    [string]$DataDir = 'C:\Aether\ledger-data'
)

$ErrorActionPreference = 'Stop'

# 输出统一 UTF-8（治 CI 端 GBK mojibake——同 ctpsim-remote-deploy 的修复）
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

function Fail([string]$msg) {
    [Console]::Error.WriteLine("[LEDGER-DEPLOY] $msg")
    exit 1
}
function Info([string]$msg) { Write-Host "[LEDGER-DEPLOY] $msg" }

# 1) Preflight：服务已注册（setup 没跑过 = 配置缺失——立即失败好过静默错配）
if (-not (Get-Service -Name $NodeService -ErrorAction SilentlyContinue)) {
    Fail "服务 $NodeService 不存在——请先在服务器上以管理员运行 scripts/ledger-setup-server.ps1（一次性）。"
}

# 2) 干净解包（tar 不清旧文件——不删 staging\out 的话，已删除的旧 DLL 会被 /MIR 带进部署目录）。
#    tar 布局：node/ + wallet/ + verify/（workflow 的三项目 publish 产物）。
$tar = Join-Path $StagingDir $TarName
$out = Join-Path $StagingDir 'out'
if (-not (Test-Path $tar)) {
    Fail "staging 缺少 $tar（workflow 的 scp 步骤未送达？）"
}
if (Test-Path $out) { Remove-Item -Recurse -Force $out }
New-Item -ItemType Directory -Path $out -Force | Out-Null
& tar.exe -xzf $tar -C $out
if ($LASTEXITCODE -ne 0) {
    Fail "解包 $tar 失败（exit $LASTEXITCODE）"
}
foreach ($part in @('node', 'wallet', 'verify')) {
    if (-not (Test-Path (Join-Path $out $part))) {
        Fail "解包产物缺 $part\ 子目录（workflow 的 Bundle 布局已变？）"
    }
}

# 3) 停节点（等 Stopped——链文件逐块落盘，无需额外 flush）
try { Stop-Service -Name $NodeService -ErrorAction SilentlyContinue } catch { }
$deadline = [DateTime]::Now.AddSeconds(30)
while ((Get-Service -Name $NodeService).Status -ne 'Stopped' -and [DateTime]::Now -lt $deadline) {
    Start-Sleep -Milliseconds 500
}
if ((Get-Service -Name $NodeService).Status -ne 'Stopped') {
    Fail "停服务 $NodeService 超时（30s）——二进制未动，服务仍在跑旧版本，安全（手工排查后重跑）"
}

# 4) 换文件：robocopy /MIR 三目录（退出码 0-7 都是成功，>=8 才失败）。链数据在 DataDir（部署目录之外）。
robocopy (Join-Path $out 'node') $NodeDir /MIR /R:2 /W:2 /NFL /NDL /NJH /NJS
if ($LASTEXITCODE -ge 8) { Fail "robocopy 节点失败（code $LASTEXITCODE）" }
robocopy (Join-Path $out 'wallet') $WalletDir /MIR /R:2 /W:2 /NFL /NDL /NJH /NJS
if ($LASTEXITCODE -ge 8) { Fail "robocopy 钱包 CLI 失败（code $LASTEXITCODE）——节点已更新，重跑一次部署即可恢复" }
robocopy (Join-Path $out 'verify') $VerifyDir /MIR /R:2 /W:2 /NFL /NDL /NJH /NJS
if ($LASTEXITCODE -ge 8) { Fail "robocopy 验证器失败（code $LASTEXITCODE）——节点已更新，重跑一次部署即可恢复" }

# 5) 起节点（等 Running——首次启动生成新链：treasury/producer 种子打印进 $DataDir\node.log，务必抄录备份）
try { Start-Service -Name $NodeService } catch { }
$deadline = [DateTime]::Now.AddSeconds(60)
while ((Get-Service -Name $NodeService).Status -ne 'Running' -and [DateTime]::Now -lt $deadline) {
    Start-Sleep -Milliseconds 500
}
if ((Get-Service -Name $NodeService).Status -ne 'Running') {
    $log = Join-Path $DataDir 'node.log'
    if (Test-Path $log) {
        Write-Host "[LEDGER-DEPLOY] ── $log 尾部 30 行 ──"
        Get-Content $log -Tail 30 -ErrorAction SilentlyContinue | ForEach-Object { Write-Host "  $_" }
    }
    Fail "链节点未能启动（见上方日志尾部——二进制已就位，修复后重跑部署即可恢复）"
}

# 6) RPC 探针：钱包 CLI info（自包含 win-x64，服务器裸跑）——链就绪 + 高度可见即部署成功
$probe = & (Join-Path $WalletDir 'Aether.Ledger.Wallet.exe') info --node 127.0.0.1:17805 2>&1
Write-Host "[LEDGER-DEPLOY] RPC 探针：$probe"
if ($LASTEXITCODE -ne 0) {
    Fail "链节点 Running 但 RPC 探针失败（钱包 CLI info 退出码 $LASTEXITCODE）——见上方输出"
}

# 7) 汇报（显式 exit 0——robocopy 的非零退出码不能漏成本脚本退出码毒死 workflow step）
$version = Get-Content (Join-Path $NodeDir 'VERSION') -ErrorAction SilentlyContinue
Write-Host "[LEDGER-DEPLOY] ==> $NodeService 已部署并运行（版本 $version）"
Write-Host "[LEDGER-DEPLOY]     链文件: $DataDir\ledger.chain（重部署不动）| 节点日志: $DataDir\node.log"
Write-Host "[LEDGER-DEPLOY]     钱包 CLI: $WalletDir\Aether.Ledger.Wallet.exe（发币/查余额/提币）| 独立验证: $VerifyDir\Aether.Ledger.Verify.exe"
Write-Host "[LEDGER-DEPLOY]     ⚠ 首次生成链后从 node.log 抄录 treasury/producer 种子并离线备份"
exit 0
