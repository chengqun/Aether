# Aether 交易所运维手册（全栈：链 → 交易所 → 前置 → bot）

> 本文是**运维与交易手册**（怎么跑起来、怎么查、怎么挂 bot、怎么加算法）。设计文档见仓库 `CLAUDE.md`。
> 面向对象：交易所的运营者（你）+ 未来的算法交易者。
> 服务器：`117.72.186.128`（Windows，OpenSSH，密钥 `~/.ssh/ctpsim_deploy`）。

---

## 0. 全景：各模块与解耦关系

```
┌─────────────────── 链（资产层）───────────────────┐
│  AetherLedgerNode 服务   127.0.0.1:17805            │
│  coin（ax 币，21M 固定供给） + AQ（计价币，210 亿）   │
└──────────────┬───────────────────────────────────┘
               │ 链桥（custody 托管，只处理 AQ）
┌──────────────▼─────── 交易所（撮合）───────────────┐
│  AetherCtpExchange 服务  127.0.0.1:17800（JSON-RPC）  │
│  品种 ax001（AQ 计价保证金合约，10% 保证金=10 倍杠杆，│
│  双向持仓：买=开多 / 卖=开空 / 平多平空）              │
└──────────────┬─────────────────────────────────────┘
               │ JSON-RPC（每客户端会话一条连接）
┌──────────────▼────── 前置（CTP 线协议）────────────┐
│  AetherCtpFront 服务    17801 交易腿 / 17802 行情腿   │
│  官方 CTP 协议——任何 CTP 客户端都能连                │
└──────────────┬─────────────────────────────────────┘
               │ CTP 协议（与连真实期货公司同形）
   ┌───────────┴───────────┬─────────────────┐
   │ console 客户端（bot）  │ 桌面端 Stockapp │ 第三方 CTP 软件
   │ C:\Aether\ctp-console  │ 期货页/钱包页    │ （未来接其他工具）
   └───────────────────────┴─────────────────┘
```

**解耦要点**（每一层可以独立重启/替换，不影响其它层）：

| 层 | 生死影响 | 说明 |
|---|---|---|
| 链节点挂 | 交易所充值/提现暂停，**交易不受影响** | 链是资产的最终真相，交易所 IOU 从链重扫推导 |
| 交易所挂 | 交易停止，**链上资产无损** | 重启后：挂单作废、账本续用、客户端须重登 |
| 前置挂 | 客户端断连，**账本不动** | 前置可独立重启（真实故障注入）；客户端重登即恢复 |
| bot 挂 | 只是少了一个市场参与者 | 重跑即可，持仓/资金都在账本里 |
| 桌面端挂 | 什么都不影响 | GUI 只是又一个客户端 |

**资金语义（2026-10-10 定稿）**：
- **交易所只收付 AQ**（计价币）——充 AQ = 入金现金（1 AQ = 1 现金）；coin（币）**不进交易所**，留在链上钱包；
- 行情价格 = 1 coin 的 AQ 计价（当前 ax001 无成交时停在基价 100，有成交后随行就市）；
- ax001 与期货完全一致：双向持仓 + 10% 保证金（10 倍杠杆），可开多可开空。

---

## 1. 日常巡检（服务器命令）

SSH 上去后：

```powershell
# ① 三服务状态（都应 Running）
Get-Service AetherCtpExchange, AetherCtpFront, AetherLedgerNode

# ② 交易所尾部日志（上市表/账户清单/最近入账）
Get-Content C:\Aether\ctpsim-data\ctpsim-exchange.log -Tail 30 -Encoding UTF8

# ③ 前置尾部日志（客户端连接/报单流转）
Get-Content C:\Aether\ctpsim-data\ctpsim-front.log -Tail 30 -Encoding UTF8

# ④ 链节点状态（高度/交易数——心跳空块之外无交易 = 空闲链）
C:\Aether\LedgerWallet\Aether.Ledger.Wallet.exe info --node 127.0.0.1:17805

# ⑤ 链独立验证（不依赖任何活进程的余额真相）
C:\Aether\LedgerVerify\Aether.Ledger.Verify.exe C:\Aether\ledger-data\ledger.chain
```

### 账户列表（用户列表）

**当前没有实时的账户查询接口**（wire 层无 queryAccounts op）——两个途径：

```powershell
# ① 交易所启动日志的账户清单（每次重启打印）
Select-String -Path C:\Aether\ctpsim-data\ctpsim-exchange.log -Pattern "账户 \d+ 个" -Encoding UTF8 |
  Select-Object -Last 3

# ② 重启交易所（账本不丢，挂单作废+客户端重登）
Restart-Service AetherCtpExchange
Get-Content C:\Aether\ctpsim-data\ctpsim-exchange.log -Tail 20 -Encoding UTF8
# → 「账户 N 个（M 个已设密码）：botmm,botnoise,…」+ 每账户资金/持仓汇总
```

### 资金流水（充值/提现记录）

```powershell
Select-String -Path C:\Aether\ctpsim-data\ctpsim-exchange.log -Pattern "现金入账|现金对账扣减|提现" -Encoding UTF8 |
  Select-Object -Last 10
```

---

## 2. 服务与部署

### 2.1 三个 Windows 服务

| 服务 | 目录 | 端口 | 说明 |
|---|---|---|---|
| `AetherCtpExchange` | C:\Aether\CtpExchange | 127.0.0.1:17800 | 交易所（撮合核心 + JSON-RPC，只绑回环） |
| `AetherCtpFront` | C:\Aether\CtpFront | 0.0.0.0:17801/17802 | 前置（对客户端公网开放） |
| `AetherLedgerNode` | C:\Aether\LedgerNode | 0.0.0.0:17805 | 链节点（公网直连，钱包页/CLI 用） |

服务级 env 在注册表（`HKLM:\SYSTEM\CurrentControlSet\Services\<服务名>\Environment`）：
- 交易所：`AETHER_CTP_SIM_ADDR` / `AETHER_CTP_SIM_DB`（C:\Aether\ctpsim-data\ctp-sim.db） / `AETHER_CTP_SIM_LOG` / `AETHER_CTP_SIM_BOARD`（**上市合约表**） / `AETHER_LEDGER_CUSTODY_SEED`（桥托管种子）/ `AETHER_LEDGER_NODE_ADDR` / 防爆破 `AETHER_CTP_SIM_LOGIN_*`
- 前置：`AETHER_CTP_EXCHANGE_ADDR`（连本机交易所）/ `AETHER_CTP_FRONT_LOG`
- 链节点：`AETHER_LEDGER_ADDR` / `AETHER_LEDGER_CHAIN_FILE`（**链文件=账本本身，别动**） / `AETHER_LEDGER_PRODUCER_SEED` / 心跳间隔

改 env 后须 `Restart-Service` 生效。

### 2.2 部署 = push 即发（GitHub Actions）

| Workflow | 触发路径 | 部署什么 |
|---|---|---|
| `deploy-ctpsim.yml` | 交易所/前置代码 | 两个服务（停前置→停交易所→换文件→起交易所→起前置→TCP 探针） |
| `deploy-ledger.yml` | 链代码 | 链三件套（node/wallet/verify；重启链节点不影响交易所账本） |
| `deploy-ctp-console.yml` | console/算法/引擎代码 | console 到 C:\Aether\ctp-console（**不停任何进程**；实例 config 永不覆盖） |

手动重发：GitHub 仓库 → Actions → 选 workflow → Run workflow。

### 2.3 重启语义（谁该重启、谁不能）

- **前置**：随便重启（客户端重连，账本不动）——真实故障注入练习。
- **交易所**：要重启（挂单作废、客户端须重登、账本/序列续用不丢）。
- **链节点**：万不得已才动（链文件 append-only，重启安全，但**生产者种子必须在 env**——丢了出不了块）。
- **交易所 DB（ctp-sim.db）**：可重置（交易所私有数据，链桥会从链上重扫重建账户/现金——币在链上不丢）。

### 2.4 上市合约（BOARD）

当前：`ax001`（Aether 币价合约）。改法：

```powershell
$k = 'HKLM:\SYSTEM\CurrentControlSet\Services\AetherCtpExchange'
$envs = @((Get-ItemProperty $k).Environment | Where-Object { $_ -notlike 'AETHER_CTP_SIM_BOARD=*' })
$envs += 'AETHER_CTP_SIM_BOARD=ax001'          # 逗号分隔可加品种
Set-ItemProperty $k -Name Environment -Type MultiString -Value $envs
Restart-Service AetherCtpExchange
```

新 id 经品种派生/合成兜底（「我就是交易所」的上架自由），改完在期货页「查询合约」或 console `instruments` 可见。

---

## 3. 钱与账户（AQ 入金 → 现金 → 交易）

### 3.1 开户 = 首登

任何客户端（console/桌面期货页/第三方 CTP 软件）首次用「新账户名 + 密码」登录 = 自动开户（零余额）。双密码域独立：期货页走前置域、钱包页提现走明文域——同一个账户两扇门，首登各设各的。

### 3.2 充值（AQ 入金）——唯一进钱通道

**桌面钱包页**「充值到交易所」：金额（AQ）+ 交易所账号（**memo 归账键，必须与登录账户名一致**）→ 1~2 秒桥对账入账现金（1 AQ = 1 现金）。

验证（三选一）：

```powershell
# 服务器日志（带账户名与金额的入账行）
Select-String -Path C:\Aether\ctpsim-data\ctpsim-exchange.log -Pattern "现金入账" -Encoding UTF8 | Select-Object -Last 5

# console 登录查（最直接）
cd C:\Aether\ctp-console\botmm; ..\publish\Acme.Ctp.Console.exe
> account        # → 「可用 = xxx」

# 桌面期货页登录该账户 → 查资金
```

充不到账 → 查 `[错误]`：最常见是 **memo 没填账户名**（日志会有「⚠ 无 memo 充值」——AQ 还在链上 custody，没丢，重新充一笔 memo 填对的）。

### 3.3 提现（AQ 出金）

钱包页「提现」：交易所账号 + 密码 + 金额 → 扣场内**可用现金**（锁定保证金/挂单冻结/浮盈不可提——浮盈须平仓落袋）→ 链上 AQ 到你的钱包。

### 3.4 coin（币）的现状

币（coin）**只住链上钱包**（2026-10-10 币通道退役）：不能充进交易所、无提币通道。链上转账合法（钱包页/CLI `transfer`）。想变现 = 未来的兑换柜台功能（已设计搁置）。链上余额：

```powershell
C:\Aether\LedgerWallet\Aether.Ledger.Wallet.exe balance --seed <种子hex> --node 127.0.0.1:17805
# treasury 种子离线备份（node.log 里「新链已生成」行，见 ledger-runbook §2.A）
```

---

## 4. bot 挂机（双实例：做市商 + 噪声交易者）

### 4.1 布局（一个目录一个完整实例）

```
C:\Aether\ctp-console\
  publish\           ← 程序本体（CI 整替换，带 VERSION）
  botmm\             ← 做市商实例（config.json + labs\ + ctp.db + lean-algo-cache）
  botnoise\          ← 噪声实例（同构）
```

实例 `config.json` 由 CI 首次部署自动生成（**默认凭据：账户名 = 密码**），之后**部署永不覆盖**（改密码编辑该文件即可）。labs 算法每次部署从仓库 `algorithms/` 覆盖同步。

### 4.2 跑法（两个窗口）

```powershell
# ── 窗口 1：做市商（双边挂单提供流动性）────────────
cd C:\Aether\ctp-console\botmm
..\publish\Acme.Ctp.Console.exe
> account                                              # 验证现金到账
> tick off                                             # 挂机关 tick 打印
> strategy run market-maker ax001 --real --max-volume 5
[实盘] 状态: Running ← 挂着

# ── 窗口 2：噪声交易者（随机方向吃单制造成交）────────
cd C:\Aether\ctp-console\botnoise
..\publish\Acme.Ctp.Console.exe
> account
> tick off
> strategy run noise-taker ax001 --real --max-volume 3
```

**市场语义**：做市商在最新价上下挂阶梯买/卖单（双边流动性）→ 噪声随机会吃它 → 成交推动价格 → 做市商移档补挂 → 循环。两个账户互为对手，成交/持仓/盈亏都在各自账上。

### 4.3 挂机注意

- **断开 RDP（点 X）**：会话保留，进程继续跑 ✓；**注销（Sign out）**：进程全死 ✗。
- 验证还活着：`Get-Process Acme.Ctp.Console`（应 2 个）；或看前置日志有持续报单。
- 停止：各窗口 `strategy stop` → `quit`。
- **想做真正的 24h 无人值守**（重启自动拉起/无窗口）：说一声，给 console 加无 REPL 直跑模式 + Windows 计划任务（当前 RDP 断开方式够用）。

### 4.4 观察市场是否转起来

```powershell
# console 里（tick on 打开行情流）
> tick on
[tick] ax001 last=100.02 bid=100/5 ask=100.04/5 …    ← 双边有价 = 做市商在场

> positions
ax001 多 pos=…                                       ← 成交在累积

# 或服务器前置日志（报单/成交流转）
Get-Content C:\Aether\ctpsim-data\ctpsim-front.log -Tail 40 -Encoding UTF8
```

行情不动/单边 → 见 §7 排障。

---

## 5. 加新算法（核心用途：写算法 → 验证算法）

**算法即笔记**：`labs/leanalgorithms/*.lean.cs`（直写 Lean `QCAlgorithm` 子类），**仓库 `algorithms/` 是分发权威**。

### 5.1 流程（写 → 验 → 发 → 跑）

```
1. 写算法     本地写 my-algo.lean.cs（参考 algorithms/market-maker.lean.cs 的结构）
2. 入仓       cp 到仓库 algorithms/（跑 LeanBotStrategyCompileTests 编译锁定）
3. 信号验证   本地/服务器 console：strategy run my-algo ax001（不带 --real = 信号模式，
              只打信号日志不真实下单——验证逻辑零成本）
4. push       CI 自动部署 → labs 覆盖同步进两个实例目录
5. 实盘       console：strategy run my-algo ax001 --real --max-volume 3
```

### 5.2 console 策略命令速查

```
strategy list                                   ← 列出已编译算法（labs 加载结果）
strategy run <名> <合约> [
    --engine lean|strategy                      ← lean=QCAlgorithm 直写（默认）；strategy=CodeStrategy/YAML 动态壳
    --real                                       ← 真实下单（缺省=信号模式）
    --max-volume N                               ← 单笔开仓上限（保险丝，默认 1）
    --resolution Tick|Second|Minute|Hour|Daily   ← K 线分辨率（bot 用 Tick）
    --param k=v …                                ← 算法参数（对应 ParameterDefinitions）
    --verbose                                    ← OnData/Bar 调试日志
]
strategy stop
```

### 5.3 算法可用的信息与下单能力

- 行情：订阅合约的 tick（last/bid/ask/volume/OI）与聚合 K 线；
- 持仓/资金：`Portfolio`（Lean 标准对象），保证金 10%（10 倍杠杆）；
- 下单：限价/市价，买=开多、卖=开空、平多平空全部支持（与期货一致）；
- 手续费：maker 2bp / taker 5bp。

### 5.4 回测

同一份算法（QCAlgorithm）可在桌面回测页跑历史回测（引擎选 LeanAlgorithm）——写算法 → 回测调参 → 信号验证 → 实盘，一条链。

---

## 6. 端口与网络总表

| 端口 | 服务 | 绑定 | 公网 | 用途 |
|---|---|---|---|---|
| 17800 | 交易所 | 127.0.0.1 | 否（回环） | JSON-RPC：前置/钱包提现（远程走 SSH 隧道） |
| 17801 | 前置 | 0.0.0.0 | 是（防火墙放行） | CTP 交易腿——所有客户端 |
| 17802 | 前置 | 0.0.0.0 | 是 | CTP 行情腿 |
| 17805 | 链节点 | 0.0.0.0 | 是 | 链 RPC——钱包页/CLI |

---

## 7. 排障速查

| 现象 | 根因 | 处置 |
|---|---|---|
| 登录报 90017 凭据不匹配 | 密码错 / 账户已设密 | 核对密码；连续 5 次错锁 60 秒（等或看日志剩余秒数） |
| 下单报 90011 禁止开空 | **服务器交易所还是旧版**（开空解禁前） | 确认 CI deploy-ctpsim 跑完、交易所已重启 |
| 市价单报 90016 无对手价 | 该方向无人挂单（做市缺席/单边） | 先把做市商跑起来；或挂限价单等对手 |
| 下单报 90005 资金不足 | 账户没现金 | 钱包页充 AQ（§3.2），1~2 秒入账 |
| 查报单 30 秒超时 | 已知边界：空表时 SDK 不派发应答 | 管线自动恢复，重试即可（console 已包装成提示） |
| 充值不到账 | memo 没填账户名 | 日志查「无 memo」；AQ 在链上没丢，重新充（memo 对） |
| tick 不动 / 价格恒 100 | 做市商没跑（ask 空）或无成交 | 跑 market-maker；看 bid/ask 双边是否都有 |
| 客户端全部断线 | 前置挂了 | `Get-Service AetherCtpFront`；重启前置客户端重登（账本不动） |
| 提现报可提现金不足 | 现金被保证金/挂单/浮盈占用 | 平仓落袋或撤单释放后提 |
| 服务器重启后 bot 没跑 | RDP 会话结束 | 重新连上按 §4.2 跑（将来加开机自启） |

---

## 8. 错误码表（交易所自定义）

| 码 | 含义 |
|---|---|
| 0 | 成功 |
| 90001 | 未登录 |
| 90002 | 未知/未上市合约 |
| 90003/90004 | 手数/价格非法 |
| 90005 | 资金不足 |
| 90006/90007 | 无仓可平 / 平仓量超持仓 |
| 90008/90009 | 报单不存在 / 不可撤 |
| 90010 | 非交易时段 |
| 90011 | 参数非法（现货禁开空已解禁——出现此码多为参数问题） |
| 90016 | 无对手价（市价单空簿） |
| 90017 | 凭据不匹配（密码错/防爆破锁定） |
| 90018 | post-only 会吃单 |
| 90019 | 提现失败（现金不足/币通道已退役/桥未配——消息带具体原因） |

---

*维护：本文随架构演进同步更新（最近：2026-10-11 币通道退役 + 开空解禁 + console 客户端 + 自动部署三线）。设计依据见仓库根 CLAUDE.md。*
