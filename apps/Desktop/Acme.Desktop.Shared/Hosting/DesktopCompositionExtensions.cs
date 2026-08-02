using System.IO;
using Acme.ApkParser.AI;
using Acme.Agents.AI;
using Acme.Shell;
using Aether.AI.Harness.Agents;
using Aether.AI.Hosting;
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
using Acme.Llm.Domain;
using Acme.Llm.Infrastructure;
using Acme.Llm.Infrastructure.DependencyInjection;
using Acme.Notes.AI;
using Acme.Notes.Application.DependencyInjection;
using Acme.Notes.Infrastructure.DependencyInjection;
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
        var llm = DesktopLlmBootstrap.GetOrCreateActive(Path.Combine(tenantDir, "llm.db"));
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

        // —— 公共业务上下文（两租户共用）——
        services.AddAppAuthInfrastructure(o =>
        {
            o.BaseUrl = Environment.GetEnvironmentVariable("APP_BASEURL") ?? "https://117.72.186.128/";
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

        services.AddAutomationInfrastructure(Path.Combine(tenantDir, "automation.db"));
        services.AddAutomationApplication();
        // IAgentExecutor 实现：复用聊天会话流程（ICustomAgentRunner + IConversationStore），
        // 定时触发落库进「最近会话」并保留跨次记忆。实现住宿主 VM 库（Acme.Shell）。
        services.AddSingleton<Acme.Automation.Domain.Interfaces.IAgentExecutor, Acme.Shell.ConversationBackedAgentExecutor>();

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

        // ask_user 交互对话框工具：agent 需用户拍板/澄清时调用（替代纯文本提问）。AgentFactory 自动纳入各 agent。
        services.AddSingleton<IAskUserDialogService, AskUserDialogService>();
        services.AddTransient<AskUserTool>();
        aether.ConfigureToolRegistry(reg => reg.Register(
            "ask_user", "向用户提问/确认并阻塞等待回答（返回用户选择或自由文本）。需要用户拍板/澄清时用此工具而非纯文本提问。", "交互",
            sp => AIFunctionFactory.Create(
                typeof(AskUserTool).GetMethod(nameof(AskUserTool.AskAsync))!, sp.GetRequiredService<AskUserTool>())));
        // —— 公共 AI skill（提示词级能力，从租户数据目录的 skills/ 加载 SKILL.md）——
        var skillsDir = Path.Combine(tenantDir, "skills");
        aether.ConfigureSkillRegistry(reg => reg.LoadFromDirectory(skillsDir));

        // 自定义 Agent 目录 + 运行器（依赖上面的 INoteService 与 ToolRegistry）
        aether.AddCustomAgents();
        aether.AddCustomWorkflows();
        // 桌面端模型源/客户端工厂：把专家的 ModelId(=LLM 配置 Id) 解析为对应 provider 客户端
        services.AddSingleton<IModelSource, LlmConfigModelSource>();
        services.AddSingleton<IChatClientFactory, DesktopChatClientFactory>();
        // 会话持久化（覆盖平台默认的 InMemoryConversationStore）+ 偏好持久化（跨重启保留召唤/选中）
        services.AddSingleton<IConversationStore>(sp => SqliteConversationStore.ForFile(Path.Combine(tenantDir, "conversations.db")));
        // 长期记忆持久化（覆盖平台默认的 InMemoryMemoryStore）。桌面端单用户 → userId=null 即全局记忆。
        services.AddSingleton<IMemoryStore>(sp => SqliteMemoryStore.ForFile(Path.Combine(tenantDir, "memory.db")));
        services.AddSingleton<IPreferencesStore>(sp => new JsonPreferencesStore(tenantDir));
        // UI 线程调度抽象（供 UI 无关的 ExpertsViewModel 使用）
        services.AddSingleton<IUiDispatcher, AvaloniaDispatcher>();
        // MCP 工具服务器管理 VM（Experts 页 MCP tab；IMcpManagementService 由 AddMcpServersFromDirectory 注册）
        services.AddSingleton<McpViewModel>();
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

/// <summary>
/// 启动期 LLM 配置引导：读 SQLite 活跃配置；无则从 OPENAI_API_KEY/MODEL/ENDPOINT 种子一条默认配置（默认 DeepSeek）。
/// 镜像 Sample.Console 的 bootstrap，供桌面端 AddAetherAI 配置 IChatClient。
/// </summary>
public static class DesktopLlmBootstrap
{
    public static LlmConfig GetOrCreateActive(string dbPath)
    {
        var repo = SqliteLlmConfigRepository.ForFile(dbPath);
        var all = repo.GetAllAsync().GetAwaiter().GetResult();
        var active = all.FirstOrDefault(c => c.IsDefault) ?? all.FirstOrDefault();
        if (active != null) return active;

        var envKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        active = new LlmConfig
        {
            Id = Guid.NewGuid(),
            Name = "desktop-default",
            ModelName = Environment.GetEnvironmentVariable("OPENAI_MODEL") ?? "deepseek-chat",
            ApiKey = string.IsNullOrWhiteSpace(envKey) ? "dummy" : envKey,
            Endpoint = Environment.GetEnvironmentVariable("OPENAI_ENDPOINT") ?? "https://api.deepseek.com",
            Temperature = 0.5f,
            IsDefault = true,
        };
        repo.UpsertRangeAsync(new[] { active }).GetAwaiter().GetResult();
        return active;
    }
}
