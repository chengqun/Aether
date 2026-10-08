# 虚拟币实操手册（Aether.Ledger 运维 / 发币 / 交易 Runbook）

> **定位**：服务器部署完成后的操作手册——验证部署、备份种子、发币开账户、充值交易提币全流程。
> 设计文档见 CLAUDE.md「虚拟币链引擎」节；本文只管**怎么操作**。
> 前提：已跑 `scripts/ledger-setup-server.ps1`（一次性）+ `deploy-ledger.yml` 首次部署成功。
> ⚠ 本文档不含任何真实种子/密钥——它们只存在于你的离线备份里。

---

## 0. 全景（30 秒）

> **批 6 双资产（2026-10-08）**：链上有**两种资产**——币（AEX，21M 稀缺资产）+ 计价币（AQ，210 亿，报价侧）。
> 交易所交易对两边都是真资产：充**币**得现货持仓、充 **AQ** 得场内现金（1 AQ = 1 现金）——**交易所永不发钱**，
> 「注册就有钱」不复存在。CLI 的转账/充值/提币都支持 `--asset coin|quote`（缺省 coin）。
> ⚠ 协议升级是硬分叉：升级前的旧链文件不兼容——删除 `ledger.chain` 重启生成新链（旧链上的测试币作废）。



```
链上（资产层，服务器 <IP>:17805，公网直连）           交易所（venue 层）
┌────────────────────────────────────┐            ┌────────────────────────────────────┐
│ 国库 treasury（21M 币全部）          │──发币────→ │ 交易所账户（userId+密码首登开户）      │
│   持 treasury 种子（= 全部供给）      │            │   ax001 现货持仓（充值自动铸造）       │
│ 用户钱包（种子即身份，32B hex）       │←──提 币─── │   现金（计价货币，卖出币获得）⇄ 买入    │
│ custody 托管地址（交易所持有）        │←──充 倁─── │                                    │
└────────────────────────────────────┘            └────────────────────────────────────┘
```

- **桌面端交易**走前置 `tcp://<服务器IP>:17801/17802`（公网开放，无需隧道）。
- **链上操作**（CLI 发币/充值/提币/桌面钱包页）走 17800/17805——**已开公网直连（§2.D）**，桌面端零隧道。
  （历史形态曾为「只绑回环 + SSH 隧道」；若你的服务器仍是回环绑定，按 §2.D 开放。）
- **三把钥匙**：`treasury 种子`（花 21M 供给）/ `producer 种子`（出块权）/ `custody 种子`（交易所托管钱包——启桥时你自己生成）。
- CLI：服务器上 `C:\Aether\LedgerWallet\Aether.Ledger.Wallet.exe`；本地 `dotnet run --project apps/LedgerWallet/Aether.Ledger.Wallet -- <命令>`（下文简写 `wallet`）。币数支持 8 位小数。

---

## 1. 验证部署（服务器上 3 分钟）

SSH 一条命令做完三个健康检查：

```powershell
powershell -NoProfile -Command "
[Console]::OutputEncoding=[Text.Encoding]::UTF8;
Get-Service AetherLedgerNode,AetherCtpExchange,AetherCtpFront | Format-Table Name,Status;
Write-Host '── 节点日志（链+种子）──';
Get-Content C:\Aether\ledger-data\node.log | Select-String '新链已生成','种子','就绪' | Select-Object -First 6;
Write-Host '── 交易所日志尾部（桥状态）──';
Get-Content C:\Aether\ctpsim-data\ctpsim-exchange.log -Tail 6"
```

| 看什么 | 期望 | 含义 |
|---|---|---|
| 三服务 Status | 全 `Running` | 部署健康 |
| 节点日志「新链已生成」+ 两条「⚠ 种子」 | 存在 | 链已生成，种子就在日志里 |
| 交易所日志 `[CTP-SIM] …已就绪` | 存在 | 交易所正常 |
| 交易所日志 `[LEDGER-BRIDGE] 交易所桥就绪` | 有 → 跳过 §2.B；无 → 做 §2.B | 桥是否已启用 |

两个探针（服务器上直接跑）：

```powershell
C:\Aether\LedgerWallet\Aether.Ledger.Wallet.exe info
# 期望：链就绪：高度 N | chainId 77 | 总供给 21,000,000.00000000（固定——不可增发）

C:\Aether\LedgerVerify\Aether.Ledger.Verify.exe C:\Aether\ledger-data\ledger.chain
# 期望：验证通过 + 余额清单（新链只有 treasury 21M）——零活进程依赖的余额真相
```

---

## 2. 一次性配置

### 2.A 备份两把种子（**最重要，现在就做**）

```powershell
Select-String -Path C:\Aether\ledger-data\node.log -Pattern '种子'
# ⚠ treasury 种子（hex，丢失 = 丢失全部供给）：<64位hex>
# ⚠ producer 种子（hex，丢失 = 无法出块）：<64位hex>
```

抄录到**离线**位置（密码管理器/加密盘）。treasury 种子建议同时存成本地文件 `treasury.seed`（内容 = 那串 hex）供 CLI 使用。**保管等级 = 财富本身。**

### 2.B 启用交易所现货桥（若 §1 检查未见桥就绪）

```powershell
# 1) 生成 custody 种子（64位hex 随机）——交易所托管钥匙，丢了 = 托管币不可提，离线备份！
$seed = '<64位hex>'

# 2) 写进交易所服务 env（幂等，重跑安全）+ BOARD 上架 ax001
$k = 'HKLM:\SYSTEM\CurrentControlSet\Services\AetherCtpExchange'
$envs = @((Get-ItemProperty $k).Environment | Where-Object {
    $_ -notlike 'AETHER_LEDGER_CUSTODY_SEED=*' -and $_ -notlike 'AETHER_CTP_SIM_BOARD=*' -and $_ -notlike 'AETHER_CTP_SIM_BALANCE=*' })
$envs += "AETHER_LEDGER_CUSTODY_SEED=$seed"
$envs += 'AETHER_CTP_SIM_BOARD=zx001,ax001'
Set-ItemProperty $k -Name Environment -Type MultiString -Value $envs
# 注：不设 AETHER_CTP_SIM_BALANCE（默认 0）——批 6 起现金从 AQ 充值来（见 2.C）；
#     纯期货沙盒（不起链桥测策略）才显式设它当开发水龙头。

# 3) 重启生效（语义：未成交挂单作废、客户端须重登；账本/铸造台账不丢）
Restart-Service AetherCtpExchange

# 4) 记下 custody 地址（充值和 agent 工具都要用）
C:\Aether\LedgerWallet\Aether.Ledger.Wallet.exe balance --seed $seed
# 输出「<40位hex地址>：0.00000000」——这个地址就是 custody
```

重启后验证：交易所日志出现 `[LEDGER-BRIDGE] 交易所桥就绪` + `[LEDGER-SPOT] 现货桥就绪：ax001`。

### 2.C 现金从哪来（批 6 双资产——死锁已根治）

**首登零余额（默认 0，不设 BALANCE）**。批 6 起，**场内现金 = AQ 充值的托管凭证**（1 AQ = 1 现金）——
买方充 AQ 得到现金、卖方充币得到持仓，市场完整自转（旗舰测试钉死）。「注册就有钱」不复存在，
「现金只能从卖币来」的死锁也根治——**现金从充 AQ 来**。

`AETHER_CTP_SIM_BALANCE` 仅剩一个用途：**纯期货沙盒**（不起链桥、只测 zx001 期货策略）时当开发水龙头。正常双资产玩法永远不设。

**AQ 从哪来**：国库（新链生成时 treasury 同拿 210 亿 AQ）——你分发。可以卖 AQ 收 RMB
（运营上承诺 1 AQ = 1 元回购 ⇒ AQ 成为你个人的「USDT」，币价自动获得真实法币读数），也可以送（内部靶场记分模式）。

---

### 2.D 公网直连（17800/17805 开放——用户拍板「先不管安全问题」）

默认形态是回环绑定（安全默认）；本节把它切到公网直连——桌面钱包页/CLI **零隧道**。

```powershell
# 前置安全检查：若批 6 新链未就位（节点崩溃循环 + 日志报「旧单资产协议」），先删链：
#   Remove-Item C:\Aether\ledger-data\ledger.chain    # ⚠ 若日志已见「新链已生成 … 计价币 …」则跳过（新链上有真币勿删！）

# ① 节点改绑 0.0.0.0（幂等）
$k = 'HKLM:\SYSTEM\CurrentControlSet\Services\AetherLedgerNode'
$envs = @((Get-ItemProperty $k).Environment | Where-Object { $_ -notlike 'AETHER_LEDGER_ADDR=*' })
$envs += 'AETHER_LEDGER_ADDR=0.0.0.0:17805'
Set-ItemProperty $k -Name Environment -Type MultiString -Value $envs

# ② 交易所改绑 0.0.0.0（幂等——前置仍走本机 127.0.0.1 连它，不受影响）
$k = 'HKLM:\SYSTEM\CurrentControlSet\Services\AetherCtpExchange'
$envs = @((Get-ItemProperty $k).Environment | Where-Object { $_ -notlike 'AETHER_CTP_SIM_ADDR=*' })
$envs += 'AETHER_CTP_SIM_ADDR=0.0.0.0:17800'
Set-ItemProperty $k -Name Environment -Type MultiString -Value $envs

# ③ 防火墙放行（幂等）
foreach ($port in 17800, 17805) {
    if (-not (Get-NetFirewallRule -DisplayName "Aether Public ($port)" -ErrorAction SilentlyContinue)) {
        New-NetFirewallRule -DisplayName "Aether Public ($port)" -Direction Inbound -Protocol TCP -LocalPort $port -Action Allow | Out-Null
    }
}

# ④ 重启两服务（不动链文件）
Restart-Service AetherLedgerNode; Start-Sleep 6
Restart-Service AetherCtpExchange
Get-Service AetherLedgerNode,AetherCtpExchange,AetherCtpFront | Format-Table Name,Status
```

桌面钱包页「设置」随之更新：链节点 `<IP>:17805`、交易所 `<IP>:17800`——全部操作免隧道。

**诚实边界（用户已接受）**：17800 是明文 JSON + 密码登录（公网嗅探可冒充提币）；17805 无秘密（签名安全，链节点公开是 Bitcoin 常态）。**⚠ 重跑 setup 脚本会把绑定重置回环环**（脚本幂等重写 env）——重跑后须重做本节。

---

## 3. 发币：给新人开账户 + 发钱

**「新来的人」= 链上钱包 + 交易所账户，两样**：

```bash
# 1) 链上钱包：无需注册——生成种子即拥有（种子 = 全部财富与身份，交给新人离线保管）
wallet new --out alice.seed
# 种子（hex，⚠ 丢了没人能找回）：<64位hex>
# 地址：<40位hex>                              ← 把地址告诉付款方

# 2) 国库发资产（你用 treasury 种子签名；出块约 0.5-1 秒；发 AQ 加 --asset quote）
wallet transfer --seed @treasury.seed --to <alice地址> --amount 5
# 已提交（币）：5.00000000 → <地址>（交易哈希 …）
wallet transfer --seed @treasury.seed --to <alice地址> --amount 500 --asset quote
# 已提交（计价币）：500.00000000 → <地址>（交易哈希 …）

# 3) 验证
wallet balance <alice地址>        # 5.00000000
wallet info                        # 高度 +1
```

交易所账户在 §5 首登时自动开（见下）。

---

## 4. 充值：链上资产 → 交易所（币=持仓 / AQ=现金）

```bash
# 充币 → 现货持仓（1 手 = 1 币）
wallet deposit --seed @alice.seed --custody <custody地址> --account u1 --amount 5
# 已提交充值（币）：5.00000000 → custody …（memo=u1）

# 充 AQ → 场内现金（1 AQ = 1 现金——买币的钱）
wallet deposit --seed @alice.seed --custody <custody地址> --account u1 --amount 500 --asset quote
# 已提交充值（计价币）：500.00000000 → custody …（memo=u1）
```

- **memo = 交易所 userId（归账键，必填）**——无 memo 的充值不归账（资产在链上 custody 不丢，但换不到持仓/现金）。
- **约 1~2 秒到账**（桥 500ms 轮询 + 对账 1s）。交易所日志：
  ```
  [LEDGER-BRIDGE] 充币（币）：账户 u1 +5.00000000（from=<alice地址前12位>…）
  [LEDGER-SPOT]   补铸 5 手 → u1（IOU 5.00000000，台账已同步）
  [LEDGER-BRIDGE] 充币（计价币）：账户 u1 +500.00000000（from=…）
  [LEDGER-QUOTE]  现金入账 500 → u1（AQ IOU 500.00000000）
  ```
- 结果：`u1` 名下多出 **ax001 现货多头 5 手 + 场内现金 500**。
- 粉尘（不足 1 币的零头）不铸造，留在链上 custody。
- **买方路径**：只充 AQ 就是买方（有现金）；只充币就是卖方（有持仓）——两边都是真资产充值。

---

## 5. 交易（桌面端）

```bash
dotnet run --project apps/Stockapp/Acme.Stockapp.Desktop
```

1. **期货页 → 登录窗口**：交易前置 `tcp://<服务器IP>:17801`、行情前置 `tcp://<服务器IP>:17802`；userId 填 **`u1`**（必须与充值 memo 一致）、密码任意——**首登即开户**。
2. **行情 tab**：自动显示上市合约（`zx001` + `ax001`）。
3. **交易（两边都是真资产）**：买方（充了 AQ）用现金买开多（100% 保证金 = 全额付款）；
   卖方（充了币）卖平多——**卖出充币持仓的全额成交价即已实现现金**（充币零成本），
   例：充 5 币卖 5 手 @100 → 现金 +500（扣 5bp taker 费）。
4. **规则速查**：

| 项 | 值 |
|---|---|
| 买 = 开多（全额付款）/ 卖 = 平多 / **开空被拒** | 现货语义 |
| 最小变动 0.01 · 1 手 = 1 币 | ax001 |
| 挂单方 maker 2bp / 吃单方 taker 5bp（下单按 taker 预冻，maker 成交退差额） | 手续费 |
| 撮合：订单簿价格-时间优先、按 maker 价成交、自成交跳过 | 同期货 |
| 供给约束：撮合只转移持仓不增发，总持仓 ≤ custody 链上余额 | 结构性守死（CreditSpotPosition 是唯一铸造入口） |

---

## 6. 提币：交易所 → 链上（币=烧持仓 / AQ=扣现金）

```bash
# 提币（默认 asset=coin——烧现货持仓出金）
wallet withdraw --user u1 --password <登录密码> --to <alice地址> --amount 2
# 提币已提交（币（烧现货持仓））：2.00000000 → <地址>（交易哈希 …）

# 提 AQ（扣场内现金——静态可用闸门：锁定保证金/浮盈不可提）
wallet withdraw --user u1 --password <登录密码> --to <alice地址> --amount 100 --asset quote
# 提币已提交（计价币（扣场内现金））：100.00000000 → <地址>（交易哈希 …）
```

- **登录即鉴权**（绑定账户 = 出金账户）；币 = 先烧持仓再出金（买来的照样提）；AQ = 扣静态可用现金（锁定保证金/浮盈不可提）。
- 验证：`wallet balance <alice地址>`（双显示：币 + AQ）；桌面端持仓/资金三查。

**最小闭环（照抄可复现，批 6 形态）**：国库发 5 币 + 500 AQ → alice 充 5 币 + 500 AQ（memo=u1）
→ 登录 u1：持仓 5 手 + 现金 500 → 卖 2 手 @100（现金 +200）→ 提币 1 币 + 提 100 AQ
→ alice 链上：币 1 + AQ 100。整个经济的一圈——**全程交易所没发过一分钱**。

---

## 6.5 钱包页（桌面客户端，2026-10-08 起——零命令行操作面）

桌面 app 侧边栏「钱包」页（两租户都有）覆盖 CLI 的全部日常操作：**创建/导入钱包**（种子只此一次展示 + 备份确认）、**双余额显示**（币 + AQ）、**转账**、**充值到交易所**（币=持仓 / 计价币=现金，资产下拉选）、**从交易所提币**（币=烧持仓 / 计价币=扣现金，资产下拉选；地址留空默认本钱包）。设置区可改节点/custody/交易所地址（持久化，公网直连形态填 `<服务器IP>:17805/17800`——见 §2.D）。用户与 agent 共用同一个钱包（`<tenantDir>/ledger/agent-wallet.seed`）。

## 7. agent 玩法（可选）

桌面端 agent 已带 4 个工具（专家编辑器勾选即用，cat「链上钱包」）：`LedgerGetInfo` / `LedgerGetBalance`（缺省查自己钱包）/ `LedgerTransfer`（Dangerous——链上转账不可逆）/ `LedgerDepositToExchange`（充值）。

- **agent 自己的钱包**：`<tenantDir>/ledger/agent-wallet.seed`——首次签名操作自动生成（日志打印地址 ⚠ 备份提示）。种子每次调用现读（换文件免重启）、不出宿主进程、不进 LLM 上下文。给 agent 发币 = 转账到它日志里打印的地址。
- **连服务器的链**：公网直连形态（§2.D）设 env `AETHER_LEDGER_NODE_ADDR=<服务器IP>:17805` 即可零隧道；充值工具还需 env `AETHER_LEDGER_CUSTODY_ADDRESS=<custody地址>`。

---

## 8. 日常语义速查

| 操作 | 后果 |
|---|---|
| 重启链节点 | 链文件重放恢复（余额/nonce 续排）；**交易所不用动**——桥轮询自动重连 |
| 重启交易所 | 未成交挂单作废、客户端须重登；账本/铸造台账/现货状态全续用（is_spot 已落盘） |
| 重启前置 | 客户端重连；交易所/链均不动 |
| 随时独立复核 | `LedgerVerify.exe C:\Aether\ledger-data\ledger.chain`（零活进程依赖） |
| 种子保管 | treasury/custody = **财富**（丢了没人能找回）；producer = 出块权（可换 producer 重建链头） |
| 重部署链 | `deploy-ledger.yml` 触发；链数据在 `C:\Aether\ledger-data`（部署目录之外，/MIR 不动） |

## 附：本地全栈（不经服务器）

```bash
dotnet run --project apps/LedgerNode                                  # 链节点（新链自动生成，种子打印日志）
AETHER_LEDGER_CUSTODY_SEED=<hex> AETHER_CTP_SIM_BOARD=zx001,ax001 \
  dotnet run --project apps/CtpSim/Acme.Ctp.Sim.Host                  # 交易所（桥+现货接线）
dotnet run --project apps/CtpSim/Acme.Ctp.Front.Host                  # 前置 17801/17802
dotnet run --project apps/Stockapp/Acme.Stockapp.Desktop              # 客户端
# 验证器：dotnet run --project apps/LedgerVerify -- ~/.aether/ledger.chain
```
