using Aether.AI.Abstractions;
using Acme.Notes.Domain;
using Acme.Notes.Domain.Agents;
using Aether.AI.Core.Agents;

namespace Acme.Desktop.Shared.Shell;

/// <summary>
/// 添加 MCP server 后，把其工具**自动勾选进所有普通 LLM agent**（全局默认启用，不想用的 agent 再手动去掉）。
/// 订阅 <see cref="IMcpManagementService.Changed"/>（在 SaveServerAsync 末尾、RefreshAsync 重连后触发），
/// 手动添加（McpViewModel.CommitEditAsync）与 AI 添加（McpManagementTool）两路径共用此入口——故只需订阅一次。
/// </summary>
/// <remarks>
/// 只对 <see cref="NoteNodeType.Agent"/>（普通 LLM agent）勾选——规则 agent（Rule/CodeRule）工具由规则程序点名，
/// 不参与此自动勾选。幂等合并（HashSet），重复触发无害。删 server 不主动移除 agent 里残留的工具名——残留名在
/// <see cref="ToolRegistry.ResolveAll"/> 时因找不到而静默跳过，无害。
/// </remarks>
public sealed class McpAutoAssignToAllAgents
{
    private readonly IMcpManagementService _mgmt;
    private readonly ICustomAgentCatalog _catalog;
    private readonly ICustomAgentRunner _runner;
    private int _running; // 防重入（Changed 可能快速连发）

    public McpAutoAssignToAllAgents(IMcpManagementService mgmt, ICustomAgentCatalog catalog, ICustomAgentRunner runner)
    {
        _mgmt = mgmt;
        _catalog = catalog;
        _runner = runner;

        _mgmt.Changed += async (_, _) =>
        {
            try { _ = AutoAssignAsync(CancellationToken.None); }
            catch { /* 自动勾选失败不阻断 */ }
        };
    }

    /// <summary>把当前所有已连接 MCP server 的工具，合并勾选进所有普通 agent 的 Tools。</summary>
    private async Task AutoAssignAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _running, 1) == 1) return; // 防重入
        try
        {
            // 所有 MCP 工具名
            var servers = await _mgmt.GetServersAsync(cancellationToken).ConfigureAwait(false);
            var mcpTools = servers.Where(s => s.IsConnected)
                                  .SelectMany(s => s.Tools)
                                  .Select(t => t.Name)
                                  .Distinct()
                                  .ToList();
            if (mcpTools.Count == 0) return;

            // 所有普通 LLM agent
            var agents = await _catalog.ListAsync(cancellationToken).ConfigureAwait(false);
            foreach (var def in agents.Where(a => a.NodeType == NoteNodeType.Agent))
            {
                var merged = new HashSet<string>(def.Tools);
                bool changed = false;
                foreach (var t in mcpTools)
                    if (merged.Add(t)) changed = true;
                if (!changed) continue;

                def.Tools = merged.ToList();
                await _catalog.SaveAsync(def, cancellationToken).ConfigureAwait(false);
            }
        }
        catch { /* 自动勾选失败不阻断（页面/添加仍成功） */ }
        finally { Interlocked.Exchange(ref _running, 0); }
    }
}
