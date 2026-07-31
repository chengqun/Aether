# Ctp.Net.Core — CTP 6.7.2 跨平台 .NET 绑定 设计文档

CTP（综合交易平台，上海期货信息技术公司）的官方 API 是 C++ 的（`CThostFtdcTraderApi` /
`CThostFtdcMdApi`，虚函数接口 + SPI 回调），无法被 .NET 直接使用。`Ctp.Net.Core` 把它包装成
跨平台 .NET 库：**C++ 薄封装 → flat C ABI → C# P/Invoke → 面向对象的 .NET API**。

本文档描述当前仓库的真实设计与三平台（Windows / macOS / Linux）构建运行方式。

---

## 1. 三层架构

```
┌─────────────────────────────────────────────────────────────────┐
│  C# 业务层  (Trader/CtpTrader.cs, MarketData/CtpMarketData.cs)   │
│  面向对象：sealed class + IDisposable + 事件 + async/await        │
│  对外暴露：CtpTrader / CtpMarketData + Account/Position/Order…    │
└───────────────────────────────┬─────────────────────────────────┘
                                │ P/Invoke  (DllImport "ctp_native")
┌───────────────────────────────┴─────────────────────────────────┐
│  C# 互操作层  (Native/NativeMethods.cs, Native/CtpStructs.cs)     │
│  extern 声明 24 个 flat C 函数 + 镜像 wrapper 的 POD 结构体布局   │
└───────────────────────────────┬─────────────────────────────────┘
                                │  C ABI (extern "C", 跨平台稳定)
┌───────────────────────────────┴─────────────────────────────────┐
│  C++ wrapper 层  (Ctp.Native/TraderWrapper.cpp, MdWrapper.cpp)    │
│  把 CTP C++ 虚函数接口 + SPI 回调 转成 flat C ABI                │
│  从 CTP 大结构体提取关键字段填入稳定的小 POD（CtpAccountInfo 等） │
└───────────────────────────────┬─────────────────────────────────┘
                                │  C++ (CThostFtdc*Api, 闭源二进制)
┌───────────────────────────────┴─────────────────────────────────┐
│  CTP 官方 SDK 6.7.2  (externals/Ctp.Net.Core/ctp-sdk/<平台>)      │
│  Windows: thost*.dll + .lib + 头文件                              │
│  macOS:   thost*.framework + MacDataCollect.framework             │
│  Linux:   thost*.so + 头文件                                     │
│  iOS:     thost*.framework + IOSDataCollect.framework (静态库)    │
└─────────────────────────────────────────────────────────────────┘
```

为什么是三层而不是直接 P/Invoke CTP：
- CTP 的 C++ 接口是虚函数 + 回调，没有 flat C 导出，P/Invoke 无法直接调用。
- wrapper 用 C++ 实现一个 `CThostFtdcTraderSpi` 子类接收回调，再转成 C 函数指针回调；
  .NET 侧用 `GCHandle` 把托管对象指针透传到 C，回调时反向找回对象（见 `CtpTrader.Self`）。
- 结构体不直接暴露 CTP 的完整定义（跨版本/平台布局会变），wrapper 提取关键字段
  填入自己定义的稳定小 POD（`CtpAccountInfo`/`CtpOrderInfo` 等），C# 侧镜像这些小结构。

### 1.1 C++ wrapper 层（`externals/Ctp.Net.Core/Ctp.Native/`）

| 文件 | 作用 |
|---|---|
| `include/ctp_api.h` | C ABI 公共头：导出宏 `CTP_API` + 24 个函数声明 + 稳定 POD 结构体 + 回调函数指针表 |
| `TraderWrapper.cpp` | 交易接口封装：`CtpTrader_*` 系列函数 + `TraderSpi`（SPI 回调实现） |
| `MdWrapper.cpp` | 行情接口封装：`CtpMd_*` 系列函数 + `MdSpi` |
| `CollectionInfoIOS.mm` | iOS 采集信息（穿透式监管，Objective-C++） |
| `CMakeLists.txt` | 跨平台 CMake 构建：按平台选 SDK 目录、库类型、导出宏、`/utf-8` |

**导出宏 `CTP_API`**（跨平台关键）：
```c
#if defined(_WIN32)
  #if defined(CTP_NATIVE_EXPORTS)
    #define CTP_API __declspec(dllexport)   // 编译 wrapper 时
  #else
    #define CTP_API __declspec(dllimport)   // 消费方包含头文件时
  #endif
#else
  #define CTP_API                            // macOS/Linux 默认导出符号
#endif
```
Windows DLL 默认不导出 `extern "C"` 符号，必须显式 `dllexport`，否则 P/Invoke 报
`Unable to find entry point named CtpTrader_Create`。macOS/Linux 符号默认导出，宏为空。

### 1.2 C# 互操作层（`Native/`）

- `NativeMethods.cs`：`internal static class`，`private const string Lib = "ctp_native"`，
  24 个 `[DllImport]` 声明。运行时按平台解析：Windows→`ctp_native.dll`、
  macOS→`libctp_native.dylib`、Linux→`libctp_native.so`（DllImport 自动补平台后缀）。
- `CtpStructs.cs`：用 `[StructLayout(LayoutKind.Sequential)]` 镜像 wrapper 的 POD，
  `char[N]` 映射为 `[MarshalAs(UnmanagedType.ByValTStr, SizeConst=N)] public string`。

### 1.3 C# 业务层（`Trader/`、`MarketData/`）

- `CtpTrader` / `CtpMarketData`：`sealed class : IDisposable`，把 flat C 函数包成
  面向对象 API。事件驱动（`FrontConnected`/`OrderReceived`/`TickReceived`…），
  异步查询用 `TaskCompletionSource` + `QueryCollector` 把多次回调聚合为一次 `Task`。
- `GCHandle` 透传：`CtpTrader_Create` 把 `GCHandle.ToIntPtr(self)` 作为 `ctx` 传给 C，
  C 在回调时原样回传，`CtpTrader.Self(IntPtr ctx)` 反查回托管对象。
  （托管侧必须持有该 GCHandle 引用以防 GC 回收——否则回调时函数指针失效会崩溃。）

---

## 2. 跨平台编译机制（核心设计）

### 2.1 资源分布（全部入库，clone 即用）

```
externals/Ctp.Net.Core/
├── Ctp.Native/                 wrapper C++ 源码（入库）
│   ├── CMakeLists.txt          跨平台构建
│   ├── TraderWrapper.cpp / MdWrapper.cpp / include/ctp_api.h
│   └── build-<platform>/       CMake 构建产物（gitignore，不入库）
├── ctp-sdk/                    CTP 官方 6.7.2 闭源二进制 SDK（全平台入库）
│   ├── win-x64/   win-x86/     *.dll + *.lib + *.h
│   ├── macos-universal/        *.framework（含 MacDataCollect）
│   ├── linux-x64/              *.so + *.h
│   └── ios/                    *.framework（含 IOSDataCollect）
└── build/native/               wrapper 编译产物（ctp_native.* + thost*.dll/so）随 publish 流出
                                预编译的 dylib/.a 历史产物入库（macOS/iOS 走预编译路径）
```

**关键决策**：CTP 官方 SDK 是闭源二进制，没有比"入库"更好的分发方式。
`ctp-sdk` 全平台入库（约 61MB），任何人 clone 后无需额外放置任何东西。

### 2.2 混合模式：现场编译优先 + 预编译兜底

三平台统一采用**混合模式**：脚本检测到本机有 CMake + 编译器 → 现场编出对应平台的
wrapper（覆盖入库旧产物，保证与源码一致）；工具链不全 → 回退用 `build/native/` 里入库的
预编译产物兜底（clone 即用，无需编译器）。这样既保证产物匹配源码，又让无编译器环境开箱即用。

现场编译时，`CMakeLists.txt` 按平台选 SDK 目录：
```cmake
set(CTP_SDK ${CMAKE_SOURCE_DIR}/../ctp-sdk)
if(APPLE)         set(CTP_ROOT ${CTP_SDK}/macos-universal) ...
elseif(UNIX)      set(CTP_ROOT ${CTP_SDK}/linux-x64) ...
elseif(WIN32)     set(CTP_ROOT ${CTP_SDK}/win-x64 或 win-x86) ...
```
- Windows：`NMake Makefiles` + MSVC，定义 `CTP_NATIVE_EXPORTS`（用 dllexport）+ `/utf-8`
  （MSVC 默认按 GBK 解析源码，UTF-8 中文注释会乱码致行号错位）。
- macOS：`Unix Makefiles`（或 Xcode）+ clang，链接 framework，`@loader_path` rpath。
- Linux：`Unix Makefiles` + gcc，链接 `thost*.so`，`$ORIGIN` rpath，`POST_BUILD` 拷 `thost*.so` 到 `build/native`。
- 产物输出到 `build/native/`，由 `Ctp.Net.Core.csproj` 的 `Content` glob 流出：
  ```xml
  <Content Include="build\native\*.dylib;build\native\*.so;build\native\*.dll"
           CopyToPublishDirectory="PreserveNewest" />
  ```

### 2.3 预编译兜底产物（入库）

`build/native/` 里入库了各平台的预编译 wrapper 产物，作为无编译器环境的兜底：

| 平台 | 兜底产物 | 状态 |
|---|---|---|
| macOS | `libctp_native.dylib` | 已入库（历史 x86_64 产物；现场编译会覆盖为最新） |
| iOS | `ios/libctp_native.a` | 已入库（MAUI 静态库路径，非桌面打包范围） |
| Windows | `ctp_native.dll` | 待入库（在 Windows 上跑脚本现场编一次后提交） |
| Linux | `libctp_native.so` | 待入库（在 Linux 上跑脚本现场编一次后提交） |

**维护约定**：每次修改 wrapper 源码（`Ctp.Native/*.cpp`、`ctp_api.h`）后，应在三平台各跑
一次脚本，把现场编出的新产物 `git add` 到 `build/native/` 更新兜底——否则无编译器环境会用到过期产物。

`build/native/` 里入库了历史的预编译产物：`libctp_native.dylib`（macOS x86_64）、
`ios/libctp_native.a`。mac 打包脚本现已改为**现场编译**（见 §3.2），不再依赖这些旧产物；
但旧产物仍入库作为兜底。iOS 走 MAUI 静态库路径（`LIB_TYPE STATIC`），不在桌面打包范围。

---

## 3. 三平台打包流程

三个脚本逻辑对齐，固定打包 **Stockapp 股票版**（本仓库提交到 GitHub 的只有 Stockapp 租户，
无需参数）。共同步骤：① 确保 Lean 子模块 ② 现场编 CTP wrapper ③ `dotnet publish`（self-contained）
④ 组装产物 ⑤ 打分发包。CTP wrapper 编译失败时只警告不中断——产物仍可启动，
仅 CTP 期货行情/交易不可用（聊天/专家/回测/Lean/笔记等不受影响）。

### 3.1 Windows ✅ 已验证

**脚本**：`scripts/build-stockapp-windows.ps1`（PowerShell 5.1 兼容，UTF-8 BOM）
**前置**：.NET 10 SDK + CMake + MSVC（Visual Studio 2022 / Build Tools，含 VC++ 工作负载）

```
.\scripts\build-stockapp-windows.ps1
```

流程：
1. `Build-CtpNative`：探测 CMake（`Find-CMake`，兜底查 `C:\Program Files\CMake\bin`）+
   MSVC（`Find-MSVC`，vswhere → 常见目录 → PATH）。用 `vcvars64.bat` 初始化 MSVC 环境
   （调用期间临时放宽 `$ErrorActionPreference`，因 cmd/vcvars 写 stderr 会触发 Stop），
   `cmake -G "NMake Makefiles"` 编出 `ctp_native.dll`。
2. `dotnet publish ... -r win-x64 --self-contained true`。
3. 组装 `dist/Aether-Stockapp/`（exe + dll + 原生库），清掉无用的 `.dylib/.so`，
   `Unblock-File`（去 Mark of the Web），生成 `.lnk` 快捷方式 + `.zip`。

**产物**：
- `dist/Aether-Stockapp/Acme.Stockapp.Desktop.exe` —— **双击运行**
- `dist/Aether-Stockapp.lnk` —— 快捷方式
- `dist/Aether-Stockapp-1.0.0-win-x64.zip` —— 分发包

**验证结果（2026-07）**：干净状态（清 build 缓存 + 删 `build/native` 的 dll）下重跑成功。
`ctp_native.dll` 现场编译，dumpbin 确认 24 个导出符号齐全（`CtpTrader_Create` 等），
三件套（`ctp_native.dll` + `thostmduserapi_se.dll` + `thosttraderapi_se.dll`）随包分发。

**安装工具链**（本机若无）：
```powershell
winget install --id Kitware.CMake
winget install --id Microsoft.VisualStudio.2022.BuildTools --override "--quiet --wait --norestart --add Microsoft.VisualStudio.Workload.VCTools --add Microsoft.VisualStudio.Component.VC.Tools.x86.x64 --add Microsoft.VisualStudio.Component.Windows11SDK.22621 --includeRecommended"
```

### 3.2 macOS ✅ 已验证

**脚本**：`scripts/build-stockapp-macos.sh`（GitHub 同步，固定打 Stockapp 租户）。
另有一个本地脚本 `scripts/build-desktop-macos.sh`（已加入 `.gitignore`，不推 GitHub），
可同时打 Qifu + Stockapp（`./scripts/build-desktop-macos.sh [stockapp|qifu|all]`）；
CTP 现场编译 + framework 随包逻辑与 stockapp 脚本一致，仅 Stockapp 走（Qifu 无 CTP）。
**前置**：.NET 10 SDK + CMake + clang（Xcode Command Line Tools：`xcode-select --install`）

```bash
./scripts/build-stockapp-macos.sh
```

流程：
1. `build_ctp_native`：`cmake` + clang，从 `ctp-sdk/macos-universal` 编出 `libctp_native.dylib`。
2. **rpath 归一化**：`cmake --build`（非 `--install`）只写入绝对 `BUILD_RPATH`（指向源码树
   `ctp-sdk/macos-universal`，不可移植）。脚本用 `install_name_tool` 删掉绝对 rpath、加
   `@loader_path`，使发布包内 dylib 用相对 rpath 解析随包的 framework。dev 模式 `dotnet run`
   仍用 build/native 里默认的绝对 rpath 在源码树解析，不受影响。**不动 CMakeLists** →
   Windows/Linux 构建零影响（PE 无 rpath 概念；Linux 的 `$ORIGIN` 已由 CMakeLists 设好）。
3. `dotnet publish ... -r osx-x64 --self-contained true`（默认 osx-x64；Apple Silicon 走 Rosetta。
   如需 arm64 原生，改 `RUNTIME=osx-arm64` 后重跑——注意 wrapper 架构需与 RUNTIME 匹配）。
4. 组装 `dist/Aether-Stockapp.app` bundle（`Contents/MacOS` + `Info.plist` + `PkgInfo`），
   **随包 3 个 CTP framework**（`ditto` 拷 `thosttraderapi_se`/`thostmduserapi_se`/
   `MacDataCollect` framework 到 `Contents/MacOS/`，由 `@loader_path` 解析——csproj 只 copy
   `*.dylib` 不 copy framework，不随包则运行时 `dyld: Library not loaded`），
   `xattr -cr` 去 Gatekeeper 隔离属性，`chmod +x`。

**产物**：`dist/Aether-Stockapp.app` —— 双击运行，或 `open dist/Aether-Stockapp.app`

**验证结果（2026-07，Intel x86_64 / macOS 26）**：干净状态重跑成功。`libctp_native.dylib`
现场编译，`nm -gU` 确认 24 个导出符号齐全；bundle 内 dylib 的 `LC_RPATH` 仅 `@loader_path`
（无绝对路径泄漏）；3 个 framework 随包；`python3 ctypes.CDLL` 运行时 dlopen 成功，
`CtpTrader_Create` 符号可寻址（dyld 解析 libctp_native.dylib + 3 framework 全部通过）。

**待验证点**：
- Apple Silicon（arm64）机器上 `RUNTIME=osx-x64` + Rosetta 的运行；以及切 `osx-arm64` 时
  wrapper 编译架构与 RID 的一致性（SDK framework 已是 universal：x86_64+arm64）。

### 3.3 Linux ⏳ 待验证

**脚本**：`scripts/build-stockapp-linux.sh`
**前置**：.NET 10 SDK + CMake + gcc/g++
```bash
# Debian/Ubuntu
sudo apt install -y cmake build-essential
# Fedora/RHEL
sudo dnf install -y cmake gcc-c++
```

```bash
./scripts/build-stockapp-linux.sh
```

流程：
1. `build_ctp_native`：`cmake` + gcc，从 `ctp-sdk/linux-x64` 编出 `libctp_native.so`。
2. `dotnet publish ... -r linux-x64 --self-contained true`。
3. 组装 `dist/Aether-Stockapp/`，清掉无用的 `.dll/.dylib`，写 `VERSION.txt`，打 `.tar.gz`。

**产物**：
- `dist/Aether-Stockapp/Acme.Stockapp.Desktop` —— 直接运行
- `dist/Aether-Stockapp-1.0.0-linux-x64.tar.gz` —— 分发包

**待验证点**：
- 现场编译 `libctp_native.so` 是否成功（Linux 此前无打包脚本，本次新建）。
- `libctp_native.so` 链接的 `thost*.so` 运行时能否正确解析（`$ORIGIN` rpath）。

---

## 4. 验证状态总览

| 平台 | 脚本 | wrapper 编译方式 | 状态 | 验证日期 |
|---|---|---|---|---|
| Windows | `build-stockapp-windows.ps1` | 混合：现场编(CMake+MSVC) / 预编译兜底 | ✅ 已验证 | 2026-07 |
| macOS | `build-stockapp-macos.sh` | 混合：现场编(CMake+clang) / 预编译兜底 | ✅ 已验证 | 2026-07 |
| Linux | `build-stockapp-linux.sh` | 混合：现场编(CMake+gcc) / 预编译兜底 | ⏳ 待验证 | — |

**验证方法**（mac/Linux）：clone 仓库到对应平台 → 装好工具链 → 跑脚本 → 确认
`ctp_native` 产物生成 + 24 个符号导出 + 产物包内三件套齐全 → 启动应用进入期货页测试
CTP 行情/交易连接。

---

## 5. 常见问题

**`Unable to find entry point named CtpTrader_Create in dll ctp_native`**
Windows 上 wrapper 没用 `__declspec(dllexport)` 导出符号。已修复：`ctp_api.h` 加 `CTP_API`
宏，`CMakeLists` 定义 `CTP_NATIVE_EXPORTS`。若仍出现，确认是重新编译的 dll（非旧产物）。

**MSVC 编译一堆 `C2059 语法错误:"if"` 行号错位**
源码是 UTF-8（含中文注释），MSVC 在中文 Windows 默认按 GBK 解析致乱码。已修复：
`CMakeLists` 的 MSVC 分支加 `target_compile_options(... /utf-8)`。

**找不到 CMake / MSVC**
Windows 脚本的 `Find-CMake`/`Find-MSVC` 兜底查常见安装位置；winget 安装命令见 §3.1。
mac 装 Xcode Command Line Tools；Linux 装 `cmake` + `build-essential`/`gcc-c++`。

**CTP wrapper 编译失败但应用能启动**
设计如此：编译失败只警告不中断。产物可启动，仅 CTP 期货功能不可用，其余功能正常。

**macOS 链接报 `unknown file type in '.../*.framework/<name>'`**
`ctp-sdk/macos-universal/` 下 3 个 framework 的符号链接（顶层二进制 / `Versions/Current` /
`Headers` / `Resources`，共 12 个）被 git 存成了普通文本文件（mode 100644，内容是链接目标
路径）。这是在禁用 symlink 的环境（Windows / `core.symlinks=false`）下 `git add` 提交导致的。
链接器用 `-framework` 解析到顶层文本文件 → `unknown file type`。已修复：12 个符号链接恢复为
真 symlink（mode 120000）。Windows 不受影响（Windows 走 `win-x64` 的 `.dll/.lib`，不碰
`macos-universal`）。**注意**：若将来从 Windows 重新 `git add` 这些 framework 文件，
可能再次腐化——在 mac/Linux 上提交 framework 改动最稳妥。

**macOS 运行时 `dyld: Library not loaded: @rpath/thosttraderapi_se.framework/...`**
`libctp_native.dylib` 用 `@rpath/...framework` 引用 3 个 framework（rpath=`@loader_path`），
而 `Ctp.Net.Core.csproj` 只 copy `build/native/*.{dylib,so,dll}`、不 copy framework。发布包
必须把 3 个 framework 随进 `Contents/MacOS/`（脚本用 `ditto` 拷贝，保留符号链接），否则
`@loader_path` 解析不到。dev 模式 `dotnet run` 不受影响（dylib 默认绝对 rpath 指向源码树 ctp-sdk）。

**CTP 官方 SDK 在哪**
`externals/Ctp.Net.Core/ctp-sdk/<平台>`，全平台已入库，clone 即有，无需额外放置。
（历史上的 `externals/ctp-6.7.2` 已移除，统一用 `ctp-sdk`。）
