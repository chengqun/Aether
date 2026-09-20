using Acme.Desktop.Mvvm;
using Acme.Desktop.Shared.Shell;
using Aether.AI.Core.Agents;
using Aether.AI.Hosting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.Desktop.Shared.Hosting;

/// <summary>
/// 桌面端宿主层 AI 工具的注册下沉：每个工具类自持「AddXxx(this <see cref="AetherAIBuilder"/>)」——
/// 镜像 ai/ 业务适配器的 <c>AddXxxTools</c> 惯例（注册工具类 Transient + 用 <c>ConfigureToolRegistry</c> 注册
/// 工具的元数据/工厂/审批等级）。原这些注册散落在 <c>DesktopCompositionExtensions.AddDesktopCommon</c> 单方法里
/// （一个 450 行文件硬编码 8 大工具类 + 描述文本 + 审批等级，职责崩塌）。下沉后 <c>AddDesktopCommon</c> 只留
/// 一串 <c>aether.AddXxx()</c> 编排调用，每个工具类的「自描述」与「注册」聚在一起。
/// </summary>
public static class DesktopToolRegistrations
{
    /// <summary>
    /// ask_user 交互对话框工具：agent 需用户拍板/澄清时调用（替代纯文本提问）。<c>AgentFactory</c> 自动纳入各 agent，
    /// 标 Safe（它本身就是交互 UI，永不被审批门控）。
    /// </summary>
    public static AetherAIBuilder AddAskUserTool(this AetherAIBuilder aether)
    {
        aether.Services.AddTransient<AskUserTool>();
        aether.ConfigureToolRegistry(reg => reg.Register(
            "ask_user", "向用户提问/确认并阻塞等待回答（返回用户选择或自由文本）。需要用户拍板/澄清时用此工具而非纯文本提问。", "交互",
            sp => AIFunctionFactory.Create(
                typeof(AskUserTool).GetMethod(nameof(AskUserTool.AskAsync))!, sp.GetRequiredService<AskUserTool>(),
                "ask_user")));
        return aether;
    }

    /// <summary>
    /// GetCurrentProjectDirectory：让 agent 查文档页当前打开的项目目录路径与顶层结构。
    /// .app 启动 CWD=/，SystemTools 的 Glob/Grep/Shell 默认 path 打不到用户打开的项目；此工具经
    /// ICurrentProjectDirectoryProvider 取真实目录。标 Safe（只读）。仅注册工具——provider/IGitService 等
    /// 依赖由 <c>AddDesktopCommon</c> 注册（它们在别处也被消费）。
    /// </summary>
    public static AetherAIBuilder AddCurrentDirectoryTool(this AetherAIBuilder aether)
    {
        aether.Services.AddTransient<GetCurrentProjectDirectoryTool>();
        aether.ConfigureToolRegistry(reg => reg.Register(
            "GetCurrentProjectDirectory", "获取文档页当前打开的项目目录路径与顶层文件/目录结构", "系统工具",
            sp => AIFunctionFactory.Create(
                typeof(GetCurrentProjectDirectoryTool).GetMethod(nameof(GetCurrentProjectDirectoryTool.GetCurrentProjectDirectory))!,
                sp.GetRequiredService<GetCurrentProjectDirectoryTool>(),
                "GetCurrentProjectDirectory")));
        return aether;
    }

    /// <summary>
    /// GetCurrentKnowledgeDirectory：让 agent 查资料库（labs/知识对象）目录的绝对路径与顶层结构。
    /// 与 GetCurrentProjectDirectory 平行——后者取文档页项目目录，此工具取资料库目录（IKnowledgeDirectoryProvider，
    /// 独立单例）。用户在知识对象页打开过目录则用之，否则回退 DefaultRoot（&lt;tenantDir&gt;/labs，始终存在）。
    /// agent 拿到路径后可配合 ReadFile/Grep/ExploreDirectory 读写资料库文件，或作为 git 工具的 workingDirectory 对资料库跑 git。
    /// 标 Safe（只读）。
    /// </summary>
    public static AetherAIBuilder AddCurrentKnowledgeDirectoryTool(this AetherAIBuilder aether)
    {
        aether.Services.AddTransient<GetCurrentKnowledgeDirectoryTool>();
        aether.ConfigureToolRegistry(reg => reg.Register(
            "GetCurrentKnowledgeDirectory", "获取资料库（labs/知识对象）目录的绝对路径与顶层文件/目录结构。" +
            "资料库存放 agent/rule/workflow/knowledge/skill/mcp-server 等创作资产。拿到路径后可用 ReadFile/Grep/ExploreDirectory 读写资料库文件，或作为 git 工具的 workingDirectory 参数对资料库跑 git。", "系统工具",
            sp => AIFunctionFactory.Create(
                typeof(GetCurrentKnowledgeDirectoryTool).GetMethod(nameof(GetCurrentKnowledgeDirectoryTool.GetCurrentKnowledgeDirectory))!,
                sp.GetRequiredService<GetCurrentKnowledgeDirectoryTool>(),
                "GetCurrentKnowledgeDirectory"),
            ToolApprovalLevel.Safe));
        return aether;
    }

    /// <summary>
    /// 文档版知识对象工具（.md 文件 CRUD）：镜像 note KnowledgeTool 的 4 工具，操作磁盘 .md 文件
    /// （note 版操作 note 节点）。宿主层注册（ai/ 不可引 apps 层 IKnowledgeDirectoryProvider）。
    /// List/Read 只读标 Safe；Create/Update 写盘标 Confirm（对齐 note 版）。解析/序列化复用 IDocumentKnowledgeSourceParser。
    /// </summary>
    public static AetherAIBuilder AddDocumentKnowledgeTool(this AetherAIBuilder aether)
    {
        aether.Services.AddTransient<DocumentKnowledgeTool>();
        aether.ConfigureToolRegistry(reg =>
        {
            reg.Register("ListDocumentKnowledgeObjects", "列出文档版知识对象（.md 文件，磁盘上的知识资产）。返回路径/名称/kind/正文预览。可选 folder 只列指定子目录。资料库目录取知识对象页打开的目录，未打开则回退默认 labs 根。", "文档知识",
                sp => AIFunctionFactory.Create(
                    typeof(DocumentKnowledgeTool).GetMethod(nameof(DocumentKnowledgeTool.ListDocumentKnowledgeObjects))!,
                    sp.GetRequiredService<DocumentKnowledgeTool>(),
                    "ListDocumentKnowledgeObjects"),
                ToolApprovalLevel.Safe);
            reg.Register("ReadDocumentKnowledgeObject", "按相对路径读取一个文档知识对象（.md）的完整定义：kind+正文+links 指向的子 .md 文件内容（只展开一层）。name=相对路径不含 .md（如 业务分析/退款会员日志分析）。先调此工具获取知识再执行。", "文档知识",
                sp => AIFunctionFactory.Create(
                    typeof(DocumentKnowledgeTool).GetMethod(nameof(DocumentKnowledgeTool.ReadDocumentKnowledgeObject))!,
                    sp.GetRequiredService<DocumentKnowledgeTool>(),
                    "ReadDocumentKnowledgeObject"),
                ToolApprovalLevel.Safe);
            reg.Register("CreateDocumentKnowledgeObject", "创建一个文档知识对象（.md 文件）。name=相对路径不含扩展名，将创建 <知识库目录>/<name>.md。kind 分类自由字符串。links 可选 JSON 数组（targetId=相对路径权威 或 target=名称）。extraJson 可选 front-matter 额外字段 JSON 对象（Intelligence 生命周期 subject/claim/status 等）。", "文档知识",
                sp => AIFunctionFactory.Create(
                    typeof(DocumentKnowledgeTool).GetMethod(nameof(DocumentKnowledgeTool.CreateDocumentKnowledgeObject))!,
                    sp.GetRequiredService<DocumentKnowledgeTool>(),
                    "CreateDocumentKnowledgeObject"),
                ToolApprovalLevel.Confirm);
            reg.Register("UpdateDocumentKnowledgeObject", "更新一个文档知识对象（.md 文件）的 kind/body/links/额外字段。按相对路径定位；传哪个参数更新哪个，不传保留原值。links 全量替换；extraJson 按键合并（未提到的额外键原样保留）。", "文档知识",
                sp => AIFunctionFactory.Create(
                    typeof(DocumentKnowledgeTool).GetMethod(nameof(DocumentKnowledgeTool.UpdateDocumentKnowledgeObject))!,
                    sp.GetRequiredService<DocumentKnowledgeTool>(),
                    "UpdateDocumentKnowledgeObject"),
                ToolApprovalLevel.Confirm);
            reg.Register("ValidateKnowledge", "体检知识对象库：检查断链（error 级）/kind 词表/边方向矩阵/按 kind 必填 front-matter 字段（软警告）。规则来自知识库根 knowledge-manifest.yaml（可编辑；无清单只查断链）。写完/改完知识对象后自检调用。", "文档知识",
                sp => AIFunctionFactory.Create(
                    typeof(DocumentKnowledgeTool).GetMethod(nameof(DocumentKnowledgeTool.ValidateKnowledge))!,
                    sp.GetRequiredService<DocumentKnowledgeTool>(),
                    "ValidateKnowledge"),
                ToolApprovalLevel.Safe);
        });
        return aether;
    }

    /// <summary>
    /// git 工具（git_status 只读 / git_commit 暂存全部并提交 / git_push 推送 / git_pull 拉取合并）：
    /// 让 agent 查看与提交、同步文档页当前项目的 git 变更。git 无业务上下文 → 宿主层注册（非 ai/ 适配器）。
    /// commit/push/pull 不可逆 → 标 Dangerous；status 只读标 Safe。仅注册工具——IGitService/通知/dialog 由 AddDesktopCommon 注册。
    /// </summary>
    public static AetherAIBuilder AddGitTool(this AetherAIBuilder aether)
    {
        aether.Services.AddTransient<GitTool>();
        aether.ConfigureToolRegistry(reg =>
        {
            reg.Register("git_status", "查看 git 工作区状态：已暂存/未暂存的变更文件列表。只读。" +
                "可选 workingDirectory 指定仓库目录（对资料库等非项目目录跑 git 时传入，值用 GetCurrentKnowledgeDirectory 获取）；不传默认用文档页打开的项目目录。", "Git",
                sp => AIFunctionFactory.Create(
                    typeof(GitTool).GetMethod(nameof(GitTool.GitStatus))!, sp.GetRequiredService<GitTool>(),
                    "git_status"),
                ToolApprovalLevel.Safe);
            reg.Register("git_commit", "将所有变更（含未跟踪文件）暂存并提交。需要用户审批确认。提交前建议先调用 git_status 查看变更。" +
                "可选 workingDirectory 指定仓库目录（对资料库等非项目目录跑 git 时传入，值用 GetCurrentKnowledgeDirectory 获取）；不传默认用文档页打开的项目目录。", "Git",
                sp => AIFunctionFactory.Create(
                    typeof(GitTool).GetMethod(nameof(GitTool.GitCommitAsync))!, sp.GetRequiredService<GitTool>(),
                    "git_commit"),
                ToolApprovalLevel.Dangerous);
            reg.Register("git_push", "推送当前分支提交到远程仓库（如 GitHub）。不可逆——公开本地提交到远程。需用户审批确认。" +
                "可选 workingDirectory 指定仓库目录（对资料库等非项目目录跑 git 时传入，值用 GetCurrentKnowledgeDirectory 获取）；不传默认用文档页打开的项目目录。", "Git",
                sp => AIFunctionFactory.Create(
                    typeof(GitTool).GetMethod(nameof(GitTool.GitPushAsync))!, sp.GetRequiredService<GitTool>(),
                    "git_push"),
                ToolApprovalLevel.Dangerous);
            reg.Register("git_pull", "从远程仓库拉取并合并更新到当前分支。会修改本地工作区，可能产生冲突。需用户审批确认。" +
                "可选 workingDirectory 指定仓库目录（对资料库等非项目目录跑 git 时传入，值用 GetCurrentKnowledgeDirectory 获取）；不传默认用文档页打开的项目目录。", "Git",
                sp => AIFunctionFactory.Create(
                    typeof(GitTool).GetMethod(nameof(GitTool.GitPullAsync))!, sp.GetRequiredService<GitTool>(),
                    "git_pull"),
                ToolApprovalLevel.Dangerous);
        });
        return aether;
    }

    /// <summary>
    /// MCP server 管理工具（mcp_list_servers / mcp_add_server / mcp_remove_server / mcp_refresh）：
    /// 让 agent 复用专家页「MCP工具」tab 同一套 IMcpManagementService 动态增删 MCP server。
    /// SaveServerAsync/DeleteServerAsync/RefreshAsync 内部均「重连 → 注册工具进 ToolRegistry」闭环，故 add/remove
    /// 是当轮会话即时生效的能力注入入口 → 标 Dangerous；list 只读标 Safe、refresh 标 Confirm。
    /// 不自动勾选进 agent：装好 server 后工具仅进 ToolRegistry 池，勾选由用户在专家编辑器完成（或 agent 经 agent_configure 显式勾）。
    /// </summary>
    public static AetherAIBuilder AddMcpManagementTool(this AetherAIBuilder aether)
    {
        aether.Services.AddTransient<McpManagementTool>();
        aether.ConfigureToolRegistry(reg =>
        {
            reg.Register("mcp_list_servers", "列出当前所有已配置的 MCP server：名称、传输方式（stdio/http）、连接状态、工具清单。只读，无副作用。", "MCP工具",
                sp => AIFunctionFactory.Create(
                    typeof(McpManagementTool).GetMethod(nameof(McpManagementTool.ListServersAsync))!, sp.GetRequiredService<McpManagementTool>(),
                    "mcp_list_servers"),
                ToolApprovalLevel.Safe);
            reg.Register("mcp_add_server", "新增或更新一个 MCP server 配置（.mcp.json 格式），写盘后立即重连并把其工具注册进当前 agent 可用工具池（当轮会话生效）。此操作会给 agent 安装新工具/新能力，需要用户审批确认。", "MCP工具",
                sp => AIFunctionFactory.Create(
                    typeof(McpManagementTool).GetMethod(nameof(McpManagementTool.AddServerAsync))!, sp.GetRequiredService<McpManagementTool>(),
                    "mcp_add_server"),
                ToolApprovalLevel.Dangerous);
            reg.Register("mcp_remove_server", "删除一个已配置的 MCP server（按 name 删配置文件）并重连，卸载其提供的工具。需要用户审批确认。", "MCP工具",
                sp => AIFunctionFactory.Create(
                    typeof(McpManagementTool).GetMethod(nameof(McpManagementTool.RemoveServerAsync))!, sp.GetRequiredService<McpManagementTool>(),
                    "mcp_remove_server"),
                ToolApprovalLevel.Dangerous);
            reg.Register("mcp_refresh", "重连所有 MCP server（重读 mcp-servers 目录）。手动改过配置目录 json 或某 server 掉线需重连时调用。只读目录无配置写入。", "MCP工具",
                sp => AIFunctionFactory.Create(
                    typeof(McpManagementTool).GetMethod(nameof(McpManagementTool.RefreshAsync))!, sp.GetRequiredService<McpManagementTool>(),
                    "mcp_refresh"),
                ToolApprovalLevel.Confirm);
        });
        return aether;
    }

    /// <summary>
    /// agent 配置/召唤工具（agent_configure / agent_reload / get_current_agent）：让 agent 像页面一样「先按名字选一个
    /// agent → 改字段 → 保存 → 重新召唤」。configure 改配置（Dangerous）；reload 重新召唤（Confirm）；get_current_agent
    /// 只读（Safe）。与页面 ExpertsViewModel.SaveAsync 同构。缺省参数用 ambient 上下文（AgentExecutionContext.CurrentAgentId）定位「当前 agent」。
    /// </summary>
    public static AetherAIBuilder AddAgentManagementTool(this AetherAIBuilder aether)
    {
        aether.Services.AddTransient<AgentManagementTool>();
        aether.ConfigureToolRegistry(reg =>
        {
            reg.Register("agent_configure", "配置一个自定义 agent（按名字定位；可配置自己或任意 agent）。只改传入字段，未传保留原值。改完自动重新召唤使其生效。此操作改写 agent 定义，需要用户审批确认。", "Agent自举",
                sp => AIFunctionFactory.Create(
                    typeof(AgentManagementTool).GetMethod(nameof(AgentManagementTool.ConfigureAsync))!, sp.GetRequiredService<AgentManagementTool>(),
                    "agent_configure"),
                ToolApprovalLevel.Dangerous);
            reg.Register("agent_reload", "重新召唤一个自定义 agent（按名字定位），使其缓存失效、下一轮 run 重建（带上最新配置）。通常在配置完工具后调用。注意会丢弃该 agent 会话的运行时记忆。需要用户审批确认。", "Agent自举",
                sp => AIFunctionFactory.Create(
                    typeof(AgentManagementTool).GetMethod(nameof(AgentManagementTool.ReloadAsync))!, sp.GetRequiredService<AgentManagementTool>(),
                    "agent_reload"),
                ToolApprovalLevel.Confirm);
            reg.Register("get_current_agent", "查询一个自定义 agent 的当前定义（缺省定位「正在调用本工具的当前 agent」，留空名字即可，无须知道自己叫什么；也可显式按名字查别的 agent）。返回 id/名字/描述/分类/模型/已勾选工具/是否开启工具审批。只读。", "Agent自举",
                sp => AIFunctionFactory.Create(
                    typeof(AgentManagementTool).GetMethod(nameof(AgentManagementTool.GetCurrentAgentAsync))!, sp.GetRequiredService<AgentManagementTool>(),
                    "get_current_agent"),
                ToolApprovalLevel.Safe);
        });
        return aether;
    }

    /// <summary>
    /// skill 管理工具（skill_list / skill_add / skill_remove）：让 agent 管理文件式 skill（目录扫描式
    /// <SkillsDirectory>/<name>/SKILL.md，运行时 MAF AgentFileSkillsSource 直读目录、无需重启）。
    /// add/remove 是「注入流程知识」的写入口 → 标 Dangerous；list 只读标 Safe。
    /// </summary>
    public static AetherAIBuilder AddSkillManagementTool(this AetherAIBuilder aether)
    {
        aether.Services.AddTransient<SkillManagementTool>();
        aether.ConfigureToolRegistry(reg =>
        {
            reg.Register("skill_list", "列出当前已加载的 skill（名称/描述/分类）。只读，无副作用。", "技能",
                sp => AIFunctionFactory.Create(
                    typeof(SkillManagementTool).GetMethod(nameof(SkillManagementTool.ListSkillsAsync))!, sp.GetRequiredService<SkillManagementTool>(),
                    "skill_list"),
                ToolApprovalLevel.Safe);
            reg.Register("skill_add", "新增或更新一个 skill（写到 <skills>/<name>/SKILL.md）并立即重载索引，令其下轮起对 agent 生效。装一个 skill 等于给 agent 注入新流程知识，需要用户审批确认。", "技能",
                sp => AIFunctionFactory.Create(
                    typeof(SkillManagementTool).GetMethod(nameof(SkillManagementTool.AddSkillAsync))!, sp.GetRequiredService<SkillManagementTool>(),
                    "skill_add"),
                ToolApprovalLevel.Dangerous);
            reg.Register("skill_remove", "删除一个 skill（按名删 <skills>/<name> 目录）并立即重载索引。需要用户审批确认。", "技能",
                sp => AIFunctionFactory.Create(
                    typeof(SkillManagementTool).GetMethod(nameof(SkillManagementTool.RemoveSkillAsync))!, sp.GetRequiredService<SkillManagementTool>(),
                    "skill_remove"),
                ToolApprovalLevel.Dangerous);
        });
        return aether;
    }

    /// <summary>
    /// build/test 结构化反馈工具（get_build_errors / run_tests）：让 agent 拿到结构化编译错误/测试失败，而非裸 dotnet
    /// 输出。标 Safe（agentic 编程闭环核心高频动作）。命令执行复用 ShellCommandTool。
    /// </summary>
    public static AetherAIBuilder AddBuildTool(this AetherAIBuilder aether)
    {
        aether.Services.AddTransient<BuildTool>();
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
        return aether;
    }

    /// <summary>
    /// 语义感知符号编辑工具（edit_symbol）：基于 Roslyn 定位 C# 符号并替换其源码文本。仅 C#。标 Safe。
    /// diff/撤销走 SCM 页 + git（路径由 Roslyn 执行时定位，不走单文件事后 diff 闭环）。
    /// </summary>
    public static AetherAIBuilder AddEditSymbolTool(this AetherAIBuilder aether)
    {
        aether.Services.AddTransient<EditSymbolTool>();
        aether.ConfigureToolRegistry(reg =>
        {
            reg.Register("edit_symbol", "基于 Roslyn 语义分析定位并替换当前项目中的一个 C# 符号（类型/方法/属性/字段）的源码文本。比 EditFile 字符串匹配更精确。仅支持 C#。", "编程",
                sp => AIFunctionFactory.Create(
                    typeof(EditSymbolTool).GetMethod(nameof(EditSymbolTool.EditSymbol))!, sp.GetRequiredService<EditSymbolTool>(),
                    "edit_symbol"),
                ToolApprovalLevel.Safe);
        });
        return aether;
    }

    /// <summary>
    /// 集成终端工具（run_in_terminal）：在可见终端面板执行命令（真实 PTY / 完整 env / 可观察可中断）。
    /// 标 Dangerous（执行任意命令）。宿显示器注册（需 ITerminalService + 项目目录）。
    /// </summary>
    public static AetherAIBuilder AddRunInTerminalTool(this AetherAIBuilder aether)
    {
        aether.Services.AddTransient<RunInTerminalTool>();
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
        return aether;
    }

    /// <summary>
    /// 浏览器工具（browser_open / browser_get_cookies / browser_get_requests / browser_close）：
    /// Playwright headed 独立窗口——用户手动登录，面板记录 HTTP 请求流；agent 拿 cookie 后 curl 带访问需登录接口。
    /// open/get_requests/close 标 Safe；get_cookies 标 Confirm（cookie 是登录凭证会进 agent 上下文）。
    /// </summary>
    public static AetherAIBuilder AddBrowserTool(this AetherAIBuilder aether)
    {
        aether.Services.AddTransient<BrowserTool>();
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
        return aether;
    }
}
