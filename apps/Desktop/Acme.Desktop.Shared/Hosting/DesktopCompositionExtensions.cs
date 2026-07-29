using System.IO;
using Acme.ApkParser.AI;
using Acme.Agents.AI;
using Acme.Shell;
using Aether.AI.Harness.Agents;
using Aether.AI.Memory;
using Aether.AI.Memory.Stores;
using Acme.AppAuth.Application.DependencyInjection;
using Acme.AppAuth.Domain;
using Acme.AppAuth.Infrastructure.DependencyInjection;
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
        });

        // —— 公共业务上下文（两租户共用）——
        services.AddAppAuthInfrastructure(o =>
        {
            o.BaseUrl = Environment.GetEnvironmentVariable("APP_BASEURL") ?? "https://117.72.186.128/";
            o.ClientId = Environment.GetEnvironmentVariable("APP_CLIENT_ID") ?? "alphaagent_chat";
            o.ClientSecret = Environment.GetEnvironmentVariable("APP_CLIENT_SECRET") ?? "chat_secret";
            o.Scope = "Abp alphaagent_chat";   // 必须带 Abp 受众，否则签发的 token 无 API 受众 → invalid_token
            o.Tenant = tenant;
            o.DbPath = Path.Combine(tenantDir, "platform.db");
        });
        services.AddAppAuthApplication();

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
        // —— 公共 AI skill（提示词级能力，从租户数据目录的 skills/ 加载 SKILL.md）——
        var skillsDir = Path.Combine(tenantDir, "skills");
        SeedDefaultSkill(skillsDir);
        aether.ConfigureSkillRegistry(reg => reg.LoadFromDirectory(skillsDir));

        // 自定义 Agent 目录 + 运行器（依赖上面的 INoteService 与 ToolRegistry）
        aether.AddCustomAgents();
        // 桌面端模型源/客户端工厂：把专家的 ModelId(=LLM 配置 Id) 解析为对应 provider 客户端
        services.AddSingleton<IModelSource, LlmConfigModelSource>();
        services.AddSingleton<IChatClientFactory, DesktopChatClientFactory>();
        // 会话持久化（覆盖平台默认的 InMemoryConversationStore）+ 偏好持久化（跨重启保留召唤/选中）
        services.AddSingleton<IConversationStore>(sp => SqliteConversationStore.ForFile(Path.Combine(tenantDir, "conversations.db")));
        services.AddSingleton<IPreferencesStore>(sp => new JsonPreferencesStore(tenantDir));
        // UI 线程调度抽象（供 UI 无关的 ExpertsViewModel 使用）
        services.AddSingleton<IUiDispatcher, AvaloniaDispatcher>();
        // 启动期设备自动连接（委托给 SettingsViewModel）
        services.AddSingleton<IDeviceAutoConnectService, DeviceAutoConnectService>();

        // 登录对话框服务（从 Application.Current 取主窗口弹模态登录窗）
        services.AddSingleton<ILoginDialogService, LoginDialogService>();

        // 设置对话框服务
        services.AddSingleton<ISettingsDialogService, SettingsDialogService>();
        services.AddTransient<SettingsViewModel>();

        // shell 主 ViewModel（租户子类）由各租户入口项目注册，此处不注册。
        return aether;
    }

    /// <summary>
    /// 首次运行时在 skills 目录种子一个示例 SKILL.md（organize-notes），
    /// 让用户开箱即用并作为 SKILL.md 格式范本。已存在则不覆盖。
    /// </summary>
    private static void SeedDefaultSkill(string skillsDir)
    {
        var exampleDir = Path.Combine(skillsDir, "organize-notes");
        var exampleFile = Path.Combine(exampleDir, "SKILL.md");
        if (File.Exists(exampleFile)) return;

        try
        {
            Directory.CreateDirectory(exampleDir);
            File.WriteAllText(exampleFile, """
                ---
                name: organize-notes
                description: 把零散笔记整理成主题分类的结构化笔记
                category: 笔记技能
                ---

                # 整理笔记

                当用户要求整理笔记时，按以下步骤进行：

                1. 用 ListNotes 列出当前所有笔记，了解全貌。
                2. 用 SearchNotes 按关键词聚类，识别共同主题。
                3. 为每个主题新建一篇结构化笔记（CreateNote），标题含主题名，
                   正文用二级标题分「要点」「待办」「相关笔记」三段。
                4. 把归入该主题的旧笔记关键内容追加到对应结构化笔记（AppendNote），
                   并在旧笔记里标注去向（UpdateNote）。
                5. 不要直接删除旧笔记；整理完成后用 ListNotes 复核并给用户一份
                   「主题 → 笔记」对照表。

                原则：保留原始信息、可追溯、不重复。
                """);
        }
        catch { /* 种子失败不阻塞启动 */ }
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
