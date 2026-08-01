# mcp/ — 自定义 MCP Server

这个目录放**自己写的 MCP server 源码**，每个 server 一个独立子项目（自带 csproj），互不依赖、不引用 Aether。
Agent 通过 MCP 协议（stdio）调用它们——和 Claude Code / Cursor 配 MCP server 是同一套主流做法。

## 写一个 MCP server

1. 在 `mcp/` 下建子目录（如 `mcp/MyServer/`），加 `MyServer.csproj` + `Program.cs`。
2. 引用 `ModelContextProtocol`（server SDK）+ `Microsoft.Extensions.Hosting`。
3. `AddMcpServer().WithStdioServerTransport().WithToolsFromAssembly()` 经 stdio 暴露工具。
4. 用 `[McpServerToolType]` 标记工具类、`[McpServerTool]` 标记方法。

完整范例见 [`SampleServer/Program.cs`](SampleServer/Program.cs)（暴露 `Add(a,b)` / `Now()` 两个工具）。

## 配置 / 启用一个 server

**方式一（推荐）：在 app 内配置。** 专家页 → 「MCP工具」tab → 「+ 添加」→ 粘贴 server 的 JSON 配置 → 保存。
列表会显示已配置的 server、连接状态、以及每个 server 暴露的具体工具。可增删改 + 刷新。

**方式二：放 JSON 文件。** 在租户数据目录的 `mcp-servers/` 下放 `*.json`（如 `~/Library/Application Support/Aether/Desktop-Stockapp/mcp-servers/`），启动时自动扫描。

### JSON 格式

单个 server 对象（app 内编辑用这种）：
```json
{
  "name": "sample",
  "command": "dotnet",
  "args": ["run", "--project", "/绝对路径/Aether/mcp/SampleServer"],
  "env": {}
}
```
也兼容主流 `.mcp.json` 的 `mcpServers` 映射（可直接把 Claude Code/Cursor 的配置丢进 `mcp-servers/`）：
```json
{ "mcpServers": { "sample": { "command": "...", "args": [...] } } }
```

字段：`command`+`args`+`env`（stdio）或 `url`+`headers`（HTTP/SSE 远程）。`arguments` 作为 `args` 别名。

### 配置本地程序（两种都行）

- **跑源码**（开发期方便，但机器需装 .NET SDK）：
  ```json
  { "name": "sample", "command": "dotnet", "args": ["run", "--project", "/绝对路径/Aether/mcp/SampleServer"] }
  ```
- **跑编译好的程序**（分发用，无需 SDK；先 `dotnet publish` 出可执行文件）：
  ```json
  { "name": "sample", "command": "/绝对路径/Aether/mcp/SampleServer/bin/Release/net10.0/osx-arm64/publish/SampleServer" }
  ```
- **远程 HTTP server**：
  ```json
  { "name": "remote", "url": "https://host/sse", "headers": { "Authorization": "Bearer xxx" } }
  ```

> 从 `.app` 启动时 CWD 是 `/`，`--project` / 程序路径用**绝对路径**最稳。

## 让 agent 用上 MCP 工具

1. 配好 server（见上），app 启动时自动连接 + 工具注册进 `ToolRegistry`。
2. 专家编辑器 → 工具选择器 → 勾选想要的 MCP 工具（和原生工具一起，分类 "MCP"）→ 保存。
3. 该专家就只用勾选的 MCP 工具。MCP 工具默认按 Dangerous 审批等级（来源未知，`RequireToolApproval` 开启时会弹审批）。

## 源码目录 vs 配置目录

- `mcp/`（本目录，仓库里）—— **server 源码**，进版本管理。
- `mcp-servers/`（租户数据目录，不在仓库）—— **运行时 JSON 配置**，按机器/租户落地。app 内编辑写的就是这个目录。

两者分离：源码进版本管理，配置按机器落地。
