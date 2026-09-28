# 本地 CTP 仿真柜台设计文档（「我就是交易所」）

> 状态：现行设计（2026-09-28 整理；含当日四批落地：交易所模式、做市商义务约束层、账号系统、**交易所模式设为默认 + 真实行情转发明确不做**）。
> 权威来源：代码（`business/Ctp/Acme.Ctp.{Simulator,Sim,Sim.Server}` + `apps/CtpSim/Acme.Ctp.Sim.Host` + `business/Ctp/Acme.Ctp.Infrastructure/Sim/`）+ CLAUDE.md「CTP 本地仿真柜台」节。本文档是设计综述，代码以仓库为准。

---

## 1. 一句话定位

**一个跑在本机/服务器上的独立期货交易所进程：多用户经薄 RPC 客户端接入，行情由参与者订单与做市机器人产生，撮合/风控/清算全部本地完成——用前置地址 `sim` 一键切换，真实链路零感知。**

| 对比项 | 外部柜台（SimNow / 实盘） | **本设计** |
|---|---|---|
| 可用性 | 受交易时段/网络/账户开通约束 | **随时可跑**、离线可跑、确定性可复现 |
| 极端场景 | 无法构造（穿仓/拒单/部分成交） | **可编程构造**（价格源可替换、参数可配） |
| 接入方式 | 官方 CTP SDK 线协议 | 自有 RPC（TCP + 长度前缀 JSON） |
| 多用户互为对手 | ❌（对手是外部市场） | ✅ **订单簿撮合 + 做市机器人供给流动性** |
| 客户端改动 | — | **零改动**（前置地址路由，同 ICtpSession 接缝） |

## 2. 动机

策略研发 / 下单流程测试 / 风控验证 / 交易逻辑回归此前全依赖外部柜台——受交易时段、网络、账户、行情质量约束，无法离线、无法确定性复现、无法构造极端场景。仿真柜台是**本地交易沙箱**；发展目标是「server 自己就是交易所」：不同用户接入、价格发现归簿、真实撮合体验（仿真资金）。

## 3. 形态与接缝（核心决策）

**独立服务端进程 + 薄 RPC 客户端，接缝在 `ICtpSession`（C# 侧），刻意不实现 CTP 线协议。**

「客户端零机制 + 真协议 + 独立服务端」三者不可兼得，本设计取**免协议**：

- **接缝 = `ICtpSession`**（`business/Ctp/Acme.Ctp.Domain/ICtpSession.cs`）：签名只用 Domain DTO，不泄漏 SDK 类型。`RoutingCtpSession` 一个注册即覆盖全部消费面——6 个 CTP AI 工具、桌面期货页、Lean 实盘链路、`IAgentExecutor` 链路，全部零改动。
- **为什么不替换官方 DLL**：官方 SDK ↔ 柜台间是私有二进制协议（握手/心跳/流控、与 SDK 版本强耦合、无文档只能逆向）。`externals/Ctp.Net.Core` 的 `ctp_native` 是我们自己的 24 函数扁平 C ABI，接缝放在它之上是唯一能不碰协议的位置。
- **代价（诚实说）**：非我方协议的第三方 CTP 客户端（快期/文华/TB 等官方 SDK 系软件）连不上——那是 Phase 2 的 native ABI 替换路线（见 §13）。
- **红利**：host **纯托管零原生依赖**（不引 externals），Linux VPS 上 self-contained publish 即跑；客户端 app 不携带仿真核心（Infrastructure 对 Simulator 的引用已删）。

## 4. 分层架构

```
business/Ctp/                          ← 限界上下文（域组织，非进程组织）
  Acme.Ctp.Domain         （零引用——ICtpSession 契约 + DTO + 错误码 + 交易日历）
  Acme.Ctp.Application    → Domain
  Acme.Ctp.Infrastructure → Domain + Sim + Ctp.Net.Core + Persistence   ┐ 客户端半边
  Acme.Ctp.Sim            → Domain        （薄协议：帧/RPC 客户端——两端共用）
  Acme.Ctp.Simulator      → Domain + Persistence                        ┐
  Acme.Ctp.Sim.Server     → Sim + Simulator                             ┘ 服务端半边

apps/CtpSim/Acme.Ctp.Sim.Host → Sim.Server        ← 唯一可执行文件（console，IHostedService）
```

- 客户端依赖闭包 = `Infrastructure → Sim(薄协议) → Domain`——撮合核心永远进不了客户端（引用图机器保证）。
- 「两个程序」的边界在 **apps/ 层**（可执行文件），不在 business/（按域组织）。服务端三件与客户端三件是同上下文的平级兄弟，标 `Tier=Other`。
- 测试 `tests/Acme.Ctp.Simulator.Tests` 同时覆盖两端（核心直驱 + `LoopbackSim` 回环契约）。

## 5. 线协议

- TCP `127.0.0.1:17800`（host 侧 `AETHER_CTP_SIM_ADDR` 可绑 `[host:]port`——公网/局域网部署用 `0.0.0.0:17800`；客户端侧同变量坏值容忍回默认）。
- 帧 = 4 字节小端长度前缀 + UTF-8 JSON（16 MiB 上限）。三类帧：
  - 请求 `{id,op,arg}` / 应答 `{id,ok,result}` 或 `{id,ok:false,err{code,msg}}` / 推送 `{ev,arg}`（ev ∈ order/trade/tick，无 id 不回执）。
- 连接即 **Hello** 握手回 `{protocolVersion}`（客户端不校验，仅诊断）。
- op 集：`login` / `logout` / `queryAccount` / `queryPositions` / `queryOrders` / `insertOrder` / `cancelOrder` / `subscribe` / `unsubscribe` / `queryInstruments`（无登录守卫——行情板数据源）。
- 应答 `err` 恒在客户端重建为 `CtpBusinessException(code,msg)`——**ErrorId 精度是接缝契约**（测试与消费方都断言），绝不能降级成 IOException 泛型。
- **顺序保证（关键，别拆）**：`SimTradingCore.InsertOrder` 在返回前同步抛订单/成交事件——朴素 RPC 会让推送先于应答到达，而 `CtpBrokerage` 在应答后才注册 OrderRef（先到的成交回报会被静默丢弃）。`SimConnection` 的 **capture-during-dispatch** 复刻真 CTP「rc 返回先于 SPI 回调」：本连接 op 执行期间路由给它的推送先入捕获缓冲，应答帧写入后同锁排空。跨线保序链：核心按序抛 → 出站通道 FIFO 单写者 → TCP FIFO → 客户端泵 FIFO。

## 6. 账号系统（2026-09-28）

**自助开户 + 密码认证（密码必填）+ 防爆破。**

- 登录帧带 `LoginArg(BrokerId, UserId, Password)`——对齐真实 CTP 登录核心三件（AppId/AuthCode 留客户端侧不消费）。
- **密码必填**：空/空白一律拒 90017——开新户也不例外，账户从出生即受保护。
- **首次登录 = 开户**：密码 PBKDF2-SHA256 哈希入档（`pbkdf2$迭代$盐hex$哈希hex` 自描述单列 `sim_account.password_hash`），发初始资金（默认 1e6）。
- **存量无密码账户**（升级前的库）首次带密码登录 = 补设（平滑迁移，`SqliteMigrations.AddColumn` 幂等补列）。
- **防爆破**：连续 5 次失败锁定 60 秒（`LoginMaxFailures`/`LoginLockoutSeconds` 可配，按 SimClock 计；锁定期内正确密码也拒；内存态重启清零）。
- `BrokerId` 记为账户元数据（身份键仍是 userId——单一交易所内经纪商是形式字段）。
- **服务端先认证后绑定**：凭据失败时连接不得绑上账户。
- 管理面：`QueryAccounts()`（资金/持仓/活单/是否设密）+ host 启动日志账户清单。

## 7. 交易核心

### 7.1 账本（`SimTradingCore` + `SimStore`）

- 全部状态变更在 `lock (_gate)` 内串行化；**事件在锁外触发**（回调可重入）；落盘尽力而为（fire-and-forget，失败只 Trace）。
- 5 张表：`sim_account` / `sim_position` / `sim_order` / `sim_trade` / `sim_instrument`。
- 资金口径：`Balance = StaticBalance + PositionProfit`；`Available = Balance − CurrMargin − FrozenMargin − FrozenCash`。冻结→消耗/退还两条路径严格区分。
- **重启恢复两条硬规则**：①挂单不复活（冻结额不可精确恢复——非终态单改 `'4'` + 清冻结）；②单号/成交号序列必须续排（重号会覆盖历史）。

### 7.2 风控与错误码

自定义错误码（刻意不冒充官方）：0 成功 / 90001 未登录 / 90002 未知合约 / 90003 手数 / 90004 价格 / 90005 资金不足 / 90006 无仓可平 / 90007 平仓超量 / 90008 报单不存在 / 90009 不可撤 / 90010 非交易时段 / 90011 参数非法 / 90016 无对手价 / 90017 凭据不匹配（90012-90015 为传输层保留段）。

### 7.3 合约表（「我就是交易所」的上架面）

- `InstrumentCatalog`：SIM 自有品种（sim001 系列：乘数 10/保证金 10%/tick 1/基准 3800）+ 9 个真实品种元数据（ag/au/cu/rb SHFE、m/i DCE、ta CZCE、IF CFFEX、sc INE）。
- 上市表默认 `sim001`，`AETHER_CTP_SIM_BOARD` 逗号分隔自定义；wire `queryInstruments` 供行情板。
- **解析链**（两条入口）：下单路径（合成锚价 = 下单价）/ 行情订阅路径（锚价 0 ⇒ 基价 100）——精确命中 → 品种派生（剥数字）→ 合成兜底（`AcceptUnknownInstruments`）。

## 8. 撮合与行情：交易所模式为默认的两种撮合 + 三态可用价格源

### 8.1 价格源（`SimOptions.MarketMode` + 驱动层）

> 2026-09-28 用户定调：**本柜台是自己的交易所，只需要交易所模式 + 历史 tick 回放**——交易所模式成为默认；随机游走降为显式退路（回放机制骑在它上面，不删）；真实行情转发（接外部期货行情）**明确不做**。

| 态 | 启用 | 行情来源 | 适用 |
|---|---|---|---|
| ① **交易所模式（默认）** | 不设变量 / `AETHER_CTP_SIM_MODE=exchange` | **订单簿顶 + 成交流**（价格发现归簿） | 多用户真实交易体验 |
| ② 历史 tick 回放 | `AETHER_CTP_SIM_REPLAY=<csv>`（设定即回放模式） | CSV 时间轴驱动注价（速度/循环可配） | 策略回归、确定性测试 |
| ③ 随机游走（显式退路） | `AETHER_CTP_SIM_MODE=walk` | 定时器推进中枢价，bid/ask = mid ∓ 1 tick | 旧行为、回放的宿主机制 |
| ④ 真实行情转发 | —— | —— | **明确不做**（不接外部期货行情） |

host 优先级（显式程度优先）：`REPLAY`（给了 CSV 就是回放）> `MODE=walk`（显式退路）> 默认交易所模式。**回放骑 walk 撮合机制**（`InjectQuote` 在交易所模式 no-op）⇒ `SimHostOptions.FromEnvironment` 在 REPLAY 设定时强制归一 `Walk`——不归一回放会静默失效。**回放启动失败 ⇒ 柜台退出**（不静默换价格源——回放是显式意图，静默换源会毁掉确定性测试）。`MODE` 坏值 ⇒ 启动抛（打错字宁可知晓）。`InjectQuote` 是 ②③ 的唯一注价入口（注前经解析链、注后重试挂单），交易所模式下 no-op。`SimOptions.MarketMode` 默认 = `Exchange`；walk 语义的测试用例全部显式 pin `MarketMode = SimMarketMode.Walk`。

### 8.2 walk 模式撮合（混合订单簿）

- 双层流动性：①`OrderBook`（每合约一本）：**客户端互为对手**——价格-时间优先、**maker 价成交**、自成交跳过；②庄家兜底 = `SimpleMatchingEngine`（随机游走报价做合成做市商，MaxFillVolume/PartialFillChance 模拟流动性）。
- 可吃边界 = min/max(限价, 庄家报价对手价)——簿与报价合并成有效最优价，劣价挂单等行情穿越走庄家路径。
- 空簿（单客户端）⇒ 纯庄家路径逐字节等于旧行为。

### 8.3 交易所模式（2026-09-28——「server 就是交易所」）

- **行情 = 簿顶 + 成交流**：bid/ask = 簿顶真实挂单（同价档聚合量、空侧 0 = 真实 CTP 单边市场语义）；last = **最新成交价**（首笔前 = 昨结算基准）；open/high/low/volume 从真实成交维护；open interest = 全市场多头合计。
- **tick 事件驱动**：挂单/撤单/成交即推（全在 `_gate` 内天然串行有序），无定时器；**订阅即推当前快照**。
- **成交回写行情**（`RecordTrade`）——walk 模式刻意不回写（pinned），交易所模式成交即行情。
- 撮合差异：庄家兜底关闭、市价单空簿拒 90016（自己的挂单不算对手——自成交跳过）、市价残量作废不挂单、可吃边界 = 限价本身（市价无界吃全簿）、FOK 干跑只看簿量。

## 9. 做市机器人 + 交易所约束层

**两层分离（用户定调）：交易所管约束（人人须守），做市商自管策略（各有各）。**

### 9.1 做市机器人（策略层的当前唯一实例）

- 账户 `SIM-MM`（`MarketMakerAccountId`）——**普通账户**：走完整风控/清算、资金一次性补足 1e9、与用户单同权同责。区别于旧庄家的无限流动性暗对手：**占真实簿档位、留成交记录、资金守恒、价格发现仍归簿**（用户挂单优于机器人即成簿顶）。
- 行为：围绕最新成交价 ± `MmSpreadTicks`(默认2)×tick 挂双边 GFD 限价单（单侧 `MmVolume` 默认 5 手）；价位过期撤旧、缺边补边；成交后移心重挂。触发点 = 用户下单尾部 + 合约激活；`_mmQuoting` 重入守卫防递归。
- 默认开（`AETHER_CTP_SIM_MM=off` 纯净交易所——无人挂单即无对手价、板空，真实死市场）。

### 9.2 交易所约束层（真实做市协议条款的强制面）

| 义务 | 参数（默认） | 强制行为 |
|---|---|---|
| 最大价差 | `MmMaxSpreadTicks` = 4 | 策略想挂更宽也被钳 |
| 最小报价量 | `MmMinQuoteVolume` = 1 | 单侧量下限 |
| 持仓限额 | `MmPositionLimit` = 200/合约/单边 | 触限停挂**加仓侧**（=「超限禁开」+ 最简库存偏斜：多头触限只剩卖报价） |
| 超限强平 | 同上 | 朝对手侧最优价挂 `Close` 限价单自动减仓（交易所强平的沙盒等价；同向已有活平仓单不堆积） |

- 平仓减仓单**不算报价义务、不被闸门拉**（它在卸库存）。
- **监测面**：`MmObligationMonitor`（1s 采样，`MmMonitorIntervalMs` 可调）——每合约双边报价时间占比（触限单边期**豁免**不计分母，真实做市协议的持仓豁免条款）、违约进入次数、持仓；`QueryMakerObligations()` 查询。
- **询价回应**：`RequestQuote(instrumentId)` 触发即时双边报价（wire 化给客户端按钮是后续小活）。
- 未做（策略层升级项）：连续库存偏斜、动态点差、多机器人竞争阵容——见 §13。

## 10. 客户端集成

- `RoutingCtpSession`（唯一 `ICtpSession` 注册）：前置 `sim` / `127.0.0.1:17800` ⇒ `RemoteCtpSession`；真实前置 ⇒ `CtpSessionService`。**切柜台 = 换前置地址重新登录**，无环境变量无重启。
- **行情随柜台**：sim 登录 ⇒ host RPC 推送（行情前置留空、无本地 MD 腿、看到的价 = 撮合用的价）；真实登录 ⇒ 真实前置行情。切换时订阅同步跟随（Connected 统一重载板 + 重订）。
- 多连接 profile：ctp.db 存多档案（前置+用户号），chip 点击即切换登录。
- `RemoteCtpSession`：泵 Channel 4096/DropOldest + RPC 客户端（单飞连接、15s 超时销毁、推送在接收线程触发、连接被拒包装 90012 含启动命令）。

**必须精确复刻的客户端契约**（否则上层静默出错）：①每个请求原样回显 `requestId`、每个响应恰好一次；②`MaxOrderRef` 可 `int.Parse`；③`ErrorID == 0` = 成功、错误串 GB18030（native 边界之上是 .NET 原生中文，线上原样透传）；④多行查询除末行外 `isLast=false`（接缝上一次返整列表，天然满足）；⑤`OrderInsertAsync` fire-and-forget（成功无错误通道，风控拒单 await 时抛）；⑥`OrderStatus` 用 CTP 字符码（'0'全成/'1'部分在队/'2'部分不在队/'3'在队/'4'不在队/'5'已撤）；⑦交易时段复用 `CtpTradingCalendar.IsTradingTime`。

## 11. 已知限制（诚实清单）

| 限制 | 说明 | 计划 |
|---|---|---|
| 一账户一客户端 | 同账户双连接 OrderRef 撞号（`Accept` 覆盖历史单） | 租约踢人（§13 批次二） |
| 无自动重连 | host 死 ⇒ Disconnected + 后续 90013；重登即重连 | 客户端自动重连（§13 批次二） |
| 无 TLS | 明文 JSON——**公网部署前必须加** | SslStream（§13 批次一） |
| 远端寻址 | `IsSimFront` 只认 `sim`/`127.0.0.1:17800`；远端目前靠 env 变量 | `sim://host:port` 前置地址（§13 批次一） |
| 单连接串行分发 | 内存操作 sub-ms，良性；客户端 15s 超时兜底 | 不急 |
| 第三方 CTP 软件连不上 | 刻意不实现线协议 | Phase 2 native ABI（§13） |
| Phase 1 习惯的 env 变量在客户端侧被忽略 | `AETHER_CTP_SIM_DB/TICK_MS/...` 全是 host 进程的变量 | 文档已明说 |

## 12. 测试（`tests/Acme.Ctp.Simulator.Tests/`，167 例）

纯托管回环 ⇒ 无需 native/柜台/外部进程：

- **核心直驱**：`LoginAndQueryTests` / `OrderLifecycleTests` / `RiskRejectionTests` / `AccountingInvariantTests`（资金守恒）/ `DeterminismTests`（固定种子可复现）/ `TradingTimeTests`。
- **跨账户撮合**：`CrossAccountMatchingTests`（walk 模式混合簿：maker 价/价格时间优先/自成交跳过/FOK 干跑）。
- **交易所模式**：`ExchangeModeTests`（12 例——空簿拒 90016/成交驱动最新价/吃全簿/做市移心/用户单成簿顶/持仓盈亏按最新成交价…）。
- **做市义务**：`MarketMakerObligationTests`（6 例——价差量钳制/触限停侧/超限自动平仓从对手买回/监测统计/询价）。
- **账号系统**：`AccountAuthTests`（8 例——首登设密/空密一律拒/存量补设/防爆破锁定解锁/凭据失败不绑定/跨线 E2E/落盘续用）。
- **传输层**：`SimRpcWireTests`（应答先于推送/并发无串扰/host 未启动 90012/账户隔离广播/订阅路由）+ `SessionAdapterTests`（`LoopbackSim` 回环契约，含双客户端互为对手 E2E）。
- **其它**：`RoutingSessionTests`（前置路由矩阵）/ `TickReplayTests` / `PersistenceTests`（host 重启 E2E）。
- `SimOptions` 是 sealed record + init-only ⇒ 测试用 `with` 改选项；时钟 `SimClock(() => now)` 可变闭包可推进。

## 13. 未来路线

### 批次一：公网上线的最小安全集（下一步）

1. **TLS**（`SslStream`）：server 侧证书 + 客户端验证。证书路线待定：域名 + Let's Encrypt（正规、要续期）vs 自签 + 首次连接信任钉住（无域名可跑、朋友圈规模）。
2. **`sim://host:port` 前置地址**：profile 直接存远端柜台地址，路由识别 + 端点解析（替代 env 覆盖）。
3. （账号系统已落地 ✓）

### 批次二：可用性（公网断连是常态）

1. **租约踢人**：服务端每账户持活租约，新登录踢旧连接 + `MaxOrderRef` 续排——解决一账户多端。
2. **客户端自动重连**：指数退避重连 → 存的凭据重登录 → 重订阅行情 → 查询补拉委托/成交/持仓。

### 批次三：运营

- 管理面：账户列表/资金调整/市场重置（core API 已有 `QueryAccounts()`，UI 后置）；部署脚本（systemd/Docker + 自包含发布）、`ctp-sim.db` 定时备份；注册门槛（开放 vs 邀请码）；连接限流。

### 策略层做市升级（异质性即真实感）

- **多机器人竞争阵容**：`RunMarketMaker` 参数化（点差/量/偏斜/上限）+ 多实例——窄点差高频型 + 宽点差深度型，盘口自然长出多档、点差竞争收敛。
- **连续库存偏斜**：持仓越多双边越朝卸仓方向移（当前只有限额触发的二态版）。
- **动态点差**：波动率/簿深自适应。

### Phase 2：native ABI 替换（第三方 CTP 客户端直连）

NativeAOT 产出与 `ctp_native` **同名同 ABI** 的共享库（`[UnmanagedCallersOnly(EntryPoint=…)]`），经 `NativeLibrary.SetDllImportResolver` 按环境变量切换——官方 SDK 系客户端（快期/文华/TB）即可直连本地柜台。适用：测真实 SDK 断线重连/心跳行为、拿现成 CTP 图形终端当客户端。已知限制：AOT 库不可卸载、同进程双 .NET 运行时不支持、不能跨 OS 编译、macOS `LC_ID_DYLIB` 路径问题。

### 真实行情转发：明确不做

（原路线图第四态）host 侧接真实 CTP MD 腿、真实 tick 注入同一管线——**2026-09-28 用户拍板划掉**：本柜台是自己的交易所，不接外部期货行情。价格只来自参与者订单（交易所模式）或历史数据（回放）。

### 远期候选

- 日结算/交易时段闸门（当前 24h 可跑是刻意简化）、开盘集合竞价。
- 性能：连接级并行分发、内存账本快照——当前规模（百用户、低 TPS）不需要。
- 持久化对齐仓库 EF Core 统一策略（`SqliteStoreBase` 遗留化路线）。

## 14. 快速上手

```bash
# 起柜台——默认就是交易所模式（多用户真实交易体验；无需任何环境变量）
dotnet run --project apps/CtpSim/Acme.Ctp.Sim.Host

# 确定性测试（历史 tick 回放；启动失败柜台会退出，不静默换价格源）
AETHER_CTP_SIM_REPLAY=ticks.csv dotnet run --project apps/CtpSim/Acme.Ctp.Sim.Host

# 旧行为（随机游走——显式退路）
AETHER_CTP_SIM_MODE=walk dotnet run --project apps/CtpSim/Acme.Ctp.Sim.Host

# 起桌面端（无需任何环境变量）
dotnet run --project apps/Stockapp/Acme.Stockapp.Desktop
```

1. 期货页登录窗口：前置地址填 `sim`、用户号 `alice`、**密码必填**（首登即开户设密）。
2. 行情 tab 自动订阅——板显示上市合约（默认 `sim001`），价 = 做市机器人围绕昨结算的双边报价。
3. 下单 → 状态经 `OrderReceived` 逐级推、成交经 `TradeReceived`；资金/持仓/委托三查返回仿真账本。
4. 多用户：各桌面实例用**不同账户**登录——挂单互为对手（maker 价成交）、双账户持仓镜像资金对账。
5. 切回真实柜台：前置地址改回真实前置重新登录（旧会话自动登出，行情板回证券库主力列表）。

**host 环境变量全表**：`AETHER_CTP_SIM_ADDR`（监听端点，默认 127.0.0.1:17800）/ `_DB`（库路径，默认 `~/.aether/ctp-sim.db`）/ `_TICK_MS`（walk tick 间隔）/ `_SEED` / `_BALANCE` / `_BOARD`（上市表）/ `_REPLAY`/`_REPLAY_SPEED`/`_REPLAY_LOOP`（回放）/ `_MODE`（exchange）/ `_MM`/`_MM_SPREAD`/`_MM_VOLUME`（做市）/ `_MM_MAX_SPREAD`/`_MM_MIN_VOLUME`/`_MM_MIN_RATIO`/`_MM_POS_LIMIT`/`_MM_MONITOR_MS`（做市义务）/ `_LOGIN_MAX_FAILURES`/`_LOGIN_LOCKOUT_SEC`（防爆破）。客户端侧零必设项（柜台由前置地址选）。
