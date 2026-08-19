using System.Collections.Concurrent;
using System.Diagnostics;
using Aether.AI.Core.Agents;
using Aether.AI.Core.Messages;
using Aether.AI.Core.Sessions;
using Aether.AI.Harness.Agents;
using Aether.AI.Harness.Sessions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

// Both the framework and Aether.AI.Core define a type named AgentSession.
// Alias them to avoid ambiguity: the framework's is the model-side chat
// thread; Aether's is the runtime execution context.
using FrameworkAgentSession = Microsoft.Agents.AI.AgentSession;
using AetherSession = Aether.AI.Core.Sessions.AgentSession;

namespace Aether.AI.Harness;

/// <summary>
/// The Aether runtime: turns registered <see cref="AgentDefinition"/>s into
/// live agents, owns session lifecycle, and drives multi-turn execution on top
/// of Microsoft Agent Framework.
/// <para>
/// Responsibilities (Phase 1):
/// <list type="bullet">
///   <item>Agent creation (delegated to <see cref="AgentFactory"/>, cached).</item>
///   <item>Session management (delegated to <see cref="SessionManager"/>).</item>
///   <item>Multi-turn conversation via the framework's <c>AgentSession</c> thread.</item>
///   <item>Recording messages and tool calls into the Aether <see cref="AgentSession"/>.</item>
/// </list>
/// </para>
/// </summary>
public sealed class HarnessRuntime
{
    private readonly AgentFactory _factory;
    private readonly SessionManager _sessions;
    private readonly SessionConversationMap _sessionConversationMap;
    private readonly ConcurrentDictionary<string, AgentDefinition> _definitions = new();
    private readonly ConcurrentDictionary<string, AIAgent> _agents = new();
    private readonly ConcurrentDictionary<Guid, FrameworkAgentSession> _frameworkSessions = new();

    public HarnessRuntime(AgentFactory factory, SessionManager sessions, SessionConversationMap? sessionConversationMap = null)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _sessionConversationMap = sessionConversationMap ?? new SessionConversationMap();
    }

    public SessionManager Sessions => _sessions;

    /// <summary>Registers an agent definition so it can be started by id.</summary>
    public HarnessRuntime RegisterAgent(AgentDefinition definition)
    {
        if (definition is null) throw new ArgumentNullException(nameof(definition));
        _definitions[definition.Id] = definition;
        return this;
    }

    /// <summary>True if an agent with the given id is registered.</summary>
    public bool HasAgent(string agentId) => _definitions.ContainsKey(agentId);

    /// <summary>
    /// 使已注册 agent 的缓存失效：移除其定义与已构建的 agent 实例，下次运行时重建。
    /// 用于自定义 Agent 被编辑后丢弃旧配置。会话线程由调用方（runner）丢弃并新建。
    /// </summary>
    public HarnessRuntime Invalidate(string agentId)
    {
        _definitions.TryRemove(agentId, out _);
        _agents.TryRemove(agentId, out _);
        return this;
    }

    /// <summary>Starts a new runtime session for the named agent.</summary>
    public AetherSession StartSession(string agentId)
    {
        if (!_definitions.TryGetValue(agentId, out var definition))
            throw new InvalidOperationException($"No agent registered with id '{agentId}'.");

        return _sessions.Create(definition.Id, definition.Name);
    }

    /// <summary>
    /// Runs one turn of the agent bound to <paramref name="sessionId"/>,
    /// sending <paramref name="userMessage"/> and returning the assistant's
    /// textual response. Prior turns in the same session are remembered.
    /// </summary>
    public async Task<string> RunAsync(
        Guid sessionId,
        string userMessage,
        CancellationToken cancellationToken = default)
    {
        var session = _sessions.Get(sessionId)
            ?? throw new InvalidOperationException($"Session '{sessionId}' not found.");

        var definition = ResolveDefinition(session.AgentId);
        var agent = GetOrCreateAgent(definition);
        var frameworkSession = await GetOrCreateFrameworkSessionAsync(sessionId, agent, cancellationToken);

        session.Messages.Add(Message.User(userMessage));
        session.State = SessionState.Active;
        session.Touch();

        AgentResponse response;
        try
        {
            response = await agent.RunAsync(userMessage, frameworkSession, cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            session.State = SessionState.Faulted;
            session.Properties["lastError"] = ex.Message;
            session.Touch();
            throw;
        }

        var assistantText = response.Text ?? string.Empty;
        session.Messages.Add(Message.Assistant(assistantText));

        // Record any tool invocations the framework performed this turn.
        foreach (var toolName in ExtractToolCalls(response))
        {
            session.ToolCalls.Add(toolName);
        }

        session.State = SessionState.Idle;
        session.Touch();
        return assistantText;
    }

    /// <summary>
    /// Runs one turn as a streaming response. 答案文本经 <see cref="StreamHandlers.OnToken"/> 流式上抛，
    /// 工具调用/结果、模型思考(<c>TextReasoningContent</c>)、审批请求、待办列表、非致命错误、用量等
    /// 经 <paramref name="handlers"/> 的各回调上抛。<paramref name="approvalResponse"/> 非空表示
    /// 审批恢复轮（带 <c>ToolApprovalResponseContent</c> 二次调用以恢复被审批挂起的工具执行）。
    /// </summary>
    public async Task<string> RunStreamingAsync(
        Guid sessionId,
        string userMessage,
        StreamHandlers? handlers = null,
        ToolApprovalDecision? approvalResponse = null,
        string? desiredMode = null,
        CancellationToken cancellationToken = default)
    {
        var session = _sessions.Get(sessionId)
            ?? throw new InvalidOperationException($"Session '{sessionId}' not found.");

        var definition = ResolveDefinition(session.AgentId);
        var agent = GetOrCreateAgent(definition);
        var frameworkSession = await GetOrCreateFrameworkSessionAsync(sessionId, agent, cancellationToken);

        // 运行前应用期望模式（plan/execute 等）；会话新建或已存在均生效。null=不改变当前模式。
        if (desiredMode is not null)
            await TrySetModeAsync(agent, frameworkSession, desiredMode, cancellationToken).ConfigureAwait(false);

        // 审批恢复轮不再追加用户消息（首轮已记录）；仅首轮记录用户消息。
        if (approvalResponse is null)
        {
            session.Messages.Add(Message.User(userMessage));
        }
        session.State = SessionState.Active;
        session.Touch();

        var fullText = await RunStreamingCoreAsync(agent, frameworkSession, userMessage, handlers, approvalResponse, cancellationToken,
            name => session.ToolCalls.Add(name),
            emitTelemetry: definition.EnableOpenTelemetry, agentId: definition.Id, conversationId: sessionId.ToString(), mode: desiredMode);

        session.Messages.Add(Message.Assistant(fullText));
        session.State = SessionState.Idle;
        session.Touch();
        return fullText;
    }

    /// <summary>
    /// 用一个**已构建好的** <see cref="AIAgent"/>（如 workflow 包成的 agent）流式运行一轮，
    /// 复用与 <see cref="RunStreamingAsync(Guid,string,StreamHandlers?,ToolApprovalDecision?,string?,CancellationToken)"/>
    /// 相同的流式循环 + 事件提取。无 AetherSession 簿记（调用方自管会话）。
    /// 供 ICustomWorkflowRunner 等把组合 agent 当单 agent 跑的场景使用。
    /// </summary>
    public async Task<string> RunStreamingAsync(
        AIAgent agent,
        FrameworkAgentSession frameworkSession,
        string userMessage,
        StreamHandlers? handlers = null,
        ToolApprovalDecision? approvalResponse = null,
        string? desiredMode = null,
        CancellationToken cancellationToken = default)
    {
        if (desiredMode is not null)
            await TrySetModeAsync(agent, frameworkSession, desiredMode, cancellationToken).ConfigureAwait(false);
        return await RunStreamingCoreAsync(agent, frameworkSession, userMessage, handlers, approvalResponse, cancellationToken, onToolCallName: null);
    }

    /// <summary>读取当前会话的 AgentMode（plan/execute…）；agent 未启用 AgentModeProvider 返回 null。</summary>
    public async Task<string?> GetModeAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        var session = _sessions.Get(sessionId);
        if (session is null) return null;
        var agent = GetOrCreateAgent(ResolveDefinition(session.AgentId));
        var frameworkSession = await GetOrCreateFrameworkSessionAsync(sessionId, agent, cancellationToken);
        return await TryGetModeAsync(agent, frameworkSession, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>读取已构建 agent 的当前 AgentMode（供 workflow 路径复用）。</summary>
    public static async Task<string?> TryGetModeAsync(AIAgent agent, FrameworkAgentSession frameworkSession, CancellationToken cancellationToken = default)
    {
        var provider = agent.GetService<AgentModeProvider>();
        if (provider is null) return null;
        return await provider.GetModeAsync(frameworkSession, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>设置当前会话的 AgentMode；agent 未启用 AgentModeProvider 返回 false。</summary>
    public async Task<bool> SetModeAsync(Guid sessionId, string mode, CancellationToken cancellationToken = default)
    {
        var session = _sessions.Get(sessionId);
        if (session is null) return false;
        var agent = GetOrCreateAgent(ResolveDefinition(session.AgentId));
        var frameworkSession = await GetOrCreateFrameworkSessionAsync(sessionId, agent, cancellationToken);
        return await TrySetModeAsync(agent, frameworkSession, mode, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>设置已构建 agent 的 AgentMode（供 workflow 路径复用）。</summary>
    public static async Task<bool> TrySetModeAsync(AIAgent agent, FrameworkAgentSession frameworkSession, string mode, CancellationToken cancellationToken = default)
    {
        var provider = agent.GetService<AgentModeProvider>();
        if (provider is null) return false;
        await provider.SetModeAsync(frameworkSession, mode, cancellationToken).ConfigureAwait(false);
        return true;
    }


    /// <summary>
    /// 流式运行核心：遍历 <see cref="AIAgent.RunStreamingAsync"/>，按 <see cref="StreamHandlers"/>
    /// 上抛答案 token / 推理 / 工具事件 / 审批请求 / 待办 / 错误 / 用量。
    /// <paramref name="onToolCallName"/> 供带 AetherSession 的调用方记录工具名（可选）。
    /// <para>审批恢复轮（<paramref name="approvalResponse"/> 非空）：从暂存取回原
    /// <c>ToolApprovalRequestContent</c>，构造 <c>ToolApprovalResponseContent</c>（或 always-approve 包裹），
    /// 经 <c>IEnumerable&lt;ChatMessage&gt;</c> 重载二次调用以恢复工具执行。</para>
    /// </summary>
    private static async Task<string> RunStreamingCoreAsync(
        AIAgent agent, FrameworkAgentSession frameworkSession, string userMessage,
        StreamHandlers? handlers, ToolApprovalDecision? approvalResponse,
        CancellationToken cancellationToken,
        Action<string>? onToolCallName,
        bool emitTelemetry = false, string? agentId = null, string? conversationId = null, string? mode = null)
    {
        var sb = new System.Text.StringBuilder();
        // callId -> 工具名：FunctionResultContent 无 Name 字段，靠 CallId 回查调用时记录的名称。
        var toolNamesByCallId = new Dictionary<string, string>();

        // OpenTelemetry：EnableOpenTelemetry 时围本轮 run 起 Activity（无 listener 时 StartActivity 返回 null，cheap）。
        using var activity = emitTelemetry
            ? Aether.AI.Harness.Telemetry.AetherActivitySource.Instance.StartActivity(Aether.AI.Harness.Telemetry.AetherActivitySource.ActivityAgentRun)
            : null;
        if (activity is not null)
        {
            if (agentId is not null) activity.SetTag(Aether.AI.Harness.Telemetry.AetherActivitySource.TagAgentId, agentId);
            if (conversationId is not null) activity.SetTag(Aether.AI.Harness.Telemetry.AetherActivitySource.TagConversationId, conversationId);
            if (mode is not null) activity.SetTag(Aether.AI.Harness.Telemetry.AetherActivitySource.TagMode, mode);
        }

        // 选择调用重载：审批恢复轮带响应消息；首轮带用户文本。
        IAsyncEnumerable<AgentResponseUpdate> stream;
        if (approvalResponse is not null)
        {
            var req = GetPendingApproval(frameworkSession, approvalResponse.RequestId);
            if (req is null)
            {
                // 无对应待审请求（可能已被 always-approve 规则自动消化）→ 本轮无内容。
                return string.Empty;
            }
            // 防御：原请求必须带可绑定的 tool_use（FunctionCallContent + CallId）。否则构造出的
            // ToolApprovalResponseContent（本质 tool result）会成为「无前置 tool_use 的孤儿」，
            // 让 GLM/Anthropic 兼容端点报 messages 非法（如 GLM [1214]）。正常流程下 tool_use 是
            // 历史里最新的 assistant 消息、不会被滑动窗口截断；此守卫兜底暂存被污染/失效的异常情形。
            if (req.ToolCall is not FunctionCallContent fc || string.IsNullOrEmpty(fc.CallId))
            {
                RemovePendingApproval(frameworkSession, approvalResponse.RequestId);
                if (handlers?.OnError is not null)
                    await handlers.OnError("审批请求已失效（缺少对应的工具调用），请重试。", cancellationToken).ConfigureAwait(false);
                return string.Empty;
            }
            var reason = approvalResponse.Reason ?? string.Empty;
            AIContent responseContent = approvalResponse.AlwaysApprove
                ? (AIContent)req.CreateAlwaysApproveToolResponse(reason)
                : (AIContent)req.CreateResponse(approvalResponse.Approved, reason);
            RemovePendingApproval(frameworkSession, approvalResponse.RequestId);
            var msg = new ChatMessage { Role = ChatRole.User };
            msg.Contents.Add(responseContent);
            stream = agent.RunStreamingAsync(new[] { msg }, frameworkSession, cancellationToken: cancellationToken);
        }
        else
        {
            stream = agent.RunStreamingAsync(userMessage, frameworkSession, cancellationToken: cancellationToken);
        }

        await foreach (var update in stream.ConfigureAwait(false))
        {
            // 答案文本（update.Text 仅聚合 TextContent，不含推理）。
            if (!string.IsNullOrEmpty(update.Text))
            {
                sb.Append(update.Text);
                if (handlers?.OnToken is not null)
                    await handlers.OnToken(update.Text, cancellationToken).ConfigureAwait(false);
            }

            if (update.Contents is null) continue;

            foreach (var content in update.Contents)
            {
                switch (content)
                {
                    case FunctionCallContent call when !string.IsNullOrEmpty(call.Name):
                        if (!string.IsNullOrEmpty(call.CallId))
                            toolNamesByCallId[call.CallId] = call.Name!;
                        onToolCallName?.Invoke(call.Name!);
                        activity?.AddEvent(new ActivityEvent(Aether.AI.Harness.Telemetry.AetherActivitySource.EventToolCall,
                            tags: new ActivityTagsCollection(new[] { new KeyValuePair<string, object?>("name", call.Name), new KeyValuePair<string, object?>("callId", call.CallId) })));
                        if (handlers?.OnToolEvent is not null)
                        {
                            await handlers.OnToolEvent(
                                new ToolStreamEvent(ToolStreamEventKind.Call, call.Name!, call.CallId ?? string.Empty,
                                    ToInputDict(call.Arguments), null),
                                cancellationToken).ConfigureAwait(false);
                        }
                        break;

                    case FunctionResultContent result:
                    {
                        var name = (result.CallId is not null && toolNamesByCallId.TryGetValue(result.CallId, out var n))
                            ? n : "工具";
                        activity?.AddEvent(new ActivityEvent(Aether.AI.Harness.Telemetry.AetherActivitySource.EventToolResult,
                            tags: new ActivityTagsCollection(new[] { new KeyValuePair<string, object?>("name", name), new KeyValuePair<string, object?>("callId", result.CallId) })));
                        if (handlers?.OnToolEvent is not null)
                        {
                            await handlers.OnToolEvent(
                                new ToolStreamEvent(ToolStreamEventKind.Result, name, result.CallId ?? string.Empty,
                                    null, ToOutputDict(result.Result)),
                                cancellationToken).ConfigureAwait(false);
                        }
                        // todos_* 调用后读 live 待办列表上抛（计划 UI）。
                        // 放宽匹配：MAF 版本不同工具名可能为 todos_add / todos / todo_add 等。
                        if ((name.StartsWith("todo", StringComparison.OrdinalIgnoreCase))
                            && handlers?.OnTodoList is not null)
                        {
                            await RaiseTodoListAsync(agent, frameworkSession, handlers.OnTodoList, cancellationToken).ConfigureAwait(false);
                        }
                        break;
                    }

                    // 内置 web search（EnableWebSearch 开启时，hosted 服务发起的搜索）。与普通工具同走
                    // ToolStreamEvent 路径——两者皆继承自 ToolCallContent/ToolResultContent 故带 CallId，
                    // 可按 CallId 配对（桌面合并成一卡、手机两卡），复用既有工具卡渲染/持久化/重启重建管线，
                    // 不静默丢。OpenAI 内置 web search 实际多走 TextContent（已由 OnToken 处理），此分支兜底
                    // provider 直接发结构化 WebSearch 内容的少见情形。
                    case WebSearchToolCallContent wsCall:
                    {
                        var query = wsCall.Queries is null ? string.Empty : string.Join(" | ", wsCall.Queries);
                        if (!string.IsNullOrEmpty(wsCall.CallId))
                            toolNamesByCallId[wsCall.CallId] = WebSearchToolName;
                        activity?.AddEvent(new ActivityEvent(Aether.AI.Harness.Telemetry.AetherActivitySource.EventToolCall,
                            tags: new ActivityTagsCollection(new[] { new KeyValuePair<string, object?>("name", WebSearchToolName), new KeyValuePair<string, object?>("callId", wsCall.CallId) })));
                        if (handlers?.OnToolEvent is not null)
                        {
                            await handlers.OnToolEvent(
                                new ToolStreamEvent(ToolStreamEventKind.Call, WebSearchToolName, wsCall.CallId ?? string.Empty,
                                    string.IsNullOrEmpty(query) ? null : new Dictionary<string, object> { ["query"] = query }, null),
                                cancellationToken).ConfigureAwait(false);
                        }
                        break;
                    }

                    case WebSearchToolResultContent wsResult:
                    {
                        var name = (wsResult.CallId is not null && toolNamesByCallId.TryGetValue(wsResult.CallId, out var n))
                            ? n : WebSearchToolName;
                        activity?.AddEvent(new ActivityEvent(Aether.AI.Harness.Telemetry.AetherActivitySource.EventToolResult,
                            tags: new ActivityTagsCollection(new[] { new KeyValuePair<string, object?>("name", name), new KeyValuePair<string, object?>("callId", wsResult.CallId) })));
                        if (handlers?.OnToolEvent is not null)
                        {
                            await handlers.OnToolEvent(
                                new ToolStreamEvent(ToolStreamEventKind.Result, name, wsResult.CallId ?? string.Empty,
                                    null, ToWebSearchOutputDict(wsResult.Outputs)),
                                cancellationToken).ConfigureAwait(false);
                        }
                        break;
                    }

                    case TextReasoningContent tr when !string.IsNullOrEmpty(tr.Text):
                        if (handlers?.OnReasoning is not null)
                            await handlers.OnReasoning(tr.Text!, cancellationToken).ConfigureAwait(false);
                        break;

                    case ToolApprovalRequestContent approvalReq:
                        // 暂存原 req，供恢复轮 CreateResponse 使用。
                        SetPendingApproval(frameworkSession, approvalReq);
                        activity?.AddEvent(new ActivityEvent(Aether.AI.Harness.Telemetry.AetherActivitySource.EventApproval,
                            tags: new ActivityTagsCollection(new[] { new KeyValuePair<string, object?>("requestId", approvalReq.RequestId) })));
                        if (handlers?.OnApprovalRequest is not null)
                        {
                            var fc = approvalReq.ToolCall as FunctionCallContent;
                            await handlers.OnApprovalRequest(
                                new ToolApprovalRequest(
                                    approvalReq.RequestId ?? string.Empty,
                                    fc?.Name ?? "工具",
                                    approvalReq.ToolCall?.CallId ?? string.Empty,
                                    fc is not null ? ToInputDict(fc.Arguments) : null),
                                cancellationToken).ConfigureAwait(false);
                        }
                        break;

                    case ErrorContent err when !string.IsNullOrEmpty(err.Message):
                        activity?.AddEvent(new ActivityEvent(Aether.AI.Harness.Telemetry.AetherActivitySource.EventError,
                            tags: new ActivityTagsCollection(new[] { new KeyValuePair<string, object?>("message", err.Message) })));
                        if (handlers?.OnError is not null)
                            await handlers.OnError(err.Message!, cancellationToken).ConfigureAwait(false);
                        break;

                    case UsageContent usage:
                        activity?.AddEvent(new ActivityEvent(Aether.AI.Harness.Telemetry.AetherActivitySource.EventUsage,
                            tags: new ActivityTagsCollection(new[] { new KeyValuePair<string, object?>("summary", SummarizeUsage(usage)) })));
                        handlers?.OnUsage?.Invoke(SummarizeUsage(usage));
                        break;
                }
            }
        }
        if (activity is not null) activity.SetTag(Aether.AI.Harness.Telemetry.AetherActivitySource.TagStatus, "ok");
        return sb.ToString();
    }

    /// <summary>读 TodoProvider 当前 live 待办列表并映射为 <see cref="TodoItemInfo"/> 上抛。</summary>
    private static async Task RaiseTodoListAsync(
        AIAgent agent, FrameworkAgentSession session,
        Func<IReadOnlyList<TodoItemInfo>, CancellationToken, Task> onTodoList, CancellationToken ct)
    {
        var provider = agent.GetService<TodoProvider>();
        if (provider is null) return;
        var items = await provider.GetAllTodosAsync(session, ct).ConfigureAwait(false);
        var list = items
            .Select(i => new TodoItemInfo(i.Id.ToString(), i.Title ?? string.Empty, i.Description, i.IsComplete))
            .ToList();
        await onTodoList(list, ct).ConfigureAwait(false);
    }

    /// <summary>把 <see cref="UsageContent"/> 摘要为一行可展示文本。</summary>
    private static string SummarizeUsage(UsageContent usage)
    {
        var d = usage.Details;
        var input = d.InputTokenCount;
        var output = d.OutputTokenCount;
        return $"输入 {(input is null ? 0 : input)} / 输出 {(output is null ? 0 : output)} token";
    }

    // —— 审批请求暂存：frameworkSession -> (requestId -> 原 ToolApprovalRequestContent)。
    //   流冒出请求时存入；恢复轮按 RequestId 取回以构造正确关联的响应。会话结束随键自动回收。
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<FrameworkAgentSession, Dictionary<string, ToolApprovalRequestContent>> _pendingApprovals = new();

    private static void SetPendingApproval(FrameworkAgentSession session, ToolApprovalRequestContent req)
    {
        var dict = _pendingApprovals.GetOrCreateValue(session);
        lock (dict) dict[req.RequestId ?? string.Empty] = req;
    }

    private static ToolApprovalRequestContent? GetPendingApproval(FrameworkAgentSession session, string requestId)
    {
        if (_pendingApprovals.TryGetValue(session, out var dict))
        {
            lock (dict) return dict.TryGetValue(requestId, out var req) ? req : null;
        }
        return null;
    }

    private static void RemovePendingApproval(FrameworkAgentSession session, string requestId)
    {
        if (_pendingApprovals.TryGetValue(session, out var dict))
        {
            lock (dict) dict.Remove(requestId);
        }
    }

    /// <summary>把 FunctionCallContent.Arguments（可能为字典/JsonElement/字符串）归一为可展示字典。</summary>
    private static Dictionary<string, object>? ToInputDict(object? arguments)
    {
        if (arguments is null) return null;

        if (arguments is IDictionary<string, object?> nd)
            return nd.ToDictionary(kv => kv.Key, kv => kv.Value ?? string.Empty);
        if (arguments is IDictionary<string, object> d)
            return new Dictionary<string, object>(d);
        if (arguments is IReadOnlyDictionary<string, object?> rnd)
            return rnd.ToDictionary(kv => kv.Key, kv => kv.Value ?? string.Empty);

        if (arguments is System.Text.Json.JsonElement je)
        {
            if (je.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                try { return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(je); }
                catch { }
            }
            return new Dictionary<string, object> { ["input"] = je.ToString() };
        }

        return new Dictionary<string, object> { ["input"] = arguments.ToString() ?? string.Empty };
    }

    /// <summary>把 FunctionResultContent.Result 归一为可展示字典。</summary>
    private static Dictionary<string, object>? ToOutputDict(object? result)
    {
        if (result is null) return null;

        if (result is string s)
            return new Dictionary<string, object> { ["result"] = s };

        if (result is IDictionary<string, object?> nd)
            return nd.ToDictionary(kv => kv.Key, kv => kv.Value ?? string.Empty);
        if (result is IDictionary<string, object> d)
            return new Dictionary<string, object>(d);

        if (result is System.Text.Json.JsonElement je)
        {
            if (je.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                try { return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(je); }
                catch { }
            }
            return new Dictionary<string, object> { ["result"] = je.ToString() };
        }

        return new Dictionary<string, object> { ["result"] = result.ToString() ?? string.Empty };
    }

    /// <summary>内置 web search 工具的展示名（toolNamesByCallId 登记名 + 卡片名）。</summary>
    private const string WebSearchToolName = "web_search";

    /// <summary>把 <see cref="WebSearchToolResultContent.Outputs"/>（<c>IList&lt;AIContent&gt;</c>）归一为可展示字典。
    /// 镜像 <see cref="ToOutputDict"/>：<see cref="ChatMessageItem"/> 的 NormalizeToolResult 读 "result" 键 →
    /// 多行渲染为 code 块。每条输出取 <c>TextContent.Text</c>，回退 <c>ToString()</c>；空集返 null。</summary>
    private static Dictionary<string, object>? ToWebSearchOutputDict(System.Collections.Generic.IList<AIContent>? outputs)
    {
        if (outputs is null || outputs.Count == 0) return null;
        var sb = new System.Text.StringBuilder();
        foreach (var o in outputs)
        {
            var t = o is TextContent tc ? tc.Text : o.ToString();
            if (!string.IsNullOrEmpty(t))
            {
                if (sb.Length > 0) sb.AppendLine();
                sb.Append(t);
            }
        }
        var text = sb.ToString();
        return string.IsNullOrEmpty(text) ? null : new Dictionary<string, object> { ["result"] = text };
    }

    private AgentDefinition ResolveDefinition(string agentId)
        => _definitions.TryGetValue(agentId, out var definition)
            ? definition
            : throw new InvalidOperationException($"No agent registered with id '{agentId}'.");

    private AIAgent GetOrCreateAgent(AgentDefinition definition)
        => _agents.GetOrAdd(definition.Id, _ => _factory.Create(definition));

    private async Task<FrameworkAgentSession> GetOrCreateFrameworkSessionAsync(
        Guid sessionId, AIAgent agent, CancellationToken cancellationToken)
    {
        if (_frameworkSessions.TryGetValue(sessionId, out var existing))
            return existing;

        var created = await agent.CreateSessionAsync(cancellationToken);
        _frameworkSessions[sessionId] = created;
        _sessionConversationMap.Register(created, sessionId); // 供 RestoringChatHistoryProvider 按 conversationId 加载 transcript
        return created;
    }

    /// <summary>
    /// 注册工作流会话→conversationId 映射，供 <c>RestoringChatHistoryProvider</c> 在重启续聊时
    /// 按 conversationId 加载 transcript 重建 LLM 历史。工作流路径（<c>RunStreamingAsync(AIAgent,…)</c>
    /// 重载）自管 <c>FrameworkAgentSession</c>、不经 <see cref="GetOrCreateFrameworkSessionAsync"/>，
    /// 故需显式注册——否则 RestoringChatHistoryProvider 对工作流会话失效（LLM 上下文空，只 UI 显示有历史）。
    /// </summary>
    public void RegisterWorkflowSession(FrameworkAgentSession session, Guid conversationId)
        => _sessionConversationMap.Register(session, conversationId);

    /// <summary>
    /// 会话删除时清出运行时会话状态：丢弃 <see cref="_frameworkSessions"/> 里的 FrameworkAgentSession
    /// （agent 路径）+ <see cref="SessionManager"/> 里的 AgentSession。一旦这两个强引用切断，session 对象即
    /// 可回收——RestoringChatHistoryProvider 的 per-session 缓存（ConditionalWeakTable 弱键）随之随 GC 自动清，
    /// 无需 provider↔runtime 清理布线。工作流路径的 session 不在此处（自管于 CustomWorkflowRunner._sessions），
    /// 由 <c>ICustomWorkflowRunner.EvictConversation</c> 直接清。会话删除前 UI 已 <c>CancelRunningRun</c>，故无在飞 run 持有该 session。
    /// </summary>
    public void EvictSession(Guid sessionId)
    {
        _frameworkSessions.TryRemove(sessionId, out _);
        _sessions.Remove(sessionId);
        // SessionConversationMap 是 ConditionalWeakTable 弱键，session 回收后自动清，无需显式删。
    }

    private static IEnumerable<string> ExtractToolCalls(AgentResponse response)
    {
        var names = new List<string>();
        foreach (var message in response.Messages)
        {
            foreach (var content in message.Contents)
            {
                if (content is FunctionCallContent call && !string.IsNullOrEmpty(call.Name))
                    names.Add(call.Name);
            }
        }
        return names;
    }
}
