# ── Aether CTP 仿真柜台·远程部署脚本（在服务器上执行）──────────────────────────────────
# 由 .github/workflows/deploy-ctpsim.yml 经 SSH 调起：
#   powershell -NoProfile -ExecutionPolicy Bypass -File C:/Aether/ctpsim-tmp/ctpsim-remote-deploy.ps1
# 前提（一次性）：已以管理员运行 scripts/ctpsim-setup-server.ps1（服务 AetherCtpSim + 服务级 env 注册表 + 防火墙）。
# 流程：preflight → 干净解包 → 停服务（等 Stopped）→ robocopy /MIR 换文件 → 起服务（等 Running）→ 汇报。
# 兼容 Windows PowerShell 5.1（OpenSSH 默认 shell 是 cmd → powershell.exe；不用 ?? / 三元 / &&）。
# 注意：$StagingDir/$DeployDir/$ServiceName 默认值与 workflow 的 env.CTPSIM_STAGING、setup 脚本的目录约定是
# 同值常量——改一处须同步另一处。

[CmdletBinding()]
param(
    [string]$ServiceName = 'AetherCtpSim',
    [string]$StagingDir  = 'C:\Aether\ctpsim-tmp',
    [string]$DeployDir   = 'C:\Aether\CtpSim',
    [string]$TarName     = 'ctpsim.tar.gz',
    [string]$DataDir     = 'C:\Aether\ctpsim-data'
)

$ErrorActionPreference = 'Stop'

function Fail([string]$msg) {
    [Console]::Error.WriteLine("[CTPSIM-DEPLOY] $msg")
    exit 1
}

# 1) Preflight：服务不存在 = setup 没跑过——服务的全部配置（env 注册表）都在服务注册项上，
#    缺失时部署只会让 exe 以错误默认值跑（LocalSystem 的 ~/.aether），立即失败好过静默错配。
$svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if (-not $svc) {
    Fail "服务 $ServiceName 不存在——请先在服务器上以管理员运行 scripts/ctpsim-setup-server.ps1（一次性）。"
}

# 2) 干净解包（tar 不清旧文件——不删 staging\out 的话，已删除的旧 DLL 会被 /MIR 带进部署目录）
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

# 3) 停服务 + 等 Stopped（超时即失败——此时二进制未动，服务还在跑旧版本，安全）
if ($svc.Status -ne 'Stopped') {
    try {
        Stop-Service -Name $ServiceName
        $svc.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
    }
    catch {
        Fail "停服务失败/超时：$($_.Exception.Message)——二进制未动，服务仍在跑旧版本"
    }
}

# 4) 换文件：robocopy /MIR（退出码 0-7 都是成功，>=8 才失败）。
#    账本 ctp-sim.db 与日志在 DataDir（部署目录之外），/MIR 永不波及。
robocopy $out $DeployDir /MIR /R:2 /W:2 /NFL /NDL /NJH /NJS
if ($LASTEXITCODE -ge 8) {
    Fail "robocopy 失败（code $LASTEXITCODE）——服务已停、二进制可能不完整，重跑一次部署即可恢复"
}

# 5) 起服务 + 等 Running；失败时打日志尾部自证故障（AETHER_CTP_SIM_LOG 指向 $DataDir\ctpsim.log）
try {
    Start-Service -Name $ServiceName
    $svc.WaitForStatus('Running', [TimeSpan]::FromSeconds(60))
}
catch {
    Write-Host '── 部署后启动失败，ctpsim.log 尾部：'
    Get-Content (Join-Path $DataDir 'ctpsim.log') -Tail 30 -ErrorAction SilentlyContinue
    Fail "服务 $ServiceName 未能进入 Running（见上方日志尾部；Windows 事件查看器亦可能有 .NET Runtime 错误）"
}

# 6) 汇报（显式 exit 0——robocopy 的非零退出码不能漏成本脚本退出码毒死 workflow step）
$svc.Refresh()
$version = Get-Content (Join-Path $DeployDir 'VERSION') -ErrorAction SilentlyContinue
Write-Host "[CTPSIM-DEPLOY] ==> $ServiceName 已部署并运行（版本 $version）"
Write-Host "[CTPSIM-DEPLOY]     日志: $DataDir\ctpsim.log    账本: $DataDir\ctp-sim.db"
Write-Host "[CTPSIM-DEPLOY]     客户端前置: tcp://<本机IP>:17801（交易）/ tcp://<本机IP>:17802（行情）"
exit 0
