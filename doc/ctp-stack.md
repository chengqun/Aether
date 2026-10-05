# CTP 交易栈参考（客户端 / 前置服务器 / 交易所 / 真实柜台）

> **状态**：已完成（2026-10-03）。本地前置服务器可以正常连接、正常交易——双 bot（market-maker + noise-taker）经官方 SDK 直连本地前置跑通全链路。
> 权威来源：代码。本文档是综述参考，具体以仓库为准（CLAUDE.md「CTP 线协议前置」节有机器可执行的细节）。

---

## 1. 一句话定位

**四层可分离的交易栈：真实 CTP 客户端（官方 SDK）→ 前置服务器（逆向线协议翻译）→ 交易所（本地撮合引擎）→ 真实柜台（SimNow 备选）。** 客户端不感知连的是真实前置还是本地前置——切换柜台 = 换前置地址重新登录。

```
┌─────────────────────────────────────────────────────────┐
│                    客户端（不变）                         │
│  externals/Ctp.Net.Core（官方 C++ SDK P/Invoke 封装）    │
│  business/Ctp/Acme.Ctp.Infrastructure                   │
│    CtpSessionService : ICtpSession                      │
│    （唯一注册——前置地址选柜台）                            │
├─────────────────────────────────────────────────────────┤
│                 前置服务器（线协议翻译）                    │
│  business/CtpFront/Acme.Ctp.Front                            │
│    CtpFrontServer   : 17801（交易腿）                    │
│    CtpFrontMdServer : 17802（行情腿）                    │
│    把 CTP 线协议帧 ↔ SimTradingCore 方法调用 互译          │
├─────────────────────────────────────────────────────────┤
│                  交易所（撮合/账本/行情）                  │
│  business/CtpExchange/Acme.Ctp.Simulator                        │
│    SimTradingCore（撮合/风控/持仓/行情引擎）               │
│  business/Ctp/Acme.Ctp.{Sim, Sim.Server}                │
│    JSON-RPC 旧路径（与前置并存于同一 host）                │
├─────────────────────────────────────────────────────────┤
│                真实柜台（SimNow，备选）                    │
│  前置地址填 SimNow 地址 ⇒ 同一客户端直连真实柜台            │
└─────────────────────────────────────────────────────────┘
```

## 2. 四层各自职责

| 层 | 项目 | 职责 | 依赖方向 |
|---|---|---|---|
| **客户端** | `externals/Ctp.Net.Core` + `Acme.Ctp.Infrastructure` | P/Invoke 官方 SDK → `ICtpSession` 契约；登录/查询/报单/行情订阅/推送事件 | 引用 Domain DTO，不感知前置/交易所 |
| **前置服务器** | `Acme.Ctp.Front` | TCP 监听 → 线协议分帧 → op 签名分派 → `SimTradingCore` 调用 → 模板化应答/推送 | 只引 `Simulator`（门面）+ `Domain` |
| **交易所** | `Acme.Ctp.Simulator` + `Sim` + `Sim.Server` | 撮合/账本/风控/行情引擎（协议无关）；JSON-RPC 旧路径 | 模型与传输完全解耦 |
| **真实柜台** | —（外部） | SimNow 仿真环境或实盘柜台 | 客户端直接连，前置服务器不参与 |

## 3. 客户端（真实 CTP 客户端栈）

### 组件

- **`externals/Ctp.Net.Core`**（C++ + C# 四层）：
  - 官方 CTP SDK 二进制（`thosttraderapi_se` / `thostmduserapi_se` framework + `ctp_native.dylib/so/dll`）
  - C++ wrapper（`TraderWrapper.cpp` / `MdWrapper.cpp`）导出扁平 `extern "C"` ABI 24 函数（`CtpTrader_*` 16 + `CtpMd_*` 8）
  - C# P/Invoke（`NativeMethods.cs: DllImport("ctp_native")`）
  - `CtpTrader` / `CtpMarketData` 管理类
- **`Acme.Ctp.Infrastructure`**：
  - `CtpSessionService : ICtpSession`（唯一 DI 注册——直连官方 SDK 链路）
  - 增量行情订阅（已连接+同账户不重建连接，只增量累加）
  - 最新 tick 缓存（`GetLatestTick` 供快照种子和市价单对手价）

### 柜台切换

前置地址（登录窗口填入）：
- `tcp://127.0.0.1:17801`（交易）+ `tcp://127.0.0.1:17802`（行情）→ **本地前置服务器**
- `tcp://180.168.146.187:10130`（交易）+ `tcp://180.168.146.187:10131`（行情）→ **SimNow**
- 切柜台 = 换前置地址重新登录（地址随凭据存 ctp.db）

客户端对两者**零区分**——都是官方 SDK 的 TCP 前置地址。

### 期货页行情板

- 本地前置（`IsSimFront` 判定 17801）→ `QueryInstrumentsAsync` → 上市合约表（默认 `zx001`）
- 真实前置 → 证券库主力合约（~250 个）

## 4. 前置服务器（`Acme.Ctp.Front`，线协议翻译层）

### 形态

**两独立进程（2026-10-05 分离，真实世界对齐——一个交易所 + N 券商前置）**：

| 进程 | 端口 | 服务 | 用途 |
|---|---|---|---|
| `Acme.Ctp.Sim.Host`（交易所） | 17800 | `SimRpcServer` | JSON-RPC（默认绑 127.0.0.1——前置进程/E2E 的连接口） |
| `Acme.Ctp.Front.Host`（前置） | 17801 | `CtpFrontServer` | CTP 线协议交易腿（对客户端；`env AETHER_CTP_FRONT_ADDR`） |
| `Acme.Ctp.Front.Host`（前置） | 17802 | `CtpFrontMdServer` | CTP 线协议行情腿（`env AETHER_CTP_MD_FRONT_PORT`） |

前置与交易所**分进程**：每个客户端会话一条 `SimRpcClient` 连 17800（登录绑定账户 ⇒ 推送按连接路由 ⇒ 券商柜台镜像）；前置可独立重启（账本不丢——交易所不动）；启动序无硬依赖（前置探活重试）。**前置只是协议翻译，交易所是唯一权威状态**——行情腿快照/时钟经 `quotes` wire op（非推送）保「订阅应答+快照单次写」形状。

### 进程分离设计（2026-10-05——如何解耦）

#### 连接模型：每客户端会话一条交易所连接

```
客户端(官方SDK)          前置进程                    交易所进程
   │                        │                            │
   │── CTP 线协议 ────────▶│ CtpFrontSession            │
   │  tcp://ip:17801        │   └── SimRpcClient ══════▶ │ SimRpcServer
   │                        │        (登录绑定账户)        │   └── SimTradingCore
   │                        │                            │     （撮合/账本/行情引擎）
   │ ◀── F001/F002 推送 ────│ ◀══ OrderPushed/TradePushed│
```

为什么**每会话一条连接**而不是前置共享一条：RPC 推送 payload（`CtpOrder`/`CtpTrade`）**没有 accountId 字段**，服务端只能按「哪条连接登录了这个账户」路由——共享连接无法解复用。而每会话一条恰好是**真实券商柜台的同构**（柜台为每个客户会话独立服务），登录即绑定（`SimConnection` 的 `AccountId`），推送按 `MatchesAccount` 路由到该账户的全部连接。行情腿同理（tick 按连接的 `Subscriptions` 集合路由），但无账户语义——不登录，只 Hello + `quotes`/`subscribe`。

#### 解耦接缝：wire op 面是唯一契约

前置对交易所的全部依赖收敛为 **11 个 wire op + 3 个推送事件**（`SimWire`）——前置项目（`Acme.Ctp.Front`）**只引 `Acme.Ctp.Sim`**（RPC 客户端契约层），**不引 `Acme.Ctp.Simulator` 核心**（原引擎桥层化例外退役）。换交易所实现 = 实现 wire op 面；换前置 = 新写 `XxxServer + XxxSession` 调同一批 op。

| 前置用 | op | 交易腿/行情腿 |
|---|---|---|
| 登录（密文域） | `login` | 交易腿 ⑦——密文 hex 塞 `LoginArg.Password`（核心当不透明字符串哈希） |
| 查资金/持仓/合约 | `queryAccount` / `queryPositions` / `queryInstruments` | ⑮⑰/查合约——无账户参数（绑定账户） |
| 报单/撤单 | `insertOrder` / `cancelOrder` | ⑲/㉑——OrderRef 直通、TC 透传 |
| 行情快照+时钟 | `quotes`（**分离时新增**） | 行情腿订阅快照 + TradingDay/TimeOfDay——替代进程内直调 `Market.GetQuote`/核心时钟 |
| 订阅 | `subscribe` | 行情腿——服务端登记订阅集 + 激活行情 |
| 推送 | `order` / `trade` / `tick` 事件 | 服务端按账户/订阅集路由到对应连接 |

#### 密码域：零改动跨进程

核心 `Login(broker, user, password)` 把密码当**不透明字符串**（PBKDF2 哈希任意串）。前置拿到的是 CTP 固定挑战密文 hex——直接塞进 `LoginArg.Password` 走 wire，交易所哈希的就是这个 hex ⇒ 与进程内直调**逐字节一致**。密码域隔离（前置密文域 vs JSON-RPC 明文域）由字符串值承载，不需要 op 级区分。

#### 顺序保证：双层 capture，跨进程保「应答先于推送」

真 CTP 语义「rc 返回先于 SPI 回调」跨进程后由两层 capture 复刻：

1. **RPC 侧（交易所→前置）**：`SimConnection` 的 capture-during-dispatch——`InsertOrder` 在 op 内同步抛事件，路由回本连接的推送先入捕获缓冲，**应答帧先写 wire**，同锁排空（F1 顺序契约）。
2. **前置侧（前置→客户端）**：`CtpFrontSession.DispatchFrameAsync` 的 `_dispatching` 捕获窗**跨 await 有效**——RPC 应答（threadpool continuation）与推送事件（RPC 接收线程）谁先跑都无所谓：推送在窗内必入捕获缓冲，应答帧入队后同锁排空。成功报单无应答帧（真 CTP 语义）时先让路 10ms 护客户端 tracker 注册。

两层叠加 ⇒ 客户端看到的顺序与单进程版逐帧一致（E2E 12/12 全绿钉住）。

#### 生命周期与故障语义

| 场景 | 行为 |
|---|---|
| 前置先起、交易所没起 | 前置监听照常起（客户端能连）；每会话登录时 RPC 连接失败 ⇒ 诚实关连接（客户端见断线）；后台每 5s 探活直到连上 |
| **前置崩溃/重启** | **交易所不动、账本不丢**；在线客户端断线，重新登录即恢复——bot 断线重连测试的真实故障注入 |
| 交易所崩溃/重启 | 交易所崩溃自恢复（`sc failure restart/5000`）；重启后挂单作废、客户端须重登（与单进程版同语义） |
| 会话结束（客户端断开） | 前置会话 `RpcClient.Logout()`（服务端清绑定/订阅 + flush）→ Dispose |

#### 部署形态（服务器两服务）

`AetherCtpExchange`（交易所——`127.0.0.1:17800` 只绑回环，比旧单进程 `0.0.0.0` 更收敛）+ `AetherCtpFront`（前置——`0.0.0.0:17801/17802` 对客户端，`AETHER_CTP_EXCHANGE_ADDR` 指交易所）。同机部署即默认值；交易所远端部署改此 env 即可（届时 17800 需对外 + TLS——已知公网化批次）。CI（`deploy-ctpsim.yml`）两项目 publish → `exchange/`+`front/` 布局单 tar → 远端脚本「停前置→停交易所→换文件→起交易所→起前置」。

### 核心类（5 文件）

| 类 | 职责 |
|---|---|
| `CtpFrameParser` | 流式分帧器：标准帧/握手帧/复合帧/半帧/心跳剥离（剥出为 `IsHeartbeat` 帧交会话层回射）/未知帧头逐字节重对齐 |
| `CtpFrontSession` | 交易腿状态机（握手→挑战→认证→登录→业务）；按 op 签名先长后短分派；**自持一条 `SimRpcClient` 连交易所**（登录绑定账户，业务 op/推送经它走） |
| `CtpFrontMdSession` | 行情腿：握手/挑战/MD 登录/订阅/tick 推送（自持 `SimRpcClient`——`quotes` op 取快照/时钟、`subscribe` op 订阅、`TickPushed` 事件 → 流式帧） |
| `CtpFrontServer` / `CtpFrontMdServer` | TCP 监听，每连接一会话 + 每会话一条新建 `SimRpcClient` |
| `CtpOrderWireCodec` | ⑲ 报单请求解析（tag 宽度自适应）+ 数值解码 |
| `CtpFrontTemplates` | 捕获帧模板 + 等宽编辑（生成器产物，勿手改） |

### 交易腿会话序列

```
① 客户端握手 → ② 服务端挑战（固定 nonce）
③ 客户端挑战响应 → ④ 服务端确认
⑤ 客户端认证 → ⑥ 服务端认证应答
⑦ 客户端登录 → ⑧ 服务端登录预响应
⑨ 客户端系统信息请求 → ⑩ 服务端登录结果（交易日/FrontID/SessionID/MaxOrderRef）
⑪⑬ 结算确认 → ⑫⑭ 应答
⑮ 查资金 → ⑯ 应答     ⑰ 查持仓 → ⑱ 应答（逐仓一行）
⑲ 报单 → （无应答帧——成功走 F001/F002 推送，失败走 ⑳+F003）
㉑ 撤单 → ㉒ 应答 + F004 错误推送（失败时）
```

### 行情腿会话序列

```
① 握手 → ② 挑战 → ③ 挑战响应 → ④ 确认
MD 登录（op E430E603，凭据不校验）→ MD 登录应答
订阅（op E44401）→ 订阅应答 + 订阅快照 tick（同一次 TCP 写）
之后：流式 tick 推送（E4F103 增量 op——挂单/撤单/成交事件驱动）
```

### 密码策略

服务端发**固定挑战**（帧② 175B nonce 抓包原样）⇒ 同密码 + 同 nonce ⇒ 同密文 ⇒ **存密文 hex 前 16 字符当哈希比对**（同 bcrypt 模式）。首登开户，第二次起验密文确定性。密码域与 JSON-RPC 路径（PBKDF2）隔离。

## 5. 交易所（`Acme.Ctp.Simulator`，撮合/账本/行情引擎）

### 设计

交易所**不感知线协议**——核心门面 `SimTradingCore` 的方法签名全用 Domain DTO。前置服务器和 JSON-RPC 服务器都是它之上的协议翻译层。

### 核心模块

| 模块 | 职责 |
|---|---|
| `SimTradingCore` | 门面：Login/QueryAccount/QueryPositions/InsertOrder/CancelOrder/QueryListedInstruments |
| `SimOrderManager` | 报单生命周期（状态机、冻结额、今昨拆分） |
| `OrderBook` | 限价订单簿（唯一撮合机制）：客户端互为对手（价格-时间优先、maker 价成交、自成交跳过） |
| `SimPositionManager` | 持仓管理（今昨仓、方向、盈亏盯市） |
| `SimRiskEngine` | 风控（资金不足/手数/价格/时段校验） |
| `SimMarketDataEngine` | 行情引擎：tick 事件驱动（挂单/撤单/成交即推） |
| `SimInstrument` / `InstrumentCatalog` | 合约元数据（乘数/保证金率/tick/上市表） |
| `SimStore` / `NullSimStore` | 持久化（SQLite 或纯内存） |

### 交易所（唯一模式：订单簿价格发现）

- **价格发现归订单簿**——bid/ask = 簿顶真实挂单，last = 最新成交价
- **零参与者代码**——做市商/噪声交易者是普通客户端（Lean 策略笔记），服务端无特殊账户
- 空簿市价单拒 90016（无对手价——真实交易所语义）
- 挂单/撤单/成交即推 tick（事件驱动，非定时器）

### 上市合约表

- 默认 `zx001`（SIM 交易所自有品种，乘数 10 / 保证金 10% / tick 1 / 基准 3800）
- `AETHER_CTP_SIM_BOARD` 自定义（逗号分隔）
- 上市表 = 唯一可交易/可订阅集合

## 6. 线协议参考（逆向成果）

### 帧格式

```
标准帧:  [0x02][0x00][payload_len: 2B 大端][payload]
握手帧:  [0x00][0x06]...（含 "API_20171207_V1"）
心跳:    6B [00 02 00 00 05 00]（~15s 一拍，前置逐拍回射）
路由:    payload 前 4B（01 03 34 4C = tag / 01 04 21 03 = 二进制列式 / 01 04 FA 0F = LZ4）
```

### tag 字段编码

- 字段标记：`E1..EF` + `FE`（全集终止符）
- 字符串值：`E7`/`EB`/`FE` 等 tag 后跟 ASCII run（到下一 tag 止）
- 数值：trimmed 大端 double（尾剥零）或 minimal int（前导零剥）
- **E0 转义**：值内 {E0..EF, FE} 字节前插 E0（资金/GBK 消息）
- 心跳可出现在任意两帧之间——分帧器须在解析循环内每轮剥

### 关键协议规则（多轮 SDK 即裁判实证）

| 规则 | 违反后果 |
|---|---|
| **推送/应答帧的字符串字段必须等宽**（`F()` 截断/补空格） | SDK 解析失步 → 无限挂死 |
| **F0 族推送序号单调递增** | SDK 丢帧 |
| **80 族应答 E3 = 应答侧记录流水**（逐条递增，非请求回显） | SDK 读而不派发 → 30s 超时 |
| **4007 的 RspInfo 等宽 25B** | 其后所有推送帧静默失效 |
| **RspInfo 变宽编辑** | SDK 不触发回调 |
| **GBK 需 CodePagesEncodingProvider** | 运行时编码异常 |

### 报单请求解析（⑲）

- op 签名 `E1 01 E2 40 E4`（末字节 = 请求计数器）
- **orderRef** tag 随会话字段形状漂：6 字符用户 → `E8`，7 字符 → `E7`（codec 两者都搜）
- **量字段** tag 随宽度变：1 字节量走 `E8`，≥256 手走 `E7`
- **TimeCondition 透传**：`CtpOrderRequestMapper` 把市价映射成「对手价限价 + IOC」
- 成功 = 无应答帧（真 CTP 语义）；风控拒单 = ⑳ + F003 错误推送

### 行情 tick 推送

- **流式增量 op `E4F103`**（2026-10-02 定案）——旧模板误用 `E4F101` 一次性快照 op，重复推送触发 native 无限重投 ~25 万次/秒
- 单合约 109B：价格三槽（last/bid/ask）+ 时间/合约/交易日等宽编辑，量串 verbatim
- **量串 verbatim** 是已知显示边界（bidvol/askvol/volume 显示陈旧捕获值）

### 查报单

- 请求 op `E1 04 E2 80 E4 <计数器>`（计数在 op 字节不在 E3）
- 前置恒回 17B tag 空表——SDK **不派发** → 30s 超时后管线恢复（E2E 钉住不堵死后续查询）
- 真前置自己也在首查即过滤时超时（录制实证）
- 多行报单走二进制路由 `01 04 21 03`（列式共享字段格式 ≠ tag ≠ LZ4）——**不逆向**
- 委托 tab 由 F001 推送实时维护（推送驱动，查询仅历史回填——失败不清空推送攒的列表）

## 7. 已知边界（诚实列表）

| 边界 | 影响 | 状态 |
|---|---|---|
| 查报单 17B tag 空应答 SDK 不派发 | 委托 tab 30s 后显示「查询超时」→ 列表由推送维护 | **已知行为** |
| ⑳/F003 拒单回调不触发 RspError | C# 层不可见拒单错误 | 客户端靠 watchdog 兜底 |
| 部成/终态推送 VolumeTraded verbatim | 成交量回显失真（真实量由 Trade 回报驱动） | 显示层 |
| FrontID/SessionID verbatim | 固定值（客户端按 OrderRef 匹配不受影响） | 无实际影响 |
| >5 字符合约推送回显截断 | 长合约名截断显示 | 显示层 |
| 多合约查合约只有首行到达 SDK | isLast 编码未逆向 | 默认单合约 zx001 无感 |
| 心跳发射 | 客户端 ~15s 一拍，前置逐拍回射 | 已实现 |
| 行情腿心跳 | 同交易腿 | 已实现 |

## 8. 运行指南

### 起本地前置服务器（含交易所）

```bash
dotnet run --project apps/CtpSim/Acme.Ctp.Sim.Host
# 就绪日志:
#   [CTP-SIM] 本地仿真柜台已就绪：127.0.0.1:17800（JSON RPC）+ :17801（CTP 前置）
#   [CTP-SIM]   行情 交易所模式：订单簿 + 成交驱动
#   [CTP-SIM]   上市合约 1 个：zx001
```

### 连接客户端（桌面 Stockapp）

期货页 → 登录窗口 → 前置地址 `tcp://127.0.0.1:17801` + 行情前置 `tcp://127.0.0.1:17802` → 任意账号密码登录 → 首登自动开户。

### 跑双 bot（market-maker + noise-taker）

```bash
# 两个终端各起一个桌面实例（不同账户！）
dotnet run --project apps/Stockapp/Acme.Stockapp.Desktop   # 实例 1（SIM-MM 账户）
dotnet run --project apps/Stockapp/Acme.Stockapp.Desktop   # 实例 2（SIM-NOISE 账户）
```

各自期货页登录（不同账户）→ 实盘 tab → 引擎选 `LeanAlgorithm` → 合约填 `zx001` → 勾「真实下单」 → 一个选 `market-maker`、另一个选 `noise-taker`。

### 服务器部署（Windows 服务 + GitHub Actions）

柜台是裸 TCP（非 HTTP）——**不走 IIS**，部署形态 = Windows 服务器上的常驻 Windows 服务 `AetherCtpSim`。

- **一次性准备**（服务器上管理员跑）：`scripts/ctpsim-setup-server.ps1` —— OpenSSH Server + Windows 服务（binPath `C:\Aether\CtpSim\Acme.Ctp.Sim.Host.exe`，self-contained 无需装 .NET）+ 服务级 env（注册表 `Environment`：`AETHER_CTP_SIM_ADDR=0.0.0.0:17800` 三监听全绑 + DB/LOG/BOARD 指向 `C:\Aether\ctpsim-data`）+ 崩溃自恢复 + **防火墙只放 17801/17802**（17800 JSON-RPC 故意不放行——默认入站拒绝即拦）。
- **每次部署** = `.github/workflows/deploy-ctpsim.yml`（push main 触及 CTP 相关路径自动触发，或手动按钮）：ubuntu runner 交叉 publish self-contained win-x64 → tar → scp → 远端 `scripts/ctpsim-remote-deploy.ps1`（停服务 → `robocopy /MIR` 换文件 → 起服务 → TCP 探针）。账本与日志在 `C:\Aether\ctpsim-data`（部署目录之外，/MIR 永不波及）。
- **客户端**：前置地址填 `tcp://<服务器IP>:17801` + 行情前置 `tcp://<服务器IP>:17802`（`IsSimFront` 按端口 `:17801` 识别远程仿真前置，与本地同体验）。
- **注意**：服务器时区须中国时区 UTC+8（`SimClock` 用 `DateTime.Now`）；部署/重启后未成交挂单作废、在线客户端须重新登录（客户端无自动重连）；SSH 用户须在 Administrators 组（控服务）。GitHub Secrets：`CTPSIM_SSH_HOST` / `CTPSIM_SSH_USER` / `CTPSIM_SSH_KEY` / 可选 `CTPSIM_SSH_PORT`。

### 环境变量

| 变量 | 默认 | 说明 |
|---|---|---|
| `AETHER_CTP_FRONT_PORT` | 17801 | CTP 交易腿前置端口 |
| `AETHER_CTP_MD_FRONT_PORT` | 17802 | CTP 行情腿前置端口 |
| `AETHER_CTP_SIM_ADDR` | 127.0.0.1:17800 | JSON-RPC 端点 |
| `AETHER_CTP_SIM_DB` | ~/.aether/ctp-sim.db | 账本库路径 |
| `AETHER_CTP_SIM_BOARD` | zx001 | 上市合约表 |
| `AETHER_CTP_SIM_LOG` | 空（仅控制台） | 日志文件路径（Windows 服务形态由 setup 脚本设置） |
| `AETHER_CTP_SIM_BALANCE` | 1000000 | 新账户初始资金 |
| `AETHER_CTP_SIM_LOGIN_MAX_FAILURES` | 5 | 防爆破阈值 |
| `AETHER_CTP_SIM_LOGIN_LOCKOUT_SEC` | 60 | 防爆破锁定秒数 |

已废除（设了被忽略）：`AETHER_CTP_SIM_MODE` / `AETHER_CTP_SIM_TICK_MS` / `AETHER_CTP_SIM_SEED` / `AETHER_CTP_SIM_REPLAY` / `AETHER_CTP_SIM_REPLAY_SPEED` / `AETHER_CTP_SIM_REPLAY_LOOP`（walk/回放模式已删除）。

### 工具

```bash
# 手动直连验收（官方 SDK 驱动，任意宽度账号可登录）
dotnet run --project apps/CtpSim/Acme.Ctp.ProtoCapture -- tcp://127.0.0.1:17801 <账号> <密码>

# E2E 测试（SDK 即裁判——进程内回环，官方 SDK 解析我们的帧）
dotnet test tests/Acme.Ctp.Front.Tests
# 当前 11/11 全绿

# 协议抓包（对 SimNow 真前置录制帧流）
dotnet run --project apps/CtpSim/Acme.Ctp.ProtoCapture -- \
  --front tcp://180.168.146.187:10130 \
  --mdfront tcp://180.168.146.187:10131 \
  --user <SimNow账号> --password <密码> --broker 9999
```
