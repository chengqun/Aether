# ── Aether CTP 仿真柜台·服务器一次性准备脚本（管理员、Windows 服务器上手动跑）──────────
# ⚠ 编码：UTF-8 带 BOM（必须——Windows PowerShell 5.1 对无 BOM 的 UTF-8 按系统 ANSI/GBK 误读，
#    中文字节错位会吞掉字符串引号致解析错误；BOM 是 5.1 识别 UTF-8 的唯一信号）。改本文件勿去 BOM。
# 之后的每次部署由 GitHub Actions（.github/workflows/deploy-ctpsim.yml，纯 SSH 通道）自动完成——
# 本脚本不复制二进制，只做环境准备。幂等：重复运行安全。兼容 Windows PowerShell 5.1。
# 五件事：目录 / OpenSSH Server / Windows 服务 AetherCtpSim + 服务级 env（注册表）/ 崩溃自恢复 / 防火墙。
# 要求 Windows Server 2019+（bsdtar 解包 + REG_MULTI_SZ 服务 env）。

[CmdletBinding()]
param(
    [string]$ServiceName = 'AetherCtpSim',
    [string]$DeployDir   = 'C:\Aether\CtpSim',
    [string]$DataDir     = 'C:\Aether\ctpsim-data',
    [string]$StagingDir  = 'C:\Aether\ctpsim-tmp',
    [string]$Board       = 'zx001',
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
foreach ($d in @($DeployDir, $DataDir, $StagingDir)) {
    New-Item -ItemType Directory -Path $d -Force | Out-Null
}
Info "目录就绪：$DeployDir（部署）/ $DataDir（账本+日志）/ $StagingDir（部署中转）"

# 2) OpenSSH Server（-SkipOpenSsh 跳过；幂等）——GitHub Actions 的 SSH 通道
if (-not $SkipOpenSsh) {
    $cap = Get-WindowsCapability -Online -Name 'OpenSSH.Server*' | Where-Object { $_.State -ne 'Installed' }
    if ($cap) {
        Info '安装 OpenSSH Server（Windows 内置能力，可能需要几分钟）…'
        Add-WindowsCapability -Online -Name 'OpenSSH.Server~~~~0.0.1.0' | Out-Null
    }
    Set-Service -Name sshd -StartupType Automatic
    if ((Get-Service -Name sshd).Status -ne 'Running') { Start-Service -Name sshd }
    if (-not (Get-NetFirewallRule -DisplayName 'OpenSSH-Server-In-TCP' -ErrorAction SilentlyContinue)) {
        New-NetFirewallRule -DisplayName 'OpenSSH-Server-In-TCP' -Direction Inbound -Protocol TCP -LocalPort 22 -Action Allow | Out-Null
    }
    Info 'OpenSSH Server 就绪（22 端口已放行）'
}
else {
    Info '跳过 OpenSSH（-SkipOpenSsh）——确认服务器已有 SSH 通道'
}

# 3) Windows 服务（幂等；exe 此时不存在属预期——首次部署才由 workflow 复制）
if (-not (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue)) {
    New-Service -Name $ServiceName `
        -BinaryPathName (Join-Path $DeployDir 'Acme.Ctp.Sim.Host.exe') `
        -DisplayName 'Aether CTP 仿真柜台' `
        -Description 'Aether CTP 仿真柜台（交易前置 17801 / 行情前置 17802 / JSON-RPC 17800 仅本机）' `
        -StartupType Automatic | Out-Null
    Info "服务 $ServiceName 已创建（exe 待首次部署复制）"
}
else {
    Info "服务 $ServiceName 已存在"
}

# 4) 服务级环境变量（注册表 REG_MULTI_SZ，幂等重写；改动需重启服务生效）。
#    三条监听共用 AETHER_CTP_SIM_ADDR 的 host：0.0.0.0 = 全绑所有网卡（17800/17801/17802）——
#    17800（JSON-RPC，密码是明文 JSON）不放防火墙即被默认入站拒绝拦掉，远程只走 17801/17802 前置双腿。
$svcKey = "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName"
$envLines = @(
    "AETHER_CTP_SIM_ADDR=0.0.0.0:17800",
    "AETHER_CTP_SIM_DB=$DataDir\ctp-sim.db",
    "AETHER_CTP_SIM_LOG=$DataDir\ctpsim.log",
    "AETHER_CTP_SIM_BOARD=$Board"
)
Set-ItemProperty -Path $svcKey -Name Environment -Type MultiString -Value $envLines
Info '服务 env 已写入注册表（ADDR=0.0.0.0:17800 / DB / LOG / BOARD）'

# 5) 崩溃自恢复：挂了 5 秒后自动拉起（失败计数窗口 1 天）
sc.exe failure $ServiceName reset= 86400 actions= restart/5000 | Out-Null
Info '崩溃自恢复已配置（restart/5000）'

# 6) 防火墙：只放 17801 + 17802 入站（幂等）；17800 故意不放行
$firewallRules = @(
    @{ DisplayName = 'Aether CtpSim Trade Front (17801)';     Port = 17801 },
    @{ DisplayName = 'Aether CtpSim Market Data Front (17802)'; Port = 17802 }
)
foreach ($rule in $firewallRules) {
    if (-not (Get-NetFirewallRule -DisplayName $rule.DisplayName -ErrorAction SilentlyContinue)) {
        New-NetFirewallRule -DisplayName $rule.DisplayName -Direction Inbound -Protocol TCP -LocalPort $rule.Port -Action Allow | Out-Null
    }
}
Info '防火墙已放行 17801/17802（17800 JSON-RPC 故意不放行——默认入站拒绝即拦）'

# 7) 收尾清单
Info '── 完成。还差三步：'
Info '1) 部署公钥追加到 C:\ProgramData\ssh\administrators_authorized_keys（管理员组用户走此文件，非 ~/.ssh），并修 ACL：'
Info '   icacls "C:\ProgramData\ssh\administrators_authorized_keys" /inheritance:r /grant "Administrators:F" /grant "SYSTEM:F"'
Info '2) GitHub 仓库 Secrets：CTPSIM_SSH_HOST / CTPSIM_SSH_USER（Administrators 组）/ CTPSIM_SSH_KEY（私钥）/ 可选 CTPSIM_SSH_PORT'
Info '3) 触发 deploy-ctpsim workflow（push 或手动）完成首次部署'
Info '提示：服务器时区建议中国时区——tzutil /s "China Standard Time"（SimClock 按本地时间算交易日/交易时段）'
Info '提示：部署/重启语义 = 未成交挂单作废 + 在线客户端断开须重新登录（客户端无自动重连）'
