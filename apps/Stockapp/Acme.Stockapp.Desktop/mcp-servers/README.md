# 桌面控制 MCP server 配置（desktop.json）

本目录的 `desktop.json` 是**模板**。运行时 Aether 从 **tenant 目录**的 `mcp-servers/` 扫描配置：

```
<用户主目录>/Library/Application Support/Aether/Desktop-Stockapp/mcp-servers/desktop.json
```

> 仓库里这份不会被运行时扫描——把它复制到上面的 tenant 目录，再按需改路径。

## MCP server：FlaUI-MCP

仓库已把 [FlaUI-MCP](https://github.com/shanselman/FlaUI-MCP) 克隆到 `externals/FlaUI-MCP/`（第三方引擎，git submodule，类比 `externals/Lean`）。它是 **ref 驱动**的 Windows 桌面自动化 server（仿 Playwright：先 `windows_snapshot` 取 accessibility 树 + refs，再用 ref 调 `windows_click`/`windows_type`）。

> **TFM 已改为 `net10.0-windows`**：原 repo 是 `net8.0-windows`，但本机只装 .NET 10 → 启动即「server shut down」。
> submodule 内 csproj 改成 `net10.0-windows` + `RollForward`，用本机 .NET 10 直接跑。FlaUI 5.0 兼容 .NET 10。

`McpDesktopAutomation`（`business/AppAutomation/Acme.AppAutomation.Infrastructure`）把上层的 `DesktopLocator`（ByName/ByRole）翻译成「snapshot → 树里按 Name/Role 找 ref → windows_click ref」序列；`FocusWindowAsync` 记下 server 返回的 handle，后续 snapshot 带上它定位正确窗口。工具名对齐 server：

| McpDesktopAutomation 原语 | FlaUI-MCP 工具 | 参数 |
|---|---|---|
| FocusWindow(title) | `windows_focus` | {title} |
| WindowExists(title) | `windows_list_windows` | — |
| Click/DoubleClick/RightClick(locator) | `windows_click` | {ref, button?, doubleClick?} |
| Type(text) | `windows_type` | {text} |
| Key(combo) | `windows_send_keys` | {chord}（ctrl+a→Ctrl+A） |
| GetText(locator) | `windows_get_text` | {ref} |
| GetUITree | `windows_snapshot` | {handle?} |

## 启用 MCP 下单路径

`desktop.json` 已指向发布好的 exe（`C:\Users\iamch\AppData\Local\Aether\FlaUI-MCP\FlaUI.Mcp.exe`），并已复制到 tenant 目录，开箱即用。若要重建/换路径：

1. **发布 server**（一次性）：`dotnet publish externals/FlaUI-MCP/src/FlaUI.Mcp/FlaUI.Mcp.csproj -c Release -r win-x64 --self-contained false -o <目录>`
   （`--self-contained false` 依赖本机 .NET 10；要免装运行时改 `--self-contained true`）。
2. **`desktop.json` 的 `command` 指向 exe 绝对路径**，`args: []`。或用 `dotnet run --no-build --project <csproj绝对路径>`（首次需先 build）。
3. **复制 `desktop.json` 到 tenant 目录** `<用户主目录>/Library/Application Support/Aether/Desktop-Stockapp/mcp-servers/`。
4. **重启 Aether**。股票交易页顶部「下单方式」切到 **MCP**（持久化到 `stocktrade.method`）。
5. **启动同花顺「网上股票交易系统5.0」并登录** → 连接器 tab 点「连接」确认窗口 → 下单。

`App.axaml.cs` 按 server 名 `desktop` 查找；找不到就回退 `NotConnectedMcpToolInvoker` 桩，操作优雅失败「桌面控制后端未连接」，不崩。

## 注意

- FlaUI-MCP 是 `net8.0-windows` + Windows-only（FlaUI/UIAutomation）。Mac 无此 server，需自建 Mac-MCP（见 `docs/desktop-control-mcp.md`）。
- 同一个 `desktop.json` 也让 agent 自动获得桌面工具（② LLM 路径，经 `McpServerRegistry`）。① 固定过程路径走独立的 `McpDesktopToolInvoker`，自己连一次 server，与 agent 连接互不影响（doc §4.1）。
