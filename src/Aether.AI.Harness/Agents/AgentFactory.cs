using System.Diagnostics;
using System.Text.Json;
using Aether.AI.Core.Agents;
using Aether.AI.Harness.Skills;
using Aether.AI.Harness.Tools;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Aether.AI.Harness.Agents;

/// <summary>
/// Builds a runnable Microsoft Agent Framework <see cref="AIAgent"/> from an
/// <see cref="AgentDefinition"/>. By default the agent is a
/// <see cref="HarnessAgent"/> — the batteries-included agent from
/// <c>Microsoft.Agents.AI.Harness</c> — with the heavyweight default providers
/// (web search, todos, file memory, skills, OpenTelemetry) disabled so that
/// Phase 1 runs as a clean, dependency-light runtime.
/// <para>
/// Set <see cref="UseChatClientAgent"/> to use the lighter
/// <see cref="ChatClientAgent"/> instead, which skips the Harness pipeline
/// entirely.
/// </para>
/// <para>
/// The agent's <see cref="AgentDefinition.ModelId"/> is resolved to an
/// <see cref="IChatClient"/> via <see cref="IChatClientFactory"/>, and its
/// <see cref="AgentDefinition.Memory"/> policy is enforced by configuring the
/// framework's chat-history provider at build time.
/// </para>
/// </summary>
public sealed class AgentFactory
{
    private readonly IChatClientFactory _chatClientFactory;
    private readonly ToolRegistry _toolRegistry;
    private readonly SkillRegistry _skillRegistry;
    private readonly ILoggerFactory? _loggerFactory;
    private readonly AgentFileSkillScriptRunner _skillScriptRunner;
    private readonly Func<AgentDefinition, AIContextProvider?>? _memoryContextProviderFactory;
    private readonly Func<AgentDefinition, ChatHistoryProvider?>? _historyProviderFactory;
    private readonly ICodeBrainRegistry? _codeBrainRegistry;

    /// <summary>
    /// When <c>true</c> (default), builds a <see cref="HarnessAgent"/>. When
    /// <c>false</c>, builds a plain <see cref="ChatClientAgent"/>.
    /// </summary>
    public bool UseChatClientAgent { get; set; } = false;

    /// <summary>
    /// HarnessAgent 文件记忆 provider 的可写根目录（每 agent 在其下建子目录）。
    /// 为空时启用文件记忆的 agent 会被静默禁用该 provider，避免默认相对路径在只读 CWD 下崩溃。
    /// 由宿主通过 AetherAIOptions.FileMemoryRoot 设置。
    /// </summary>
    public string? FileMemoryRoot { get; set; }

    /// <summary>skills 目录。配置后用 MAF AgentFileSkillsSource 加载（支持脚本）；否则退回 SkillRegistry 纯提示词。</summary>
    public string? SkillsDirectory { get; set; }

    /// <summary>执行 .py 脚本的解释器（默认 python3）。由宿主通过 AetherAIOptions.PythonExecutable 设置。</summary>
    public string? PythonExecutable { get; set; }

    /// <param name="chatClientFactory">
    /// Resolves the <see cref="IChatClient"/> for a given agent model id.
    /// Invoked lazily per agent so the runtime does not eagerly build a client.
    /// </param>
    public AgentFactory(IChatClientFactory chatClientFactory, ToolRegistry toolRegistry, SkillRegistry skillRegistry, ILoggerFactory? loggerFactory = null,
        Func<AgentDefinition, AIContextProvider?>? memoryContextProviderFactory = null,
        Func<AgentDefinition, ChatHistoryProvider?>? historyProviderFactory = null,
        ICodeBrainRegistry? codeBrainRegistry = null)
    {
        _chatClientFactory = chatClientFactory ?? throw new ArgumentNullException(nameof(chatClientFactory));
        _toolRegistry = toolRegistry ?? throw new ArgumentNullException(nameof(toolRegistry));
        _skillRegistry = skillRegistry ?? throw new ArgumentNullException(nameof(skillRegistry));
        _loggerFactory = loggerFactory;
        _skillScriptRunner = RunSkillScriptAsync;
        _memoryContextProviderFactory = memoryContextProviderFactory;
        _historyProviderFactory = historyProviderFactory;
        _codeBrainRegistry = codeBrainRegistry;
    }

    /// <summary>Builds a cached, runnable agent for the given definition.</summary>
    public AIAgent Create(AgentDefinition definition)
    {
        if (definition is null) throw new ArgumentNullException(nameof(definition));

        // AIFunction derives from AITool, but List<AIFunction> is not assignable
        // to IList<AITool>; widen to IList<AITool> for the framework APIs.
        // ask_user（交互提问工具）若已注册则自动纳入各 agent，免逐 agent 配置；agent 仍需 prompt 指示才会调用。
        // MCP 工具由宿主在启动后注册进 ToolRegistry（与原生工具同源），专家在工具选择器勾选 → definition.Tools，
        // 这里一并 ResolveAll 解析——MCP 工具走与原生工具完全相同的路径（含审批包装）。
        var toolNames = definition.Tools.ToList();
        if (!toolNames.Contains("ask_user") && _toolRegistry.Contains("ask_user"))
            toolNames.Add("ask_user");
        IList<AITool> tools = _toolRegistry.ResolveAll(toolNames).Cast<AITool>().ToList();

        // 按专家开关把"危险"工具包成 ApprovalRequiredAIFunction：模型调用时流冒出
        // ToolApprovalRequestContent，由 UI 弹审批框（协作式审批）。等级优先取专家的 ToolApprovals 覆盖，
        // 否则取 ToolRegistry 声明等级——Safe 自动放行，Confirm/Dangerous 才包装。
        // MCP 工具注册时默认标 Dangerous（来源未知），故 RequireToolApproval 时也会被包装。
        if (definition.RequireToolApproval)
        {
            tools = tools
                .Select(t => t is AIFunction fn && ResolveApprovalLevel(fn.Name, definition) != ToolApprovalLevel.Safe
                    ? new ApprovalRequiredAIFunction(fn) : t)
                .ToList();
        }

        var historyProvider = _historyProviderFactory?.Invoke(definition) ?? BuildHistoryProvider(definition);

        // 规则大脑：确定性规则驱动工具调用，不委托 LLM。接缝是 IChatClient——RuleChatClient 产 AIContent，
        // 工具执行/回填/循环仍归 MAF HarnessAgent（与 LLM 路径同源）。规则不消费注入式 NL 上下文
        //（todo/mode/skill/memory-recall），BuildRuleAgent 内关掉这些 provider（1.0 已验证 TodoProvider
        // 会注入第二条 user 消息污染 LatestUser）。
        if (definition.Engine == EngineKind.Rule)
        {
            // 规则 agent 复用编辑器勾选的工具（definition.Tools + ask_user），与 LLM agent 一致——
            // 规则只调程序里点名的工具，但工具池由作者在编辑器显式选定。
            // 不经审批（规则定义 RequireToolApproval=false；规则是 Safe 确定性流程）。
            return BuildRuleAgent(definition, tools, historyProvider);
        }

        return UseChatClientAgent
            ? BuildChatClientAgent(definition, tools, historyProvider)
            : BuildHarnessAgent(definition, tools, historyProvider);
    }

    /// <summary>
    /// 构建规则大脑 agent：用 <see cref="RuleChatClient"/>（IChatClient）代替 LLM 客户端接入 HarnessAgent。
    /// 规则程序取自 <see cref="AgentDefinition.Rules"/>（已由适配器规整成 <see cref="RuleProgram"/> 对象）；
    /// null 退空程序（NoMatch 兜底）。关掉所有注入式 NL 上下文 provider——规则不消费它们，且 TodoProvider 会污染 LatestUser。
    /// 工具执行、结果回填、循环仍由 MAF 框架负责（与 LLM 路径同源）。
    /// </summary>
    private AIAgent BuildRuleAgent(AgentDefinition definition, IList<AITool> tools, ChatHistoryProvider? historyProvider)
    {
        // 代码规则大脑：按笔记 Id 从注册表取工厂得 fresh 实例（Roslyn 即时编译的 CodeRuleBrain）。
        // 未注册/编译失败 → ErrorReplyBrain 兜底，产一句提示终止循环。
        // 声明式 YAML 规则：RuleProgramBrain（definition.Rules）。
        IRuleBrain brain;
        if (definition.IsCodeRule)
        {
            // 未配 registry / 未注册 / 编译失败 → ErrorReplyBrain 兜底，产一句提示终止循环。
            // 预声明 + 预赋值，避免 TryCreate 短路未调时的「未赋值局部变量」编译错。
            IRuleBrain? codeBrain = null;
            string? codeError = null;
            bool got = _codeBrainRegistry is not null && _codeBrainRegistry.TryCreate(definition.Id, out codeBrain, out codeError);
            brain = got ? codeBrain! : new ErrorReplyBrain(codeError);
        }
        else
        {
            var program = definition.Rules ?? new RuleProgram();
            brain = new RuleProgramBrain(program);
        }
        var client = new RuleChatClient(brain, _loggerFactory?.CreateLogger<RuleChatClient>());

        var options = new HarnessAgentOptions
        {
            Id = definition.Id,
            Name = definition.Name,
            Description = $"Aether.AI rule agent '{definition.Id}'.",
            // 规则不消费注入式 NL 上下文——全关。TodoProvider 默认开，会注入「### Current todo list」
            // user 消息污染 RuleChatClient 的 LatestUser 检测（1.0 实测）。
            DisableWebSearch = true,
            DisableTodoProvider = true,
            DisableAgentModeProvider = true,
            DisableFileMemory = true,
            DisableOpenTelemetry = true,
            DisableAgentSkillsProvider = true, // skill 指令需 LLM 解析；规则大脑不读
            ChatOptions = new ChatOptions { Instructions = definition.Instructions, Tools = tools },
            ToolApprovalAgentOptions = BuildToolApprovalOptions(definition), // 规则 agent 若挂审批工具同样按名放行
            // 规则大脑靠 history 重建轮内状态（最新 user 消息 + 本轮工具调用计数），必须保留轮内 history。
            // NoMemoryChatHistoryProvider（Memory=Off）连轮内 history 也清掉 → 工具调用后 MAF re-invoke 时
            // 只传 [tool:result]，user 消息丢失 → LatestUser=null → 误报「无匹配规则」。故规则 agent 遇
            // NoMemory 退回框架默认（InMemory，保留轮内 history）；规则大脑只读「最后一条 user + 其后工具调用」，
            // 跨轮累积的历史被忽略，无害。SlidingWindow/Full 本就保留轮内 history，原样放行。
            ChatHistoryProvider = historyProvider is NoMemoryChatHistoryProvider ? null : historyProvider,
        };

        return client.AsHarnessAgent(options, _loggerFactory);
    }

    private AIAgent BuildHarnessAgent(AgentDefinition definition, IList<AITool> tools, ChatHistoryProvider? historyProvider)
    {
        // 文件记忆仅在配置了可写根目录时才真正启用；否则静默禁用，避免默认相对路径在只读 CWD 下崩溃。
        var fileMemoryEnabled = definition.EnableFileMemory && !string.IsNullOrEmpty(FileMemoryRoot);

        var options = new HarnessAgentOptions
        {
            Id = definition.Id,
            Name = definition.Name,
            Description = $"Aether.AI agent '{definition.Id}'.",
            // Provider 开关由 AgentDefinition 的 Enable* 字段驱动（默认全 false = 全部禁用），
            // 保持 Phase 1 的轻量运行时行为；需要 web search / file memory / OTel 等能力的
            // agent 在定义里把对应 Enable* 置 true 即可。(Compaction 在未设 token 上限时自动禁用，无需开关。)
            DisableWebSearch = !definition.EnableWebSearch,
            DisableTodoProvider = !definition.EnableTodoProvider,
            DisableAgentModeProvider = !definition.EnableAgentModeProvider,
            DisableFileMemory = !fileMemoryEnabled,
            DisableOpenTelemetry = !definition.EnableOpenTelemetry,
            ChatOptions = BuildChatOptions(definition, tools),
            ChatHistoryProvider = historyProvider,
            ToolApprovalAgentOptions = BuildToolApprovalOptions(definition),
            FileMemoryStore = fileMemoryEnabled
                ? new FileSystemAgentFileStore(Path.Combine(FileMemoryRoot!, definition.Id))
                : null,
        };

        // skills provider 自建注入（见 ConfigureSkillsProvider）：聊天页无审批 UI，
        // 必须关掉 load/read/run 审批，否则 run_skill_script 被默认审批挡住、模型只 load 不执行。
        ConfigureSkillsProvider(options, definition);

        // 长期记忆 provider：EnableLongTermMemory 时由宿主工厂构造 MemoryContextProvider 注入。
        // 与 skills provider 累加（ConfigureSkillsProvider 可能已设置 AIContextProviders）。
        var memoryProvider = _memoryContextProviderFactory?.Invoke(definition);
        if (memoryProvider is not null)
        {
            var combined = options.AIContextProviders is null
                ? new List<AIContextProvider>()
                : new List<AIContextProvider>(options.AIContextProviders);
            combined.Add(memoryProvider);
            options.AIContextProviders = combined;
        }

        var client = _chatClientFactory.Create(definition.ModelId);

        // AgentModeProvider 开启时，过滤掉 get_mode/set_mode 工具不暴露给 LLM
        // （GLM 会无限调用 mode_get；但保留 AgentModeProvider 的模式注入 + 状态管理）
        if (definition.EnableAgentModeProvider)
        {
            client = new ToolFilteringChatClient(client, "get_mode", "set_mode");
        }

        return client.AsHarnessAgent(options, _loggerFactory);
    }
    private void ConfigureSkillsProvider(HarnessAgentOptions options, AgentDefinition definition)
    {
        // 技能选择器是唯一控制：选了技能 = 启用，没选 = 禁用。不再需要 EnableAgentSkills 开关。
        if (definition.Skills.Count == 0)
        {
            options.DisableAgentSkillsProvider = true;
            return;
        }

        AgentSkillsSource? source = null;
        if (!string.IsNullOrEmpty(SkillsDirectory))
        {
            source = new AgentFileSkillsSource(SkillsDirectory, _skillScriptRunner, options: null, _loggerFactory);
            if (definition.Skills.Count > 0)
            {
                var allowed = new HashSet<string>(definition.Skills, StringComparer.OrdinalIgnoreCase);
                source = new FilteringAgentSkillsSource(source, (s, _) => allowed.Contains(s.Frontmatter.Name), _loggerFactory);
            }
        }
        else
        {
            var skills = definition.Skills.Count > 0
                ? _skillRegistry.ResolveAll(definition.Skills)
                : _skillRegistry.GetAll();
            if (skills.Count == 0) { options.DisableAgentSkillsProvider = true; return; }

            var agentSkills = skills
                .Select(s => (AgentSkill)new AgentInlineSkill(
                    new AgentSkillFrontmatter(s.Name, s.Description, ""), s.Content, null, null))
                .ToList();
            source = new AgentInMemorySkillsSource(agentSkills);
        }

        var provider = new AgentSkillsProvider(source, new AgentSkillsProviderOptions
        {
            DisableLoadSkillApproval = true,
            DisableReadSkillResourceApproval = true,
            DisableRunSkillScriptApproval = true,
        }, _loggerFactory, ownsSource: false);

        options.DisableAgentSkillsProvider = true; // 关内部默认 provider，避免双重 + 避免其默认审批
        options.AIContextProviders = new List<AIContextProvider> { provider };
    }

    private AIAgent BuildChatClientAgent(AgentDefinition definition, IList<AITool> tools, ChatHistoryProvider? historyProvider)
    {
        var client = _chatClientFactory.Create(definition.ModelId);

        // When no custom history provider is needed (Full), use the lightweight
        // ctor which defaults to InMemoryChatHistoryProvider. Otherwise wire the
        // provider through ChatClientAgentOptions.
        if (historyProvider is null)
        {
            return new ChatClientAgent(
                client,
                instructions: definition.Instructions,
                name: definition.Name,
                description: $"Aether.AI agent '{definition.Id}'.",
                tools: tools,
                loggerFactory: _loggerFactory);
        }

        var agentOptions = new ChatClientAgentOptions
        {
            Name = definition.Name,
            Description = $"Aether.AI agent '{definition.Id}'.",
            ChatOptions = BuildChatOptions(definition, tools),
            ChatHistoryProvider = historyProvider,
        };
        return new ChatClientAgent(client, agentOptions, _loggerFactory);
    }

    /// <summary>
    /// Builds the framework chat-history provider that enforces the agent's
    /// memory policy. <c>null</c> (Full) lets the framework use its default
    /// unbounded in-memory provider.
    /// </summary>
    /// <summary>解析工具审批等级：专家 ToolApprovals 覆盖优先，否则取 ToolRegistry 声明等级。</summary>
    private ToolApprovalLevel ResolveApprovalLevel(string toolName, AgentDefinition definition)
    {
        if (definition.ToolApprovals is not null
            && definition.ToolApprovals.TryGetValue(toolName, out var overrideLevel)
            && Enum.TryParse<ToolApprovalLevel>(overrideLevel, ignoreCase: true, out var lvl))
        {
            return lvl;
        }
        return _toolRegistry.LevelOf(toolName);
    }

    /// <summary>
    /// 把 <see cref="AgentDefinition.AutoApprovalRules"/>（按工具名自动放行列表）转成 MAF
    /// <see cref="ToolApprovalAgentOptions"/>。规则在弹用户前评估：命中工具名→自动放行（true），
    /// 否则→继续评估下一条规则（false），最终落到 UI 弹框。与 <see cref="ApprovalRequiredAIFunction"/>
    /// 包装叠加：包装让非-Safe 工具发审批请求 → 中间件看请求 → 命中规则自动放行，否则弹框。
    /// <see cref="AgentDefinition.RequireToolApproval"/> 关时无包装→无审批请求→本规则惰性无副作用。
    /// </summary>
    private static ToolApprovalAgentOptions? BuildToolApprovalOptions(AgentDefinition definition)
    {
        if (definition.AutoApprovalRules is not { Count: > 0 } names) return null;
        var set = names.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new ToolApprovalAgentOptions
        {
            AutoApprovalRules = new Func<ToolAutoApprovalRuleContext, ValueTask<bool>>[]
            {
                ctx => new ValueTask<bool>(set.Contains(ctx.FunctionCallContent.Name ?? string.Empty)),
            },
        };
    }

    private static ChatHistoryProvider? BuildHistoryProvider(AgentDefinition definition)
        => definition.Memory switch
        {
            AgentMemoryMode.Off => new NoMemoryChatHistoryProvider(),
            AgentMemoryMode.SlidingWindow => new InMemoryChatHistoryProvider(
                new InMemoryChatHistoryProviderOptions
                {
                    ChatReducer = new SlidingWindowChatReducer(definition.MaxHistory),
                }),
            // Summarize 在此 static fallback 里退回滑动窗口——static 路径无 IChatClientFactory，
            // 无法构造 SummarizingChatReducer。运行时由 Hosting 的 _historyProviderFactory（持 chatClientFactory）
            // 接真 SummarizingChatReducer；fallback 仅在未配工厂（如测试）时使用。
            AgentMemoryMode.Summarize => new InMemoryChatHistoryProvider(
                new InMemoryChatHistoryProviderOptions
                {
                    ChatReducer = new SlidingWindowChatReducer(definition.MaxHistory),
                }),
            _ => null, // Full = framework default
        };

    private static ChatOptions BuildChatOptions(AgentDefinition definition, IList<AITool> tools)
    {
        var chatOptions = new ChatOptions
        {
            Instructions = definition.Instructions,
            Tools = tools,
        };

        // ask_user 可用时自动追加使用指示（否则 agent 默认纯文本提问，交互对话框形同虚设）。
        if (tools.Any(t => t is AIFunction f && string.Equals(f.Name, "ask_user", StringComparison.OrdinalIgnoreCase)))
        {
            chatOptions.Instructions = (string.IsNullOrEmpty(chatOptions.Instructions) ? "" : chatOptions.Instructions + "\n\n")
                + "当需要用户确认、拍板、或从多个选项中选择时，必须调用 ask_user 工具阻塞等待用户回答；"
                + "不要用纯文本提问后结束轮次等待用户输入。有多个需要确认的问题时，一次性传入 questions 数组（表单一次提交），"
                + "返回 JSON 数组按提问顺序给出每个的回答。"
                + "特别注意：当你打算用文本列出多个选项让用户选（如『○ 选项A / ○ 选项B / ○ 选项C』『你可以回复：…』『请指示👇』），"
                + "必须改为调用 ask_user，把这些选项作为单个 question 的 options 数组传入——绝不要用纯文本列选项后结束轮次。"
                + "在 execute（执行）模式下：开始执行计划前，先调用 ask_user 确认是否开始；"
                + "执行过程中遇到写、删、下单、运行脚本等不可逆或高风险操作前，也先调用 ask_user 确认，不要自主直接执行。";
        }

        if (definition.Temperature is { } temperature)
        {
            chatOptions.Temperature = (float)temperature;
        }

        if (definition.TopP is { } topP)
        {
            chatOptions.TopP = (float)topP;
        }

        if (definition.MaxOutputTokens is { } maxTokens)
        {
            chatOptions.MaxOutputTokens = maxTokens;
        }

        if (definition.ResponseFormat != AgentResponseFormat.Default)
        {
            chatOptions.ResponseFormat = definition.ResponseFormat switch
            {
                AgentResponseFormat.Text => ChatResponseFormat.Text,
                AgentResponseFormat.Json => ChatResponseFormat.Json,
                _ => null,
            };
        }

        return chatOptions;
    }

    /// <summary>
    /// 当 agent 选了技能（<see cref="AgentDefinition.Skills"/> 非空）时构建 skill 源。
    /// 配了 <see cref="SkillsDirectory"/> 时用 MAF <see cref="AgentFileSkillsSource"/>（支持 SKILL.md 正文 + 脚本/resources），
    /// 并按 <see cref="AgentDefinition.Skills"/> 过滤；否则退回 SkillRegistry 的纯提示词 <see cref="AgentInMemorySkillsSource"/>。
    /// </summary>
    /// <summary>
    /// skill 脚本运行器：按扩展名选解释器（.py→python3，.sh→sh，.js→node），
    /// 以子进程执行脚本并传入 CLI 参数，返回 stdout。失败时返回含退出码/ stderr 的字符串。
    /// </summary>
    private async Task<object?> RunSkillScriptAsync(
        AgentSkill skill, AgentFileSkillScript script, JsonElement? rawArguments,
        IServiceProvider serviceProvider, CancellationToken cancellationToken)
    {
        var args = ParseCliArgs(rawArguments);
        var log = _loggerFactory?.CreateLogger<AgentFactory>();
        log?.LogDebug("run_skill_script {Script} raw={Raw} → args={Args}", script.FullPath, rawArguments, string.Join(" ", args));
        if (rawArguments is not null && args.Count == 0)
            log?.LogWarning("run_skill_script 收到的参数未能解析出 CLI 参数（raw={Raw}），脚本将以无参数运行。", rawArguments);
        var interpreter = ResolveInterpreter(script.FullPath);
        var allArgs = new List<string> { script.FullPath };
        allArgs.AddRange(args);

        // 和 MCP 一样：相对解释器（python3/sh/node）经登录 shell 跑（解 GUI 受限 PATH）；
        // 绝对路径（PythonExecutable）直接跑。CWD 默认用户主目录（避 .app CWD=/）。
        ProcessStartInfo psi;
        if (Path.IsPathRooted(interpreter))
        {
            psi = new ProcessStartInfo
            {
                FileName = interpreter,
                RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true,
            };
            foreach (var a in allArgs) psi.ArgumentList.Add(a);
        }
        else
        {
            var shell = Environment.GetEnvironmentVariable("SHELL");
            if (!string.IsNullOrEmpty(shell))
            {
                var cmd = ShellQuote(interpreter) + " " + string.Join(" ", allArgs.Select(ShellQuote));
                psi = new ProcessStartInfo
                {
                    FileName = shell,
                    RedirectStandardOutput = true, RedirectStandardError = true,
                    UseShellExecute = false, CreateNoWindow = true,
                };
                psi.ArgumentList.Add("-l");
                psi.ArgumentList.Add("-c");
                psi.ArgumentList.Add(cmd);
            }
            else
            {
                psi = new ProcessStartInfo
                {
                    FileName = interpreter,
                    RedirectStandardOutput = true, RedirectStandardError = true,
                    UseShellExecute = false, CreateNoWindow = true,
                };
                foreach (var a in allArgs) psi.ArgumentList.Add(a);
            }
        }

        try
        {
            using var process = new Process { StartInfo = psi };
            if (!process.Start()) return $"无法启动脚本进程：{interpreter}";
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                var stderr = await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
                return $"脚本退出码 {process.ExitCode}：{stderr}";
            }
            return stdout;
        }
        catch (Exception ex)
        {
            return $"脚本执行失败：{ex.Message}";
        }
    }

    private static List<string> ParseCliArgs(JsonElement? raw)
    {
        var list = new List<string>();
        if (raw is not { } el) return list;

        switch (el.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (var item in el.EnumerateArray()) list.Add(ToArg(item));
                break;
            case JsonValueKind.String:
                var s = el.GetString();
                if (!string.IsNullOrEmpty(s)) list.AddRange(ShellSplit(s));
                break;
            case JsonValueKind.Object:
                // {key: value} → --key value（bool true → 仅 --key 标志，false → 跳过）
                foreach (var prop in el.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.False) continue;
                    list.Add("--" + prop.Name);
                    if (prop.Value.ValueKind == JsonValueKind.True) continue;
                    list.Add(ToArg(prop.Value));
                }
                break;
        }
        return StripLeadingPrefix(list);
    }

    private static string ToArg(JsonElement item)
    {
        if (item.ValueKind == JsonValueKind.String) return item.GetString() ?? "";
        if (item.ValueKind == JsonValueKind.Number) return item.GetRawText();
        if (item.ValueKind == JsonValueKind.True) return "true";
        if (item.ValueKind == JsonValueKind.False) return "false";
        return item.ToString();
    }

    /// <summary>
    /// 剥掉 agent 可能带进来的前缀：SKILL.md 里写的是 <c>python "&lt;SQLQ&gt;" asyncSqlQuery ...</c>（Bash 写法），
    /// agent 可能把 <c>python</c> 解释器和 <c>&lt;SQLQ&gt;</c> 占位符 / 脚本路径也塞进参数。run_skill_script 已自带
    /// 解释器 + 脚本路径，这里把它们剥掉，只留真正的子命令 + 参数。
    /// </summary>
    private static List<string> StripLeadingPrefix(List<string> args)
    {
        int i = 0;
        if (i < args.Count && IsPython(args[i])) i++;
        if (i < args.Count && IsScriptRef(args[i])) i++;
        return i == 0 ? args : args.Skip(i).ToList();
    }

    private static bool IsPython(string s)
        => s is "python" or "python3" or "py"
           || s.EndsWith("/python", StringComparison.OrdinalIgnoreCase)
           || s.EndsWith("/python3", StringComparison.OrdinalIgnoreCase);

    private static bool IsScriptRef(string s)
        => !string.IsNullOrEmpty(s)
           && (s.EndsWith(".py", StringComparison.OrdinalIgnoreCase)
               || (s.StartsWith('<') && s.EndsWith('>'))            // <SQLQ> 占位符
               || s.Contains("sql_query", StringComparison.OrdinalIgnoreCase)
               || s.Contains('/') || s.Contains('\\'));

    /// <summary>简单 shell 分词（支持单/双引号 + 反斜杠转义），用于 agent 传整条命令串的情况。</summary>
    private static List<string> ShellSplit(string s)
    {
        var list = new List<string>();
        var sb = new System.Text.StringBuilder();
        char? quote = null;
        bool inTok = false;
        for (int i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (quote is not null)
            {
                if (c == quote.Value) quote = null;
                else if (c == '\\' && i + 1 < s.Length) sb.Append(s[++i]);
                else sb.Append(c);
            }
            else if (c == '"' || c == '\'') { quote = c; inTok = true; }
            else if (char.IsWhiteSpace(c)) { if (inTok) { list.Add(sb.ToString()); sb.Clear(); inTok = false; } }
            else { sb.Append(c); inTok = true; }
        }
        if (inTok) list.Add(sb.ToString());
        return list;
    }

    private string ResolveInterpreter(string scriptPath)
    {
        var ext = Path.GetExtension(scriptPath).ToLowerInvariant();
        return ext switch
        {
            ".py" => string.IsNullOrWhiteSpace(PythonExecutable) ? "python3" : PythonExecutable,
            ".sh" => "sh",
            ".js" => "node",
            _ => "sh",
        };
    }

    /// <summary>POSIX 单引号转义：把参数包成 '…'，内部 ' 转义为 '\''。</summary>
    private static string ShellQuote(string s)
    {
        if (string.IsNullOrEmpty(s)) return "''";
        return "'" + s.Replace("'", "'\\''") + "'";
    }
}
