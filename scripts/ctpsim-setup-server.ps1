# ── Aether CTP 仿真栈·服务器一次性准备脚本（管理员、Windows 服务器上手动跑）──────────
# 两服务形态（2026-10-05 进程分离，真实世界对齐——一个交易所 + 一个前置，前置经 RPC 连交易所）：
#   AetherCtpExchange = 交易所（Sim.Host：撮合核心 + JSON-RPC，只绑 127.0.0.1:17800——前置进程的连接口）
#   AetherCtpFront    = 前置（Front.Host：CTP 线协议双腿 17801/17802 对客户端 → 经 RPC 连交易所）
# 此后每次部署由 GitHub Actions（.github/workflows/deploy-ctpsim.yml，纯 SSH 通道）自动完成——
# 本脚本不复制二进制，只做环境准备。幂等：重复运行安全。兼容 Windows PowerShell 5.1。
# 要求 Windows Server 2019+。

[CmdletBinding()]
param(
    [string]$ExchangeService = 'AetherCtpExchange',
    [string]$ExchangeDir = 'C:\Aether\CtpExchange',
    [string]$FrontService = 'AetherCtpFront',
    [string]$FrontDir = 'C:\Aether\CtpFront',
    [string]$DataDir = 'C:\Aether\ctpsim-data',
    [string]$StagingDir = 'C:\Aether\ctpsim-tmp',
    [string]$Board = 'zx001',
    [string]$LegacyService = 'AetherCtpSim',      # 旧单进程服务名——存在则迁移删除（数据在 DataDir 不受影响）
    [string]$LegacyDir = 'C:\Aether\CtpSim',      # 旧部署目录——存在则清空（二进制已分家到 Exchange/Front 两目录）
    [switch]$SkipOpenSsh
)

$ErrorActionPreference = 'Stop'

function Fail([string]$msg) {
    [Console]::Error.WriteLine("[CTPSIM-SETUP] $msg")
    exit 1
}
function Info([string]$msg) { Write-Host "[CTPSIM-SETUP] $msg" }

# 0) 管理员守卫
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Fail '必须以管理员运行（服务/防火墙/注册表写入都需要管理员权限）'
}

# 1) 目录
foreach ($d in @($ExchangeDir, $FrontDir, $DataDir, $StagingDir)) {
    New-Item -ItemType Directory -Path $d -Force | Out-Null
}
Info "目录就绪：$ExchangeDir（交易所）/ $FrontDir（前置）/ $DataDir（账本+日志）/ $StagingDir（部署中转）"

# 2) OpenSSH Server（-SkipOpenSsh 跳过；幂等）——GitHub Actions 的 SSH 通道
if (-not $SkipOpenSsh) {
    $cap = Get-WindowsCapability -Online -Name 'OpenSSH.Server*' | Where-Object { $_.State -ne 'Installed' }
    if ($cap) {
        Info '安装 OpenSSH Server（Windows 内置能力，可能需要几分钟）…'
        Add-WindowsCapability -Online -Name 'OpenSSH.Server~~~~0.0.1.0' | Out-Null
    }
    Set-Service -Name sshd -StartupType Automatic
    if ((Get-Service -Name sshd).Status -ne 'Running') { Start-Service sshd }
    if (-not (Get-NetFirewallRule -DisplayName 'OpenSSH-Server-In-TCP' -ErrorAction SilentlyContinue)) {
        New-NetFirewallRule -DisplayName 'OpenSSH-Server-In-TCP' -Direction Inbound -Protocol TCP -LocalPort 22 -Action Allow | Out-Null
    }
    Info 'OpenSSH Server 就绪（22 端口已放行）'
}
else {
    Info '跳过 OpenSSH（-SkipOpenSsh）——确认服务器已有 SSH 通道'
}

# 3) 旧单进程服务迁移（2026-10-05 前的部署形态）：删旧服务（账本在 DataDir 不受影响）
if (Get-Service -Name $LegacyService -ErrorAction SilentlyContinue) {
    Info "检测到旧单进程服务 $LegacyService——停止并删除（数据 $DataDir 不受影响；新形态 = 交易所 + 前置两服务）"
    try { Stop-Service -Name $LegacyService -Force -ErrorAction SilentlyContinue } catch { }
    try { sc.exe delete $LegacyService | Out-Null } catch { }
    Start-Sleep -Seconds 2
}
if (Test-Path $LegacyDir) {
    Info "清理旧部署目录 $LegacyDir（二进制已分家到 $ExchangeDir/$FrontDir）"
    try { Remove-Item -Recurse -Force $LegacyDir -ErrorAction SilentlyContinue } catch { }
}

# 4) 交易所服务（幂等；exe 待首次部署复制）
if (-not (Get-Service -Name $ExchangeService -ErrorAction SilentlyContinue)) {
    New-Service -Name $ExchangeService `
        -BinaryPathName (Join-Path $ExchangeDir 'Acme.Ctp.Sim.Host.exe') `
        -DisplayName 'Aether CTP 交易所（仿真撮合核心）' `
        -Description 'Aether CTP 仿真交易所（撮合/账本/行情引擎——JSON-RPC 127.0.0.1:17800 仅本机，前置进程连接口）' `
        -StartupType Automatic | Out-Null
    Info "服务 $ExchangeService 已创建（exe 待首次部署复制）"
}
else {
    Info "服务 $ExchangeService 已存在"
}

# 5) 交易所服务级 env（注册表 REG_MULTI_SZ，幂等重写；改动需重启服务生效）。
#    交易所只绑 127.0.0.1:17800（前置同机连它——比旧单进程形态 0.0.0.0 更收敛；公网只触达前置 17801/17802）。
$exchangeKey = "HKLM:\SYSTEM\CurrentControlSet\Services\$ExchangeService"
$exchangeEnv = @(
    'AETHER_CTP_SIM_ADDR=127.0.0.1:17800',
    "AETHER_CTP_SIM_DB=$DataDir\ctp-sim.db",
    "AETHER_CTP_SIM_LOG=$DataDir\ctpsim-exchange.log",
    "AETHER_CTP_SIM_BOARD=$Board"
)
Set-ItemProperty -Path $exchangeKey -Name Environment -Type MultiString -Value $exchangeEnv
Info '交易所 env 已写入注册表（ADDR=127.0.0.1:17800 / DB / LOG / BOARD）'

# 6) 交易所崩溃自恢复
sc.exe failure $ExchangeService reset= 86400 actions= restart/5000 | Out-Null
Info '交易所崩溃自恢复已配置（restart/5000）'

# 7) 前置服务（幂等）
if (-not (Get-Service -Name $FrontService -ErrorAction SilentlyContinue)) {
    New-Service -Name $FrontService `
        -BinaryPathName (Join-Path $FrontDir 'Acme.Ctp.Front.Host.exe') `
        -DisplayName 'Aether CTP 前置（券商侧线协议接入）' `
        -Description 'Aether CTP 仿真前置（交易腿 17801 / 行情腿 17802 对客户端，经 RPC 连本机交易所 17800——真实券商前置形态）' `
        -StartupType Automatic | Out-Null
    Info "服务 $FrontService 已创建（exe 待首次部署复制）"
}
else {
    Info "服务 $FrontService 已存在"
}

# 8) 前置服务级 env（注册表，幂等重写）
$frontKey = "HKLM:\SYSTEM\CurrentControlSet\Services\$FrontService"
$frontEnv = @(
    'AETHER_CTP_EXCHANGE_ADDR=127.0.0.1:17800',      # 交易所端点（同机）
    "AETHER_CTP_FRONT_LOG=$DataDir\ctpsim-front.log"
    # 监听端口默认 0.0.0.0:17801/17802（AETHER_CTP_FRONT_ADDR / AETHER_CTP_MD_FRONT_PORT 可覆盖）
)
Set-ItemProperty -Path $frontKey -Name Environment -Type MultiString -Value $frontEnv
Info '前置 env 已写入注册表（EXCHANGE_ADDR=127.0.0.1:17800 / LOG）'

# 9) 前置崩溃自恢复
sc.exe failure $FrontService reset= 86400 actions= restart/5000 | Out-Null
Info '前置崩溃自恢复已配置（restart/5000）'

# 10) 防火墙：只放前置双腿 17801 + 17802（幂等）。17800 无需放行——交易所只绑回环（比旧形态更收敛）。
$firewallRules = @(
    @{ DisplayName = 'Aether CtpFront Trade Leg (17801)'; Port = 17801 },
    @{ DisplayName = 'Aether CtpFront Market Data Leg (17802)'; Port = 17802 }
)
foreach ($rule in $firewallRules) {
    if (-not (Get-NetFirewallRule -DisplayName $rule.DisplayName -ErrorAction SilentlyContinue)) {
        New-NetFirewallRule -DisplayName $rule.DisplayName -Direction Inbound -Protocol TCP -LocalPort $rule.Port -Action Allow | Out-Null
    }
}
Info '防火墙已放行前置 17801/17802（交易所 17800 只绑回环——无需对外放行）'

# 11) 收尾清单
Info '── 完成。还差三步：'
Info '1) 部署公钥追加到 C:\ProgramData\ssh\administrators_authorized_keys（管理员组用户走此文件，非 ~/.ssh），并修 ACL：'
Info '   icacls "C:\ProgramData\ssh\administrators_authorized_keys" /inheritance:r /grant "Administrators:F" /grant "SYSTEM:F"'
Info '2) GitHub 仓库 Secrets：CTPSIM_SSH_HOST / CTPSIM_SSH_USER（Administrators 组）/ CTPSIM_SSH_KEY（私钥）/ 可选 CTPSIM_SSH_PORT'
Info '3) 触发 deploy-ctpsim workflow（push 或手动）完成首次部署（两服务二进制一起发布）'
Info '提示：服务器时区建议中国时区——tzutil /s "China Standard Time"（SimClock 按本地时间算交易日/交易时段）'
Info '提示：前置可独立重启（账本不丢——交易所不动）；交易所重启 = 未成交挂单作废 + 在线客户端须重登'
