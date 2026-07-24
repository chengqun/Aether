# Ctp.Net.Core 设计文档

> 基于 CTP 官方 API（pin 6.7.2）的现代化 C# CTP 封装库。本文档是动代码前的权威依据，锁定了平台矩阵、ABI 边界、生命周期、会话/流控/重连等关键决策。

> **项目位置（2026-07-19 迁入 AlphaAgent 成子项目）**：C# 库在 `src/Ctp.Net.Core/`（`Ctp.Net.Core.csproj` + `Native/` + `Trader/` + `Ctp.Native/` C++ wrapper），已加入 `AlphaAgent.sln`，可被其它项目 `<ProjectReference>` 引用；CTP 官方二进制在 `externals/ctp-6.7.2/`（与 `externals/Lean` 平级，**Git LFS 跟踪**，`git clone` 即得，无需手动下载）；构建脚本 `scripts/build-ctp-native.sh`；示例 `tests/Ctp.Samples.Console/`。下文 §5 的目录树为早期独立项目结构，实际路径以上述为准（组件划分仍有效）。


## 1. 项目目标与范围

**目标**：纯 .NET CTP SDK，支持 Console / Worker Service 调用，跨 Windows x64 / Linux x64 / macOS（arm64 原生 + x86_64），不依赖 WPF / Windows UI。

**只做**：CTP 行情接口封装、CTP 交易接口封装、回调事件封装、数据结构转换、生命周期管理。

**不做**：策略系统、风控、回测、数据库、Web UI、交易框架。

**定位**：类似 Python pyctp / vn.py CTP Gateway，但用 C# + .NET 10。

## 2. 技术决策（已锁定）

| 决策 | 结论 | 理由 |
|---|---|---|
| 封装方式 | **P/Invoke + C ABI wrapper**，放弃 C++/CLI | C++/CLI 仅 Windows；P/Invoke 跨平台 |
| CTP 版本 | **pin 6.7.2 三平台统一** | 三平台 6.7.2 二进制全在手；Windows 6.7.2 历史跑通过，风险最低；对齐后结构体布局一致 |
| ABI 结构体传递 | **直传 CTP 原始结构体指针** | 6.7.2 对齐使三平台布局一致，wrapper 可直接传指针，C# 用 `[StructLayout(Sequential)]` 镜像 6.7.2 头文件；wrapper 变薄，免做 opaque handle getter/setter |
| 公共 API 模型 | **干净模型**（`OrderRequest`/`TickData` 等） | 不暴露原始 CTP 结构给用户（原始结构仅在 ABI/P/Invoke 层使用，公共层映射成干净模型） |
| 异步模型 | **TCS 包回调**（`LoginAsync`/`QueryAsync<T>`） | CTP 登录/查询是"请求-回调"对，不能做成同步阻塞 |

## 3. 平台矩阵（已验证，2026-07-19）

三平台 6.7.2 二进制均来自 SimNow 官方下载，全部在手：

| 平台 | 版本 | 架构 | 格式 | 来源包 |
|---|---|---|---|---|
| Windows | 6.7.2 | x86 + x64 | `thosttraderapi_se.dll` / `thostmduserapi_se.dll` (+ `.lib`) | `~/Downloads/v6.7.2_traderapi_20230913` |
| Linux | 6.7.2 | x86-64 | `thosttraderapi_se.so` / `thostmduserapi_se.so` | 同上包内 `linux64` 目录 |
| macOS | 6.7.2 | universal (x86_64 + arm64) | `thosttraderapi_se.framework` / `thostmduserapi_se.framework`（签名） | `~/Downloads/TraderapiMduserapi_6.7.2_MacOS` |

- macOS 是通用二进制，Apple Silicon 原生运行，无需 Rosetta；min macOS 10.15。
- framework 已签名（SFIT 代码签名），嵌入 .NET 应用需注意 `@rpath`/install_name 配置（见 §7.5）。
- 各平台头文件（`ThostFtdcTraderApi.h` / `ThostFtdcMdApi.h` / `ThostFtdcUserApiStruct.h` / `ThostFtdcUserApiDataType.h`）随包附带，wrapper 按平台链接各自头文件。

## 4. 两个 ABI 分歧与应对

### 4.1 分歧 1：结构体布局不一致 —— **对齐 6.7.2 后消除**

不同 CTP 版本会改结构体字段（如 6.7.13 给 `CThostFtdcReqUserLoginField` 加了 `SMSCode`）。三平台 pin 同一版本 6.7.2 后，结构体内存布局一致 → **ABI 直传结构体指针安全**（§2 决策）。

### 4.2 分歧 2：Mac `ReqUserLogin` 4 参 + 采集库 —— **平台协议，对齐救不了，必须保留**

6.7.2 三平台 `ReqUserLogin` 头文件实测：

```
Win/Linux:  ReqUserLogin(CThostFtdcReqUserLoginField*, int nRequestID)                          // 2 参
Mac:       ReqUserLogin(CThostFtdcReqUserLoginField*, int nRequestID, length, systemInfo)      // 4 参
```

Mac/iOS **直连模式**登录前必须调采集库 `CTP_GetSystemInfoUnAesEncode(result, length)` 取加密系统信息，填入后两个参数（穿透式监管上报）。**采集库不在上述下载包内，需单独获取**（SimNow 仿真可能不强制，生产柜台 Mac 直连必需）。

**应对**：
- C ABI 暴露统一的 `CtpTrader_Authenticate()` + `CtpTrader_Login()`，wrapper 内部按平台 `#ifdef` 分叉：Mac 调 4 参版（并先调采集库），Win/Linux 调 2 参版。
- 采集库作 Mac wrapper 的**可选依赖**：Phase 1 跑 SimNow 可先空实现（systemInfo 传空/长度 0，仿真大概率放过），留接入点 `CtpTrader_SetCollectionInfo(handle, buf, len)`，生产再接真实采集库。
- C# 公共层只见一个 `LoginAsync(brokerId, userId, password, appId?, authCode?)`，平台差异全在 wrapper 内吸收。

## 5. 项目结构

```
Ctp.Net.Core/
├── DESIGN.md                      # 本文档
├── externals/
│   └── ctp-6.7.2/                 # 三平台官方二进制 + 头文件（gitignore 大文件，保留头文件）
│       ├── win-x86/
│       ├── win-x64/
│       ├── linux-x64/
│       └── macos-universal/      # 两个 .framework
├── src/
│   ├── Ctp.Native/               # C++ wrapper（C ABI），CMake 构建
│   │   ├── CMakeLists.txt
│   │   ├── include/              # 对外 C 头（ctp_api.h）
│   │   ├── TraderWrapper.cpp     # 封装 CThostFtdcTraderApi + SPI
│   │   ├── MdWrapper.cpp         # 封装 CThostFtdcMdApi + SPI
│   │   └── CollectionInfo.cpp    # Mac 采集库接入（Mac only，可选）
│   └── Ctp.Net.Core/             # C# 核心（P/Invoke + 公共模型 + 生命周期）
│       ├── Native/               # P/Invoke 绑定 + 结构体镜像
│       ├── Models/               # 干净公共模型（OrderRequest/TickData/...）
│       ├── Trader/               # CtpTrader：async 封装 + 会话/引用/流控/重连
│       ├── MarketData/           # CtpMarketData：订阅 + Tick 事件
│       └── CtpException.cs       # 错误码映射
├── samples/
│   └── Ctp.Samples.Console/      # Console 示例（Trader + Md）
└── tests/                        # 单测（mock 回调，不打真柜台）
```

## 6. 分层架构

```
C# 应用 (Console / Worker)
        │
        │  公共模型 + async + 事件
        ▼
Ctp.Net.Core (C#)            ← 干净 API：CtpTrader / CtpMarketData
        │  P/Invoke
        ▼
Ctp.Native (C++ → C ABI)     ← 薄 wrapper：封装 CTP C++ 虚函数接口为 flat C
        │  动态链接
        ▼
CTP 官方二进制 (6.7.2)        ← .dll / .so / .framework
        │
        ▼
交易柜台 / 行情源
```

## 7. Native Wrapper 设计

### 7.1 职责

把 CTP 的 C++ 虚函数接口（`CThostFtdcTraderApi` + `CThostFtdcTraderSpi`，Md 同理）封装成 flat C ABI。wrapper 按平台分别编译，各自链接对应平台的 CTP 二进制 + 头文件。因 6.7.2 对齐，**结构体可直接按值/指针传递**，wrapper 不做字段转换。

### 7.2 C ABI 草案（`ctp_api.h`）

用不透明 handle + 回调函数指针表。C# 侧把委托转函数指针传入。

```c
#ifdef __cplusplus
extern "C" {
#endif

/* ---- 不透明 handle ---- */
typedef struct CtpTrader CtpTraderHandle;
typedef struct CtpMd     CtpMdHandle;

/* ---- 回调约定 ----
 * 所有回调由 CTP 内部线程发起，wrapper 原样转发。
 * C# 侧收到后必须快速返回，不得阻塞、不得在该线程直接改 UI；
 * 应把数据投递到内部队列再处理。结构体指针仅在回调期间有效，C# 需立即拷贝。 */

/* ---- Trader 回调表 ---- */
typedef struct CtpTraderCallbacks {
    void (*OnFrontConnected)(void* ctx);
    void (*OnFrontDisconnected)(void* ctx, int reason);
    void (*OnRspAuthenticate)(void* ctx,
        const CThostFtdcRspAuthenticateField* field,
        const CThostFtdcRspInfoField* info, int requestId, bool isLast);
    void (*OnRspUserLogin)(void* ctx,
        const CThostFtdcRspUserLoginField* field,
        const CThostFtdcRspInfoField* info, int requestId, bool isLast);
    void (*OnRspError)(void* ctx,
        const CThostFtdcRspInfoField* info, int requestId, bool isLast);
    void (*OnRspQryTradingAccount)(void* ctx,
        const CThostFtdcTradingAccountField* field,
        const CThostFtdcRspInfoField* info, int requestId, bool isLast);
    void (*OnRspQryInvestorPosition)(void* ctx,
        const CThostFtdcInvestorPositionField* field,
        const CThostFtdcRspInfoField* info, int requestId, bool isLast);
    void (*OnRspQryOrder)(void* ctx,
        const CThostFtdcOrderField* field,
        const CThostFtdcRspInfoField* info, int requestId, bool isLast);
    void (*OnRspQryInstrument)(void* ctx,
        const CThostFtdcInstrumentField* field,
        const CThostFtdcRspInfoField* info, int requestId, bool isLast);
    void (*OnRspOrderInsert)(void* ctx,
        const CThostFtdcInputOrderField* field,
        const CThostFtdcRspInfoField* info, int requestId, bool isLast);
    void (*OnErrRtnOrderInsert)(void* ctx,
        const CThostFtdcInputOrderField* field,
        const CThostFtdcRspInfoField* info);
    void (*OnRspOrderAction)(void* ctx,
        const CThostFtdcInputOrderActionField* field,
        const CThostFtdcRspInfoField* info, int requestId, bool isLast);
    void (*OnRtnOrder)(void* ctx, const CThostFtdcOrderField* field);
    void (*OnRtnTrade)(void* ctx, const CThostFtdcTradeField* field);
} CtpTraderCallbacks;

/* ---- 生命周期 ---- */
CtpTraderHandle CtpTrader_Create(const char* flowPath,
    const CtpTraderCallbacks* callbacks, void* ctx);
void CtpTrader_Release(CtpTraderHandle h);   /* 必须在 Join 后、非回调线程调用 */

/* ---- 连接 ---- */
int CtpTrader_RegisterFront(CtpTraderHandle h, const char* front);
int CtpTrader_Init(CtpTraderHandle h);
int CtpTrader_Join(CtpTraderHandle h);

/* ---- 认证 + 登录（统一；Mac 内部 4 参 + 采集库，见 §4.2）---- */
int CtpTrader_Authenticate(CtpTraderHandle h,
    const char* brokerId, const char* userId,
    const char* appId, const char* authCode, int requestId);
int CtpTrader_Login(CtpTraderHandle h,
    const char* brokerId, const char* userId, const char* password,
    const char* userAgent, int requestId);

/* ---- Mac 采集库接入点（可选，生产用）---- */
int CtpTrader_SetCollectionInfo(CtpTraderHandle h, const char* buf, int len);

/* ---- 查询 ---- */
int CtpTrader_QryTradingAccount(CtpTraderHandle h,
    const char* brokerId, const char* userId, int requestId);
int CtpTrader_QryInvestorPosition(CtpTraderHandle h,
    const char* brokerId, const char* investorId, int requestId);
int CtpTrader_QryOrder(CtpTraderHandle h,
    const char* brokerId, const char* investorId,
    const char* instrumentId, int requestId);
int CtpTrader_QryInstrument(CtpTraderHandle h,
    const char* instrumentId, const char* exchangeId, int requestId);

/* ---- 下单 / 撤单（直传结构体）---- */
int CtpTrader_OrderInsert(CtpTraderHandle h,
    const CThostFtdcInputOrderField* order, int requestId);
int CtpTrader_OrderAction(CtpTraderHandle h,
    const CThostFtdcInputOrderActionField* action, int requestId);

/* ---- 流订阅 ---- */
int CtpTrader_SubscribePrivateTopic(CtpTraderHandle h, int resumeType);
int CtpTrader_SubscribePublicTopic(CtpTraderHandle h, int resumeType);

/* ---- Md 回调表 + 接口（结构同上，略）---- */
typedef struct CtpMdCallbacks {
    void (*OnFrontConnected)(void* ctx);
    void (*OnFrontDisconnected)(void* ctx, int reason);
    void (*OnRspUserLogin)(void* ctx,
        const CThostFtdcRspUserLoginField* field,
        const CThostFtdcRspInfoField* info, int requestId, bool isLast);
    void (*OnRspSubMarketData)(void* ctx,
        const CThostFtdcSpecificInstrumentField* field,
        const CThostFtdcRspInfoField* info, int requestId, bool isLast);
    void (*OnRspUnSubMarketData)(void* ctx,
        const CThostFtdcSpecificInstrumentField* field,
        const CThostFtdcRspInfoField* info, int requestId, bool isLast);
    void (*OnRtnDepthMarketData)(void* ctx,
        const CThostFtdcDepthMarketDataField* field);
} CtpMdCallbacks;

CtpMdHandle CtpMd_Create(const char* flowPath,
    const CtpMdCallbacks* callbacks, void* ctx);
void CtpMd_Release(CtpMdHandle h);
int CtpMd_RegisterFront(CtpMdHandle h, const char* front);
int CtpMd_Init(CtpMdHandle h);
int CtpMd_Login(CtpMdHandle h, const char* brokerId,
    const char* userId, const char* password, int requestId);
int CtpMd_SubscribeMarketData(CtpMdHandle h, const char* const* symbols, int count);
int CtpMd_UnSubscribeMarketData(CtpMdHandle h, const char* const* symbols, int count);

#ifdef __cplusplus
}
#endif
```

### 7.3 回调约定与线程模型

- CTP 的 `OnRspXXX` / `OnRtnXXX` 在 CTP **内部线程**触发。wrapper 的 SPI 实现把这些调用原样转成 C 函数指针调用，**不在 wrapper 内加锁、不在 wrapper 内排队**——线程模型由 C# 层负责。
- C# 侧用 `Marshal.GetFunctionPointerForDelegate` 把委托转函数指针传入。**委托必须用 GCHandle 或静态字段钉住防止 GC 回收**（否则 CTP 回调时函数指针指向已回收委托 → 崩溃）。
- C# 回调入口把结构体**立即浅拷贝**成托管结构体，投递到无锁队列，由专用处理线程消费后再 raise .NET 事件。回调内绝不阻塞、绝不直接 mutate UI 绑定集合。
- 结构体指针**仅在回调期间有效**，跨线程使用必须先拷贝。

### 7.4 handle 生命周期与 Release 陷阱

- `Create` 内部 `CThostFtdcTraderApi::CreateTraderApi(flowPath)` 并 new 一个 SPI，handle 持有两者。
- `Release` 调 `api->Release()`。**坑**：CTP 的 `Release` 不得在回调线程内调用（会死锁/崩溃），且 `Release` 后 API 可能还有收尾线程在跑，调用方应先 `Join` 等待退出再 `Release`。wrapper 文档化此顺序。
- wrapper 自身析构不调 `Release`（避免双重释放）；C# 侧 `Dispose` 负责 `Release` + 释放 SPI。

### 7.5 错误码传递

- `Req*` 返回值：`0` 成功，`-1` 发送失败（未连/网络），`-2` 不支持，`-3` 请求过多（流控）。C# 映射成 `CtpException` 子类。
- 业务错误走回调里的 `CThostFtdcRspInfoField`（`ErrorID` int + `ErrorMsg` char[81]）。C# 侧 `RspInfo` → `CtpErrorException`。

### 7.6 三平台 CMake 配置

单 `CMakeLists.txt`，按平台分支链接 CTP：

```cmake
cmake_minimum_required(VERSION 3.20)
project(ctp_native CXX)
set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)

# CTP 二进制根目录（按平台）
if(WIN32)
    if(CMAKE_SIZEOF_VOID_P EQUAL 8)
        set(CTP_ROOT ${CMAKE_SOURCE_DIR}/../externals/ctp-6.7.2/win-x64)
    else()
        set(CTP_ROOT ${CMAKE_SOURCE_DIR}/../externals/ctp-6.7.2/win-x86)
    endif()
    set(CTP_LIB thosttraderapi_se thostmduserapi_se)
    set(CTP_INC ${CTP_ROOT})
    set(CTP_LINK_DIR ${CTP_ROOT})
    set(CTP_LINK_LIBS thosttraderapi_se thostmduserapi_se)
elseif(APPLE)
    set(CTP_ROOT ${CMAKE_SOURCE_DIR}/../externals/ctp-6.7.2/macos-universal)
    set(CTP_INC ${CTP_ROOT}/thosttraderapi_se.framework/Headers
                 ${CTP_ROOT}/thostmduserapi_se.framework/Headers)
    set(CTP_LINK_LIBS "-framework thosttraderapi_se" "-framework thostmduserapi_se")
    set(CTP_LINK_DIR ${CTP_ROOT})
elseif(UNIX)
    set(CTP_ROOT ${CMAKE_SOURCE_DIR}/../externals/ctp-6.7.2/linux-x64)
    set(CTP_INC ${CTP_ROOT})
    set(CTP_LINK_LIBS thosttraderapi_se thostmduserapi_se)
    set(CTP_LINK_DIR ${CTP_ROOT})
endif()

add_library(ctp_native SHARED
    TraderWrapper.cpp MdWrapper.cpp CollectionInfo.cpp)
target_include_directories(ctp_native PRIVATE ${CTP_INC})
target_link_directories(ctp_native PRIVATE ${CTP_LINK_DIR})
target_link_libraries(ctp_native PRIVATE ${CTP_LINK_LIBS})

# 输出名 ctp_native（.dll/.so/.libctp_native.dylib），C# 用 DllImport("ctp_native")
set_target_properties(ctp_native PROPERTIES OUTPUT_NAME ctp_native)

# macOS：让 wrapper 能在运行时找到 CTP framework
if(APPLE)
    set_target_properties(ctp_native PROPERTIES
        BUILD_RPATH ${CTP_ROOT}
        INSTALL_RPATH "@loader_path")
    # CollectionInfo.cpp 仅 Mac 编译采集库接入
    target_compile_definitions(ctp_private CTP_MAC=1)
endif()
if(UNIX AND NOT APPLE)
    target_compile_definitions(ctp_native PRIVATE CTP_LINUX=1)
endif()
if(WIN32)
    target_compile_definitions(ctp_native PRIVATE CTP_WIN=1)
endif()
```

Wrapper 内 `CtpTrader_Login` 平台分叉示例：

```cpp
int CtpTrader_Login(CtpTraderHandle h, const char* broker, const char* user,
                    const char* pwd, const char* ua, int reqId) {
    auto* t = reinterpret_cast<TraderImpl*>(h);
    CThostFtdcReqUserLoginField f{};
    std::strncpy(f.BrokerID, broker, sizeof(f.BrokerID));
    std::strncpy(f.UserID, user, sizeof(f.UserID));
    std::strncpy(f.Password, pwd, sizeof(f.Password));
#ifdef CTP_MAC
    // Mac 直连模式：填采集信息后调 4 参版
    char info[264]{}; int len = 264;
    if (t->collectionInfo) {            // 生产接入点
        std::memcpy(info, t->collectionInfo, t->collectionLen);
        len = t->collectionLen;
    }
    return t->api->ReqUserLogin(&f, reqId, len, info);
#else
    return t->api->ReqUserLogin(&f, reqId);   // Win/Linux 2 参版
#endif
}
```

## 8. C# 层设计

### 8.1 结构体镜像（P/Invoke 层）

用 `[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]` 按 6.7.2 头文件 1:1 镜像，字段用 `[MarshalAs(UnmanagedType.ByValTString, SizeConst = N)]` 对应 CTP 定长 char 数组。**只镜像 ABI/P/Invoke 必需的结构体，不作为公共 API。**

### 8.2 公共模型（`Models/`，干净 API）

```csharp
public sealed class TickData {
    public string InstrumentId { get; init; }
    public string ExchangeId    { get; init; }
    public string TradingDay     { get; init; }
    public string ActionDay      { get; init; }
    public DateTime UpdateTime    { get; init; }   // 含毫秒
    public double LastPrice      { get; init; }
    public long   Volume        { get; init; }
    public double OpenInterest  { get; init; }
    public double Turnover      { get; init; }
    public double BidPrice      { get; init; }
    public int    BidVolume     { get; init; }
    public double AskPrice      { get; init; }
    public int    AskVolume     { get; init; }
    public double PreSettlement { get; init; }
    // 一档够 MVP；五档用 PriceLevel[5] 扩展
}

public sealed class OrderRequest {
    public string InstrumentId { get; init; }
    public Direction Direction { get; init; }     // Buy/Sell
    public OffsetFlag Offset    { get; init; }     // Open/Close/CloseToday/CloseYesterday
    public OrderPriceType PriceType { get; init; } // Limit/Market
    public double LimitPrice { get; init; }
    public int    Volume     { get; init; }
    public TimeCondition TimeCondition { get; init; } // GFD/IOC/FOK
    // ……
}
```

### 8.3 async 封装（TCS 包回调）

```csharp
public sealed class CtpTrader : IDisposable {
    private readonly Dictionary<int, TaskCompletionSource<object>> _pending = new();
    // requestId -> TCS；查询型回调按 isLast 聚合成 List 后 complete

    public Task<LoginResult> LoginAsync(LoginRequest req, CancellationToken ct = default) {
        var id = NextRequestId();
        var tcs = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        _ = WithTimeoutAsync(tcs, TimeSpan.FromSeconds(30), ct);  // 超时取消
        _api.Authenticate(...);    // 认证完成回调里再发 Login
        // 或 OnRspAuthenticate → _api.Login(...)
        return tcs.Task.ContinueWith(t => ToLoginResult(t.Result));
    }
    public Task<IReadOnlyList<Position>> QueryInvestorPositionAsync(...) { ... }  // isLast 聚合
}
```

### 8.4 事件（流式，不 await）

`OnRtnOrder` / `OnRtnTrade` / `OnTick` 暴露成标准 .NET 事件，回调线程经队列投递后 raise。

## 9. 生命周期与登录流程

完整流程（设计文档原稿漏了 ReqAuthenticate，补上）：

```
RegisterFront → Init
   → OnFrontConnected
      → ReqAuthenticate(BrokerID, UserID, AppID, AuthCode)        // 穿透式认证，6.3+ 必需
         → OnRspAuthenticate
            → ReqUserLogin(BrokerID, UserID, Password [,采集信息@Mac])
               → OnRspUserLogin
                  → 保存 FrontID/SessionID/MaxOrderRef
                  → SubscribePrivateTopic / SubscribePublicTopic（按需）
                  → 后续查询/下单
OnFrontDisconnected → 见 §11 重连
退出 → Join → Release
```

- **ReqAuthenticate 在 ReqUserLogin 之前**，AppID/AuthCode 由柜台分配（SimNow 也提供测试用）。
- Mac 直连登录的采集信息在 wrapper 内部吸收，C# 无感。

## 10. 会话与引用管理（C# 层拥有）

CTP 要求调用方维护这些标识，否则订单回报无法匹配：

- **RequestID**：每个 API 实例原子自增 int（`Interlocked.Increment`），每次 `Req*` 调用递增。
- **FrontID / SessionID**：登录成功后从 `CThostFtdcRspUserLoginField` 保存，订单回报匹配键 = `(FrontID, SessionID, OrderRef)`。
- **OrderRef**：自增，`string` 零填充（如 `"000001"`），每次下单前递增并填充。多线程下单需加锁串行化 OrderRef 分配。

## 11. 流控

- CTP 查询限流约 **1 req/s**，超限柜台可能断开。C# 层内置串行查询队列 + 令牌桶（查询 1/s，可配），`QueryXxxAsync` 入队等待。
- 下单/撤单限流较宽（柜台不同，常见 ~10/s），仍走队列。
- 触发 `-3`（请求过多）时自动退避重试一次。

## 12. 断线重连

- CTP 本身**不自动重连**。`OnFrontDisconnected(reason)` 触发后，C# 层按指数退避（1s→2s→4s… 封顶 30s）重新 `RegisterFront + Init`，重连成功后**重新走认证+登录**（SessionID 变了，旧 OrderRef 需用 FrontID/SessionID 重新匹配）。
- 暴露 `Disconnected` 事件让上层感知；可配置是否自动重连。

## 13. 错误码映射

`CtpException` 基类 + 子类：`CtpSendFailedException`（-1）、`CtpUnsupportedException`（-2）、`CtpTooManyRequestsException`（-3，自动重试）、`CtpBusinessException(ErrorID, ErrorMsg)`（来自 RspInfo）。错误码表常量化（CTP error.xml 可作为来源）。

## 14. 开发阶段

- **Phase 1**：native wrapper（C ABI）+ `CtpTrader.Create/Init/Authenticate/Login` + ConsoleSample，目标在 Windows 跑通 SimNow 登录（用户历史已跑通，主要验证 wrapper 接线）。
- **Phase 2**：Md 接口（`CtpMd_*`）+ Tick 订阅 + `OnTick` 事件，ConsoleSample 打印 Tick。
- **Phase 3**：交易接口（`QueryTradingAccount`/`QueryInvestorPosition`/`OrderInsert`/`OrderAction` + `OnRtnOrder`/`OnRtnTrade`），完整 async + 会话/引用/流控。
- **Phase 4**（可选）：Mac 采集库接入、断线重连、错误码全量映射。

## 15. 参考与不做的事

- **参考**：Tendx/Ctp.Net-6.7.2-win-x64 —— **仅作字段映射字典**（`TraderApi.g.h`/`Objects.g.h` 是 6.7.2 头文件的全量 C# 映射，可直接借鉴结构体定义）。**不抄其技术**（C++/CLI、仅 Win、暴露原始结构名）——恰是本项目 §2 要抛弃的。
- **不做**：C++/CLI、暴露原始 CTP 结构作公共 API、内置策略/风控/回测、依赖任何 UI 框架。

---

## 待确认 / 后续

- [x] `externals/ctp-6.7.2/` 大二进制如何纳入：~~gitignore + 下载脚本？还是 git-lfs？~~ → **已采用 Git LFS**（2026-07-22）。`.gitattributes` 跟踪三平台大二进制（win dll/lib、linux so、macos/ios framework 无扩展名二进制），头文件/plist/CodeSignature 等文本小文件留普通 git；`.gitignore` 移除该目录忽略行。任意机器 `git clone` 即得，无需手动从 SimNow 下载就位。
- [ ] Mac 采集库：从哪获取真实生产版？Phase 1 先用空实现过 SimNow。
- [ ] NuGet 打包 native 的方式（`runtimes/{rid}/native/` 放 `.dll/.so/.dylib`，按 RID 自动解析）。
