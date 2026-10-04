# 交易所设计（Acme.Ctp.Simulator）

> **定位**：本地期货交易所撮合引擎——「我就是交易所」。价格发现完全归订单簿，流动性由参与者（客户端）供给。
> 协议无关（不引任何网络/SDK 依赖）、部署无关（进程内嵌或独立进程均可）。
> 代码：`business/CtpExchange/Acme.Ctp.Simulator/`。
> 前置服务器（线协议翻译）见 [ctp-stack.md](ctp-stack.md) §4。

---

## 1. 设计原则

| 原则 | 含义 |
|---|---|
| **我就是交易所** | 不转发外部行情；价格发现归订单簿；流动性完全由参与者（客户端）供给 |
| **协议无关** | 核心类不引 TCP/HTTP/SDK——前置服务器和 JSON-RPC 服务器都是它之上的适配层 |
| **零参与者代码** | 无做市商/噪声机器人/特殊账户——所有参与者（含 bot）是普通客户端 |
| **CTP 语义精确对齐** | 报单状态机（'0'-'5'）、今昨仓、开平仓、错误码——与真实 CTP 精确同形 |

## 2. 门面（`SimTradingCore`）

交易所唯一入口——所有外部适配器（前置服务器、JSON-RPC 服务器）只调这一个类。

### 公开 API

```
登录       Login(brokerId, userId, password) → CtpLoginResult
查账户     QueryAccount(accountId) → CtpAccount
查持仓     QueryPositions(accountId, instrumentId?) → CtpPosition[]
查报单     QueryOrders(accountId, instrumentId?) → CtpOrder[]
查上市合约 QueryListedInstruments() → SimInstrument[]
报单       InsertOrder(accountId, CtpOrderRequest) → CtpOrderInsertResult
撤单       CancelOrder(accountId, CtpOrderRef)
初始化     InitializeAsync()                   （种子/恢复/建表）
```

### 事件（推送源——前置服务器订阅后翻译成 wire 帧）

```
event Action<string, CtpOrder>  OrderChanged   (accountId, order)
event Action<string, CtpTrade>  TradeFilled    (accountId, trade)
event Action<CtpTick>           TickProduced   (tick)
```

**关键**：事件在方法返回前**同步**抛出（`InsertOrder` 返回前 `OrderChanged`/`TradeFilled` 已触发）——前置服务器的 capture-during-dispatch 靠这一保证实现「应答先于推送到达客户端」。

### 线程安全

所有公开方法内部持 `_gate`（`lock`）——适配器可从任意线程调用，无需额外同步。

## 3. 模块

```
Acme.Ctp.Simulator/
├── SimTradingCore.cs          ← 门面
├── SimClock.cs                ← 时钟（真实或注入）
├── SimOptions.cs              ← 配置 record
├── Accounts/SimAccount.cs     ← 账户（资金会计）
├── Orders/
│   ├── SimOrderManager.cs     ← 报单生命周期
│   └── SimOrderStatus.cs      ← 状态码
├── Matching/
│   └── OrderBook.cs           ← 限价订单簿（CLOB）——唯一撮合机制
├── Positions/SimPositionManager.cs ← 持仓管理
├── Risk/
│   ├── SimRiskEngine.cs       ← 风控校验
│   └── SimErrorCodes.cs       ← 错误码 90001-90017
├── Market/
│   ├── SimMarketDataEngine.cs ← 行情引擎（事件驱动）
│   ├── SimInstrument.cs       ← 合约元数据
│   └── InstrumentCatalog.cs   ← 品种目录/派生
└── Storage/
    ├── ISimStore.cs           ← 持久化端口
    └── SimStore.cs            ← SQLite 实现（可替换为内存）
```

## 4. 账户（`SimAccount`）

### 资金会计

```
Balance    = StaticBalance + PositionProfit     ← 动态（持仓浮盈计入）
Available  = Balance − CurrMargin − FrozenMargin − FrozenCash
```

| 字段 | 含义 |
|---|---|
| `StaticBalance` | 静态余额（入金 − 出金 + 平仓盈亏 − 手续费） |
| `PositionProfit` | 持仓浮盈（按最新价盯市，逐笔成交后重算） |
| `CurrMargin` | 当前持仓占用保证金 |
| `FrozenMargin` | 挂单冻结保证金（撤单/成交后释放/转移） |
| `FrozenCash` | 挂单冻结手续费 |

### 密码认证

- PBKDF2-SHA256 哈希入档 `sim_account.password_hash`（格式 `pbkdf2$迭代$盐hex$哈希hex`）
- 首登开户；空/空白密码一律拒 90017
- 防爆破：连续 `LoginMaxFailures` 次失败锁定 `LoginLockoutSeconds` 秒

## 5. 报单（`SimOrderManager`）

### 状态机（CTP 字符码）

```
'3' 未成交在队列 → '1' 部分成交在队列 → '0' 全部成交
              ↘ '2' 部分成交不在队列（IOC 残量作废）
              ↘ '4' 未成交不在队列（被拒/重启后作废）
'5' 已撤销
```

| 方法 | 职责 |
|---|---|
| `Accept` | 受理新单：风控校验通过后建单、冻结保证金/手续费、返回 SimOrderUpdate |
| `ApplyFill` | 成交落账：更新状态/成较量/冻结转移 |
| `FinalizeNoQueue` | IOC/FOK 残量作废（'2'/'4'）——释放冻结 |
| `Cancel` | 用户撤单（'5'）——释放冻结 |

### 报单请求（`CtpOrderRequest`，Domain DTO）

```csharp
required string BrokerId, InvestorId, InstrumentId
required CtpDirection Direction        (Buy='0'/Sell='1')
required CtpOffsetFlag OffsetFlag      (Open='0'/Close='1'/CloseToday='3'/CloseYesterday='4')
required CtpOrderPriceType PriceType   (Limit='2'/Market='0')
required double LimitPrice
required int Volume
CtpTimeCondition TimeCondition = GFD   (GFD='3'/IOC='1'/FOK='2')
string? OrderRef                       (客户端号直通——前置路径 M2)
```

**TimeCondition 语义**（交易所必须尊重）：

| 条件 | 含义 |
|---|---|
| GFD | 残量入簿挂单 |
| IOC | 立即成交可成交部分，残量作废（不入簿） |
| FOK | 全部成交或全部作废 |

## 6. 撮合（`Matching/OrderBook`，唯一撮合机制）

### 订单簿（每合约一本，CLOB）

- **客户端互为对手**——价格-时间优先（价格优先、同价先挂先成交）
- **按挂单方（maker）价成交**
- 自成交跳过（吃单跳过本账户挂单）
- 只收 GFD 活单（IOC/FOK 永不入簿）
- 簿成交全深度（簿对手是真单）
- 空簿市价单 ⇒ 拒 90016（无对手价——真实交易所语义）
- 可吃边界 = 限价本身（市价无界吃全簿）

### 报单流程（`Aggress`）

```
新单 → 风控 → 冻结 → 吃簿（WalkBook）
  ├── 有对手 → 按 maker 价成交 → RecordTrade → 推 tick
  └── 残量去向：
      ├── IOC/FOK → 作废（FinalizeNoQueue，释放冻结）
      ├── 市价残量 → 作废（无庄家逐 tick 重试——交易所模式市价不挂单）
      └── GFD 限价残量 → 入簿 + OnBookChanged → 推 tick
```

## 7. 持仓（`SimPositionManager`）

### 今昨仓模型

```
方向：Long('2') / Short('3')——按方向分仓（非净仓）
日期：Today('1') / History('2')——今仓/昨仓
```

| 方法 | 职责 |
|---|---|
| `GetOrCreate` | 取或建某合约的仓位簿 |
| `ApplyOpenFill` | 开仓：对应方向加仓 |
| `ApplyCloseFill` | 平仓：扣今仓（优先）/昨仓，返回平仓盈亏 |
| `RecomputePositionProfit` | 全仓盯市重算（按最新价） |

**平仓规则**：优先平今仓（`CloseToday`），不足扣昨仓（`CloseYesterday`）。持仓盈亏按最新价盯市。

## 8. 风控（`SimRiskEngine`）

### 校验链（按序）

| 序号 | 校验 | 失败码 |
|---|---|---|
| 1 | 上市闸门（合约在 `_listedIds` 中） | 90002 未知合约 |
| 2 | 手数（>0 整数） | 90003 手数非法 |
| 3 | 价格（>0） | 90004 价格非法 |
| 4 | 交易时段 | 90010 非交易时段 |
| 5 | 资金充足（开仓：保证金+手续费 ≤ Available） | 90005 资金不足 |
| 6 | 平仓量 ≤ 持仓量 | 90007 平仓量超持仓 |

### 自定义错误码

```
90001 未登录       90002 未知合约     90003 手数非法     90004 价格非法
90005 资金不足     90006 无仓可平     90007 平仓量超持仓  90008 报单不存在
90009 报单不可撤   90010 非交易时段   90011 参数非法
90016 无对手价（空簿市价单）
90017 凭据不匹配（密码错误或防爆破锁定）
—— 90012-90015 为传输层保留段（JSON-RPC 路径用）
```

## 9. 行情（`SimMarketDataEngine`，事件驱动）

### 设计

- **tick 事件驱动**：挂单/撤单/成交即推（`OnBookChanged`/`RecordTrade`）
- bid/ask = 簿顶真实挂单（同价档聚合量、空侧=0——真实 CTP 单边市场语义）
- last = 最新成交价（首笔前 = 昨结算 PreSettlement）
- open/high/low/volume 从真实成交维护
- open interest = 全市场多头合计
- 订阅即推当前快照

### 事件→推送流

```
InsertOrder → 簿变化 → OnBookChanged → TickProduced → 前置 MD 腿推送
InsertOrder → 成交   → RecordTrade  → TickProduced → 前置 MD 腿推送
CancelOrder → 簿变   → OnBookChanged → TickProduced → 前置 MD 腿推送
```

### `SimInstrument`（合约元数据）

```csharp
required string InstrumentId, ExchangeId
double VolumeMultiple = 1     // 合约乘数
double MarginRate = 0.10      // 保证金率
double PriceTick = 1          // 最小变动价位
double BasePrice              // 基准价
double PreSettlement          // 昨结算（涨幅基准）
double CommissionRate = 0.00005
double LimitRate = 0.08       // 涨跌停板率
double MarginPerLot(price) => price × VolumeMultiple × MarginRate
```

### `InstrumentCatalog`（品种目录/派生/合成）

- **已知品种**：`zx`（SIM 自有）+ `ag/au/cu/rb/m/i/ta/IF/sc`（9 个真实品种）
- **派生**：未知合约按品种首字母查表派生（`DeriveByVariety`）
- **合成**：再不行按下单价合成（`Synthesize`——上市表自定义 id 兜底）
- **上市闸门**：`ListedInstruments` = 唯一可交易/可订阅集合

## 10. 持久化（`Storage/`）

### 端口（`ISimStore`）

```csharp
LoadAccountsAsync / LoadPositionsAsync / LoadOrdersAsync / LoadInstrumentsAsync
LoadTradeIdsAsync / LoadRecentTicksAsync
SaveAccountAsync / SavePositionsAsync / SaveOrderAsync / SaveTradeAsync
SaveInstrumentAsync / SaveTickAsync / FlushAsync
```

### 实现

| 实现 | 场景 |
|---|---|
| `SimStore`（SQLite，默认 `~/.aether/ctp-sim.db`） | host 进程——重启续用 |
| `NullSimStore` | 测试/纯内存（`DatabasePath=null`） |

### 6 张表

```
sim_account    （账户 + 密码哈希）
sim_position   （持仓——按账户+合约+方向）
sim_order      （报单——含状态/成较量/冻结）
sim_trade      （成交——PK (account_id, trade_id)）
sim_instrument （合约元数据）
sim_tick       （成交 tick 追加式日志——按 rowid 序即时间序）
```

**写盘 = 尽力而为 fire-and-forget**（`Task.Run` + 异常只 `Trace`）——磁盘故障不拒单。`FlushAsync` 等挂起写盘。

### 重启恢复三条规则

1. **挂单不复活**——恢复时清冻结 + 未成交挂单改 `'4'` 作废
2. **两个序列续排**——`NextOrderRefSeq` / `_tradeSeq` 从已落盘最大值推进
3. **行情统计从最后成交 tick 续上**——最新价/累计量/开高低不归零

## 11. 配置（`SimOptions`）

```csharp
sealed record SimOptions
{
    double InitialBalance = 1_000_000;     // 新账户初始资金
    string? DatabasePath;                  // null=纯内存
    IReadOnlyList<string>? ListedInstruments; // 上市表（默认 zx001）
    int LoginMaxFailures = 5;              // 防爆破阈值
    int LoginLockoutSeconds = 60;          // 锁定秒数
    bool RejectOrdersOutsideTradingTime;   // 非交易时段拒单（默认关）
}
```

### 环境变量映射（`SimHostOptions.FromEnvironment`）

| 变量 | 默认 | 映射 |
|---|---|---|
| `AETHER_CTP_SIM_DB` | `~/.aether/ctp-sim.db` | `DatabasePath` |
| `AETHER_CTP_SIM_BOARD` | `zx001` | `ListedInstruments` |
| `AETHER_CTP_SIM_BALANCE` | 1000000 | `InitialBalance` |
| `AETHER_CTP_SIM_LOGIN_MAX_FAILURES` | 5 | `LoginMaxFailures` |
| `AETHER_CTP_SIM_LOGIN_LOCKOUT_SEC` | 60 | `LoginLockoutSeconds` |

已废除的环境变量（设了被忽略）：`AETHER_CTP_SIM_MODE` / `AETHER_CTP_SIM_TICK_MS` / `AETHER_CTP_SIM_SEED` / `AETHER_CTP_SIM_REPLAY` / `AETHER_CTP_SIM_REPLAY_SPEED` / `AETHER_CTP_SIM_REPLAY_LOOP`。

## 12. 时钟（`SimClock`）

```csharp
public sealed class SimClock
{
    public SimClock() : this(() => DateTime.Now) { }     // 真实时钟
    public SimClock(Func<DateTime> now) { }               // 注入（测试确定性）
    public DateTime Now { get; }
    public string TradingDay { get; }   // yyyyMMdd
    public string TimeOfDay { get; }    // HH:mm:ss
}
```

测试用注入固定时钟（如 2026-09-23 10:00）⇒ `TradingDay == "20260923"` 恒定。交易日按周五 16:00 切（中国期货市场惯例）。

## 13. 当前状态（2026-10-03）

### 已完成

| 能力 | 状态 |
|---|---|
| 订单簿价格发现 | ✅ 唯一模式，双 bot 全链路跑通 |
| 客户端互为对手（CLOC） | ✅ 跨账户撮合、maker 价成交、自成交跳过 |
| 账号系统（自助开户 + 密码 + 防爆破） | ✅ PBKDF2 哈希 + 锁定 |
| 今昨仓持仓管理 | ✅ 平今优先、盯市盈亏 |
| 风控六项校验 | ✅ 上市/手数/价格/时段/资金/平仓量 |
| 持久化 + 重启续用 | ✅ 6 表 SQLite、序列续排、挂单作废 |
| 行情引擎（事件驱动 tick） | ✅ 挂/撤/成即推 |
| 做市商/噪声交易者 | ✅ 客户端 Lean 策略笔记（非服务端内嵌） |
| CTP 前置对接 | ✅ `CtpFrontSession` 直调 `SimTradingCore` |
| JSON-RPC 对接 | ✅ `SimConnection` 经 op 分派调 `SimTradingCore` |

### 已删除（2026-10-03 walk 模式移除）

| 删除项 | 原因 |
|---|---|
| 随机游走（walk）模式 | 用户定调——交易所恒为纯订单簿 |
| tick 回放（replay）模式 | 回放骑 walk 撮合机制，walk 删 ⇒ 回放无宿主 |
| `SimpleMatchingEngine`（庄家兜底） | walk 专属——交易所模式无庄家 |
| `TickReplayDriver` | 回放专属 |
| `SimMarketMode` 枚举 | 单一模式无需枚举 |
| `SimOptions` 的 walk/replay 字段 | `TickIntervalMs`/`Seed`/`MaxWalkTicks`/`AcceptUnknownInstruments`/`ReplayPath`/`ReplaySpeed`/`ReplayLoop`/`MarketMode` |

### 测试覆盖（`tests/Acme.Ctp.Simulator.Tests`，138 例）

| 测试类 | 覆盖 |
|---|---|
| `LoginAndQueryTests` | 登录/查询/账户 |
| `OrderLifecycleTests` | 报单全状态机 |
| `RiskRejectionTests` | 风控六项 |
| `AccountingInvariantTests` | 资金守恒 |
| `TradingTimeTests` | 交易时段 |
| `CrossAccountMatchingTests` | 跨账户订单簿撮合 |
| `ExchangeModeTests` | 交易所模式 |
| `AccountAuthTests` | 账号系统 |
| `PersistenceTests` | 落盘/重启 |

### 已知限制

| 限制 | 说明 |
|---|---|
| 同账户多连接 OrderRef 撞号 | 无租约/踢人——多客户端用不同账户 |
| 无 TLS | JSON-RPC 明文——上公网前必须加 |
| 无自动重连（客户端） | host 死 ⇒ 客户端 90013 快速失败——重新登录即重连 |
| 单连接串行分发 | 内存操作 sub-ms，良性 |
| 空闲市场无对手价 | 不起做市商客户端 ⇒ 空簿市价单拒 90016（真实交易所本义） |

## 14. 部署形态

### 当前（单进程，三端口共享 core）

```
Acme.Ctp.Sim.Host
├── :17800 SimRpcServer      （JSON-RPC）
├── :17801 CtpFrontServer    （CTP 交易腿）
├── :17802 CtpFrontMdServer  （CTP 行情腿）
└── SimTradingCore           （交易所——共享实例）
```

### 分离部署（交易所独立进程）

交易所已有独立端口（:17800 JSON-RPC = 裸接口）。前置服务器改为远程连接：

```
进程 A（交易所）
└── :17800 SimRpcServer（SimTradingCore）

进程 B（前置服务器）
├── :17801 CtpFrontServer  →  经 SimRpcClient 连 :17800
└── :17802 CtpFrontMdServer →  经 SimRpcClient 连 :17800
```

改动量小：`CtpFrontSession` 中的 `SimTradingCore` 直接引用改为 `SimRpcClient` 网络调用（方法签名一一对应）。当前同进程跑是**便利选择**（省网络跳），非架构约束。

### 前置服务器替换（换协议）

新写一个 `XxxServer + XxxSession`，同样调 `SimTradingCore`（或 `SimRpcClient`），交易所零改动。协议适配层与交易所通过**方法签名 + 三个事件**解耦。
