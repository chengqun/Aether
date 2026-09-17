using System.IO;
using Acme.ApkParser.AI;
using Acme.Agents.AI;
using Acme.Agents.AI.Evals;
using Acme.Desktop.Mvvm;
using Aether.AI.Harness.Agents;
using Aether.AI.Harness.Tools;
using Aether.AI.Harness.Scripting;
using Aether.AI.Core.Agents;
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
using Acme.DeviceConnect.Infrastructure;
using Acme.DeviceManagement.Application.DependencyInjection;
using Acme.DeviceManagement.Infrastructure.DependencyInjection;
using Acme.Llm.Application.DependencyInjection;
using Acme.Llm.Infrastructure.DependencyInjection;
using Acme.Notes.Domain.Agents;
using Acme.Notes.Infrastructure.DependencyInjection;
using Acme.Shared.HostGlue;
using Acme.SystemTools.AI;
using Acme.AppAuth.Application.Auth;
using Acme.Desktop.Shared.Telemetry;
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
            o.Host = HostCapability.DesktopOnly;
            o.OpenAI.ApiKey = llm.ApiKey;
            o.OpenAI.ModelId = llm.ModelName;
            o.OpenAI.Endpoint = llm.Endpoint;
            // HarnessAgent 文件记忆 provider 的可写根目录（.app 启动 CWD=/ 只读，必须显式指定）。
            o.FileMemoryRoot = Path.Combine(tenantDir, "agent-file-memory");
            // 迁移：skills/ → labs/skills/（技能迁入 labs 统一创作面）。幂等——labs/skills 已在则跳。
            var oldSkillsDir = Path.Combine(tenantDir, "skills");
            var newSkillsDir = Path.Combine(tenantDir, "labs", "skills");
            if (Directory.Exists(oldSkillsDir) && !Directory.Exists(newSkillsDir))
            {
                Directory.CreateDirectory(Path.Combine(tenantDir, "labs"));
                try { Directory.Move(oldSkillsDir, newSkillsDir); }
                catch (IOException) { /* 并发/权限——下次启动重试，不阻断启动 */ }
            }
            // skills 目录：labs 统一创作面（labs/skills/<name>/SKILL.md）；MAF AgentFileSkillsSource 从此加载 SKILL.md（+ 脚本/资源）。
            o.SkillsDirectory = newSkillsDir;
            // Playwright 浏览器持久 profile：browser_open 工具 + 网络面板共用，登录一次后续复用（不碰日常浏览器）。
            o.BrowserProfileDir = Path.Combine(tenantDir, "browser-profile");
        });

        // OTel：控制台导出（dev 调试；专家需开 EnableOpenTelemetry 才发 Activity）。OTLP 端点按需配。
        services.AddAetherTelemetry(o => o.ConsoleEnabled = true);

        // 迁移：mcp-servers/ → labs/mcp-servers/（MCP 迁入 labs 统一创作面）。幂等——labs/mcp-servers 已在则跳。
        var oldMcpDir = Path.Combine(tenantDir, "mcp-servers");
        var newMcpDir = Path.Combine(tenantDir, "labs", "mcp-servers");
        if (Directory.Exists(oldMcpDir) && !Directory.Exists(newMcpDir))
        {
            Directory.CreateDirectory(Path.Combine(tenantDir, "labs"));
            try { Directory.Move(oldMcpDir, newMcpDir); }
            catch (IOException) { /* 并发/权限——下次启动重试，不阻断启动 */ }
        }
        // MCP server 配置目录：扫描 <tenantDir>/labs/mcp-servers/*.json 自动注册 MCP server（stdio/SSE）。
        // 放配置文件即可启用，无需改代码。空目录 = 无 MCP。连接在 InitializeAsync 启动。
        aether.AddMcpServersFromDirectory(newMcpDir);

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

        services.AddNotesInfrastructure();

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
        // 注：不再 AddNotesTools()——note 节点降级为其侧边栏纯文本功能，AI 不直接读写 note；
        // 知识对象 AI 读写在 labs 文件版（AddDocumentKnowledgeTool，见下）。
        aether.AddSystemTools().AddApkParserTools();
        // 长期记忆工具（recall_memory/save_memory）：把 IMemoryStore 暴露为显式工具。代码规则大脑经此获取
        // 跨会话记忆（其不接 MemoryContextProvider 注入式 NL）；LLM agent 亦可显式存/查，与自动注入互补。
        aether.AddMemoryTools();

        // ask_user 交互对话框工具：agent 需用户拍板/澄清时调用（替代纯文本提问）。AgentFactory 自动纳入各 agent。
        services.AddSingleton<IAskUserDialogService, AskUserDialogService>();
        // 集成终端服务（单例）：TerminalView 经 AttachView 持控件；run_in_terminal 工具 + TerminalViewModel 共用。
        services.AddSingleton<ITerminalService, TerminalService>();
        // AI 聊天面板显隐服务（单例，镜像终端）：侧栏「AI 聊天」toggle + 聊天面板内 ✕ 共用。仅持显隐态 + 持久化（聊天视图常驻、IsVisible 切换不销毁）。
        services.AddSingleton<IChatPanelService, ChatPanelService>();
        // 主侧栏（左栏：文件树/SCM/知识树）显隐服务（单例，镜像聊天面板）：标题栏「主侧栏」toggle 共用。仅持手动 toggle 态 + 持久化（树视图常驻、IsVisible 切换不销毁）。
        services.AddSingleton<ILeftTreePanelService, LeftTreePanelService>();
        // 轻量右下角 toast 提示服务（单例）：替代常驻状态条的错误/保存反馈。宿主 overlay 由 WorkbenchWindow 注册。
        services.AddSingleton<IToastService, ToastService>();
        aether.AddAskUserTool();

        // GetCurrentProjectDirectory：让 agent 查文档页当前打开的项目目录路径与顶层结构。
        // .app 启动 CWD=/，SystemTools 的 Glob/Grep/Shell 默认 path 打不到用户打开的项目；此工具经
        // ICurrentProjectDirectoryProvider（DocumentsViewModel.RootPath 变化时回写）取真实目录。标 Safe（只读）。
        services.AddSingleton<ICurrentProjectDirectoryProvider, CurrentProjectDirectoryProvider>();
        // 知识库目录持有者（独立单例，与项目目录 provider 解耦）：知识对象页打开知识库目录时写入，
        // DocumentKnowledgeTool 读取找 .md。独立单例避免知识库目录覆盖项目目录（SCM/终端/活动页订阅）。
        // 知识库目录 = labs 根（默认 <tenantDir>/labs）。DefaultRoot 供页面/目录源在「无已持久化目录」时回退打开。
        services.AddSingleton<IKnowledgeDirectoryProvider>(sp => new KnowledgeDirectoryProvider
        {
            DefaultRoot = Path.Combine(tenantDir, "labs"),
        });
        services.AddSingleton<Acme.Desktop.Mvvm.Git.IGitService, Acme.Desktop.Mvvm.Git.GitService>();
        aether.AddCurrentDirectoryTool();
        // GetCurrentKnowledgeDirectory：让 agent 查资料库（labs/知识对象）目录的绝对路径与顶层结构。
        // 与项目目录 provider 解耦的独立单例；未打开知识对象页时回退 DefaultRoot（<tenantDir>/labs）。标 Safe。
        aether.AddCurrentKnowledgeDirectoryTool();

        // 文档版知识对象工具（.md 文件 CRUD）：镜像 note KnowledgeTool 的 4 工具，操作磁盘 .md 文件
        // （note 版操作 note 节点）。需知识库目录（独立于项目目录）→ 宿主层注册（ai/ 不可引 apps 层）。
        aether.AddDocumentKnowledgeTool();

        // git 工具（git_status 只读 / git_commit 暂存全部并提交 / git_push 推送 / git_pull 拉取合并）：
        // 让 agent 查看与提交、同步文档页当前项目的 git 变更。git 无业务上下文 → 宿主层注册（非 ai/ 适配器）。
        // git_commit/git_push/git_pull 不可逆 → 标 Dangerous；git_status 只读标 Safe。
        // git 仓库变更通知（AI 工具后台提交/同步后 → SCM 页刷新）。
        services.AddSingleton<Acme.Desktop.Mvvm.Git.IGitRepositoryNotifier, Acme.Desktop.Mvvm.Git.GitRepositoryNotifier>();
        // 远程设置对话框（GitHub 用户名 + PAT）端口实现：SCM 页「远程设置」按钮经此弹窗。
        services.AddSingleton<Acme.Desktop.Mvvm.Git.IRemoteSettingsDialogService, RemoteSettingsDialogService>();
        aether.AddGitTool();

        // MCP server 管理工具（mcp_list_servers / mcp_add_server / mcp_remove_server / mcp_refresh）：
        // 让 agent 复用专家页「MCP工具」tab 同一套 IMcpManagementService 动态增删 MCP server。
        // 不自动勾选进 agent：装好 server 后工具仅进 ToolRegistry 池，勾选由用户在专家编辑器完成。
        aether.AddMcpManagementTool();

        // agent 配置/召唤工具（agent_configure / agent_reload / get_current_agent）：让 agent 像页面一样「先按名字选一个
        // agent → 改字段 → 保存 → 重新召唤」。与页面 ExpertsViewModel.SaveAsync 同构。
        aether.AddAgentManagementTool();

        // skill 管理工具（skill_list / skill_add / skill_remove）：让 agent 管理文件式 skill（目录扫描式 SKILL.md）。
        aether.AddSkillManagementTool();

        // build/test 结构化反馈工具（get_build_errors / run_tests）：结构化编译错误/测试失败，而非裸 dotnet 输出。
        aether.AddBuildTool();

        // 语义感知符号编辑工具（edit_symbol）：基于 Roslyn 定位 C# 符号并替换源码文本。仅 C#。
        aether.AddEditSymbolTool();

        // 集成终端工具（run_in_terminal）：在可见终端面板执行命令（真实 PTY / 完整 env / 可观察可中断）。
        aether.AddRunInTerminalTool();

        // 浏览器网络面板服务（单例）：BrowserView 经事件投影会话+请求列表；browser_open/get_cookies 工具共用。
        // Playwright headed 独立窗口——用户手动登录，面板记录 HTTP 请求流；agent 拿 cookie 后 curl 带访问需登录接口。
        services.AddSingleton<IBrowserService, BrowserService>();
        aether.AddBrowserTool();

        // 规则程序注册表 + run_rule 工具（共享宿主胶水）：规则 agent（Engine=Rule）的程序同时可被 LLM agent
        // 经 run_rule(name,input) 调用（省 token：executor 内部跑工具，原始结果对 LLM 不可见）。标 Safe。
        aether.AddRunRuleTool();
        // —— 公共 AI skill（提示词级能力，从 labs/skills/ 加载 SKILL.md）——
        var skillsDir = Path.Combine(tenantDir, "labs", "skills");
        aether.ConfigureSkillRegistry(reg => reg.LoadFromDirectory(skillsDir));

        // 自定义 Agent 目录 + 运行器（依赖 ToolRegistry + 各 parser）
        aether.AddCustomAgents();
        aether.AddCustomWorkflows();
        // —— labs 目录版 catalog（权威）：Agent/Workflow 读写 <tenantDir>/labs/* 文件，而非 note 节点。
        // AddCustomAgents/AddCustomWorkflows 仅注册运行器，catalog 由宿主在此显式绑定为唯一实现。
        // 运行器/页面经接口解析无感知。labs 根经 Func<string?> 注入（DirectoryAgentCatalog 在 ai/Agents，不能引 apps 层）。
        services.AddSingleton<ICustomAgentCatalog>(sp =>
            new DirectoryAgentCatalog(
                () => sp.GetRequiredService<IKnowledgeDirectoryProvider>().CurrentDirectory
                      ?? sp.GetRequiredService<IKnowledgeDirectoryProvider>().DefaultRoot,
                sp.GetRequiredService<ToolRegistry>(),
                sp.GetRequiredService<IModelSource>(),
                sp.GetRequiredService<IAgentSourceParser>(),
                sp.GetRequiredService<IRuleSourceParser>(),
                sp.GetRequiredService<ICodeSourceParser>(),
                sp.GetRequiredService<RuleRegistry>()));
        services.AddSingleton<ICustomWorkflowCatalog>(sp =>
            new DirectoryWorkflowCatalog(
                () => sp.GetRequiredService<IKnowledgeDirectoryProvider>().CurrentDirectory
                      ?? sp.GetRequiredService<IKnowledgeDirectoryProvider>().DefaultRoot,
                sp.GetRequiredService<IWorkflowSourceParser>()));
        // —— Agent 评测运行器（labs evals/*.eval.yaml，目录消费）：契约 ICustomAgentEvalRunner 在 Core.Agents
        // （镜像 ICustomAgentRunner），impl 在 ai/Agents 适配器（需 ICustomAgentCatalog + AgentFactory 双视野），
        // 不进 AddCustomAgents——适配器不知道 labs 根，根经委托由宿主注入（同上两个 catalog）。
        services.AddSingleton<ICustomAgentEvalRunner>(sp =>
            new AgentEvalRunner(
                () => sp.GetRequiredService<IKnowledgeDirectoryProvider>().CurrentDirectory
                      ?? sp.GetRequiredService<IKnowledgeDirectoryProvider>().DefaultRoot,
                sp.GetRequiredService<ICustomAgentCatalog>(),
                sp.GetRequiredService<AgentFactory>()));
        // —— 策略/代码规则/Lean 算法读源也切 labs 目录（INoteCatalogSource 换注册为目录版，绑 labs 根）。
        // StrategyNoteSource/CodeNoteSource/LeanAlgorithmNoteSource 依赖 INoteCatalogSource，换注册即自动从 labs 文件读。
        services.AddSingleton<Acme.Notes.Domain.Catalog.INoteCatalogSource>(sp =>
        {
            var labs = sp.GetRequiredService<IKnowledgeDirectoryProvider>();
            return new Acme.Notes.Infrastructure.Catalog.DirectoryNoteCatalogSource(
                () => labs.CurrentDirectory ?? labs.DefaultRoot);
        });
        // labs 目录 watcher：监听 labs 根目录文件变更，驱动上面的 DirectoryNoteCatalogSource.NotifyChanged，
        // 让代码规则/策略/Lean 算法的重编译脱离原 note 版 SQLite 变更通知。启动 fire 由各租户 App 调 Start。
        services.AddSingleton<LabsDirectoryWatcher>();
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
        // 技能管理 VM（Experts 页技能 tab，只读展示 SkillRegistry 已加载 skill；SkillRegistry 由 AddAetherAI 注册）
        services.AddSingleton<SkillViewModel>();
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

        // 活动报告生成 + 报告展示弹窗（活动页「生成日报/周报」按钮 → LLM 汇总当天/当周 runs+commits → markdown 弹窗）
        services.AddSingleton<IActivityReportService, ActivityReportService>();
        services.AddSingleton<IReportDialogService, ReportDialogService>();

        // 提交消息生成（SCM 页 ✨ 按钮 → 取当前 git 变更 diff 摘要 → LLM 生成提交消息填入输入框）
        services.AddSingleton<ICommitMessageService, CommitMessageService>();

        // 运行关键节点信号 + 桌宠逐字流：信号源已下沉 Harness（RunProgressFanout 由 AddAetherAI 注册、
        // 注入 HarnessRuntime），此处把桌宠路由器挂进该 fanout 作消费者。桌宠懒建（首次最小化才实例化），
        // 故路由器常驻、由 WorkbenchWindow 经 SetTarget 指向活的 PetWindow；未建宠物时 Post 静默丢弃。
        services.AddSingleton<PetStatusFeedRouter>();
        services.AddSingleton<IPetTokenStream>(sp => sp.GetRequiredService<PetStatusFeedRouter>());
        // 覆盖 AddAetherAI 的默认 IRunProgressSink 注册（同为 fanout 单例，额外挂上桌宠路由器）——
        // 最后注册者胜，故 HarnessRuntime 构造时解析到的即此已挂载的 fanout。
        services.AddSingleton<IRunProgressSink>(sp =>
        {
            var fanout = sp.GetRequiredService<RunProgressFanout>();
            fanout.Add(sp.GetRequiredService<PetStatusFeedRouter>());
            return fanout;
        });

        // 崩溃遥测上报器：本地 crash.db 队列 → 登录后批量上报服务端（api/app/crash/record-crashes）。
        // 未登录本地排队；失败静默重试（10 次后 Prune 清理）。各租户 App 调 CrashTelemetryHost.Start 启动
        // （DesktopApp.Services = Services 之后）。
        services.AddSingleton<CrashReportUploader>(sp => new CrashReportUploader(
            sp.GetRequiredService<IAppServerHttpClient>(),
            sp.GetRequiredService<IAppAuthService>(),
            sp.GetService<SqliteDeviceConfigRepository>(),
            tenant,
            Path.Combine(tenantDir, "crash.db")));

        // shell 主 ViewModel（租户子类）由各租户入口项目注册，此处不注册。
        return aether;
    }
}
