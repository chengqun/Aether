using System.IO;
using Acme.Agents.AI;
using Acme.AppAuth.Application.DependencyInjection;
using Acme.AppAuth.Domain;
using Acme.AppAuth.Infrastructure.DependencyInjection;
using Acme.ChatSocial.Application.DependencyInjection;
using Acme.ChatSocial.Domain;
using Acme.ChatSocial.Infrastructure.DependencyInjection;
using Acme.Llm.Application.DependencyInjection;
using Acme.Llm.Domain;
using Acme.Llm.Infrastructure;
using Acme.Llm.Infrastructure.DependencyInjection;
using Acme.Mobile.Mvvm.Abstractions;
using Acme.Mobile.Mvvm.Services;
using Acme.Mobile.Shared.Services;
using Acme.Mobile.Shared.Services.Caching;
using Acme.Moments.Application.DependencyInjection;
using Acme.Moments.Infrastructure.DependencyInjection;
using Acme.Notes.AI;
using Acme.Notes.Application.DependencyInjection;
using Acme.Notes.Infrastructure.DependencyInjection;
using Acme.Persistence;
using Acme.Securities.Application.DependencyInjection;
using Acme.Securities.Infrastructure.DependencyInjection;
using Acme.DeviceManagement.Application.DependencyInjection;
using Acme.DeviceManagement.Infrastructure.DependencyInjection;
using Acme.Video.Application.DependencyInjection;
using Acme.ServiceAccount.Application.DependencyInjection;
using Acme.ServiceAccount.Infrastructure.DependencyInjection;
using Aether.AI.Harness;
using Aether.AI.Harness.Agents;
using Aether.AI.Harness.Tools;
using Aether.AI.Hosting;
using Aether.AI.Hosting.Agents;
using Aether.AI.Memory;
using Aether.AI.Memory.Stores;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.Mobile.Shared.Hosting;

/// <summary>
/// 手机端公共 DI 组合：两租户（qifu / stockapp）共用的上下文 + AI 工具 + LLM 配置引导。
/// 镜像桌面 <c>AddDesktopCommon</c>，但替换桌面 UI 端口为 MAUI 实现，并跳过手机不可用项
/// （MCP / Roslyn 代码规则 / SystemTools shell / Lean·CTP·FlaUI）。
/// 租户专属上下文（qifu: Yushu/CloudDocs；stockapp: Quotes/股票模式）由各租户入口项目追加。
/// </summary>
public static class MobileCompositionExtensions
{
    /// <param name="services">DI 容器。</param>
    /// <param name="tenant">租户代码（"qifu" / "stockapp"），写入 AppServerOptions.Tenant。</param>
    /// <param name="tenantDir">租户数据目录（绝对路径，沙盒 <c>FileSystem.AppDataDirectory</c> 下），各上下文 SQLite 落此。</param>
    /// <returns>返回 <see cref="AetherAIBuilder"/>，供租户链式追加专属 AI 工具。</returns>
    public static AetherAIBuilder AddMobileCommon(this IServiceCollection services, string tenant, string tenantDir)
    {
        Directory.CreateDirectory(tenantDir);

        // 自签名证书：从 app 包加载 rootCA.crt（MauiAsset），建信任 handler。
        MobileCertificateHandler.Initialize();
        var aether = services.AddAetherAI();

        // LLM 配置引导：读 SQLite 活跃配置；无则从 env 种子（无 key 用 "dummy"，app 仍能启动，agent 待配置）。
        var llm = MobileLlmBootstrap.GetOrCreateActive(Path.Combine(tenantDir, "llm.db"));
        aether.Configure(o =>
        {
            o.OpenAI.ApiKey = llm.ApiKey;
            o.OpenAI.ModelId = llm.ModelName;
            o.OpenAI.Endpoint = llm.Endpoint;
            o.FileMemoryRoot = Path.Combine(tenantDir, "agent-file-memory");
            o.SkillsDirectory = Path.Combine(tenantDir, "skills");
        });

        services.AddAetherTelemetry(o => o.ConsoleEnabled = true);

        // —— 公共业务上下文（Stage 1 仅注册登录+agent聊天所需；社交上下文 Stage 2 追加）——
        services.AddAppAuthInfrastructure(o =>
        {
            o.BaseUrl = Environment.GetEnvironmentVariable("APP_BASEURL")
                ?? ReadSavedServerUrl(tenantDir)
                ?? string.Empty;
            o.ClientId = Environment.GetEnvironmentVariable("APP_CLIENT_ID") ?? "alphaagent_chat";
            o.ClientSecret = Environment.GetEnvironmentVariable("APP_CLIENT_SECRET") ?? "chat_secret";
            o.Scope = "Abp alphaagent_chat";   // 必须带 Abp 受众，否则 token 无 API 受众 → invalid_token
            o.Tenant = tenant;
            o.DbPath = Path.Combine(tenantDir, "platform.db");
        }, MobileCertificateHandler.CreateHandler);
        services.AddAppAuthApplication(MobileCertificateHandler.CreateHandler);

        services.AddLlmInfrastructure(Path.Combine(tenantDir, "llm.db"));
        services.AddLlmApplication();

        services.AddNotesInfrastructure(Path.Combine(tenantDir, "notes.db"));
        services.AddNotesApplication();

        services.AddChatSocialInfrastructure();
        services.AddChatSocialApplication();

        // —— 社交上下文（Stage 2：朋友圈 / 服务号 / 证券搜索）——
        services.AddMomentsApplication();
        services.AddMomentsInfrastructure();
        services.AddServiceAccountApplication();
        services.AddServiceAccountInfrastructure(Path.Combine(tenantDir, "serviceaccount.db"));
        services.AddSecurityApplication();
        services.AddSecurityInfrastructure(Path.Combine(tenantDir, "securities.db"));

        // —— 次要上下文（Stage 2b：视频号 / 设备管理）——
        services.AddVideoApplication();
        services.AddDeviceManagementApplication();
        services.AddDeviceManagementInfrastructure(Path.Combine(tenantDir, "devicemgmt.db"));

        // —— 公共 AI 工具 ——
        aether.AddNotesTools();
        aether.AddMemoryTools();

        // ask_user 交互工具：手机端用简化实现（Stage 1 stub：返回用户取消 []; Stage 2 接 DisplayActionSheet）。
        // AgentFactory 自动把 ask_user 纳入各 agent（免逐 agent 配置）。
        services.AddSingleton<MobileAskUserTool>();
        aether.ConfigureToolRegistry(reg => reg.Register(
            "ask_user", "向用户提问/确认并阻塞等待回答（返回用户选择或自由文本）。需要用户拍板/澄清时用此工具而非纯文本提问。", "交互",
            sp => AIFunctionFactory.Create(
                typeof(MobileAskUserTool).GetMethod(nameof(MobileAskUserTool.AskAsync))!,
                sp.GetRequiredService<MobileAskUserTool>(),
                "ask_user")));

        // 规则程序注册表 + run_rule 工具（YAML 规则；CodeRule 需 Roslyn，手机跳过）。
        services.AddSingleton<RuleRegistry>();
        aether.ConfigureToolRegistry(reg => reg.Register(
            RuleTool.ToolName, "运行一个已注册的规则程序（确定性，无 LLM）。参数 name=规则名,input=查询。", "规则",
            sp => RuleTool.Create(
                sp.GetRequiredService<RuleRegistry>(),
                name => sp.GetRequiredService<ToolRegistry>().ResolveAll(new[] { name }).FirstOrDefault()),
            ToolApprovalLevel.Safe));

        // skills（提示词级能力，从租户数据目录的 skills/ 加载 SKILL.md）
        aether.ConfigureSkillRegistry(reg => reg.LoadFromDirectory(Path.Combine(tenantDir, "skills")));

        // 自定义 Agent 目录 + 运行器（依赖 INoteService 与 ToolRegistry）
        aether.AddCustomAgents();
        aether.AddCustomWorkflows();

        // 模型源/客户端工厂：把专家的 ModelId(=LLM 配置 Id) 解析为对应 provider 客户端（UI 无关，与桌面同逻辑）。
        // 先注册具体类型再别名接口——保证 MobileAgentConfigInitializer（依赖具体类型调 ClearCache/Invalidate）
        // 与 IChatClientFactory/IModelSource 消费方拿到同一个单例实例。
        services.AddSingleton<MobileChatClientFactory>();
        services.AddSingleton<IChatClientFactory>(sp => sp.GetRequiredService<MobileChatClientFactory>());
        services.AddSingleton<MobileLlmConfigModelSource>();
        services.AddSingleton<IModelSource>(sp => sp.GetRequiredService<MobileLlmConfigModelSource>());
        // 会话 + 长期记忆持久化（SQLite，覆盖平台默认 InMemory）
        services.AddSingleton<IConversationStore>(sp => SqliteConversationStore.ForFile(Path.Combine(tenantDir, "conversations.db")));
        services.AddSingleton<IMemoryStore>(sp => SqliteMemoryStore.ForFile(Path.Combine(tenantDir, "memory.db")));

        // —— 手机平台抽象（MAUI 实现）——
        services.AddSingleton<IUIThreadDispatcher, MauiUIThreadDispatcher>();
        // MauiAppSettings ctor 取 tenantDir（string），DI 无法自动解析 → 用工厂注入实例
        services.AddSingleton<IAppSettings>(new MauiAppSettings(tenantDir));
        services.AddSingleton<IPlatformInfo, MauiPlatformInfo>();
        services.AddSingleton<IToastService, MauiToastService>();
        services.AddSingleton<IClipboardService, MauiClipboardService>();
        services.AddSingleton<IThemeManager, MauiThemeManager>();
        services.AddSingleton<IStreamingBackgroundService, MauiStreamingBackgroundService>();   // iOS: BeginBackgroundTask
        services.AddSingleton<ISpeechRecognitionService, StubSpeechRecognitionService>();        // Stage 2 接平台语音
        services.AddSingleton<IAgentConfigInitializer, MobileAgentConfigInitializer>();

        // —— 手机端社交基础设施（Stage 2：实时推送 + 离线缓存 + sync 编排 + mobile-local 服务）——
        var socialCacheDb = SqliteStoreBase.BuildConnectionString(Path.Combine(tenantDir, "social-cache.db"));

        // SignalR 实时推送（JWT 经 IAppTokenStore，自签名 CA 经 MobileCertificateHandler）
        services.AddSingleton<ISignalRChatService>(sp =>
            new SignalRChatService(sp.GetRequiredService<IAppTokenStore>(), MobileCertificateHandler.CreateHandler));

        // 4 个 SQLite 离线缓存 store（共享 social-cache.db；会话/通讯录缓存注入服务端 sync 源）
        services.AddSingleton<IMessageCacheService>(_ => SqliteMessageCacheStore.ForFile(Path.Combine(tenantDir, "social-cache.db")));
        services.AddSingleton<IConversationSyncService>(sp =>
            new SqliteConversationCacheStore(socialCacheDb, sp.GetService<IChatService>()));
        services.AddSingleton<IContactSyncService>(sp =>
            new SqliteContactCacheStore(socialCacheDb, sp.GetService<IRelationshipService>()));
        services.AddSingleton<IMomentCacheService>(_ => SqliteMomentCacheStore.ForFile(Path.Combine(tenantDir, "social-cache.db")));

        // mobile-local 事件总线 + 未读消息内存缓存 + 全局消息处理器（订阅 SignalR 事件 → 发 EventBus）
        services.AddSingleton<IEventBusService, EventBusService>();
        services.AddSingleton<IUnreadMessageCacheService, UnreadMessageCacheService>();
        services.AddSingleton<IGlobalMessageHandler, GlobalMessageHandler>();

        // 启动期核心初始化（强制建 social-cache.db schema）+ 登录后初始化（应用缓存 LLM 配置）
        services.AddSingleton<ICoreInitializer, MobileCoreInitializer>();
        services.AddSingleton<IPostLoginInitializer, MobilePostLoginInitializer>();

        return aether;
    }

    /// <summary>从 preferences.json 读 app.serverUrl（DI 未构建，不能注 IAppSettings）。</summary>
    internal static string? ReadSavedServerUrl(string tenantDir)
    {
        try
        {
            var prefsFile = Path.Combine(tenantDir, "preferences.json");
            if (!File.Exists(prefsFile)) return null;
            var json = File.ReadAllText(prefsFile);
            var match = System.Text.RegularExpressions.Regex.Match(json, @"""app\.serverUrl""\s*:\s*""([^""]+)""");
            return match.Success ? match.Groups[1].Value : null;
        }
        catch { return null; }
    }
}

/// <summary>
/// 启动期 LLM 配置引导：读 SQLite 活跃配置；无则从 env 种子一条默认配置。
/// 镜像桌面 <c>DesktopLlmBootstrap</c>（UI 无关，逻辑一致）。
/// </summary>
public static class MobileLlmBootstrap
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
            Name = "mobile-default",
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
