using Acme.Mobile.Shared.Hosting.Tools;
using Aether.AI.Core.Agents;
using Aether.AI.Hosting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.Mobile.Shared.Hosting;

/// <summary>
/// 手机端宿主层 AI 工具注册下沉：把桌面端已有的「创作类」工具（创建/管理 skill、知识对象、MCP 配置、
/// agent 自举）移植到手机端。每个工具类自持「AddXxx(this <see cref="AetherAIBuilder"/>)」——
/// 镜像桌面 <c>Acme.Desktop.Shared.Hosting.DesktopToolRegistrations</c> 的同名工具，但**复用 Notes.Domain
/// 契约 + labs 根 <c>Func&lt;string?&gt;</c>（已在 <c>AddMobileCommon</c> 注册）而非桌面 <c>IKnowledgeDirectoryProvider</c>/
/// <c>OkTargetResolver</c>（两者在 Acme.Desktop.Mvvm，手机不可引）。
/// </summary>
/// <remarks>
/// 手机端与桌面的关键差异：
/// <list type="bullet">
/// <item><b>skill/knowledge/MCP 都是 labs 文件 CRUD</b>——labs 是权威存储（云同步后桌面端可见），手机端只写盘、
/// 不重连运行时（与 <c>SkillCreateViewModel</c>/<c>McpCreateViewModel</c>/<c>KnowledgeCreateViewModel</c> 同构：
/// 写盘即「创建」，连接在桌面端做）。</item>
/// <item><b>MCP 工具 = 配置 CRUD（无实连）</b>：iOS 沙盒禁止 spawn 子进程，手机端无 <c>IMcpManagementService</c>
/// （impl 在 <c>Aether.AI.Mcp</c>，拉 alpha ModelContextProtocol）。故 mcp_list/add/remove 只操作
/// <c>labs/mcp-servers/*.json</c> 配置文件（权威存储），报「配置已写盘，连接在桌面端生效」。</item>
/// <item><b>agent_configure/reload 无 MCP 就绪检查</b>：手机端无 <c>IMcpManagementService</c>，MissingTools 诊断恒为空
/// （配置存在的 MCP 工具在桌面端才会注册进 ToolRegistry）。</item>
/// </list>
/// </remarks>
public static class MobileToolRegistrations
{
    // =====================================================================
    //  技能（skill_list / skill_add / skill_remove）
    // =====================================================================
    public static AetherAIBuilder AddSkillManagementTool(this AetherAIBuilder aether)
    {
        aether.Services.AddTransient<MobileSkillManagementTool>();
        aether.ConfigureToolRegistry(reg =>
        {
            reg.Register("skill_list", "列出当前已加载的 skill（名称/描述/分类）。只读，无副作用。添加/删除 skill 前先调此确认现有 skill，避免重名。", "技能",
                sp => Create<MobileSkillManagementTool>(sp, nameof(MobileSkillManagementTool.ListSkillsAsync), "skill_list"),
                ToolApprovalLevel.Safe);
            reg.Register("skill_add", "新增或更新一个 skill（写到 labs/skills/<name>/SKILL.md，YAML front-matter name/description/category + markdown 正文）并立即重载索引，令其下轮起对 agent 生效。装一个 skill 等于给 agent 注入新流程知识，需要用户审批确认。", "技能",
                sp => Create<MobileSkillManagementTool>(sp, nameof(MobileSkillManagementTool.AddSkillAsync), "skill_add"),
                ToolApprovalLevel.Dangerous);
            reg.Register("skill_remove", "删除一个 skill（按名删 labs/skills/<name> 目录）并立即重载索引。需要用户审批确认。", "技能",
                sp => Create<MobileSkillManagementTool>(sp, nameof(MobileSkillManagementTool.RemoveSkillAsync), "skill_remove"),
                ToolApprovalLevel.Dangerous);
        });
        return aether;
    }

    // =====================================================================
    //  知识对象（List/Read/Create/UpdateDocumentKnowledgeObject）
    // =====================================================================
    public static AetherAIBuilder AddDocumentKnowledgeTool(this AetherAIBuilder aether)
    {
        aether.Services.AddTransient<MobileDocumentKnowledgeTool>();
        aether.ConfigureToolRegistry(reg =>
        {
            reg.Register("ListDocumentKnowledgeObjects", "列出知识对象（labs/knowledge/*.md 文件）。返回相对路径/名称/kind/正文预览。可选 folder 只列指定子目录。总上限 200。", "文档知识",
                sp => Create<MobileDocumentKnowledgeTool>(sp, nameof(MobileDocumentKnowledgeTool.ListDocumentKnowledgeObjects), "ListDocumentKnowledgeObjects"),
                ToolApprovalLevel.Safe);
            reg.Register("ReadDocumentKnowledgeObject", "按相对路径读取一个知识对象（labs/knowledge/*.md）的完整定义：kind + 正文 + links 指向的子 .md 文件内容（只展开一层）。name=相对路径不含 .md（如 业务分析/退款）。", "文档知识",
                sp => Create<MobileDocumentKnowledgeTool>(sp, nameof(MobileDocumentKnowledgeTool.ReadDocumentKnowledgeObject), "ReadDocumentKnowledgeObject"),
                ToolApprovalLevel.Safe);
            reg.Register("CreateDocumentKnowledgeObject", "创建一个知识对象（labs/knowledge/<name>.md 文件）。kind 分类自由字符串（流程/数据源/规则 等）；links 可选 JSON 数组（targetId=相对路径 或 target=名称，指向 knowledge/ 下另一 .md）。已存在则失败。", "文档知识",
                sp => Create<MobileDocumentKnowledgeTool>(sp, nameof(MobileDocumentKnowledgeTool.CreateDocumentKnowledgeObject), "CreateDocumentKnowledgeObject"),
                ToolApprovalLevel.Confirm);
            reg.Register("UpdateDocumentKnowledgeObject", "更新一个知识对象（labs/knowledge/*.md）的 kind/body/links。传哪个参数更新哪个，不传保留原值。links 全量替换。", "文档知识",
                sp => Create<MobileDocumentKnowledgeTool>(sp, nameof(MobileDocumentKnowledgeTool.UpdateDocumentKnowledgeObject), "UpdateDocumentKnowledgeObject"),
                ToolApprovalLevel.Confirm);
        });
        return aether;
    }

    // =====================================================================
    //  MCP 配置（mcp_list_servers / mcp_add_server / mcp_remove_server）
    //  文件 CRUD on labs/mcp-servers/*.json。手机端无 MCP 运行时 → 不实连。
    // =====================================================================
    public static AetherAIBuilder AddMcpConfigTool(this AetherAIBuilder aether)
    {
        aether.Services.AddTransient<MobileMcpConfigTool>();
        aether.ConfigureToolRegistry(reg =>
        {
            reg.Register("mcp_list_servers", "列出当前所有 MCP server 配置文件（labs/mcp-servers/*.json）：名称、传输方式（stdio/http）。只读。注意：手机端只管理配置（权威存储），连接在桌面端生效。", "MCP工具",
                sp => Create<MobileMcpConfigTool>(sp, nameof(MobileMcpConfigTool.ListServersAsync), "mcp_list_servers"),
                ToolApprovalLevel.Safe);
            reg.Register("mcp_add_server", "新增或更新一个 MCP server 配置（写到 labs/mcp-servers/<name>.json，与 Claude Code .mcp.json 格式兼容）。stdio 用 command+args，http 用 url。写盘即创建（权威存储，云同步后桌面端可见并连接）。需要用户审批确认。", "MCP工具",
                sp => Create<MobileMcpConfigTool>(sp, nameof(MobileMcpConfigTool.AddServerAsync), "mcp_add_server"),
                ToolApprovalLevel.Dangerous);
            reg.Register("mcp_remove_server", "删除一个 MCP server 配置（按名删 labs/mcp-servers/<name>.json）。需要用户审批确认。", "MCP工具",
                sp => Create<MobileMcpConfigTool>(sp, nameof(MobileMcpConfigTool.RemoveServerAsync), "mcp_remove_server"),
                ToolApprovalLevel.Dangerous);
        });
        return aether;
    }

    // =====================================================================
    //  agent 自举（agent_configure / agent_reload / get_current_agent）
    // =====================================================================
    public static AetherAIBuilder AddAgentManagementTool(this AetherAIBuilder aether)
    {
        aether.Services.AddTransient<MobileAgentManagementTool>();
        aether.ConfigureToolRegistry(reg =>
        {
            reg.Register("agent_configure", "配置一个自定义 agent（按名字定位；可配置自己或任意 agent）。只改传入字段，未传保留原值。改完自动重新召唤使其生效。需要用户审批确认。", "Agent自举",
                sp => Create<MobileAgentManagementTool>(sp, nameof(MobileAgentManagementTool.ConfigureAsync), "agent_configure"),
                ToolApprovalLevel.Dangerous);
            reg.Register("agent_reload", "重新召唤一个自定义 agent（按名字定位），使其缓存失效、下一轮 run 重建（带上最新配置）。注意会丢弃该 agent 会话的运行时记忆。需要用户审批确认。", "Agent自举",
                sp => Create<MobileAgentManagementTool>(sp, nameof(MobileAgentManagementTool.ReloadAsync), "agent_reload"),
                ToolApprovalLevel.Confirm);
            reg.Register("get_current_agent", "查询一个自定义 agent 的当前定义（缺省定位「正在调用本工具的当前 agent」，留空名字即可；也可显式按名字查）。返回 id/名字/描述/分类/模型/已勾选工具/是否开启工具审批。只读。", "Agent自举",
                sp => Create<MobileAgentManagementTool>(sp, nameof(MobileAgentManagementTool.GetCurrentAgentAsync), "get_current_agent"),
                ToolApprovalLevel.Safe);
        });
        return aether;
    }

    private static AIFunction Create<T>(IServiceProvider sp, string method, string name) where T : class
    {
        var tool = sp.GetRequiredService<T>();
        var m = typeof(T).GetMethod(method)
            ?? throw new InvalidOperationException($"Tool method '{method}' not found on {typeof(T).Name}.");
        return AIFunctionFactory.Create(m, tool, name);
    }
}
