# ── Aether 仿真栈·端口直连配置脚本（在服务器上执行；自动化入口见 config-aether-ports.yml）──
# 作用：把链节点（17805）与交易所（17800）从回环绑定切到指定端点（公网直连/收回回环），含防火墙。
#       桌面钱包页/CLI 随之零隧道（runbook §2.D）。
# 幂等：重复运行安全（env 重写同值 + 防火墙存在即跳过）。
# ⚠ 不碰链文件（链升级是独立动作——见 runbook 批 6 段）；重启交易所会作废未成交挂单 + 客户端须重登。
# 兼容 Windows PowerShell 5.1。

[CmdletBinding()]
param(
    [string]$NodeAddr = '0.0.0.0:17805',        # 链节点绑定端点（收回到回环传 '127.0.0.1:17805'）
    [string]$ExchangeAddr = '0.0.0.0:17800',    # 交易所绑定端点（收回到回环传 '127.0.0.1:17800'）
    [string]$Restart = 'true'                   # 是否重启两服务（env 变更须重启生效；'false' 只写配置不重启）
)

$ErrorActionPreference = 'Stop'

# 输出统一 UTF-8（治 CI 端 GBK mojibake——同 ctpim-remote-deploy 的修复）
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

function Fail([string]$msg) {
    [Console]::Error.WriteLine("[AETHER-PORTS] $msg")
    exit 1
}
function Info([string]$msg) { Write-Host "[AETHER-PORTS] $msg" }

# 0) 管理员守卫
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Fail '必须以管理员运行（注册表/防火墙写入需要管理员权限）'
}

# 1) 链节点 env（幂等重写）
$nodeKey = 'HKLM:\SYSTEM\CurrentControlSet\Services\AetherLedgerNode'
if (-not (Get-Service -Name AetherLedgerNode -ErrorAction SilentlyContinue)) {
    Fail '服务 AetherLedgerNode 不存在——先运行 scripts/ledger-setup-server.ps1。'
}
$nodeEnv = @((Get-ItemProperty $nodeKey).Environment | Where-Object { $_ -notlike 'AETHER_LEDGER_ADDR=*' })
$nodeEnv += "AETHER_LEDGER_ADDR=$NodeAddr"
Set-ItemProperty -Path $nodeKey -Name Environment -Type MultiString -Value $nodeEnv
Info "链节点绑定 → $NodeAddr"

# 2) 交易所 env（幂等重写；前置仍走本机 127.0.0.1 连它，不受影响）
$exKey = 'HKLM:\SYSTEM\CurrentControlSet\Services\AetherCtpExchange'
if (-not (Get-Service -Name AetherCtpExchange -ErrorAction SilentlyContinue)) {
    Fail '服务 AetherCtpExchange 不存在——先运行 scripts/ctpsim-setup-server.ps1。'
}
$exEnv = @((Get-ItemProperty $exKey).Environment | Where-Object { $_ -notlike 'AETHER_CTP_SIM_ADDR=*' })
$exEnv += "AETHER_CTP_SIM_ADDR=$ExchangeAddr"
Set-ItemProperty -Path $exKey -Name Environment -Type MultiString -Value $exEnv
Info "交易所绑定 → $ExchangeAddr"

# 3) 防火墙：非回环绑定的端口放行（幂等）；回环绑定的历史规则保留（收回到回环时旧规则无害，可手动删）
foreach ($pair in @(@('Aether Ledger Node', $NodeAddr), @('Aether CtpExchange', $ExchangeAddr))) {
    $name = $pair[0]; $addr = $pair[1]
    if ($addr -match '^127\.0\.0\.1|^localhost') { continue }
    $port = ($addr -split ':')[-1]
    $ruleName = "$name ($port)"
    if (-not (Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue)) {
        New-NetFirewallRule -DisplayName $ruleName -Direction Inbound -Protocol TCP -LocalPort $port -Action Allow | Out-Null
    }
    Info "防火墙已放行 $ruleName"
}

# 4) 重启（env 生效；⚠ 交易所重启 = 未成交挂单作废 + 客户端须重登）
if ($Restart -eq 'true') {
    Restart-Service AetherLedgerNode
    Start-Sleep -Seconds 6
    Restart-Service AetherCtpExchange
    Start-Sleep -Seconds 3
}
Get-Service AetherLedgerNode,AetherCtpExchange,AetherCtpFront | Format-Table Name,Status

# 5) 提示（链健康前置：若节点没起来，看 node.log——旧单资产链需删链文件做批 6 升级，runbook §0）
Info "完成。桌面端设置：链节点/交易所 → 本机IP 对应端口。"
Info "（若 AetherLedgerNode 未 Running：Get-Content C:\Aether\ledger-data\node.log -Tail 5——报「旧单资产协议」= 需批 6 删链升级）"
exit 0
