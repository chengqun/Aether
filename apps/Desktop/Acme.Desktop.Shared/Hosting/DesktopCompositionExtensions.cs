using System.IO;
using Acme.ApkParser.AI;
using Acme.Agents.AI;
using Acme.Desktop.Mvvm;
using Aether.AI.Harness.Agents;
using Aether.AI.Harness.Scripting;
using Aether.AI.Harness.Tools;
using Aether.AI.Hosting;
using Aether.AI.Hosting.Agents;
using Aether.AI.Mcp;
using Aether.AI.Memory;
using Aether.AI.Memory.Stores;
using Acme.AppAuth.Application.DependencyInjection;
using Acme.AppAuth.Domain;
using Acme.AppAuth.Infrastructure.DependencyInjection;
using Acme.AppAuth.Infrastructure.Http;
using Acme.Automation.Application.DependencyInjection;
using Acme.Automation.Infrastructure.DependencyInjection;
using Acme.ChatSocial.Application.DependencyInjection;
using Acme.ChatSocial.Infrastructure.DependencyInjection;
using Acme.Desktop.Shared.Shell;
using Acme.DeviceConnect.Application.DependencyInjection;
using Acme.DeviceConnect.Infrastructure.DependencyInjection;
using Acme.DeviceManagement.Application.DependencyInjection;
using Acme.DeviceManagement.Infrastructure.DependencyInjection;
using Acme.Llm.Application.DependencyInjection;
using Acme.Llm.Infrastructure.DependencyInjection;
using Acme.Notes.AI;
using Acme.Notes.Application.DependencyInjection;
using Acme.Notes.Infrastructure.DependencyInjection;
using Acme.Shared.HostGlue;
using Acme.SystemTools.AI;
using Aether.AI.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.AI;

namespace Acme.Desktop.Shared.Hosting;

/// <summary>
/// 桌面端公共 DI 组合：两租户（qifu / stockapp）共用的上下文 + AI 工具 + LLM 配置引导。
/// 租户专属上下文（qifu: Qifu；stockapp: Ctp/Quotes/Securities/Trading）由各租户入口项目在调用本方法后追加。
/// </summary>
public static class DesktopCompositionExtensions
{
    /// <param name="tenant">租户代码（"qifu" / "stockapp"），写入 AppServerOptions.Tenant。</param>
    /// <param name="tenantDir">租户数据目录（绝对路径），各上下文 SQLite 落在此处，两租户数据隔离。</param>
    /// <returns>返回 <see cref="AetherAIBuilder"/>，供租户链式追加租户专属 AI 工具（AddQifuTools / AddQuotesTools 等）。</returns>
    public static AetherAIBuilder AddDesktopCommon(this IServiceCollection services, string tenant, string tenantDir)
    {
        Directory.CreateDirectory(tenantDir);

        // 初始化自定义证书处理器：加载内嵌 rootCA.crt，解决 Windows 下自签名 SSL 连接问题
        CustomCertificateHandler.Initialize();

        // LLM 配置：读 SQLite 活跃配置；无则从 env 种子（无 key 用 "dummy"，shell 仍能启动，agent 待配置）。
        var llm = LlmBootstrap.GetOrCreateActive(Path.Combine(tenantDir, "llm.db"), "desktop-default");
        var aether = services.AddAetherAI();
        aether.Configure(o =>
        {
            o.OpenAI.ApiKey = llm.ApiKey;
            o.OpenAI.ModelId = llm.ModelName;
            o.OpenAI.Endpoint = llm.Endpoint;
            // HarnessAgent 文件记忆 provider 的可写根目录（.app 启动 CWD=/ 只读，必须显式指定）。
            o.FileMemoryRoot = Path.Combine(tenantDir, "agent-file-memory");
            // skills 目录：MAF AgentFileSkillsSource 从此加载 SKILL.md（+ 脚本/资源）。
            o.SkillsDirectory = Path.Combine(tenantDir, "skills");
        });

        // OTel：控制台导出（dev 调试；专家需开 EnableOpenTelemetry 才发 Activity）。OTLP 端点按需配。
        services.AddAetherTelemetry(o => o.ConsoleEnabled = true);

        // MCP server 配置目录：扫描 <tenantDir>/mcp-servers/*.json 自动注册 MCP server（stdio/SSE）。
        // 放配置文件即可启用，无需改代码。空目录 = 无 MCP。连接在 InitializeAsync 启动。
        aether.AddMcpServersFromDirectory(Path.Combine(tenantDir, "mcp-servers"));

        // Notes 编辑器：粘贴图片保存根目录（markdown 引用相对文件名，预览通过 AssetPathRoot 解析）
        services.Configure<NotesEditorOptions>(o => o.ImageRoot = Path.Combine(tenantDir, "notes-media"));

        // 主题：用户主题 JSON 目录（<tenantDir>/themes/*.json）。内置 light/dark 不依赖此目录。
        services.Configure<DesktopOptions>(o => o.ThemesDirectory = Path.Combine(tenantDir, "themes"));

        // —— 公共业务上下文（两租户共用）——
        services.AddAppAuthInfrastructure(o =>
        {
            // 优先级：env APP_BASEURL → preferences.json app.serverUrl → 空（登录窗口让用户填）
            o.BaseUrl = Environment.GetEnvironmentVariable("APP_BASEURL")
                ?? PreferencesFileReader.ReadServerUrl(tenantDir);
            o.ClientId = Environment.GetEnvironmentVariable("APP_CLIENT_ID") ?? "alphaagent_chat";
            o.ClientSecret = Environment.GetEnvironmentVariable("APP_CLIENT_SECRET") ?? "chat_secret";
            o.Scope = "Abp alphaagent_chat";   // 必须带 Abp 受众，否则签发的 token 无 API 受众 → invalid_token
            o.Tenant = tenant;
            o.DbPath = Path.Combine(tenantDir, "platform.db");
        }, CustomCertificateHandler.CreateHandler);
        services.AddAppAuthApplication(CustomCertificateHandler.CreateHandler);

        services.AddLlmInfrastructure(Path.Combine(tenantDir, "llm.db"));
        services.AddLlmApplication();

        services.AddNotesInfrastructure(Path.Combine(tenantDir, "notes.db"));
        services.AddNotesApplication();

        // 代码规则脚本加载（CodeRule 笔记 → Roslyn 编译 → ICodeBrainRegistry）。两租户共用。
        // ICodeBrainRegistry 单例同时注入 AgentFactory（按笔记 Id 取代码大脑）与 IRuleScriptingService（编译注册）。
        // 启动 fire 由各租户 App 调 CodeRuleHost.Start。
        services.AddRuleScripting();
        services.AddSingleton<ICodeNoteSource, CodeNoteSource>();

        services.AddAutomationInfrastructure(Path.Combine(tenantDir, "automation.db"));
        services.AddAutomationApplication();
        // IAgentExecutor 实现：复用聊天会话流程（ICustomAgentRunner + IConversationStore），
        // 定时触发落库进「最近会话」并保留跨次记忆。实现住宿主 VM 库（Acme.Desktop.Mvvm）。
        services.AddSingleton<Acme.Automation.Domain.Interfaces.IAgentExecutor, Acme.Desktop.Mvvm.ConversationBackedAgentExecutor>();

        services.AddChatSocialInfrastructure();
        services.AddChatSocialApplication();

        services.AddDeviceConnectInfrastructure(Path.Combine(tenantDir, "device.db"));
        services.AddDeviceConnectApplication(o =>
        {
            o.Tenant = tenant;
        });

        services.AddDeviceManagementInfrastructure(Path.Combine(tenantDir, "device.db"));
        services.AddDeviceManagementApplication();

        // —— 公共 AI 工具（两租户共用）——
        aether.AddSystemTools().AddApkParserTools().AddNotesTools();
        // 长期记忆工具（recall_memory/save_memory）：把 IMemoryStore 暴露为显式工具。代码规则大脑经此获取
        // 跨会话记忆（其不接 MemoryContextProvider 注入式 NL）；LLM agent 亦可显式存/查，与自动注入互补。
        aether.AddMemoryTools();

        // ask_user 交互对话框工具：agent 需用户拍板/澄清时调用（替代纯文本提问）。AgentFactory 自动纳入各 agent。
        services.AddSingleton<IAskUserDialogService, AskUserDialogService>();
        services.AddTransient<AskUserTool>();
        aether.ConfigureToolRegistry(reg => reg.Register(
            "ask_user", "向用户提问/确认并阻塞等待回答（返回用户选择或自由文本）。需要用户拍板/澄清时用此工具而非纯文本提问。", "交互",
            sp => AIFunctionFactory.Create(
                typeof(AskUserTool).GetMethod(nameof(AskUserTool.AskAsync))!, sp.GetRequiredService<AskUserTool>(),
                "ask_user")));

        // 规则程序注册表 + run_rule 工具（共享宿主胶水）：规则 agent（Engine=Rule）的程序同时可被 LLM agent
        // 经 run_rule(name,input) 调用（省 token：executor 内部跑工具，原始结果对 LLM 不可见）。标 Safe。
        aether.AddRunRuleTool();
        // —— 公共 AI skill（提示词级能力，从租户数据目录的 skills/ 加载 SKILL.md）——
        var skillsDir = Path.Combine(tenantDir, "skills");
        aether.ConfigureSkillRegistry(reg => reg.LoadFromDirectory(skillsDir));

        // 自定义 Agent 目录 + 运行器（依赖上面的 INoteService 与 ToolRegistry）
        aether.AddCustomAgents();
        aether.AddCustomWorkflows();
        // 笔记表单编辑 VM 工厂（按 NoteNodeType 派发，替代 NotesView 的 if 链）。每个 form 类型一个。
        services.AddSingleton<INoteNodeEditVmFactory, AgentEditVmFactory>();
        services.AddSingleton<INoteNodeEditVmFactory, RuleEditVmFactory>();
        services.AddSingleton<INoteNodeEditVmFactory, CodeRuleEditVmFactory>();
        services.AddSingleton<INoteNodeEditVmFactory, WorkflowEditVmFactory>();
        services.AddSingleton<INoteNodeEditVmFactory, KnowledgeEditVmFactory>();
        // 策略表单工厂：NodeType=Strategy 派发。codec 由 Stockapp 注册（Qifu 不注册→工厂解析 null→无表单）。
        services.AddSingleton<INoteNodeEditVmFactory, StrategyEditVmFactory>();
        // 桌面端模型源/客户端工厂（共享宿主胶水）：把专家的 ModelId(=LLM 配置 Id) 解析为对应 provider 客户端。
        // 桌面不包 ToolHistorySanitizingChatClient（clientWrapper=null）。
        services.AddSingleton<IModelSource, LlmConfigModelSource>();
        services.AddSingleton<IChatClientFactory, LlmConfigChatClientFactory>();
        // 会话持久化（覆盖平台默认的 InMemoryConversationStore）+ 偏好持久化（跨重启保留召唤/选中）
        services.AddSingleton<IConversationStore>(sp => SqliteConversationStore.ForFile(Path.Combine(tenantDir, "conversations.db")));
        // 长期记忆持久化（覆盖平台默认的 InMemoryMemoryStore）。桌面端单用户 → userId=null 即全局记忆。
        services.AddSingleton<IMemoryStore>(sp => SqliteMemoryStore.ForFile(Path.Combine(tenantDir, "memory.db")));
        services.AddSingleton<IPreferencesStore>(sp => new JsonPreferencesStore(tenantDir));
        // UI 线程调度抽象（供 UI 无关的 ExpertsViewModel 使用）
        services.AddSingleton<IUiDispatcher, AvaloniaDispatcher>();
        // MCP 工具服务器管理 VM（Experts 页 MCP tab；IMcpManagementService 由 AddMcpServersFromDirectory 注册）
        services.AddSingleton<McpViewModel>();
        // 运行 Trace 查看 VM（桌面端无控制台时看 OTel agent.run span）
        services.AddSingleton<TraceViewModel>();
        // 启动期设备自动连接（委托给 SettingsViewModel）
        services.AddSingleton<IDeviceAutoConnectService, DeviceAutoConnectService>();

        // 登录对话框服务（从 Application.Current 取主窗口弹模态登录窗）
        services.AddSingleton<ILoginDialogService, LoginDialogService>();

        // 设置对话框服务
        services.AddSingleton<ISettingsDialogService, SettingsDialogService>();
        services.AddTransient<SettingsViewModel>();

        // 工具审批对话框服务（HarnessAgent 协作式审批，按专家 RequireToolApproval 开启）
        services.AddSingleton<IApprovalDialogService, ApprovalDialogService>();

        // shell 主 ViewModel（租户子类）由各租户入口项目注册，此处不注册。
        return aether;
    }
}
