# mcp/ — 自定义 MCP Server 源码

这个目录放**自己写的 MCP server 源码**，每个 server 一个子项目（自带 csproj），互不依赖、不引用 Aether。
Agent 通过 `mcp-servers/` 目录下的 JSON 配置连接它们（走 stdio）——和 Claude Code / Cursor 配 MCP server 是同一套主流做法。

## 写一个 MCP server

1. 在 `mcp/` 下建子目录，如 `mcp/MyServer/`，加 `MyServer.csproj` + `Program.cs`。
2. 引用 `ModelContextProtocol`（server SDK）+ `Microsoft.Extensions.Hosting`。
3. `AddMcpServer().WithStdioServerTransport().WithToolsFromAssembly()` 经 stdio 暴露工具。
4. 用 `[McpServerToolType]` 标记工具类、`[McpServerTool]` 标记方法（见 `SampleServer/Program.cs`）。

## 启用一个 server

在租户数据目录的 `mcp-servers/` 下放一个 JSON 文件（每个 server 一个，或一个数组文件多个）：

**源码 server（stdio，本仓库内）：**
```json
{
  "name": "sample",
  "command": "dotnet",
  "arguments": ["run", "--project", "/绝对路径/Aether/mcp/SampleServer"]
}
```
> 注意：从 `.app` 启动时 CWD 是 `/`，`--project` 用**绝对路径**最稳。

**远程 HTTP/SSE server：**
```json
{ "name": "remote", "url": "https://host/sse" }
```

启动时 Aether 扫描 `mcp-servers/*.json` 自动连接（单个 server 失败跳过不阻断）。专家编辑页勾「MCP工具」即纳入该 server 的所有工具（默认按 Dangerous 审批）。

## 运行时目录

`mcp-servers/` 是**运行时配置目录**（在租户数据目录下，如 `~/Library/Application Support/Aether/Desktop-Stockapp/mcp-servers/`），不在仓库里。`mcp/` 是**源码目录**（在仓库里）。两者分离：源码进版本管理，配置按机器/租户落地。
