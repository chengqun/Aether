using System.IO;
using Acme.ApkParser.AI;
using Acme.Agents.AI;
using Acme.Desktop.Mvvm;
using Aether.AI.Harness.Agents;
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
using Acme.DeviceManagement.Application.DependencyInjection;
using Acme.DeviceManagement.Infrastructure.DependencyInjection;
using Acme.Llm.Application.DependencyInjection;
using Acme.Llm.Infrastructure.DependencyInjection;
using Acme.Notes.AI;
using Acme.Notes.Domain.Agents;
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
            // Playwright 浏览器持久 profile：browser_open 工具 + 网络面板共用，登录一次后续复用（不碰日常浏览器）。
            o.BrowserProfileDir = Path.Combine(tenantDir, "browser-profile");
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
        // 集成终端服务（单例）：TerminalView 经 AttachView 持控件；run_in_terminal 工具 + TerminalViewModel 共用。
        services.AddSingleton<ITerminalService, TerminalService>();
        // AI 聊天面板显隐服务（单例，镜像终端）：侧栏「AI 聊天」toggle + 聊天面板内 ✕ 共用。仅持显隐态 + 持久化（聊天视图常驻、IsVisible 切换不销毁）。
        services.AddSingleton<IChatPanelService, ChatPanelService>();
        services.AddTransient<AskUserTool>();
        aether.ConfigureToolRegistry(reg => reg.Register(
            "ask_user", "向用户提问/确认并阻塞等待回答（返回用户选择或自由文本）。需要用户拍板/澄清时用此工具而非纯文本提问。", "交互",
            sp => AIFunctionFactory.Create(
                typeof(AskUserTool).GetMethod(nameof(AskUserTool.AskAsync))!, sp.GetRequiredService<AskUserTool>(),
                "ask_user")));

        // GetCurrentProjectDirectory：让 agent 查文档页当前打开的项目目录路径与顶层结构。
        // .app 启动 CWD=/，SystemTools 的 Glob/Grep/Shell 默认 path 打不到用户打开的项目；此工具经
        // ICurrentProjectDirectoryProvider（DocumentsViewModel.RootPath 变化时回写）取真实目录。标 Safe（只读）。
        services.AddSingleton<ICurrentProjectDirectoryProvider, CurrentProjectDirectoryProvider>();
        // 知识库目录持有者（独立单例，与项目目录 provider 解耦）：知识对象页打开知识库目录时写入，
        // DocumentKnowledgeTool 读取找 .ok。独立单例避免知识库目录覆盖项目目录（SCM/终端/活动页订阅）。
        services.AddSingleton<IKnowledgeDirectoryProvider, KnowledgeDirectoryProvider>();
        services.AddSingleton<Acme.Desktop.Mvvm.Git.IGitService, Acme.Desktop.Mvvm.Git.GitService>();
        services.AddTransient<GetCurrentProjectDirectoryTool>();
        aether.ConfigureToolRegistry(reg => reg.Register(
            "GetCurrentProjectDirectory", "获取文档页当前打开的项目目录路径与顶层文件/目录结构", "系统工具",
            sp => AIFunctionFactory.Create(
                typeof(GetCurrentProjectDirectoryTool).GetMethod(nameof(GetCurrentProjectDirectoryTool.GetCurrentProjectDirectory))!,
                sp.GetRequiredService<GetCurrentProjectDirectoryTool>(),
                "GetCurrentProjectDirectory")));

        // 文档版知识对象工具（.ok 文件 CRUD）：镜像 note KnowledgeTool 的 4 工具，操作磁盘 .ok 文件
        // （note 版操作 note 节点）。需知识库目录（独立于项目目录）→ 宿主层注册，非 ai/ 适配器
        // （ai/ 不可引 apps 层 IKnowledgeDirectoryProvider）。List/Read 只读标 Safe；Create/Update 写盘标 Confirm
        // （对齐 note 版 Create/Update Confirm 级；写盘可逆性低但非不可逆）。解析/序列化复用 IDocumentKnowledgeSourceParser
        // （AddNotesInfrastructure 已注册）。.ok 路径解析经 OkTargetResolver（与文档编辑器图解析同源）。
        services.AddTransient<DocumentKnowledgeTool>();
        aether.ConfigureToolRegistry(reg =>
        {
            reg.Register("ListDocumentKnowledgeObjects", "列出文档版知识对象（.ok 文件，磁盘上的知识资产）。返回路径/名称/kind/正文预览。可选 folder 只列指定子目录。需先在知识对象页打开知识库目录。", "文档知识",
                sp => AIFunctionFactory.Create(
                    typeof(DocumentKnowledgeTool).GetMethod(nameof(DocumentKnowledgeTool.ListDocumentKnowledgeObjects))!,
                    sp.GetRequiredService<DocumentKnowledgeTool>(),
                    "ListDocumentKnowledgeObjects"),
                ToolApprovalLevel.Safe);
            reg.Register("ReadDocumentKnowledgeObject", "按相对路径读取一个文档知识对象（.ok）的完整定义：kind+正文+links 指向的子 .ok 文件内容（只展开一层）。name=相对路径不含 .ok（如 业务分析/退款会员日志分析）。先调此工具获取知识再执行。", "文档知识",
                sp => AIFunctionFactory.Create(
                    typeof(DocumentKnowledgeTool).GetMethod(nameof(DocumentKnowledgeTool.ReadDocumentKnowledgeObject))!,
                    sp.GetRequiredService<DocumentKnowledgeTool>(),
                    "ReadDocumentKnowledgeObject"),
                ToolApprovalLevel.Safe);
            reg.Register("CreateDocumentKnowledgeObject", "创建一个文档知识对象（.ok 文件）。name=相对路径不含扩展名，将创建 <知识库目录>/<name>.ok。kind 分类自由字符串。links 可选 JSON 数组（targetId=相对路径权威 或 target=名称）。", "文档知识",
                sp => AIFunctionFactory.Create(
                    typeof(DocumentKnowledgeTool).GetMethod(nameof(DocumentKnowledgeTool.CreateDocumentKnowledgeObject))!,
                    sp.GetRequiredService<DocumentKnowledgeTool>(),
                    "CreateDocumentKnowledgeObject"),
                ToolApprovalLevel.Confirm);
            reg.Register("UpdateDocumentKnowledgeObject", "更新一个文档知识对象（.ok 文件）的 kind/body/links。按相对路径定位；传哪个参数更新哪个，不传保留原值。links 全量替换。", "文档知识",
                sp => AIFunctionFactory.Create(
                    typeof(DocumentKnowledgeTool).GetMethod(nameof(DocumentKnowledgeTool.UpdateDocumentKnowledgeObject))!,
                    sp.GetRequiredService<DocumentKnowledgeTool>(),
                    "UpdateDocumentKnowledgeObject"),
                ToolApprovalLevel.Confirm);
        });

        // git 工具（git_status 只读 / git_commit 暂存全部并提交 / git_push 推送 / git_pull 拉取合并）：
        // 让 agent 查看与提交、同步文档页当前项目的 git 变更。git 无业务上下文、GitService 在宿主 VM 库 →
        // 走宿主层注册（同 ask_user/GetCurrentProjectDirectory），非 ai/ 适配器。
        // git_commit/git_push/git_pull 不可逆（创建提交 / 公开到远程 / 改本地工作区）→ 标 Dangerous，
        // 专家开启 RequireToolApproval 时弹审批红徽。git_status 只读标 Safe。
        services.AddTransient<GitTool>();
        // git 仓库变更通知（AI 工具后台提交/同步后 → SCM 页刷新）。
        services.AddSingleton<Acme.Desktop.Mvvm.Git.IGitRepositoryNotifier, Acme.Desktop.Mvvm.Git.GitRepositoryNotifier>();
        // 远程设置对话框（GitHub 用户名 + PAT）端口实现：SCM 页「远程设置」按钮经此弹窗。
        services.AddSingleton<Acme.Desktop.Mvvm.Git.IRemoteSettingsDialogService, RemoteSettingsDialogService>();
        aether.ConfigureToolRegistry(reg =>
        {
            reg.Register("git_status", "查看当前项目（文档页打开的目录）的 git 工作区状态：已暂存/未暂存的变更文件列表。只读。", "Git",
                sp => AIFunctionFactory.Create(
                    typeof(GitTool).GetMethod(nameof(GitTool.GitStatus))!, sp.GetRequiredService<GitTool>(),
                    "git_status"),
                ToolApprovalLevel.Safe);
            reg.Register("git_commit", "将当前项目所有变更（含未跟踪文件）暂存并提交。需要用户审批确认。提交前建议先调用 git_status 查看变更。", "Git",
                sp => AIFunctionFactory.Create(
                    typeof(GitTool).GetMethod(nameof(GitTool.GitCommitAsync))!, sp.GetRequiredService<GitTool>(),
                    "git_commit"),
                ToolApprovalLevel.Dangerous);
            reg.Register("git_push", "推送当前分支提交到远程仓库（如 GitHub）。不可逆——公开本地提交到远程。需用户审批确认。", "Git",
                sp => AIFunctionFactory.Create(
                    typeof(GitTool).GetMethod(nameof(GitTool.GitPushAsync))!, sp.GetRequiredService<GitTool>(),
                    "git_push"),
                ToolApprovalLevel.Dangerous);
            reg.Register("git_pull", "从远程仓库拉取并合并更新到当前分支。会修改本地工作区，可能产生冲突。需用户审批确认。", "Git",
                sp => AIFunctionFactory.Create(
                    typeof(GitTool).GetMethod(nameof(GitTool.GitPullAsync))!, sp.GetRequiredService<GitTool>(),
                    "git_pull"),
                ToolApprovalLevel.Dangerous);
        });

        // build/test 结构化反馈工具：让 agent 拿到结构化编译错误/测试失败，而非裸 dotnet 输出（弱模型从
        // 上千行 MSBuild 输出找错易漏）。需项目目录（同 git_*）→ 宿主层注册。标 Safe：agentic 编程闭环核心
        // 高频动作，低摩擦。命令执行复用 ShellCommandTool（登录 shell 解决 .app 受限 PATH）。
        services.AddTransient<BuildTool>();
        aether.ConfigureToolRegistry(reg =>
        {
            reg.Register("get_build_errors", "编译当前项目并返回结构化编译错误/警告列表（file/line/错误码/消息）。改完代码用此工具确认能否编译。", "编程",
                sp => AIFunctionFactory.Create(
                    typeof(BuildTool).GetMethod(nameof(BuildTool.GetBuildErrors))!, sp.GetRequiredService<BuildTool>(),
                    "get_build_errors"),
                ToolApprovalLevel.Safe);
            reg.Register("run_tests", "运行当前项目单元测试并返回结构化结果（通过/失败数 + 失败用例错误消息与堆栈）。", "编程",
                sp => AIFunctionFactory.Create(
                    typeof(BuildTool).GetMethod(nameof(BuildTool.RunTests))!, sp.GetRequiredService<BuildTool>(),
                    "run_tests"),
                ToolApprovalLevel.Safe);
        });

        // 语义感知符号编辑工具：基于 Roslyn 定位 C# 符号（类型/方法/属性/字段）并替换其源码文本，
        // 不受注释/字符串/同名局部变量干扰。宿主层注册（需项目目录 + Roslyn Compilation）。仅 C#。标 Safe。
        // diff/撤销：路径由 Roslyn 在执行时定位（call 阶段未知）→ 不走单文件事后 diff 闭环，用户可去 SCM 页看 diff + git 撤销。
        services.AddTransient<EditSymbolTool>();
        aether.ConfigureToolRegistry(reg =>
        {
            reg.Register("edit_symbol", "基于 Roslyn 语义分析定位并替换当前项目中的一个 C# 符号（类型/方法/属性/字段）的源码文本。比 EditFile 字符串匹配更精确。仅支持 C#。", "编程",
                sp => AIFunctionFactory.Create(
                    typeof(EditSymbolTool).GetMethod(nameof(EditSymbolTool.EditSymbol))!, sp.GetRequiredService<EditSymbolTool>(),
                    "edit_symbol"),
                ToolApprovalLevel.Safe);
        });

        // 集成终端工具：在可见终端面板执行命令（真实 PTY / 完整 env 含 .zshrc / 可观察可中断）。
        // 与 ExecuteShellCommand 分工：后者静默管道拿干净输出做判断；本工具走可见终端，适合需 TTY/长任务观察/介入。
        // 标 Dangerous（执行任意命令），对齐 ExecuteShellCommand。宿主层注册（需 ITerminalService + 项目目录）。
        services.AddTransient<RunInTerminalTool>();
        aether.ConfigureToolRegistry(reg =>
        {
            reg.Register("run_in_terminal", "在集成终端（可见面板）执行命令——真实 PTY / 完整登录 env（含 .zshrc）/ 可观察可中断。" +
                "需要 TTY、长任务想观察/介入、或需完整环境时用此工具；拿干净输出做判断仍用 ExecuteShellCommand。" +
                "命令在终端可见，超时/取消后进程继续在终端跑（可手动停止）。", "编程",
                sp => AIFunctionFactory.Create(
                    typeof(RunInTerminalTool).GetMethod(nameof(RunInTerminalTool.RunInTerminal))!, sp.GetRequiredService<RunInTerminalTool>(),
                    "run_in_terminal"),
                ToolApprovalLevel.Dangerous);
        });

        // 浏览器网络面板服务（单例）：BrowserView 经事件投影会话+请求列表；browser_open/get_cookies 工具共用。
        // Playwright headed 独立窗口——用户手动登录，面板记录 HTTP 请求流；agent 拿 cookie 后 curl 带访问需登录接口。
        services.AddSingleton<IBrowserService, BrowserService>();
        services.AddTransient<BrowserTool>();
        aether.ConfigureToolRegistry(reg =>
        {
            reg.Register("browser_open", "打开浏览器到指定 URL（headed 独立 OS 窗口，用户手动登录）。有活跃会话则导航它，否则建新。" +
                "channel=chrome/msedge 复用本机已装浏览器（免 playwright install chromium）。返回会话 id 与当前 URL。" +
                "用法：browser_open(url='https://xxx/login') → 让用户登录 → browser_get_cookies() 拿 CookieHeader → " +
                "ExecuteShellCommand/run_in_terminal 跑 curl -s -H \"Cookie: <CookieHeader>\" https://同域/任意路径。", "浏览器",
                sp => AIFunctionFactory.Create(
                    typeof(BrowserTool).GetMethod(nameof(BrowserTool.BrowserOpenAsync))!, sp.GetRequiredService<BrowserTool>(),
                    "browser_open"),
                ToolApprovalLevel.Safe);
            reg.Register("browser_get_cookies", "提取当前活跃浏览器会话的 cookie（Playwright 按 host 过滤，含父子域 + HttpOnly）。" +
                "返回 curl 直接用的 CookieHeader 串（name=value; name=value; ...）+ 明细。⚠ cookie 是登录凭证，会进 agent 上下文供后续 curl 用。" +
                "cookieNames 非空时只取指定 cookie。建议先用 browser_open + 让用户登录，再调此工具。", "浏览器",
                sp => AIFunctionFactory.Create(
                    typeof(BrowserTool).GetMethod(nameof(BrowserTool.GetCookiesAsync))!, sp.GetRequiredService<BrowserTool>(),
                    "browser_get_cookies"),
                ToolApprovalLevel.Confirm);
            reg.Register("browser_get_requests", "取当前活跃浏览器会话已记录的 HTTP 请求列表（DevTools Network 风格）。" +
                "urlContains 过滤 URL；errorsOnly 只看失败/4xx/5xx；limit 限制条数（默认 50）。用于发现登录后调用的 API 端点与请求状态。", "浏览器",
                sp => AIFunctionFactory.Create(
                    typeof(BrowserTool).GetMethod(nameof(BrowserTool.GetRequestsAsync))!, sp.GetRequiredService<BrowserTool>(),
                    "browser_get_requests"),
                ToolApprovalLevel.Safe);
            reg.Register("browser_close", "关闭当前活跃浏览器会话（关浏览器 + 释放 Playwright 资源）。", "浏览器",
                sp => AIFunctionFactory.Create(
                    typeof(BrowserTool).GetMethod(nameof(BrowserTool.CloseAsync))!, sp.GetRequiredService<BrowserTool>(),
                    "browser_close"),
                ToolApprovalLevel.Safe);
        });

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

        // 活动报告生成 + 报告展示弹窗（活动页「生成日报/周报」按钮 → LLM 汇总当天/当周 runs+commits → markdown 弹窗）
        services.AddSingleton<IActivityReportService, ActivityReportService>();
        services.AddSingleton<IReportDialogService, ReportDialogService>();

        // 提交消息生成（SCM 页 ✨ 按钮 → 取当前 git 变更 diff 摘要 → LLM 生成提交消息填入输入框）
        services.AddSingleton<ICommitMessageService, CommitMessageService>();

        // shell 主 ViewModel（租户子类）由各租户入口项目注册，此处不注册。
        return aether;
    }
}
