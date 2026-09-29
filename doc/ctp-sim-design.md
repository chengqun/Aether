# 本地 CTP 仿真柜台设计文档（「我就是交易所」）

> 状态：现行设计（2026-09-28 定稿；当日完成：交易所模式设为默认、账号系统、**服务端去机器人化（纯交易所代码）**、**Lean 双 bot 实盘端到端跑通**）。
> 权威来源：代码（`business/Ctp/Acme.Ctp.{Simulator,Sim,Sim.Server}` + `apps/CtpSim/Acme.Ctp.Sim.Host` + `business/Ctp/Acme.Ctp.Infrastructure/Sim/` + `business/Trading.Runtime/Acme.Trading.Runtime.Lean/`）+ CLAUDE.md「CTP 本地仿真柜台」「Trading + Lean」节。本文档是设计综述，代码以仓库为准。

---

## 1. 一句话定位

**一个跑在本机/服务器上的独立期货交易所进程：多用户经薄 RPC 客户端接入，价格发现完全归订单簿，撮合/风控/清算全部本地完成——服务端零参与者代码（做市商/噪声交易者也是普通客户端），用前置地址 `sim` 一键切换，真实链路零感知。**

| 对比项 | 外部柜台（SimNow / 实盘） | **本设计** |
|---|---|---|
| 可用性 | 受交易时段/网络/账户开通约束 | **随时可跑**、离线可跑、确定性可复现 |
| 极端场景 | 无法构造（穿仓/拒单/部分成交） | **可编程构造**（价格源可替换、参数可配） |
| 接入方式 | 官方 CTP SDK 线协议 | 自有 RPC（TCP + 长度前缀 JSON），**上层契约与 CTP 精确同形** |
| 多用户互为对手 | ❌（对手是外部市场） | ✅ **订单簿撮合，参与者互为对手** |
| 参与者（做市商等） | 会员机构，独立接入 | ✅ 同样是普通客户端（Lean 策略笔记），服务端无任何特殊账户 |
| 客户端改动 | — | **零改动**（前置地址路由，同 `ICtpSession` 接缝） |

## 2. 动机

策略研发 / 下单流程测试 / 风控验证 / 交易逻辑回归此前全依赖外部柜台——受交易时段、网络、账户、行情质量约束，无法离线、无法确定性复现、无法构造极端场景。最终形态（用户定调 2026-09-28）：**「server 自己就是交易所」**——不同用户接入、价格发现归簿、真实撮合体验（仿真资金）；价格只来自参与者订单（交易所模式）或历史数据（回放），**不接外部期货行情**。

## 3. 形态与接缝（核心决策）

**独立服务端进程 + 薄 RPC 客户端，接缝在 `ICtpSession`（C# 侧），刻意不实现 CTP 线协议。**

「客户端零机制 + 真协议 + 独立服务端」三者不可兼得，本设计取**免协议**：

- **接缝 = `ICtpSession`**（`business/Ctp/Acme.Ctp.Domain/ICtpSession.cs`）：签名只用 Domain DTO（`CtpLoginRequest`/`CtpAccount`/`CtpPosition`/`CtpOrder`/`CtpOrderRequest`/`CtpTick`…），不泄漏任何 SDK 类型。`RoutingCtpSession` 一个注册即覆盖全部消费面——6 个 CTP AI 工具、桌面期货页、Lean 实盘链路（`CtpBrokerage`）、`IAgentExecutor` 链路，全部零改动。
- **为什么不替换官方 DLL**：官方 SDK ↔ 柜台间是私有二进制协议（握手/心跳/流控、与 SDK 版本强耦合、无文档只能逆向）。`externals/Ctp.Net.Core` 的 `ctp_native` 是我们自己的 24 函数扁平 C ABI，接缝放在它之上是唯一能不碰协议的位置。
- **代价（诚实说）**：非我方协议的第三方 CTP 客户端（快期/文华/TB 等官方 SDK 系软件）连不上——那是 Phase 2 的 native ABI 替换路线（见 §14）。
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
- 「两个程序」的边界在 **apps/ 层**（可执行文件），不在 business/（按域组织）。服务端三件与客户端三件是同上下文的平级兄弟。
- 测试 `tests/Acme.Ctp.Simulator.Tests`（162 例）同时覆盖两端（核心直驱 + `LoopbackSim` 回环契约）。

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

## 7. 交易核心（纯交易所的全部职责）

> 2026-09-28 定稿：服务端**零参与者概念**——没有任何账户有任何特殊身份（做市商、噪声交易者、普通用户在服务端眼里完全一样）。曾经内嵌的做市机器人/噪声交易者/义务监测**全部删除**：半成品的观察器（约定账户名、不强制、无消费方）不如不做；真要做 SHFE 式做市商制度，将来做成完整功能。env `AETHER_CTP_SIM_MM*`/`_NOISE*` 全部废除。

### 7.1 职责全清单

```
SimTradingCore = 撮合（订单簿/价格时间优先/maker价）+ 风控（保证金/涨跌停/平仓校验）
              + 账本（资金/持仓/报单/成交/tick 五表落盘）+ 账号（密码认证/防爆破）
              + 行情（簿顶+成交流事件驱动）+ 上市闸门 + 交易时段
```

### 7.2 账本（`SimTradingCore` + `SimStore`）

- 全部状态变更在 `lock (_gate)` 内串行化；**事件在锁外触发**（回调可重入）；落盘尽力而为（fire-and-forget，失败只 Trace）。
- 6 张表：`sim_account` / `sim_position` / `sim_order` / `sim_trade` / `sim_instrument` / **`sim_tick`**（成交 tick——真实交易消息：交易所模式每笔成交的完整快照，追加式日志；重启时行情会话统计从最后成交续上，不归零回基准）。
- 资金口径：`Balance = StaticBalance + PositionProfit`；`Available = Balance − CurrMargin − FrozenMargin − FrozenCash`。冻结→消耗/退还两条路径严格区分。
- **重启恢复三条硬规则**：①挂单不复活（冻结额不可精确恢复——非终态单改 `'4'` + 清冻结）；②单号/成交号序列必须续排（重号会覆盖历史）；③行情会话统计从最后成交 tick 续上（`Market.Restore`）。

### 7.3 风控与错误码

自定义错误码（刻意不冒充官方）：0 成功 / 90001 未登录 / 90002 未知合约·未上市 / 90003 手数 / 90004 价格 / 90005 资金不足 / 90006 无仓可平 / 90007 平仓超量 / 90008 报单不存在 / 90009 不可撤 / 90010 非交易时段 / 90011 参数非法 / 90016 无对手价 / 90017 凭据不匹配（90012-90015 为传输层保留段）。

### 7.4 合约表（「我就是交易所」的上架面）

- `InstrumentCatalog`：SIM 自有品种（sim001 系列：乘数 10/保证金 10%/tick 1/基准 3800）+ 9 个真实品种元数据（ag/au/cu/rb SHFE、m/i DCE、ta CZCE、IF CFFEX、sc INE）。
- 上市表默认 `sim001`，`AETHER_CTP_SIM_BOARD` 逗号分隔自定义；wire `queryInstruments` 供行情板。
- **解析链**（两条入口）：下单路径（合成锚价 = 下单价）/ 行情订阅路径（锚价 0 ⇒ 基价 100）——精确命中 → 品种派生（剥数字）→ 合成兜底（`AcceptUnknownInstruments`，walk 沙盒语义）。
- **交易所模式的上市闸门**：**上市表 = 唯一可交易/可订阅集合**——下单未上市 ⇒ 拒 90002「未上市」（品种可派生也不行，上架是显式决定）；订阅未上市 ⇒ 不激活。上市表自身解析保留完整链条**含合成**（`BOARD` 写什么都能上——上架自由）。walk 模式不受闸门约束（客户端行情板来自证券库，需解析链自由）。

## 8. 撮合与行情：交易所模式为默认 + 两个显式退路

### 8.1 价格源

| 态 | 启用 | 行情来源 | 适用 |
|---|---|---|---|
| ① **交易所模式（默认）** | 不设变量 / `AETHER_CTP_SIM_MODE=exchange` | **订单簿顶 + 成交流**（价格发现归簿） | 多用户真实交易体验 |
| ② 历史 tick 回放 | `AETHER_CTP_SIM_REPLAY=<csv>`（设定即回放模式） | CSV 时间轴驱动注价（速度/循环可配） | 策略回归、确定性测试 |
| ③ 随机游走（显式退路） | `AETHER_CTP_SIM_MODE=walk` | 定时器推进中枢价，bid/ask = mid ∓ 1 tick | 旧行为、回放的宿主机制 |
| ④ 真实行情转发 | —— | —— | **明确不做**（不接外部期货行情，价格只来自参与者或历史数据） |

host 优先级（显式程度优先）：`REPLAY` > `MODE=walk` > 默认交易所模式。**回放骑 walk 撮合机制**（`InjectQuote` 在交易所模式 no-op）⇒ `SimHostOptions.FromEnvironment` 在 REPLAY 设定时强制归一 `Walk`——不归一回放会静默失效。**回放启动失败 ⇒ 柜台退出**（不静默换价格源）。`MODE` 坏值 ⇒ 启动抛。`SimOptions.MarketMode` 默认 = `Exchange`；walk 语义测试显式 pin `MarketMode = SimMarketMode.Walk`。

### 8.2 walk 模式撮合（混合订单簿——显式退路）

- 双层流动性：①`OrderBook`：**客户端互为对手**——价格-时间优先、**maker 价成交**、自成交跳过；②庄家兜底 = `SimpleMatchingEngine`（随机游走报价做合成做市商）。
- 空簿（单客户端）⇒ 纯庄家路径逐字节等于旧行为。

### 8.3 交易所模式（「server 就是交易所」）

- **行情 = 簿顶 + 成交流**：bid/ask = 簿顶真实挂单（同价档聚合量、空侧 0 = 真实 CTP 单边市场语义）；last = **最新成交价**（首笔前 = 昨结算基准）；open/high/low/volume 从真实成交维护；open interest = 全市场多头合计。
- **tick 事件驱动**：挂单/撤单/成交即推（全在 `_gate` 内天然串行有序），无定时器；**订阅即推当前快照**（订阅 op 先登记订阅再激活——激活同步外发的快照落进 capture-during-dispatch，应答帧后送达）。
- **成交回写行情**（`RecordTrade`——每笔成交即时更新最新价/开高低/累计量并外发含新簿顶的 tick；成交 tick 同时落盘 `sim_tick`）——walk 模式刻意不回写（pinned）。
- 撮合差异：庄家兜底关闭、市价单空簿拒 90016（自己的挂单不算对手——自成交跳过）、市价残量作废不挂单（`'2'`）、可吃边界 = 限价本身（市价无界吃全簿）、FOK 干跑只看簿量。
- **流动性完全由参与者供给**：不起做市客户端 = 空闲市场无对手价、板空——真实交易所本义。

## 9. 参与者：全部是普通客户端（「bots 即 Lean 策略」，2026-09-28 端到端跑通）

做市商与噪声交易者不是服务端代码，而是 **labs `leanalgorithms/*.lean.cs` 策略笔记 + 独立账户**：

- **`market-maker.lean.cs`**：围绕最新成交价双边挂限价单、成交后移心重挂、持仓限额自律、超限减仓。挂单用 `OrderTicket` 自持跟踪（brokerage 的 `GetOpenOrders` 刻意返空——引擎外订单不注册，用它 = 每帧重复挂单）。
- **`noise-taker.lean.cs`**：Schedule 定时随机方向市价吃单（IOC）、净持仓软钳、固定种子可复现。
- 跑法 = 桌面**双实例**（实盘 tab：策略引擎选 `LeanAlgorithm` → 算法选笔记 → 合约 `sim001` → 勾「真实下单」→ 单笔开仓上限 ≥ 单侧量）；macOS 多开用 `open -n` 或两终端 `dotnet run`。已实盘验证：MM 挂双边 → noise 吃 MM → MM 部分成交 → 移心重挂 → 成交记录落库，市场自转。
- 策略参数（点差/单侧量/持仓限额）在策略笔记的 `ParameterDefinitions`（实盘 tab 动态渲染）——**报价自律完全在策略侧**。将来加第三个参与者（动量、套利）= 再写一个笔记，零服务端代码。
- 策略源码的编译锁定测试：`tests/Acme.Trading.Runtime.Tests/LeanBotStrategyCompileTests`（镜像 labs 文件——labs 是用户数据不进仓，改实验室文件须同步测试）。

## 10. 客户端集成与 CTP 兼容性

### 10.1 为什么 CTP 客户端能直接登录本柜台

**因为接缝在 `ICtpSession`（C# 接口），不在 CTP 线协议——客户端从头到尾没说过一句 CTP 二进制协议，我们也不听。** 具体三层：

1. **同一个接口，两个后端**：桌面端所有 CTP 消费面（登录窗口 `CtpLoginWindow`、连接器 `CtpConnectorViewModel`、期货页 `CtpViewModel`、6 个 AI 工具、Lean 实盘 `CtpBrokerage`）都只面向 `ICtpSession`。真实前置 ⇒ `CtpSessionService`（native SDK 链路）；前置 `sim` ⇒ `RemoteCtpSession`（薄 RPC 连本 host）。`RoutingCtpSession` 是唯一注册，按前置地址文本路由——**切柜台 = 换前置地址重新登录**，登录表单、字段（BrokerId/UserId/Password）、流程一个字节不变。
2. **登录语义逐项对齐真实 CTP**（客户端代码因此零感知）：
   - 登录参数同名同义：`CtpLoginRequest{FrontAddress, MdFrontAddress, BrokerId, UserId, Password, AppId, AuthCode}`——sim 后端消费前三件（AppId/AuthCode 留客户端侧），字段集与真实登录完全一致。
   - 返回 `CtpLoginResult{TradingDay, Time, FrontId, SessionId, MaxOrderRef}`——`MaxOrderRef` 必须可 `int.Parse`（native 侧 `CtpTrader.LoginAsync` 拿它做下一单号起点），本柜台返回账户报单序列当前值。
   - 错误重建：wire `err{code,msg}` 恒在客户端重建为 `CtpBusinessException(ErrorId, ErrorMsg)`——ErrorId 精度是接缝契约；真实链路的错误串在 native 边界（`CtpTrader.DecodeMsg`）已按 GB18030 解成 .NET 原生中文串，接缝之上两边都是托管字符串，线上原样透传即可。
   - **先认证后绑定**：登录失败（90017）时连接不绑账户——对齐「`OnRspOrderInsert` 只在前端校验失败时触发」的真实语义。
3. **上层交易契约逐条复刻**（`CtpBrokerage`/`TradingHostAlgorithm`/VM 靠这些不变量工作，任何一条走样都会静默出错）：
   - 每个请求原样回显 `requestId`、每个响应恰好送达一次；
   - 多行查询（资金/持仓/委托）除末行外 `isLast=false`（接缝上一次返整列表，天然满足）；
   - `OrderInsertAsync` fire-and-forget：成功无错误通道，先返回 `CtpOrderInsertResult`，状态异步经 `OrderReceived`/`TradeReceived` 推；风控拒单在 await 时抛异常；
   - `OrderStatus` 用 CTP 字符码（`'0'`全成/`'1'`部分在队/`'2'`部分不在队/`'3'`在队/`'4'`不在队/`'5'`已撤；`IsLive`=`'3'`/`'1'`，`IsTerminal`=`'0'`/`'2'`/`'5'`/`'4'`）；
   - 推送顺序：应答先于本单的推送（capture-during-dispatch）；
   - 交易时段复用 `CtpTradingCalendar.IsTradingTime`。

### 10.2 行情随柜台与订阅切换

- sim 登录 ⇒ 行情为 host RPC 推送的**交易所撮合价**（行情前置留空即可——`RemoteCtpSession` 无本地 MD 腿；看到的价 = 撮合用的价，沙盒自洽）；真实登录 ⇒ 真实前置行情。切换柜台时行情板/订阅自动跟随（Connected 统一重载板 + 重订）。
- 多连接 profile：ctp.db 存多档案（前置+用户号），chip 点击即切换登录。
- `RemoteCtpSession`：泵 Channel 4096/DropOldest + RPC 客户端（单飞连接、15s 超时销毁、推送在接收线程触发、连接被拒包装 90012 含启动命令）。

### 10.3 Lean 实盘链路（bots 的接入通道——P2 真实下单已实现）

实盘 tab「真实下单」勾选框（`TradingViewModel.LiveRealOrders` → `LiveTradingRunInput.EnableRealOrders` → `CtpLeanGateway.TradingOptions`）控制 `CtpBrokerage.PlaceOrder`：关（默认）= P1 信号模式（拦截下单仅记日志）；**开 = 经 `ICtpSession.OrderInsertAsync` 真实报单**（Lean Order → `CtpOrderRequest` 映射：SHFE 拆腿平昨/平今、开仓保险丝封顶、市价模拟成对手价限价单 + **IOC**（吃单语义——残量立即作废不挂簿）、**反转拆腿**（Lean 净仓位思考 → CTP 平仓腿+开仓腿）；成交/报单回报 → Lean OrderEvent：Submitted → PartiallyFilled/Filled/Canceled/Invalid，跨腿聚合 + TradeId 去重 + 竞态仲裁；10s 看门狗兜前置静默拒单；持仓回填 + offset 决策持仓缓存）。sim 交易所与真实前置同链路（前置地址路由）。

**实盘数据管线（六层自举链，2026-09-28 实测逐层修复后钉死）**——交易所模式事件驱动行情 × Lean 实时引擎的接合部，六层各有一刀：
1. `BarBuilder` tick/second 分辨率单点棒**立即外发**（不等下一 tick 关棒——事件驱动行情下首帧快照永不成棒）；
2. `CtpBrokerage.Subscribe` **先登记 `_symbolByInstrument` 再触发 CTP 订阅**（快照在接收线程同拍到达，登记晚一拍就被 TryGetValue 丢弃）；
3. `CtpBar : BaseData, IBar`（`SecurityCache.ProcessDataPoint` 经 `data as IBar` 更新 `Price=Close`——不实现接口则 `Security.Price` 恒 0，策略的 Price 守卫拦死一切下单且零报错）；
4. `AddCtpData` 传显式 `TimeZones.Utc`（`AddDataImpl` 显式时区触发 `SetEntryAlwaysOpen`——不传则落 NYSE 时段被市场时段过滤；且 tick 盖戳须用**客户端 UTC 时钟**（`LiveTickClock`），不能用 host 墙钟——跨时区会把数据标到未来，被 `FrontierAwareEnumerator` 扣住永不发射）；
5. 限价族取价用**类型化 `LimitOrder.LimitPrice`**（这个 Lean 版本里 `Order.Price` 对限价单恒 0）；
6. 市价模拟用 IOC + 反转拆腿（见上）。
诊断方法论的教训已入记忆：**症状不变时沿数据流逐跳验证「帧/状态死在哪一跳」**，别在同一跳反复加码；逐跳仪表日志（`[Tick]`/`[Bar]`/策略 `BotLog`）是定位工具。

## 11. 已知限制（诚实清单）

| 限制 | 说明 | 计划 |
|---|---|---|
| 一账户一客户端 | 同账户双连接 OrderRef 撞号（`Accept` 覆盖历史单） | 租约踢人（§14 批次二） |
| 无自动重连 | host 死 ⇒ Disconnected + 后续 90013；重登即重连 | 客户端自动重连（§14 批次二） |
| 无 TLS | 明文 JSON——**公网部署前必须加** | SslStream（§14 批次一） |
| 远端寻址 | `IsSimFront` 只认 `sim`/`127.0.0.1:17800`；远端目前靠 env 变量 | `sim://host:port` 前置地址（§14 批次一） |
| 单连接串行分发 | 内存操作 sub-ms，良性；客户端 15s 超时兜底 | 不急 |
| 第三方 CTP 软件连不上 | 刻意不实现线协议 | Phase 2 native ABI（§14） |
| 空闲市场无对手价 | 不起做市客户端 ⇒ 板空、市价单被拒——真实交易所本义 | 不是缺陷（是设计） |
| Phase 1 习惯的 env 变量在客户端侧被忽略 | `AETHER_CTP_SIM_DB/TICK_MS/...` 全是 host 进程的变量 | 文档已明说 |

## 12. 测试

**`tests/Acme.Ctp.Simulator.Tests/`（162 例，纯托管回环 ⇒ 无需 native/柜台/外部进程）**：

- **核心直驱**：`LoginAndQueryTests` / `OrderLifecycleTests` / `RiskRejectionTests` / `AccountingInvariantTests`（资金守恒）/ `DeterminismTests`（固定种子可复现）/ `TradingTimeTests`。
- **跨账户撮合**：`CrossAccountMatchingTests`（walk 模式混合簿：maker 价/价格时间优先/自成交跳过/FOK 干跑）。
- **交易所模式**：`ExchangeModeTests`（13 例——空簿拒 90016/未上市拒 90002/成交驱动最新价/吃全簿/市价残量作废/订阅即推快照（含 wire 级路由回归——订阅 op 先登记再激活）/持仓盈亏按最新成交价/成交 tick 落盘跨重启恢复…）。
- **账号系统**：`AccountAuthTests`（8 例——首登设密/空密一律拒/存量补设/防爆破锁定解锁/凭据失败不绑定/跨线 E2E/落盘续用）。
- **传输层**：`SimRpcWireTests`（应答先于推送/并发无串扰/host 未启动 90012/账户隔离广播/订阅路由）+ `SessionAdapterTests`（`LoopbackSim` 回环契约，含双客户端互为对手 E2E）。
- **其它**：`RoutingSessionTests`（前置路由矩阵/上市闸门/归一）/ `TickReplayTests` / `PersistenceTests`（host 重启 E2E）。
- `SimOptions` 是 sealed record + init-only ⇒ 测试用 `with` 改选项；时钟 `SimClock(() => now)` 可变闭包可推进。

**`tests/Acme.Trading.Runtime.Tests/`（79 例）**：`CtpBrokerageTradingTests`（P2 开关分流/报单/回报翻译/SHFE 拆腿/限价类型化取价/市价 IOC/反转拆腿）+ `CtpBrokerageDataTests`（首 tick 立即成棒/快照竞态/分钟级跨边界/IBar 价格更新）+ `CtpOrderRequestMapperTests`（含反转拆腿三态）+ `LeanBotStrategyCompileTests`（双 bot 策略编译锁定）+ `LeanAlgorithmTemplateCompileTests` + 既有策略/回测套件。

## 13. 演进史（一页）

| 日期 | 里程碑 |
|---|---|
| 2026-09-25 | 独立 host 进程形态定稿（用户否决进程内-only）；接缝 `ICtpSession` 免协议 |
| 2026-09-26 | 混合订单簿（跨账户互为对手）+ 上市合约表 + 多连接 profile |
| 2026-09-27 | 历史 tick 回放（价格源之二） |
| 2026-09-28 上午 | 交易所模式（价格发现归簿、事件驱动行情）+ 做市义务约束层 + 账号系统 |
| 2026-09-28 下午 | 交易所模式设为默认、真实行情转发明确不做、上市闸门、成交 tick 落盘、噪声交易者 |
| 2026-09-28 晚 | **「bots 即 Lean 策略」收尾**：P2 真实下单接线到实盘 tab（引擎选择器回测/实盘通用）、双 bot 策略笔记交付、六层自举链逐层修复后**端到端跑通**、服务端**两次删除**（内嵌机器人 → 义务监测）——**纯交易所代码定稿** |

## 14. 未来路线

### 批次一：公网上线的最小安全集（下一步）

1. **TLS**（`SslStream`）：server 侧证书 + 客户端验证。证书路线待定：域名 + Let's Encrypt（正规、要续期）vs 自签 + 首次连接信任钉住（无域名可跑、朋友圈规模）。
2. **`sim://host:port` 前置地址**：profile 直接存远端柜台地址，路由识别 + 端点解析（替代 env 覆盖）。
3. （账号系统已落地 ✓）

### 批次二：可用性（公网断连是常态）

1. **租约踢人**：服务端每账户持活租约，新登录踢旧连接 + `MaxOrderRef` 续排——解决一账户多端。
2. **客户端自动重连**：指数退避重连 → 存的凭据重登录 → 重订阅行情 → 查询补拉委托/成交/持仓。

### 批次三：运营

- 管理面：账户列表/资金调整/市场重置（core API 已有 `QueryAccounts()`，UI 后置）；部署脚本（systemd/Docker + 自包含发布）、`ctp-sim.db` 定时备份；注册门槛（开放 vs 邀请码）；连接限流。
- headless bot runner（复用 `LeanLiveTradingService` 的 DI 组合，无 GUI 服务器 24/7 跑双 bot）。

### 策略层参与者升级（异质性即真实感——全在 labs 笔记，零服务端代码）

- **多机器人竞争阵容**：复制/改写策略笔记多实例——窄点差高频型 + 宽点差深度型，盘口自然长出多档、点差竞争收敛。
- **连续库存偏斜**：持仓越多双边越朝卸仓方向移（当前只有限额触发的二态版）。
- **动态点差**：波动率/簿深自适应。
- **噪声真实感**：重尾量分布（90% 小单 + 10% 中单）、动量偏置（价格走出趋势）、日内节律。

### Phase 2：native ABI 替换（第三方 CTP 客户端直连）

NativeAOT 产出与 `ctp_native` **同名同 ABI** 的共享库（`[UnmanagedCallersOnly(EntryPoint=…)]`），经 `NativeLibrary.SetDllImportResolver` 按环境变量切换——官方 SDK 系客户端（快期/文华/TB）即可直连本地柜台。适用：测真实 SDK 断线重连/心跳行为、拿现成 CTP 图形终端当客户端。已知限制：AOT 库不可卸载、同进程双 .NET 运行时不支持、不能跨 OS 编译、macOS `LC_ID_DYLIB` 路径问题。

### 远期候选

- 日结算/交易时段闸门（当前 24h 可跑是刻意简化）、开盘集合竞价。
- 性能：连接级并行分发、内存账本快照——当前规模（百用户、低 TPS）不需要。
- 持久化对齐仓库 EF Core 统一策略（`SqliteStoreBase` 遗留化路线）。

## 15. 快速上手

```bash
# 起交易所——默认就是交易所模式（无需任何环境变量；纯撮合，无内嵌参与者）
dotnet run --project apps/CtpSim/Acme.Ctp.Sim.Host

# 确定性测试（历史 tick 回放；启动失败柜台会退出，不静默换价格源）
AETHER_CTP_SIM_REPLAY=ticks.csv dotnet run --project apps/CtpSim/Acme.Ctp.Sim.Host

# 旧行为（随机游走——显式退路）
AETHER_CTP_SIM_MODE=walk dotnet run --project apps/CtpSim/Acme.Ctp.Sim.Host

# 起桌面端（无需任何环境变量）——双 bot 场景跑两个实例（open -n / 两终端 dotnet run）
dotnet run --project apps/Stockapp/Acme.Stockapp.Desktop
```

1. 期货页登录窗口：前置地址填 `sim`、用户号 `alice`、**密码必填**（首登即开户设密）。
2. 行情 tab 自动订阅——板显示上市合约（默认 `sim001`）；价 = 簿里真实挂单（无人报价则空——空闲市场是交易所本义）。
3. 下单 → 状态经 `OrderReceived` 逐级推、成交经 `TradeReceived`；资金/持仓/委托三查返回账本。
4. **双 bot**：两个桌面实例各登不同账户（推荐 SIM-MM / SIM-NOISE），实盘 tab 各跑 `market-maker` / `noise-taker`（引擎 LeanAlgorithm + 勾「真实下单」+ 单笔开仓上限 ≥ 单侧量）——行情板开始跳动。
5. 多用户：各桌面实例用**不同账户**登录——挂单互为对手（maker 价成交）、持仓镜像资金对账。
6. 切回真实柜台：前置地址改回真实前置重新登录（旧会话自动登出，行情板回证券库主力列表）。

**host 环境变量全表**（客户端侧零必设项——柜台由前置地址选）：`AETHER_CTP_SIM_ADDR`（监听端点，默认 127.0.0.1:17800）/ `_DB`（库路径，默认 `~/.aether/ctp-sim.db`——与真实 `ctp.db` 分文件，真实库存明文 CTP 密码）/ `_TICK_MS`（walk tick 间隔）/ `_SEED`（walk 种子）/ `_BALANCE`（新账户初始资金）/ `_BOARD`（上市表，默认 sim001）/ `_REPLAY`/`_REPLAY_SPEED`/`_REPLAY_LOOP`（回放）/ `_MODE`（exchange 默认 / walk 退路，坏值拒启动）/ `_LOGIN_MAX_FAILURES`/`_LOGIN_LOCKOUT_SEC`（防爆破）。（`_MM*`/`_NOISE*` 系列已随服务端去机器人化**全部废除**。）
