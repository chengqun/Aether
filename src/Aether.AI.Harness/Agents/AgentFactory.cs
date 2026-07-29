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
    public AgentFactory(IChatClientFactory chatClientFactory, ToolRegistry toolRegistry, SkillRegistry skillRegistry, ILoggerFactory? loggerFactory = null)
    {
        _chatClientFactory = chatClientFactory ?? throw new ArgumentNullException(nameof(chatClientFactory));
        _toolRegistry = toolRegistry ?? throw new ArgumentNullException(nameof(toolRegistry));
        _skillRegistry = skillRegistry ?? throw new ArgumentNullException(nameof(skillRegistry));
        _loggerFactory = loggerFactory;
        _skillScriptRunner = RunSkillScriptAsync;
    }

    /// <summary>Builds a cached, runnable agent for the given definition.</summary>
    public AIAgent Create(AgentDefinition definition)
    {
        if (definition is null) throw new ArgumentNullException(nameof(definition));

        // AIFunction derives from AITool, but List<AIFunction> is not assignable
        // to IList<AITool>; widen to IList<AITool> for the framework APIs.
        IList<AITool> tools = _toolRegistry.ResolveAll(definition.Tools).Cast<AITool>().ToList();
        var historyProvider = BuildHistoryProvider(definition);

        return UseChatClientAgent
            ? BuildChatClientAgent(definition, tools, historyProvider)
            : BuildHarnessAgent(definition, tools, historyProvider);
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
            DisableAgentSkillsProvider = !definition.EnableAgentSkills,
            DisableOpenTelemetry = !definition.EnableOpenTelemetry,
            ChatOptions = BuildChatOptions(definition, tools),
            ChatHistoryProvider = historyProvider,
            AgentSkillsSource = BuildSkillsSource(definition),
            FileMemoryStore = fileMemoryEnabled
                ? new FileSystemAgentFileStore(Path.Combine(FileMemoryRoot!, definition.Id))
                : null,
        };

        return _chatClientFactory.Create(definition.ModelId).AsHarnessAgent(options, _loggerFactory);
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
    private static ChatHistoryProvider? BuildHistoryProvider(AgentDefinition definition)
        => definition.Memory switch
        {
            AgentMemoryMode.Off => new NoMemoryChatHistoryProvider(),
            AgentMemoryMode.SlidingWindow => new InMemoryChatHistoryProvider(
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
    /// 当 agent 启用 skills（<see cref="AgentDefinition.EnableAgentSkills"/>）时构建 skill 源。
    /// 配了 <see cref="SkillsDirectory"/> 时用 MAF <see cref="AgentFileSkillsSource"/>（支持 SKILL.md 正文 + 脚本/resources），
    /// 并按 <see cref="AgentDefinition.Skills"/> 过滤；否则退回 SkillRegistry 的纯提示词 <see cref="AgentInMemorySkillsSource"/>。
    /// </summary>
    private AgentSkillsSource? BuildSkillsSource(AgentDefinition definition)
    {
        if (!definition.EnableAgentSkills) return null;

        // 文件型 source（支持脚本）：每 agent 一个，按名过滤子集。
        if (!string.IsNullOrEmpty(SkillsDirectory))
        {
            var fileSource = new AgentFileSkillsSource(
                SkillsDirectory,
                scriptRunner: _skillScriptRunner,
                options: null,
                loggerFactory: _loggerFactory);

            if (definition.Skills.Count > 0)
            {
                var allowed = new HashSet<string>(definition.Skills, StringComparer.OrdinalIgnoreCase);
                return new FilteringAgentSkillsSource(
                    fileSource,
                    (skill, _) => allowed.Contains(skill.Frontmatter.Name),
                    _loggerFactory);
            }
            return fileSource;
        }

        // 退路：SkillRegistry 纯提示词（无脚本）。
        var skills = definition.Skills.Count > 0
            ? _skillRegistry.ResolveAll(definition.Skills)
            : _skillRegistry.GetAll();
        if (skills.Count == 0) return null;

        var agentSkills = skills
            .Select(s => (AgentSkill)new AgentInlineSkill(
                new AgentSkillFrontmatter(s.Name, s.Description, ""),
                s.Content,
                null,
                null))
            .ToList();

        return new AgentInMemorySkillsSource(agentSkills);
    }

    /// <summary>
    /// skill 脚本运行器：按扩展名选解释器（.py→python3，.sh→sh，.js→node），
    /// 以子进程执行脚本并传入 CLI 参数，返回 stdout。失败时返回含退出码/ stderr 的字符串。
    /// </summary>
    private async Task<object?> RunSkillScriptAsync(
        AgentSkill skill, AgentFileSkillScript script, JsonElement? rawArguments,
        IServiceProvider serviceProvider, CancellationToken cancellationToken)
    {
        var args = ParseCliArgs(rawArguments);
        var interpreter = ResolveInterpreter(script.FullPath);

        var psi = new ProcessStartInfo
        {
            FileName = interpreter,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add(script.FullPath);
        foreach (var a in args) psi.ArgumentList.Add(a);

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
        if (raw is { } el && el.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in el.EnumerateArray())
                list.Add(item.ValueKind == JsonValueKind.String ? item.GetString() ?? "" : item.ToString());
        }
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
}
