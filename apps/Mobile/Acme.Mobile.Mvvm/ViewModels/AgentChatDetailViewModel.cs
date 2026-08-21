using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Acme.Notes.Domain;
using Acme.Notes.Domain.Agents;
using Aether.AI.Core.Agents;
using Aether.AI.Core.Conversations;
using Aether.AI.Core.Conversations.Runs;
using Aether.AI.Hosting;
using Aether.AI.Memory;
using Acme.Mobile.Mvvm.Services;
using Acme.Mobile.Mvvm.Abstractions;
using Acme.Mobile.Mvvm.Events;
using Acme.Mobile.Mvvm.Models;

namespace Acme.Mobile.Mvvm.ViewModels;

/// <summary>
/// Agent 聊天 ViewModel：支持普通 Agent 会话和股票 Agent 会话。
/// 股票模式通过 stockId+stockName 进入，自动发起一次该股票的查询。
/// 运行时接 Aether 的 ICustomAgentRunner（回调式流式）+ IConversationStore（持久化），
/// 镜像桌面 MainViewModelBase 的 lazy-create-on-first-send 模式。
/// </summary>
public partial class AgentChatDetailViewModel : NavigableViewModelBase
{
    private readonly ICustomAgentRunner? _agentRunner;
    private readonly IConversationStore? _conversationStore;
    private readonly ICustomAgentCatalog? _agentCatalog;
    private readonly IEventBusService? _eventBusService;
    private readonly IClipboardService? _clipboardService;
    private readonly IToastService? _toastService;
    private readonly ISpeechRecognitionService? _speechRecognitionService;
    private readonly IStreamingBackgroundService? _streamingBackgroundService;
    private readonly IConversationTitleService? _titleService;
    private readonly IToolApprovalDialogService? _approvalDialog;

    /// <summary>当前流式轮内捕获的待审批请求（OnApprovalRequest 置位，审批循环读取）。
    /// 仅在 RunStreamingAsync 执行期间被同一异步流读写，await 完成边界保证可见性。</summary>
    private ToolApprovalRequest? _pendingApproval;

    private bool _isLoaded = false;
    private Guid _currentConversationId = Guid.Empty; // Guid.Empty = 未创建，首次发送时 lazy-create
    private Guid _agentId = Guid.Empty; // 由 ICatalog 按名称解析

    /// <summary>当前解析到的 agent Id（供宿主 VM 在 LLM 切换后 Invalidate 缓存）。Guid.Empty=未解析。</summary>
    public Guid AgentId => _agentId;
    private bool _agentResolved = false; // agent id/kind 是否已解析
    private bool _shouldAutoQueryStock = false; // 股票新会话需自动查询
    private string _previousKey = string.Empty;
    private int _streamVersion; // 每次 SetParameters 递增，用于废弃过期的流式操作

    // 语音识别取消令牌
    private CancellationTokenSource? _voiceCts;

    // 流式回复取消令牌：会话切换/离开页面时取消，避免 LLM 空跑浪费 token/电量
    private CancellationTokenSource? _streamingCts;

    public event Action? StreamingContentUpdated;

    [ObservableProperty]
    private string _agentName = "股票分析助手";

    [ObservableProperty]
    private string _stockId = string.Empty;

    [ObservableProperty]
    private string _stockName = string.Empty;

    /// <summary>
    /// 是否为股票模式（从通讯录股票进入），股票模式支持自动查询。
    /// </summary>
    [ObservableProperty]
    private bool _isStockMode;

    [ObservableProperty]
    private string _messageText = string.Empty;

    /// <summary>
    /// 输入框是否有文字（控制发送按钮可见性）
    /// </summary>
    [ObservableProperty]
    private bool _hasMessageText;

    /// <summary>
    /// 是否正在语音识别中
    /// </summary>
    [ObservableProperty]
    private bool _isListening;

    /// <summary>
    /// 是否为语音输入模式（默认 true：居中麦克风按钮；false：文字输入模式）
    /// </summary>
    [ObservableProperty]
    private bool _isVoiceMode = true;

    [ObservableProperty]
    private bool _canSendMessage;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    /// <summary>
    /// 当前会话累计输入 Token 数
    /// </summary>
    [ObservableProperty]
    private int _sessionInputTokens;

    /// <summary>
    /// 当前会话累计输出 Token 数
    /// </summary>
    [ObservableProperty]
    private int _sessionOutputTokens;

    /// <summary>
    /// 当前会话 Token 用量汇总（格式: "📊 1.2k tokens"）
    /// </summary>
    [ObservableProperty]
    private string _sessionTokenInfo = string.Empty;

    /// <summary>
    /// Agent 类型分类
    /// </summary>
    [ObservableProperty]
    private AgentKind _agentKind;

    /// <summary>当前会话标题（lazy-create 占位 / 恢复历史 / AI 自动生成后更新），供宿主页 header 显示。
    /// 切换会话时在 <see cref="SetParameters"/> 重置为空，由 <see cref="LoadMessagesAsync"/> 或
    /// lazy-create 或 <see cref="GenerateAndApplyTitleAsync"/> 重新填充。</summary>
    [ObservableProperty]
    private string _currentTitle = string.Empty;

    /// <summary>
    /// 是否为 LLM Agent 类型
    /// </summary>
    public bool IsAgentKindAgent => AgentKind == AgentKind.Agent;

    /// <summary>
    /// 是否为 Workflow 类型
    /// </summary>
    public bool IsAgentKindWorkflow => AgentKind == AgentKind.Workflow;

    /// <summary>
    /// 是否为 RuleBased 类型
    /// </summary>
    public bool IsAgentKindRuleBased => AgentKind == AgentKind.RuleBased;

    partial void OnAgentKindChanged(AgentKind value)
    {
        OnPropertyChanged(nameof(IsAgentKindAgent));
        OnPropertyChanged(nameof(IsAgentKindWorkflow));
        OnPropertyChanged(nameof(IsAgentKindRuleBased));
    }

    public ObservableCollection<ChatMessageItem> Messages { get; } = new();

    public IAsyncRelayCommand SendMessageCommand { get; }

    public AgentChatDetailViewModel(ICustomAgentRunner? agentRunner = null,
                                    IConversationStore? conversationStore = null,
                                    ICustomAgentCatalog? agentCatalog = null,
                                    IEventBusService? eventBusService = null,
                                    IUIThreadDispatcher? uiThreadDispatcher = null,
                                    IClipboardService? clipboardService = null,
                                    IToastService? toastService = null,
                                    ISpeechRecognitionService? speechRecognitionService = null,
                                    IStreamingBackgroundService? streamingBackgroundService = null,
                                    IConversationTitleService? titleService = null,
                                    IToolApprovalDialogService? approvalDialog = null)
    {
        _agentRunner = agentRunner;
        _conversationStore = conversationStore;
        _agentCatalog = agentCatalog;
        _eventBusService = eventBusService;
        _uiThreadDispatcher = uiThreadDispatcher;
        _clipboardService = clipboardService;
        _toastService = toastService;
        _speechRecognitionService = speechRecognitionService;
        _streamingBackgroundService = streamingBackgroundService;
        _titleService = titleService;
        _approvalDialog = approvalDialog;
        SendMessageCommand = new AsyncRelayCommand(SendMessageAsync, () => CanSendMessage);
    }

    partial void OnMessageTextChanged(string value)
    {
        CanSendMessage = !string.IsNullOrWhiteSpace(value);
        HasMessageText = !string.IsNullOrWhiteSpace(value);
        SendMessageCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// 设置导航参数（由平台层调用）
    /// </summary>
    public void SetParameters(IDictionary<string, object> query)
    {
        // 股票模式参数
        var hasStockId = query.TryGetValue("stockId", out var stockId);
        var hasStockName = query.TryGetValue("stockName", out var stockName);

        // 会话 ID（从聊天列表进入时传入，恢复已有会话）
        Guid cid = Guid.Empty;
        var hasConversationId = query.TryGetValue("conversationId", out var conversationId)
                                && Guid.TryParse(conversationId?.ToString(), out cid);
        // 兼容旧参数名 sessionId
        if (!hasConversationId)
        {
            hasConversationId = query.TryGetValue("sessionId", out var sessionId)
                                 && Guid.TryParse(sessionId?.ToString(), out cid);
        }

        // agentName 参数：恢复旧会话时传入会话自身的 AgentName，锁定 Agent
        var hasAgentName = query.TryGetValue("agentName", out var agentNameParam);

        // 先根据传入参数确定模式
        if (hasStockId)
        {
            IsStockMode = true;
            StockId = Uri.UnescapeDataString(stockId?.ToString() ?? string.Empty);
            StockName = Uri.UnescapeDataString(stockName?.ToString() ?? string.Empty);

            // 恢复旧会话时，用会话自身的 AgentName（锁定 Agent，不随全局设置变化）
            // 新建会话时用默认名称
            // TODO: 旧版从 AiSettingsViewModel.GetStockModeAgentName() 读取用户选择的全局 Agent；
            //       该 VM 暂未 re-enable，此处暂用默认值。AiSettings re-enable 后恢复。
            if (hasConversationId && hasAgentName)
            {
                AgentName = Uri.UnescapeDataString(agentNameParam?.ToString() ?? DefaultStockAgentName);
            }
            else
            {
                AgentName = DefaultStockAgentName;
            }
        }
        else
        {
            // 没有 stockId → 非股票模式，必须清除残留的股票状态
            IsStockMode = false;
            StockId = string.Empty;
            StockName = string.Empty;
        }

        // Agent 名称参数（非股票模式时生效）
        if (!IsStockMode && query.TryGetValue("agentName", out var name))
        {
            AgentName = Uri.UnescapeDataString(name?.ToString() ?? "股票分析助手");
        }

        // 构造唯一标识，检测参数变化需要重新初始化
        var currentKey = hasConversationId ? cid.ToString() : (IsStockMode ? $"stock:{StockId}" : AgentName);

        if (hasConversationId)
        {
            _currentConversationId = cid;
            _shouldAutoQueryStock = false;
        }
        else
        {
            // 没有 conversationId → 新会话，首次发送时 lazy-create
            _currentConversationId = Guid.Empty;
            // 股票新会话需自动查询
            _shouldAutoQueryStock = IsStockMode && !string.IsNullOrEmpty(StockName);
        }

        // 检测切换：不同会话需要重置状态重新初始化
        if (currentKey != _previousKey)
        {
            _previousKey = currentKey;
            _streamVersion++; // 废弃正在运行的流式操作
            _streamingCts?.Cancel(); // 取消正在进行的流式回复
            _isLoaded = false;
            _agentResolved = false; // 切换 Agent 后需重新解析
            Messages.Clear();
            // 重置会话 Token 计数 + 标题（恢复历史/lazy-create/AI 生成时重新填充）
            SessionInputTokens = 0;
            SessionOutputTokens = 0;
            SessionTokenInfo = string.Empty;
            CurrentTitle = string.Empty;
        }
    }

    /// <summary>强制下次 <see cref="SetParameters"/> 视为会话切换并重置状态。
    /// 用于「新对话」复用同一 agent 时——同 agent 的 currentKey 不变，常规 key 比较不会触发重置，
    /// 故宿主在开新对话前调此方法清空 <c>_previousKey</c>，确保消息/标题被清空。</summary>
    public void ForceNewConversation() => _previousKey = string.Empty;

    /// <summary>旧版从 AiSettingsViewModel.GetStockModeAgentName() 读取；该 VM 暂未 re-enable，用此常量占位。</summary>
    private const string DefaultStockAgentName = "股票分析助手";

    public override async Task OnActivatedAsync()
    {
        if (_isLoaded) return;

        if (_agentRunner == null || _conversationStore == null)
        {
            ErrorMessage = "服务未初始化";
            return;
        }

        var version = _streamVersion;

        try
        {
            IsLoading = true;
            ErrorMessage = null;

            // 解析 Agent Id + Kind（按名称从 catalog 查找）
            if (!_agentResolved)
            {
                await Task.Run(async () => await ResolveAgentAsync());
                if (_streamVersion != version) return;
            }

            // 恢复已有会话的历史消息
            if (_currentConversationId != Guid.Empty)
            {
                await Task.Run(async () => await LoadMessagesAsync());
                if (_streamVersion != version) return;
            }

            // 股票新会话：自动查询该股票
            if (_shouldAutoQueryStock)
            {
                _shouldAutoQueryStock = false; // 只触发一次
                await AutoQueryStockAsync();
                if (_streamVersion != version) return;
            }

            // 标记已读：通知聊天列表清零该会话的未读数
            if (_currentConversationId != Guid.Empty)
            {
                _eventBusService?.Publish(new ConversationReadEvent(_currentConversationId));
            }

            _isLoaded = true;
        }
        catch (Exception ex)
        {
            if (_streamVersion != version) return;
            ErrorMessage = $"初始化失败: {ex.Message}";
            System.Diagnostics.Debug.WriteLine($"[AgentChatDetail] 初始化失败: {ex}");
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// 从 ICustomAgentCatalog 按名称解析 Agent 的 Id 和 Kind。
    /// </summary>
    private async Task ResolveAgentAsync()
    {
        if (_agentCatalog == null)
        {
            _agentResolved = true; // 标记已尝试，避免反复查找
            return;
        }

        try
        {
            var agents = await _agentCatalog.ListAsync(default);
            var def = agents.FirstOrDefault(a => a.Name == AgentName);
            if (def != null)
            {
                _agentId = def.Id;
                AgentKind = def.NodeType switch
                {
                    NoteNodeType.Workflow => AgentKind.Workflow,
                    NoteNodeType.Rule or NoteNodeType.CodeRule => AgentKind.RuleBased,
                    _ => AgentKind.Agent,
                };
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"[AgentChatDetail] 未找到 Agent: {AgentName}");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AgentChatDetail] 解析 Agent 失败: {ex.Message}");
        }
        finally
        {
            _agentResolved = true;
        }
    }

    private async Task LoadMessagesAsync()
    {
        if (_currentConversationId == Guid.Empty || _conversationStore == null) return;

        var version = _streamVersion;

        try
        {
            var conv = await _conversationStore.GetConversationAsync(_currentConversationId, default);
            if (_streamVersion != version) return;

            var runs = conv == null
                ? Array.Empty<AgentRun>()
                : await _conversationStore.GetRunsAsync(_currentConversationId, default);
            var eventsByRun = new Dictionary<Guid, IReadOnlyList<AgentEvent>>();
            foreach (var run in runs)
                eventsByRun[run.Id] = await _conversationStore.GetEventsAsync(run.Id, default);
            if (_streamVersion != version) return;

            _uiThreadDispatcher?.Post(() =>
            {
                Messages.Clear();
                SessionInputTokens = 0;
                SessionOutputTokens = 0;
                if (conv != null)
                {
                    // 按 run.seq 顺序重建：每条 run 的 events 还原为用户气泡 + 思考/工具/文本卡。
                    foreach (var run in runs.OrderBy(r => r.Seq))
                    {
                        eventsByRun.TryGetValue(run.Id, out var events);
                        foreach (var item in ExpandRun(run, events))
                            Messages.Add(item);
                    }
                    // 恢复历史会话标题（AI 生成或首轮占位）到 header
                    CurrentTitle = conv.Title ?? string.Empty;
                }
                SessionTokenInfo = FormatTokenInfo(SessionInputTokens + SessionOutputTokens);
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AgentChatDetail] 加载消息失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 将一个 <see cref="AgentRun"/> + 其 <see cref="AgentEvent"/> 映射为扁平 <see cref="ChatMessageItem"/> 序列（恢复历史消息）。
    /// 镜像桌面 <c>BuildTurnsFromRuns</c>：用户气泡 → 思考段(text+IsReasoning) → 工具步(单卡，ToolCall+ToolResult 按 CallId 合并、
    /// 恢复 CallId) → 文本答案段。plan/approval/error/usage 不渲染。CallId 在此恢复，使续聊 dedup 与持久化配对正常工作。
    /// </summary>
    private IEnumerable<ChatMessageItem> ExpandRun(AgentRun run, IReadOnlyList<AgentEvent>? events)
    {
        var agentName = string.IsNullOrWhiteSpace(run.AgentName) ? AgentName : run.AgentName;

        // 用户气泡（用户请求属于本次执行）
        yield return new ChatMessageItem
        {
            Id = Guid.NewGuid().ToString(),
            ItemType = "text",
            Role = "user",
            Content = run.UserMessage,
            Timestamp = run.StartedAt.LocalDateTime
        };

        if (events == null) yield break;

        // CallId → 工具卡，result 到来时回填 Output + IsToolCompleted（合并、恢复 CallId）。
        var toolCardsByCallId = new Dictionary<string, ChatMessageItem>();
        foreach (var e in events.OrderBy(x => x.Seq))
        {
            switch (e.EventType)
            {
                case AgentEventType.TextChunk:
                    var text = AgentEventPayload.Deserialize<AgentEventPayload.TextChunk>(e.Payload)?.Text ?? string.Empty;
                    if (!string.IsNullOrEmpty(text))
                        yield return new ChatMessageItem
                        {
                            Id = Guid.NewGuid().ToString(),
                            ItemType = "text",
                            Role = "assistant",
                            Content = text,
                            MarkdownContent = text,
                            AuthorName = agentName,
                            Timestamp = e.CreatedAt.LocalDateTime
                        };
                    break;

                case AgentEventType.ReasoningChunk:
                    var rtext = AgentEventPayload.Deserialize<AgentEventPayload.ReasoningChunk>(e.Payload)?.Text ?? string.Empty;
                    if (!string.IsNullOrEmpty(rtext))
                        yield return new ChatMessageItem
                        {
                            Id = Guid.NewGuid().ToString(),
                            ItemType = "text",
                            Role = "assistant",
                            Content = rtext,
                            IsReasoning = true,
                            IsReasoningContent = true,
                            AuthorName = agentName,
                            Timestamp = e.CreatedAt.LocalDateTime
                        };
                    break;

                case AgentEventType.ToolCall:
                    var tc = AgentEventPayload.Deserialize<AgentEventPayload.ToolCall>(e.Payload);
                    var card = new ChatMessageItem
                    {
                        Id = Guid.NewGuid().ToString(),
                        ItemType = "tool",
                        Role = "assistant",
                        ToolName = tc?.Name ?? "tool",
                        CallId = tc?.CallId,
                        Input = tc?.Input,
                        IsToolCompleted = false,
                        AuthorName = agentName,
                        Timestamp = e.CreatedAt.LocalDateTime
                    };
                    if (!string.IsNullOrEmpty(card.CallId)) toolCardsByCallId[card.CallId!] = card;
                    yield return card;
                    break;

                case AgentEventType.ToolResult:
                    var tr = AgentEventPayload.Deserialize<AgentEventPayload.ToolResult>(e.Payload);
                    var cid = tr?.CallId;
                    if (!string.IsNullOrEmpty(cid) && toolCardsByCallId.TryGetValue(cid!, out var existing))
                    {
                        // 合并到前置 call 卡（恢复 CallId）——与实时单卡模型一致。
                        existing.Output = tr?.Output;
                        existing.IsToolCompleted = true;
                    }
                    else
                    {
                        // 无前置 call（不应发生）——单独 completed 工具卡。
                        yield return new ChatMessageItem
                        {
                            Id = Guid.NewGuid().ToString(),
                            ItemType = "tool",
                            Role = "assistant",
                            ToolName = tr?.Name ?? "tool",
                            CallId = cid,
                            Output = tr?.Output,
                            IsToolCompleted = true,
                            AuthorName = agentName,
                            Timestamp = e.CreatedAt.LocalDateTime
                        };
                    }
                    break;

                // plan/approval/error/usage：手机端不渲染，跳过（与桌面一致）。
                case AgentEventType.PlanUpdated:
                case AgentEventType.ApprovalRequested:
                case AgentEventType.ApprovalDecision:
                case AgentEventType.Error:
                case AgentEventType.Usage:
                    break;
            }
        }
    }

    public override Task OnDeactivatedAsync()
    {
        // 离开页面：取消正在进行的流式回复，避免 LLM 空跑浪费 token/电量
        _streamingCts?.Cancel();
        return base.OnDeactivatedAsync();
    }

    /// <summary>
    /// 股票新会话自动查询该股票，复用流式逻辑。
    /// </summary>
    private async Task AutoQueryStockAsync()
    {
        if (_agentRunner == null) return;

        var query = $"分析一下{StockName}";
        await StreamResponseAsync(query);
    }

    // —— 分段流式光标（镜像桌面 StreamCursor）：指向"当前正在追加 token 的消息项"；PendingTools 按 CallId 配对调用与结果。
    //    扁平 Messages 下 cursor.Current 指向 Messages 中的项；插入/移除须经 _uiThreadDispatcher 在 UI 线程。 ——
    private StreamCursor? _streamCursor;

    // —— Token 批量刷新（避免逐 token Post 到 UI 线程导致假死，镜像桌面 50ms 批量 flush）——
    private readonly StringBuilder _pendingTokens = new();
    private readonly StringBuilder _pendingReasoning = new();
    private Timer? _flushTimer;

    /// <summary>流式分段光标：指向当前正在追加 token 的消息项；PendingTools 按 CallId 配对调用与结果。</summary>
    private sealed class StreamCursor
    {
        public ChatMessageItem Current;
        public readonly Dictionary<string, ChatMessageItem> PendingTools = new();
        public StreamCursor(ChatMessageItem initial) => Current = initial;
    }

    private void EnsureFlushTimer()
    {
        _flushTimer ??= new Timer(_ => _uiThreadDispatcher?.Post(() =>
        {
            var cursor = _streamCursor;
            if (cursor is null) return;
            if (_pendingTokens.Length > 0)
            {
                EnsureTextSegment(cursor, Messages, AgentName, isReasoningContent: false);
                cursor.Current.Content += _pendingTokens.ToString();
                cursor.Current.MarkdownContent = cursor.Current.Content;
                _pendingTokens.Clear();
                StreamingContentUpdated?.Invoke();
            }
            if (_pendingReasoning.Length > 0)
            {
                EnsureTextSegment(cursor, Messages, AgentName, isReasoningContent: true);
                cursor.Current.Content += _pendingReasoning.ToString();
                _pendingReasoning.Clear();
            }
        }), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>新建一个文本段（答案/思考），Role=assistant、IsStreaming=true。isReasoningContent 区分 TextReasoningContent（思考）与 TextContent（答案）。</summary>
    private static ChatMessageItem NewTextSegment(string senderName, bool isReasoningContent) => new()
    {
        Id = Guid.NewGuid().ToString(),
        ItemType = "text",
        Role = "assistant",
        Content = string.Empty,
        IsStreaming = true,
        AuthorName = senderName,
        Timestamp = DateTime.Now,
        // 只有 TextReasoningContent（真·思考）才 IsReasoning=true；TextContent（可见叙述/答案）IsReasoning=false。
        IsReasoning = isReasoningContent,
        IsReasoningContent = isReasoningContent,
    };

    /// <summary>确保当前段是目标类型（TextContent 答案 vs TextReasoningContent 推理）的文本段；否则冻结/移除当前并新开一段。须在 UI 线程调用。</summary>
    private void EnsureTextSegment(StreamCursor cursor, IList<ChatMessageItem> items, string senderName, bool isReasoningContent)
    {
        if (cursor.Current.IsText && cursor.Current.IsReasoningContent == isReasoningContent) return;
        var idx = items.IndexOf(cursor.Current);
        if (cursor.Current.IsText && string.IsNullOrWhiteSpace(cursor.Current.Content))
        {
            items.RemoveAt(idx); // 空段不留
        }
        else
        {
            if (cursor.Current.IsText) cursor.Current.IsStreaming = false; // 冻结非空异类段
            idx++;
        }
        var seg = NewTextSegment(senderName, isReasoningContent);
        items.Insert(idx, seg);
        cursor.Current = seg;
    }

    /// <summary>工具事件处理（单卡原地 mutate，镜像桌面 HandleToolEventSegmented）：Call 冻结当前段并插一条工具步（带 CallId）；
    /// Result 按 CallId 回填到同一条，再开新答案段。审批恢复轮重发同 CallId 的 Call → 移除旧 orphan 卡再建。</summary>
    private void HandleToolEventSegmented(StreamCursor cursor, IList<ChatMessageItem> items, ToolStreamEvent ev, string senderName)
    {
        if (ev.Kind == ToolStreamEventKind.Call)
        {
            // 审批恢复轮 MAF 会重新发出已批准工具的 Call（同 CallId）。第一轮已建过同 CallId 的工具卡
            // （在审批卡之前），若不处理会留 orphan 卡 + 恢复轮再建一张 → 同一逻辑调用两张卡、显示错乱。
            // 移除旧 orphan，在当前位置（审批卡之后）新建一张，保证后续 Result 落在审批卡之后、时序正确。
            if (!string.IsNullOrEmpty(ev.CallId) && cursor.PendingTools.TryGetValue(ev.CallId, out var stale))
            {
                cursor.PendingTools.Remove(ev.CallId);
                items.Remove(stale);
            }
            var idx = items.IndexOf(cursor.Current);
            if (cursor.Current.IsText && string.IsNullOrWhiteSpace(cursor.Current.Content))
            {
                items.RemoveAt(idx); // 空段不留
            }
            else
            {
                if (cursor.Current.IsText) cursor.Current.IsStreaming = false; // 冻结非空段
                idx++;
            }
            var tool = new ChatMessageItem
            {
                Id = Guid.NewGuid().ToString(),
                ItemType = "tool",
                Role = "assistant",
                AuthorName = senderName,
                ToolName = ev.Name,
                CallId = ev.CallId,
                Input = ev.Input,
                Timestamp = DateTime.Now,
            };
            items.Insert(idx, tool);
            cursor.Current = tool;
            if (!string.IsNullOrEmpty(ev.CallId)) cursor.PendingTools[ev.CallId] = tool;
        }
        else // Result：回填到对应工具步（合并为一条），再开新答案段
        {
            ChatMessageItem? tool = null;
            if (!string.IsNullOrEmpty(ev.CallId) && cursor.PendingTools.TryGetValue(ev.CallId, out var t))
            {
                tool = t;
                cursor.PendingTools.Remove(ev.CallId);
            }
            else if (cursor.Current.IsToolStep)
            {
                tool = cursor.Current; // 退路：顺序配对
            }
            if (tool is null) return;

            tool.Output = ev.Output;
            tool.IsToolCompleted = true;

            var idx = items.IndexOf(tool);
            if (idx < 0) idx = items.Count - 1;
            var next = NewTextSegment(senderName, isReasoningContent: false);
            items.Insert(idx + 1, next);
            cursor.Current = next;
        }
    }

    /// <summary>运行结束：末段空则移除；否则停止流式。仅 TextContent 末段翻为最终回答（markdown 气泡），
    /// TextReasoningContent 末段保持思考卡。须在 UI 线程调用。</summary>
    private void FinalizeSegment(ChatMessageItem last, IList<ChatMessageItem> items)
    {
        if (!last.IsText) return;
        if (string.IsNullOrWhiteSpace(last.Content))
        {
            items.Remove(last);
        }
        else
        {
            last.IsStreaming = false;
            if (!last.IsReasoningContent)
            {
                last.IsReasoning = false; // 末段 TextContent = 最终回答
                last.MarkdownContent = last.Content; // 终态 markdown 对齐，避免流式末帧与最终气泡不一致
            }
        }
    }

    /// <summary>
    /// 流式回复核心逻辑（用户消息已添加后调用）。
    /// 镜像桌面 <c>RunSegmentedStreamAsync</c> 的分段模型：lazy-create conversation → RunStreamingAsync(分段回调) → persist。
    /// 分段交错：推理进思考段、答案进 markdown 气泡、工具单卡(call+result 合并)、审批卡 runtime-only。
    /// 协作式审批：stream 发 ToolApprovalRequestContent 后本轮结束 → 弹框拿 ToolApprovalDecision → 以 approvalResponse 重入同一会话恢复。
    /// </summary>
    private async Task StreamResponseAsync(string content)
    {
        if (_agentRunner == null || _conversationStore == null)
        {
            ErrorMessage = "服务未初始化";
            return;
        }

        if (_agentId == Guid.Empty)
        {
            ErrorMessage = $"未找到 Agent「{AgentName}」，请确认已创建";
            ShowInlineError($"未找到 Agent「{AgentName}」。该助手可能未在本机创建/同步，请在桌面端创建后同步，或重新选择助手。");
            return;
        }

        var version = _streamVersion;
        var justCreated = false;

        try
        {
            IsLoading = true;
            ErrorMessage = null;

            // 清空上一轮残留的 token 缓冲（会话切换/重发时旧缓冲不应流入新段）。
            _pendingTokens.Clear();
            _pendingReasoning.Clear();

            // 本地显示用户消息（右侧气泡）+ 开首个答案段（cursor 指向它）
            _uiThreadDispatcher?.Post(() =>
            {
                Messages.Add(new ChatMessageItem
                {
                    Id = Guid.NewGuid().ToString(),
                    ItemType = "text",
                    Role = "user",
                    Content = content,
                    Timestamp = DateTime.Now
                });
                _streamCursor = new StreamCursor(NewTextSegment(AgentName, isReasoningContent: false));
                Messages.Add(_streamCursor.Current);
            });

            // 启动后台保活（Android 前台服务 + WakeLock，iOS BeginBackgroundTask）
            if (_streamingBackgroundService != null)
                await _streamingBackgroundService.StartAsync("天机算筹", "AI正在思考...");

            // 取消上一次未完成的流式（会话切换/重发），新建本次取消令牌
            _streamingCts?.Cancel();
            _streamingCts = new CancellationTokenSource();
            var ct = _streamingCts.Token;

            // lazy-create conversation on first send（镜像桌面）
            if (_currentConversationId == Guid.Empty)
            {
                justCreated = true;
                _currentConversationId = Guid.NewGuid();
                var conv = new Conversation
                {
                    ConversationId = _currentConversationId,
                    AgentId = _agentId.ToString(),
                    AgentName = AgentName,
                    Title = content.Length > 20 ? content[..20] + "…" : content,
                };
                // 用户消息不再挂在 Conversation 上——它随 AgentRun.UserMessage 在本轮结束时持久化。
                // 首轮占位标题即时显示到 header（AI 生成后由 GenerateAndApplyTitleAsync 替换）
                CurrentTitle = conv.Title;
                await _conversationStore.SaveConversationAsync(conv, ct);

                // 通知聊天列表显示新会话
                PublishNewAgentConversation(
                    IsStockMode ? 4 : 3,
                    IsStockMode ? $"{StockName} · {AgentName}" : AgentName,
                    IsStockMode ? $"stock:{StockId}:{StockName}" : null);
            }

            // 准备批量刷新定时器 + 答案全文（标题生成用）
            EnsureFlushTimer();
            var fullText = new StringBuilder();
            var firstTokenReceived = false;

            // 构建 StreamHandlers（回调在后台线程执行，UI 更新经 _uiThreadDispatcher.Post 调度）
            var handlers = new StreamHandlers(
                OnToken: (token, _) =>
                {
                    if (_streamVersion != version) return Task.CompletedTask;
                    fullText.Append(token);
                    _pendingTokens.Append(token);
                    _flushTimer?.Change(50, Timeout.Infinite);
                    if (!firstTokenReceived)
                    {
                        firstTokenReceived = true;
                        _streamingBackgroundService?.UpdateMessage("AI正在回复...");
                    }
                    return Task.CompletedTask;
                },
                OnReasoning: (text, _) =>
                {
                    if (_streamVersion != version) return Task.CompletedTask;
                    _pendingReasoning.Append(text);
                    _flushTimer?.Change(50, Timeout.Infinite);
                    return Task.CompletedTask;
                },
                OnToolEvent: (ev, _) =>
                {
                    if (_streamVersion != version) return Task.CompletedTask;
                    // todos_* 调用由 OnTodoList 处理（手机端暂不渲染计划），不重复出工具卡。
                    if (ev.Name.StartsWith("todo", StringComparison.OrdinalIgnoreCase))
                        return Task.CompletedTask;
                    var capturedEv = ev;
                    _uiThreadDispatcher?.Post(() =>
                    {
                        if (_streamCursor is null) return;
                        HandleToolEventSegmented(_streamCursor, Messages, capturedEv, AgentName);
                    });
                    return Task.CompletedTask;
                },
                OnApprovalRequest: (req, _) =>
                {
                    if (_streamVersion != version) return Task.CompletedTask;
                    // 捕获待审批请求——审批循环（StreamResponseAsync 内）在 RunStreamingAsync 返回后读取。
                    // 协作式审批：stream 发出 ToolApprovalRequestContent 后本轮结束，host 弹框拿决定，
                    // 以 approvalResponse 重入同一会话恢复流（镜像桌面 RunSegmentedStreamAsync）。
                    _pendingApproval = req;
                    var capturedReq = req;
                    _uiThreadDispatcher?.Post(() =>
                    {
                        var cursor = _streamCursor;
                        if (cursor is null) return;
                        // 收尾当前段（冻结），插审批卡（runtime-only，不持久化）。
                        var idx = Messages.IndexOf(cursor.Current);
                        if (idx < 0) idx = Messages.Count - 1;
                        else if (cursor.Current.IsText && string.IsNullOrWhiteSpace(cursor.Current.Content))
                        {
                            Messages.RemoveAt(idx); // 空段不留
                        }
                        else
                        {
                            if (cursor.Current.IsText) cursor.Current.IsStreaming = false;
                            idx++;
                        }
                        var card = new ChatMessageItem
                        {
                            Id = Guid.NewGuid().ToString(),
                            ItemType = "approval",
                            Role = "assistant",
                            ToolName = capturedReq.ToolName,
                            CallId = capturedReq.CallId,
                            Input = capturedReq.Input,
                            ApprovalState = 0, // 等待
                            Content = $"⏳ 工具「{capturedReq.ToolName}」等待审批",
                            AuthorName = AgentName,
                            Timestamp = DateTime.Now
                        };
                        Messages.Insert(idx, card);
                        cursor.Current = card;
                    });
                    return Task.CompletedTask;
                },
                OnTodoList: null, // 手机端暂不渲染计划卡（TODO）
                OnError: (text, _) =>
                {
                    if (_streamVersion != version) return Task.CompletedTask;
                    _uiThreadDispatcher?.Post(() =>
                    {
                        if (_streamCursor is null) return;
                        EnsureTextSegment(_streamCursor, Messages, AgentName, isReasoningContent: false);
                        _streamCursor.Current.Content += $"\n\n[错误：{text}]";
                    });
                    return Task.CompletedTask;
                },
                OnUsage: info =>
                {
                    if (_streamVersion != version) return;
                    _uiThreadDispatcher?.Post(() => SessionTokenInfo = info);
                });

            // 在后台线程执行（避免 Android NetworkOnMainThreadException）
            await Task.Run(async () =>
            {
                // 协作式工具审批循环（镜像桌面 RunSegmentedStreamAsync）：RunStreamingAsync 遇需审批工具
                // → 发 ToolApprovalRequestContent 后本轮结束 → 弹框拿决定 → 以 approvalResponse 重入同一会话恢复。
                // desiredMode: 手机端无 plan/execute UI，强制 execute（自主执行）。
                ToolApprovalDecision? approvalResponse = null;
                while (true)
                {
                    _pendingApproval = null; // OnApprovalRequest 在 RunStreamingAsync 期间置位
                    await _agentRunner.RunStreamingAsync(
                        _currentConversationId, _agentId, content, handlers,
                        approvalResponse, desiredMode: "execute", ct);

                    if (_streamVersion != version) return;
                    if (_pendingApproval is null) break; // 无审批请求 → 本轮完成
                    if (_approvalDialog is null) break;  // 无审批 UI → 终止（防卡死）

                    var req = _pendingApproval;
                    // 弹审批框（ShowApprovalAsync 内部 marshal 到 UI 线程）；用户取消/Dismiss 返回 null
                    var decision = await _approvalDialog.ShowApprovalAsync(req).ConfigureAwait(false);
                    var state = decision is null ? 2 : (decision.Approved ? 1 : 2);
                    var approved = decision is not null && decision.Approved;
                    _uiThreadDispatcher?.Post(() =>
                    {
                        // 更新审批卡状态（按 CallId 定位）
                        for (int i = 0; i < Messages.Count; i++)
                        {
                            if (Messages[i].IsApprovalCard && req.CallId == Messages[i].CallId)
                            {
                                Messages[i].ApprovalState = state;
                                Messages[i].Content = state == 1
                                    ? $"✅ 工具「{req.ToolName}」已批准"
                                    : $"❌ 工具「{req.ToolName}」已拒绝";
                                break;
                            }
                        }
                        // 开新答案段承接恢复流（镜像桌面 289-294）
                        if (decision is not null && _streamCursor is not null)
                        {
                            var seg = NewTextSegment(AgentName, isReasoningContent: false);
                            var idx = Messages.IndexOf(_streamCursor.Current);
                            if (idx < 0) idx = Messages.Count - 1;
                            else idx++;
                            Messages.Insert(idx, seg);
                            _streamCursor.Current = seg;
                        }
                    });
                    if (decision is null) break; // 取消 → 终止本轮（不恢复）
                    approvalResponse = decision;
                }
            }, ct);

            if (_streamVersion != version) return;

            // 停止定时器 + drain 残留 token 到段 + finalize 末段（此处已在 UI 线程，直接操作 Messages）。
            _flushTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            FinalizeStreaming();

            // 持久化本轮 transcript（思考 / 工具 / 文本，与实时流式一致）
            await PersistRunAsync(content, ct);

            // 首轮 lazy-create 的会话：异步用 LLM 生成简短标题，回写会话并通知 UI 刷新抽屉列表项。
            // 镜像桌面 ChatWorkspaceViewModel.GenerateAndApplyTitleAsync（justCreated 后 fire-and-forget）。
            if (justCreated)
                _ = GenerateAndApplyTitleAsync(content, fullText.ToString());
        }
        catch (OperationCanceledException)
        {
            // 会话切换/离开页面主动取消流式，静默处理
        }
        catch (Exception ex)
        {
            if (_streamVersion != version) return;
            ErrorMessage = $"发送失败: {ex.Message}";
            System.Diagnostics.Debug.WriteLine($"[AgentChatDetail] 发送失败: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            // 就地把异常显示到对话流（不只依赖顶部 ErrorMessage），便于定位工具调用后断流的真因
            _uiThreadDispatcher?.Post(() =>
            {
                Messages.Add(new ChatMessageItem
                {
                    Id = Guid.NewGuid().ToString(),
                    ItemType = "text",
                    Role = "assistant",
                    Content = $"❌ 发送失败：{ex.GetType().Name}：{ex.Message}",
                    AuthorName = AgentName,
                    Timestamp = DateTime.Now
                });
            });
        }
        finally
        {
            IsLoading = false;
            // 始终停止后台保活（finally 确保不泄漏前台服务）
            if (_streamingBackgroundService != null)
                await _streamingBackgroundService.StopAsync();
        }
    }

    /// <summary>运行结束收尾：drain 残留 token/reasoning 到当前段，finalize 末段，清空 cursor。须在 UI 线程调用。</summary>
    private void FinalizeStreaming()
    {
        var cursor = _streamCursor;
        if (cursor is null) return;

        if (_pendingTokens.Length > 0)
        {
            EnsureTextSegment(cursor, Messages, AgentName, isReasoningContent: false);
            cursor.Current.Content += _pendingTokens.ToString();
            cursor.Current.MarkdownContent = cursor.Current.Content;
            _pendingTokens.Clear();
            StreamingContentUpdated?.Invoke();
        }
        if (_pendingReasoning.Length > 0)
        {
            EnsureTextSegment(cursor, Messages, AgentName, isReasoningContent: true);
            cursor.Current.Content += _pendingReasoning.ToString();
            _pendingReasoning.Clear();
        }

        FinalizeSegment(cursor.Current, Messages);
        _streamCursor = null;
    }

    /// <summary>
    /// 持久化本轮 transcript 到 <see cref="IConversationStore"/>（镜像桌面 PersistRunAsync，但走事件流模型）。
    /// 用户请求作为 <see cref="AgentRun.UserMessage"/>；本轮助手侧过程（思考 / 工具调用+结果 / 文本旁白 / 最终回答）
    /// 按 <see cref="AgentEvent"/> 顺序写入，使查看历史与实时流式一致。工具卡用真实 <see cref="ChatMessageItem.CallId"/>
    /// 配对（CallId 在事件写入时即匹配→结构不变量由构造保证）；空 CallId（异常情况）回退 <c>toolu_mobile_{n}</c> 合成。
    /// 审批卡 runtime-only、不持久化（持久化会污染 LLM 历史→续聊 400/工具重执）。
    /// </summary>
    private async Task PersistRunAsync(string userContent, CancellationToken ct)
    {
        if (_currentConversationId == Guid.Empty || _conversationStore == null) return;
        try
        {
            var conv = await _conversationStore.GetConversationAsync(_currentConversationId, ct);
            if (conv == null) return;

            // 计算下一个 run 序号（按已有 run 的最大 seq + 1）。
            var runs = await _conversationStore.GetRunsAsync(_currentConversationId, ct);
            var seq = runs.Count == 0 ? 1 : runs.Max(r => r.Seq) + 1;

            var turnItems = CollectCurrentTurnAssistantItems();
            var hasAnswer = turnItems.Any(i => i.IsText && !i.IsReasoning && !string.IsNullOrWhiteSpace(i.Content));
            var now = DateTimeOffset.UtcNow;
            var run = new AgentRun
            {
                ConversationId = _currentConversationId,
                Seq = seq,
                Status = hasAnswer ? RunStatus.Completed : RunStatus.Interrupted,
                AgentId = conv.AgentId,
                AgentName = conv.AgentName,
                UserMessage = userContent,
                StartedAt = now,
                FinishedAt = now,
            };

            // 事件映射对齐桌面 PersistRunAsync：用户消息已在 run.UserMessage；审批卡 runtime-only 不持久化；
            // 跳过空思考段；工具单卡双事件（ToolCall + IsToolCompleted 时追加 ToolResult，CallId 写入即配对）。
            var events = new List<AgentEvent>();
            var eventSeq = 1;
            int toolCounter = 0; // 兜底：异常空 CallId 时合成，正常路径卡上已有真实 CallId
            foreach (var item in turnItems)
            {
                // 用户消息已在 run.UserMessage；审批卡运行期态不持久化；跳过空思考段。
                if (item.IsUser || item.IsApprovalCard) continue;
                if (item.IsText && item.IsReasoning && string.IsNullOrWhiteSpace(item.Content)) continue;

                if (item.IsToolStep)
                {
                    var callId = !string.IsNullOrEmpty(item.CallId) ? item.CallId! : $"toolu_mobile_{++toolCounter}";
                    events.Add(MakeEvent(run.Id, ref eventSeq, AgentEventType.ToolCall,
                        new AgentEventPayload.ToolCall(callId, item.ToolName ?? "tool", item.Input)));
                    // 未完成的工具步（中断）只存 call，无 result——重建时为孤儿 call，LLM 历史重建时丢弃。
                    if (item.IsToolCompleted)
                        events.Add(MakeEvent(run.Id, ref eventSeq, AgentEventType.ToolResult,
                            new AgentEventPayload.ToolResult(callId, item.ToolName ?? "tool", item.Output)));
                }
                else if (item.IsReasoning)
                    events.Add(MakeEvent(run.Id, ref eventSeq, AgentEventType.ReasoningChunk,
                        new AgentEventPayload.ReasoningChunk(item.Content ?? string.Empty)));
                else
                    events.Add(MakeEvent(run.Id, ref eventSeq, AgentEventType.TextChunk,
                        new AgentEventPayload.TextChunk(item.Content ?? string.Empty)));
            }

            await _conversationStore.AppendRunAsync(run, events, ct);
        }
        catch (Exception ex)
        {
            // 持久化失败不阻塞对话
            System.Diagnostics.Debug.WriteLine($"[AgentChatDetail] 持久化失败: {ex.Message}");
        }
    }

    /// <summary>从 <see cref="Messages"/> 取本轮用户消息之后的所有助手项（思考/工具/文本），按显示顺序持久化。</summary>
    private List<ChatMessageItem> CollectCurrentTurnAssistantItems()
    {
        var result = new List<ChatMessageItem>();
        int userIdx = -1;
        for (int i = Messages.Count - 1; i >= 0; i--)
        {
            if (Messages[i].Role == "user" && Messages[i].ItemType == "text")
            {
                userIdx = i;
                break;
            }
        }
        if (userIdx < 0) return result;
        for (int i = userIdx + 1; i < Messages.Count; i++)
            result.Add(Messages[i]);
        return result;
    }

    /// <summary>构造一个 <see cref="AgentEvent"/> 并自增 seq（镜像桌面 MakeEvent）。</summary>
    private static AgentEvent MakeEvent(Guid runId, ref int seq, AgentEventType type, object? payload)
    {
        return new AgentEvent
        {
            RunId = runId,
            Seq = seq++,
            EventType = type,
            Payload = AgentEventPayload.Serialize(payload),
        };
    }

    private void PublishNewAgentConversation(int type, string name, string? context)
    {
        var conversation = new Acme.ChatSocial.Domain.Conversation
        {
            Id = _currentConversationId,
            Type = type,
            Name = name,
            Context = context,
            AgentName = AgentName,
            LastMessageTime = DateTime.UtcNow
        };
        _eventBusService?.Publish(new NewConversationEvent(conversation));
    }

    /// <summary>
    /// 首轮对话后用 LLM 生成简短标题，回写 <see cref="IConversationStore"/> 中的会话标题，
    /// 并发 <see cref="ConversationTitleUpdatedEvent"/> 通知 AI tab 抽屉列表就地刷新显示名
    /// （lazy-create 时标题先用 agent 名占位，生成完后替换）。失败静默，不影响对话。
    /// 镜像桌面 <c>ChatWorkspaceViewModel.GenerateAndApplyTitleAsync</c>。
    /// </summary>
    private async Task GenerateAndApplyTitleAsync(string userMessage, string assistantReply)
    {
        if (_titleService == null || _conversationStore == null || _currentConversationId == Guid.Empty)
            return;

        try
        {
            var title = await _titleService.GenerateTitleAsync(userMessage, assistantReply);
            if (string.IsNullOrWhiteSpace(title)) return;

            var conv = await _conversationStore.GetConversationAsync(_currentConversationId, default);
            if (conv == null) return;

            conv.Title = title;
            await _conversationStore.SaveConversationAsync(conv, default);

            // 即时刷新 header 标题（此方法在后台线程 fire-and-forget，UI 属性经 dispatcher 调度）
            _uiThreadDispatcher?.Post(() => CurrentTitle = title);
            _eventBusService?.Publish(new ConversationTitleUpdatedEvent(_currentConversationId, title));
        }
        catch (Exception ex)
        {
            // 标题生成失败不影响主流程
            System.Diagnostics.Debug.WriteLine($"[AgentChatDetail] 生成标题失败: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task CopyMessageAsync(string? content)
    {
        if (string.IsNullOrEmpty(content)) return;

        try
        {
            await _clipboardService!.SetTextAsync(content);
            await _toastService!.ShowAsync("已复制");
        }
        catch (Exception ex)
        {
            ErrorMessage = $"复制失败: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task CopyItemAsync(ChatMessageItem? item)
    {
        if (item == null || string.IsNullOrEmpty(item.CopyContent)) return;

        try
        {
            await _clipboardService!.SetTextAsync(item.CopyContent);
            await _toastService!.ShowAsync("已复制");
        }
        catch (Exception ex)
        {
            ErrorMessage = $"复制失败: {ex.Message}";
        }
    }

    private static string FormatTokenInfo(int total)
    {
        if (total == 0) return string.Empty;
        if (total >= 1000) return $"📊 {total / 1000.0:F1}k tokens";
        return $"📊 {total} tokens";
    }

    /// <summary>
    /// 切换语音识别状态（点击麦克风按钮或录音面板圆球时调用）
    /// </summary>
    [RelayCommand]
    private async Task ToggleListeningAsync()
    {
        if (IsListening)
        {
            // 停止录音
            _voiceCts?.Cancel();
            return;
        }

        await StartListeningCoreAsync();
    }

    /// <summary>
    /// 切换输入模式：语音模式 ↔ 文字模式
    /// </summary>
    [RelayCommand]
    private void ToggleInputMode()
    {
        IsVoiceMode = !IsVoiceMode;
    }

    /// <summary>
    /// 启动语音识别（按住说话触发，仅开始不停止）
    /// </summary>
    [RelayCommand]
    private async Task StartVoiceAsync()
    {
        if (IsListening) return;
        await StartListeningCoreAsync();
    }

    /// <summary>
    /// 语音识别核心逻辑
    /// </summary>
    private async Task StartListeningCoreAsync()
    {
        if (_speechRecognitionService == null) return;

        var granted = await _speechRecognitionService.RequestPermissionAsync();
        if (!granted)
        {
            _toastService?.ShowAsync("需要麦克风权限才能使用语音输入", 2000);
            return;
        }

        IsListening = true;
        _voiceCts = new CancellationTokenSource();
        var ct = _voiceCts.Token;

        try
        {
            var result = await _speechRecognitionService.ListenAsync(partial =>
            {
                _uiThreadDispatcher?.Post(() =>
                {
                    MessageText = partial;
                });
            }, ct);

            if (!string.IsNullOrWhiteSpace(result))
            {
                _uiThreadDispatcher?.Post(() =>
                {
                    MessageText = result;
                });
            }
        }
        catch (OperationCanceledException)
        {
            // 用户点击停止，正常行为
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AgentChatDetail] 语音识别失败: {ex.Message}");
        }
        finally
        {
            IsListening = false;
            _voiceCts?.Dispose();
            _voiceCts = null;
        }
    }

    /// <summary>
    /// 停止语音识别并发送（或取消）。由平台层在松开手指时调用。
    /// slideCanceled: 是否因上滑而取消
    /// </summary>
    public async Task StopVoiceAndSendAsync(bool slideCanceled)
    {
        if (!IsListening) return;

        // 停止录音
        _voiceCts?.Cancel();

        // 等待识别结束
        await Task.Delay(300);

        if (slideCanceled)
        {
            // 上滑取消：清空识别文字
            MessageText = string.Empty;
        }
        else if (!string.IsNullOrWhiteSpace(MessageText))
        {
            // 非取消：自动发送
            if (SendMessageCommand.CanExecute(null))
                await SendMessageCommand.ExecuteAsync(null);
        }
    }

    private async Task SendMessageAsync()
    {
        if (_agentRunner == null)
        {
            ErrorMessage = "服务未初始化";
            System.Diagnostics.Debug.WriteLine($"[AgentChatDetail] SendMessageAsync: _agentRunner is null");
            ShowInlineError("服务未初始化，无法发送。请重启 App 或检查登录状态。");
            return;
        }

        if (string.IsNullOrWhiteSpace(MessageText))
            return;

        var content = MessageText;
        MessageText = string.Empty;

        await StreamResponseAsync(content);
    }

    /// <summary>在对话流里显示一条错误提示（替代静默 return，便于定位发送失败的真因）。</summary>
    private void ShowInlineError(string message)
    {
        _uiThreadDispatcher?.Post(() =>
        {
            Messages.Add(new ChatMessageItem
            {
                Id = Guid.NewGuid().ToString(),
                ItemType = "text",
                Role = "assistant",
                Content = $"❌ {message}",
                AuthorName = AgentName,
                Timestamp = DateTime.Now
            });
        });
    }
}
