using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Acme.Agents.AI;
using Acme.Notes.Domain;
using Acme.Notes.Domain.Agents;
using Aether.AI.Core.Agents;
using Aether.AI.Core.Conversations;
using Aether.AI.Core.Messages;
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
                                    IConversationTitleService? titleService = null)
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
            var conv = await _conversationStore.GetAsync(_currentConversationId, default);
            if (_streamVersion != version) return;

            _uiThreadDispatcher?.Post(() =>
            {
                Messages.Clear();
                SessionInputTokens = 0;
                SessionOutputTokens = 0;
                if (conv != null)
                {
                    foreach (var msg in conv.Messages)
                    {
                        foreach (var item in ExpandMessage(msg))
                        {
                            Messages.Add(item);
                        }
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
    /// 将 Aether Message 映射为 ChatMessageItem（用于恢复历史消息）。
    /// 与实时流式保持一致：思考段→思考卡、工具步→tool_call+tool_result 两张卡、文本→气泡。
    /// </summary>
    private IEnumerable<ChatMessageItem> ExpandMessage(Message msg)
    {
        var role = msg.Role.ToString().ToLowerInvariant();
        var timestamp = msg.CreatedAt.LocalDateTime;

        if (role == "user")
        {
            yield return new ChatMessageItem
            {
                Id = Guid.NewGuid().ToString(),
                ItemType = "text",
                Role = "user",
                Content = msg.Content,
                Timestamp = timestamp
            };
            yield break;
        }

        var kind = msg.Metadata?.TryGetValue("kind", out var k) == true ? k : null;

        // 思考段（kind=reasoning）—— 还原为思考卡，与实时流式一致。
        // LLM 历史经 TranscriptHistoryConverter 跳过 reasoning（不入续轮上下文），故持久化安全。
        if (kind == "reasoning")
        {
            if (!string.IsNullOrEmpty(msg.Content))
            {
                yield return new ChatMessageItem
                {
                    Id = Guid.NewGuid().ToString(),
                    ItemType = "thinking",
                    Role = "assistant",
                    Content = msg.Content,
                    AuthorName = AgentName,
                    Timestamp = timestamp
                };
            }
            yield break;
        }

        // 计划卡（kind=plan）—— 手机端暂不渲染计划卡，跳过
        if (kind == "plan") yield break;

        // 工具步（Role=Tool / kind=tool，持久化时合并了 call+result）→ 还原为 tool_call + tool_result 两张卡
        if (role == "tool" || kind == "tool")
        {
            var input = ParseMetaJson(msg.Metadata, "input");
            var output = ParseMetaJson(msg.Metadata, "output");
            var toolName = msg.ToolName ?? "tool";
            yield return new ChatMessageItem
            {
                Id = Guid.NewGuid().ToString(),
                ItemType = "tool_call",
                Role = "assistant",
                ToolName = toolName,
                Input = input,
                AuthorName = AgentName,
                Timestamp = timestamp
            };
            yield return new ChatMessageItem
            {
                Id = Guid.NewGuid().ToString(),
                ItemType = "tool_result",
                Role = "assistant",
                ToolName = toolName,
                Output = output,
                AuthorName = AgentName,
                Timestamp = timestamp
            };
            yield break;
        }

        // 普通助手文本
        if (!string.IsNullOrEmpty(msg.Content))
        {
            yield return new ChatMessageItem
            {
                Id = Guid.NewGuid().ToString(),
                ItemType = "text",
                Role = "assistant",
                Content = msg.Content,
                MarkdownContent = msg.Content,
                AuthorName = AgentName,
                Timestamp = timestamp
            };
        }
    }

    /// <summary>从 Message.Metadata 解析指定 key 的 JSON 为字典（工具 input/output 还原用）。</summary>
    private static Dictionary<string, object>? ParseMetaJson(IReadOnlyDictionary<string, string>? meta, string key)
    {
        if (meta == null || !meta.TryGetValue(key, out var json) || string.IsNullOrEmpty(json)) return null;
        try { return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(json); }
        catch { return null; }
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

    // —— 流式回复的 UI 状态（必须在 UI 线程访问）——
    private ChatMessageItem? _currentTextItem;
    private ChatMessageItem? _currentThinkingItem;

    // —— Token 批量刷新（避免逐 token Post 到 UI 线程导致假死，镜像桌面 50ms 批量 flush）——
    private readonly StringBuilder _pendingTokens = new();
    private readonly StringBuilder _pendingReasoning = new();
    private Timer? _flushTimer;

    private void EnsureFlushTimer()
    {
        _flushTimer ??= new Timer(_ => _uiThreadDispatcher?.Post(() =>
        {
            // 直接读 _streamVersion（而非闭包捕获），确保会话切换后新会话的 flush 不被旧版本号拦截
            if (_pendingTokens.Length > 0)
            {
                EnsureTextItem();
                _currentTextItem!.Content += _pendingTokens.ToString();
                _currentTextItem.MarkdownContent = _currentTextItem.Content;
                _pendingTokens.Clear();
                StreamingContentUpdated?.Invoke();
            }
            if (_pendingReasoning.Length > 0)
            {
                EnsureThinkingItem();
                _currentThinkingItem!.Content += _pendingReasoning.ToString();
                _pendingReasoning.Clear();
            }
        }), null, Timeout.Infinite, Timeout.Infinite);
    }

    private void EnsureTextItem()
    {
        if (_currentTextItem != null) return;
        _currentTextItem = new ChatMessageItem
        {
            Id = Guid.NewGuid().ToString(),
            ItemType = "text",
            Role = "assistant",
            Content = string.Empty,
            IsStreaming = true,
            AuthorName = AgentName,
            Timestamp = DateTime.Now
        };
        Messages.Add(_currentTextItem);
    }

    private void EnsureThinkingItem()
    {
        if (_currentThinkingItem != null) return;
        _currentThinkingItem = new ChatMessageItem
        {
            Id = $"thinking_{Guid.NewGuid()}",
            ItemType = "thinking",
            Role = "assistant",
            AuthorName = AgentName,
            Timestamp = DateTime.Now
        };
        Messages.Add(_currentThinkingItem);
    }

    /// <summary>
    /// 流式回复核心逻辑（用户消息已添加后调用）。
    /// 镜像桌面 MainViewModelBase：lazy-create conversation → RunStreamingAsync(callbacks) → persist。
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

            // 本地显示用户消息（右侧气泡）
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
                _currentTextItem = null;
                _currentThinkingItem = null;
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
                conv.Append(Message.User(content));
                // 首轮占位标题即时显示到 header（AI 生成后由 GenerateAndApplyTitleAsync 替换）
                CurrentTitle = conv.Title;
                await _conversationStore.SaveAsync(conv, ct);

                // 通知聊天列表显示新会话
                PublishNewAgentConversation(
                    IsStockMode ? 4 : 3,
                    IsStockMode ? $"{StockName} · {AgentName}" : AgentName,
                    IsStockMode ? $"stock:{StockId}:{StockName}" : null);
            }

            // 准备批量刷新定时器
            EnsureFlushTimer();
            var fullText = new StringBuilder();

            // 构建 StreamHandlers（回调在后台线程执行，UI 更新经 _uiThreadDispatcher.Post 调度）
            var firstTokenReceived = false;
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
                    // todos_* 调用由 OnTodoList 处理，不重复出工具卡
                    if (ev.Name.StartsWith("todo", StringComparison.OrdinalIgnoreCase))
                        return Task.CompletedTask;
                    var capturedEv = ev;
                    _uiThreadDispatcher?.Post(() => ProcessToolEvent(capturedEv));
                    return Task.CompletedTask;
                },
                OnApprovalRequest: (req, _) =>
                {
                    if (_streamVersion != version) return Task.CompletedTask;
                    // 手机端无工具审批 UI：明确提示而非静默断流。
                    // 工具审批由 Agent 的 RequireToolApproval 触发；如需手机端执行工具，
                    // 请在桌面专家编辑器关闭该 Agent 的「工具审批」开关。
                    _uiThreadDispatcher?.Post(() =>
                    {
                        Messages.Add(new ChatMessageItem
                        {
                            Id = Guid.NewGuid().ToString(),
                            ItemType = "text",
                            Role = "assistant",
                            Content = $"⚠️ 工具「{req.ToolName}」需要审批，手机端暂不支持审批交互。请在桌面端专家编辑器关闭该 Agent 的工具审批后重试。",
                            AuthorName = AgentName,
                            Timestamp = DateTime.Now
                        });
                    });
                    return Task.CompletedTask;
                },
                OnTodoList: null, // 手机端暂不渲染计划卡（TODO）
                OnError: (text, _) =>
                {
                    if (_streamVersion != version) return Task.CompletedTask;
                    _uiThreadDispatcher?.Post(() =>
                    {
                        EnsureTextItem();
                        _currentTextItem!.Content += $"\n\n[错误：{text}]";
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
                // desiredMode: 手机端无 plan/execute UI，强制 execute（自主执行），
                // 否则启用了 AgentModeProvider 的 Agent 会停在默认 plan 模式等「接受计划」而断流。
                // Agent 未启用 AgentModeProvider 时 TrySetModeAsync 为 no-op，无副作用。
                await _agentRunner.RunStreamingAsync(
                    _currentConversationId, _agentId, content, handlers,
                    approvalResponse: null, desiredMode: "execute", ct);
            }, ct);

            if (_streamVersion != version) return;

            // 刷新剩余未 flush 的 token 到 UI（此处已在 UI 线程，直接调 FinalizeStreaming，
            // 确保最终文本段已落地 Messages，随后持久化读取的 transcript 完整）
            _flushTimer?.Change(0, Timeout.Infinite);
            FinalizeStreaming();

            // 持久化本轮 transcript（思考 / 工具 / 文本，与实时流式一致）
            await PersistAssistantReplyAsync(content, ct);

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

    /// <summary>
    /// 处理工具事件（Call/Result），在 UI 线程调用。
    /// </summary>
    private void ProcessToolEvent(ToolStreamEvent ev)
    {
        // 注意：此方法必须在 UI 线程调用（由 StreamHandlers 回调经 _uiThreadDispatcher.Post 调度）
        if (ev.Kind == ToolStreamEventKind.Call)
        {
            // 工具调用：终结当前文本段，添加 tool_call 卡片 + thinking 占位
            if (_currentTextItem != null)
            {
                _currentTextItem.MarkdownContent = _currentTextItem.Content;
                _currentTextItem.IsStreaming = false;
                _currentTextItem = null;
            }
            // 清掉残留的 thinking（来自 OnReasoning）
            if (_currentThinkingItem != null)
            {
                Messages.Remove(_currentThinkingItem);
                _currentThinkingItem = null;
            }
            // 清掉待 flush 的 reasoning
            _pendingReasoning.Clear();

            Messages.Add(new ChatMessageItem
            {
                Id = Guid.NewGuid().ToString(),
                ItemType = "tool_call",
                Role = "assistant",
                ToolName = ev.Name,
                Input = ev.Input,
                AuthorName = AgentName,
                Timestamp = DateTime.Now
            });
            _currentThinkingItem = new ChatMessageItem
            {
                Id = $"thinking_{ev.Name}",
                ItemType = "thinking",
                Role = "assistant",
                AuthorName = AgentName,
                Timestamp = DateTime.Now
            };
            Messages.Add(_currentThinkingItem);
        }
        else // Result
        {
            // 工具结果：移除 thinking 占位，终结当前文本段，添加 tool_result 卡片
            if (_currentThinkingItem != null)
            {
                Messages.Remove(_currentThinkingItem);
                _currentThinkingItem = null;
            }
            if (_currentTextItem != null)
            {
                _currentTextItem.MarkdownContent = _currentTextItem.Content;
                _currentTextItem.IsStreaming = false;
                _currentTextItem = null;
            }

            Messages.Add(new ChatMessageItem
            {
                Id = Guid.NewGuid().ToString(),
                ItemType = "tool_result",
                Role = "assistant",
                ToolName = ev.Name,
                Output = ev.Output,
                AuthorName = AgentName,
                Timestamp = DateTime.Now
            });
        }
    }

    private void FinalizeStreaming()
    {
        // 注意：此方法必须在 UI 线程调用
        // 先 flush 残留 token
        if (_pendingTokens.Length > 0)
        {
            EnsureTextItem();
            _currentTextItem!.Content += _pendingTokens.ToString();
            _currentTextItem.MarkdownContent = _currentTextItem.Content;
            _pendingTokens.Clear();
            StreamingContentUpdated?.Invoke();
        }

        if (_currentThinkingItem != null)
        {
            // 有内容的 reasoning 保留为可折叠思考泡；空的移除
            if (string.IsNullOrEmpty(_currentThinkingItem.Content))
            {
                Messages.Remove(_currentThinkingItem);
            }
            _currentThinkingItem = null;
        }

        if (_currentTextItem != null)
        {
            _currentTextItem.MarkdownContent = _currentTextItem.Content;
            _currentTextItem.IsStreaming = false;
            _currentTextItem = null;
        }
    }

    /// <summary>
    /// 持久化本轮 transcript 到 IConversationStore（镜像桌面 PersistTurnAsync）。
    /// 首轮 user 消息已在 lazy-create 时追加；非首轮在此补回。
    /// 本轮中间过程（思考 / 工具调用+结果 / 文本旁白 / 最终回答）按显示顺序持久化，使查看历史与实时流式一致。
    /// </summary>
    private async Task PersistAssistantReplyAsync(string userContent, CancellationToken ct)
    {
        if (_currentConversationId == Guid.Empty || _conversationStore == null) return;
        try
        {
            var conv = await _conversationStore.GetAsync(_currentConversationId, ct);
            if (conv == null) return;

            // 非首轮补回 user 消息（首轮已在 lazy-create 时追加）
            var last = conv.Messages.Count > 0 ? conv.Messages[^1] : null;
            if (last is null || last.Role != MessageRole.User || last.Content != userContent)
                conv.Append(Message.User(userContent));

            // 持久化本轮助手侧 transcript（思考 / 工具 / 文本）。工具 call+result 合并为单条 Role=Tool 消息
            // （input+output），TranscriptHistoryConverter 还原为 FunctionCall+FunctionResult 配对并经 Sanitize
            // 兜底——续轮不会触发 400。reasoning 段 converter 跳过不入 LLM 历史，仅用于 UI 还原。
            var turnItems = CollectCurrentTurnAssistantItems();
            ChatMessageItem? pendingToolCall = null;
            foreach (var item in turnItems)
            {
                switch (item.ItemType)
                {
                    case "thinking":
                        if (!string.IsNullOrEmpty(item.Content))
                            conv.Append(new Message
                            {
                                Role = MessageRole.Assistant,
                                Content = item.Content,
                                Metadata = new Dictionary<string, string> { ["kind"] = "reasoning" }
                            });
                        break;
                    case "tool_call":
                        pendingToolCall = item; // 等配对的 tool_result 合并落库
                        break;
                    case "tool_result":
                        conv.Append(ToToolMessage(pendingToolCall, item));
                        pendingToolCall = null;
                        break;
                    default: // text（旁白 / 最终回答）
                        if (!string.IsNullOrWhiteSpace(item.Content))
                            conv.Append(Message.Assistant(item.Content));
                        break;
                }
            }
            // 残留未配对的 tool_call（流式中断）→ 单独落一条（Sanitize 当孤儿丢弃，安全）
            if (pendingToolCall != null)
                conv.Append(ToToolMessage(pendingToolCall, null));

            await _conversationStore.SaveAsync(conv, ct);
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

    /// <summary>把工具调用(call)+结果(result)合并为单条 Role=Tool 持久化消息（匹配 TranscriptHistoryConverter）。</summary>
    private static Message ToToolMessage(ChatMessageItem? call, ChatMessageItem? result)
    {
        var meta = new Dictionary<string, string> { ["kind"] = "tool" };
        var toolName = (call ?? result)?.ToolName ?? "tool";
        if (call?.Input is { Count: > 0 })
            meta["input"] = System.Text.Json.JsonSerializer.Serialize(call.Input);
        if (result?.Output is { Count: > 0 })
            meta["output"] = System.Text.Json.JsonSerializer.Serialize(result.Output);
        return new Message { Role = MessageRole.Tool, ToolName = toolName, Content = string.Empty, Metadata = meta };
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

            var conv = await _conversationStore.GetAsync(_currentConversationId, default);
            if (conv == null) return;

            conv.Title = title;
            await _conversationStore.SaveAsync(conv, default);

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
