using System.Collections.ObjectModel;
using System.Windows.Input;
using Acme.Agents.AI;
using Acme.AppAuth.Application.Auth;
using Acme.AppAuth.Domain;
using Acme.Automation.Application.Interfaces;
using Acme.Automation.Domain.Interfaces;
using Acme.DeviceConnect.Domain;
using Acme.Llm.Application;
using Acme.Llm.Domain;
using Acme.Notes.Domain.Agents;
using Aether.AI.Core.Agents;
using Aether.AI.Core.Conversations;
using Aether.AI.Hosting;
using Aether.AI.Memory;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.Shell;

/// <summary>
/// 桌面 shell 主 ViewModel 基类：可收起侧边栏 + 导航 + 账户/登录态（Codex/ChatGPT 风）。
/// 租户子类在构造里 Populate <see cref="NavItems"/>。内容区使用 ChatView。
/// </summary>
public abstract partial class MainViewModelBase : ObservableObject, IChatViewModel
{
    private readonly IAppAuthService _auth;
    private readonly ICurrentUser _currentUser;
    private readonly ILoginDialogService _loginDialog;
    private readonly IAppTokenStore _tokenStore;
    private readonly ISettingsDialogService _settingsDialog;
    private readonly IServiceProvider _serviceProvider;
    private readonly IDeviceChatClient _deviceChatClient;
    private readonly ILlmConfigService _llmService;
    private readonly ICustomAgentRunner _agentRunner;
    private readonly ICustomWorkflowRunner _workflowRunner;
    private readonly IUiDispatcher _dispatcher;
    private readonly IConversationStore _conversationStore;
    private readonly IPreferencesStore _preferences;
    private readonly IConversationTitleService _titleService;
    private readonly IDeviceAutoConnectService _autoConnect;
    private readonly IApprovalDialogService? _approvalDialog;

    // 当前会话：Guid.Empty = 待新聊天（首次发送才落库）。
    private Guid _currentConversationId = Guid.Empty;
    private bool _isInitializing;
    private bool _syncingMode; // 程序化刷新 SelectedMode 时置 true，避免 OnSelectedModeChanged 回触 SetMode
    private CancellationTokenSource? _runCts; // 当前流式运行的取消令牌（停止生成用）

    // —— 历史增量加载：切换会话时只渲染最近若干轮，更早的暂存，滚到顶部再加载 ——
    // 避免长会话一次性重建全部消息 + 同步 markdown 解析导致的卡顿（全在 UI 线程）。
    private List<ChatTurnItem>? _pendingHistoryTurns; // 暂存的更早轮（时间正序，最旧在前；尚未加入 Messages 渲染）
    private const int HistoryInitialBatch = 10;       // 切换会话时初始渲染的最近轮数
    private const int HistoryLoadBatch = 10;          // 每次滚到顶部增量加载的轮数


    protected MainViewModelBase(IAppAuthService auth, ICurrentUser currentUser, ILoginDialogService loginDialog, IAppTokenStore tokenStore, ISettingsDialogService settingsDialog, IServiceProvider serviceProvider, IDeviceChatClient deviceChatClient, ILlmConfigService llmService, string tenant)
    {
        _auth = auth;
        _currentUser = currentUser;
        _loginDialog = loginDialog;
        _tokenStore = tokenStore;
        _settingsDialog = settingsDialog;
        _serviceProvider = serviceProvider;
        _deviceChatClient = deviceChatClient;
        _llmService = llmService;
        _agentRunner = serviceProvider.GetRequiredService<ICustomAgentRunner>();
        _workflowRunner = serviceProvider.GetRequiredService<ICustomWorkflowRunner>();
        _dispatcher = serviceProvider.GetRequiredService<IUiDispatcher>();
        _conversationStore = serviceProvider.GetRequiredService<IConversationStore>();
        _preferences = serviceProvider.GetRequiredService<IPreferencesStore>();
        _titleService = serviceProvider.GetRequiredService<IConversationTitleService>();
        _autoConnect = serviceProvider.GetRequiredService<IDeviceAutoConnectService>();
        _approvalDialog = serviceProvider.GetService<IApprovalDialogService>();
        Trace = serviceProvider.GetService<TraceViewModel>();
        Tenant = tenant;

        Notes = new NotesViewModel(serviceProvider, _dispatcher);
        Automation = new AutomationViewModel(
            serviceProvider.GetRequiredService<ICustomAgentCatalog>(),
            serviceProvider.GetRequiredService<IAutomationService>(),
            serviceProvider.GetRequiredService<IAutomationScheduler>(),
            _dispatcher);
        Workflows = new WorkflowsViewModel(
            serviceProvider.GetRequiredService<ICustomWorkflowCatalog>(),
            serviceProvider.GetRequiredService<ICustomAgentCatalog>(),
            _workflowRunner,
            _dispatcher);
        Workflows.SummonRequested += OnWorkflowSummonRequested;
        Workflows.WorkflowsChanged += async () => await Notes.LoadAsync();
        // Notes 的增删改会影响同源的 Experts（agent 笔记）与 Workflows（workflow 笔记），需双向刷新。
        Notes.NotesChanged += async () =>
        {
            await Experts.LoadAsync();
            await Workflows.LoadAsync();
        };

        Experts = new ExpertsViewModel(
            serviceProvider.GetRequiredService<ICustomAgentCatalog>(),
            _agentRunner,
            _dispatcher,
            serviceProvider.GetRequiredService<IAgentMarketplaceService>(),
            _auth,
            Workflows,
            serviceProvider.GetRequiredService<IAgentSourceParser>(),
            serviceProvider.GetRequiredService<McpViewModel>(),
            serviceProvider.GetService<IConnectorViewModel>());
        Experts.SummonChanged += OnSummonChanged;
        Experts.SummonChanged += async () => await Notes.LoadAsync();

        // 定时任务执行完成（成功/失败）后刷新「最近会话」——因为 executor 把运行落库进了会话存储。
        serviceProvider.GetRequiredService<IAutomationScheduler>().TaskCompleted +=
            _ => _dispatcher.Post(async () => await RefreshConversationsAsync());

        Conversations.CollectionChanged += (_, _) => HasConversations = Conversations.Count > 0;
        // turn 增删影响 plan-accept 条显隐（ShowPlanAccept 依赖 Messages.Count）。
        Messages.CollectionChanged += (_, _) => OnPropertyChanged(nameof(ShowPlanAccept));

        SubscribeToConnectionStateChanges();
    }

    public string Tenant { get; }

    public ISettingsDialogService SettingsDialog => _settingsDialog;

    private void SubscribeToConnectionStateChanges()
    {
        _deviceChatClient.ConnectionStateChanged += status =>
        {
            _dispatcher.Post(() =>
            {
                IsConnected = _deviceChatClient.IsConnected;
                StatusText = IsConnected ? string.Empty : status;
            });
        };

        _deviceChatClient.OnMessageReceived += HandleMessageReceived;
    }

    public void RefreshConnectionState()
    {
        _dispatcher.Post(() =>
        {
            IsConnected = _deviceChatClient.IsConnected;
            StatusText = IsConnected ? "已连接" : "未连接";
        });
    }

    /// <summary>LLM 配置变更后刷新聊天页的模型下拉（设置页新增/编辑/删除后调用）。</summary>
    public async Task RefreshLlmConfigsAsync()
    {
        await _dispatcher.InvokeAsync(async () => await LoadLlmConfigsAsync());
    }

    /// <summary>流式分段光标：指向"当前正在追加 token 的消息项"；PendingTools 按 CallId 配对调用与结果。</summary>
    private sealed class StreamCursor
    {
        public ChatMessageItem Current;
        public readonly Dictionary<string, ChatMessageItem> PendingTools = new();
        public StreamCursor(ChatMessageItem initial) => Current = initial;
    }

    /// <summary>
    /// 分段交错运行：agent 推理→调工具→再推理→… 的过程中，把每段推理(<c>TextReasoningContent</c>)、
    /// 答案(<c>TextContent</c>)、工具步、审批卡、计划卡按真实时序交错插入聊天流。
    /// 推理进 💭 思考卡、答案进 markdown 气泡（建段时即区分 IsReasoning，不再靠末段翻转）。
    /// 审批请求冒出时本轮流结束 → 弹审批框 → 带 <see cref="ToolApprovalDecision"/> 再次 runAsync 恢复执行（协作循环）。
    /// 返回本轮产生的 transcript 项（按时序），供 PersistTurnAsync 持久化。
    /// </summary>
    private async Task<List<ChatMessageItem>> RunSegmentedStreamAsync(ChatTurnItem turn,
        string senderName,
        Func<StreamHandlers, ToolApprovalDecision?, CancellationToken, Task> runAsync,
        System.Text.StringBuilder fullText, CancellationToken cancellationToken = default)
    {
        var items = turn.Children; // 本轮工作列表 = turn 的 Children（用户气泡为第一项，agent 产出随后）
        var cursor = new StreamCursor(NewTextSegment(senderName, isReasoningContent: false));
        await _dispatcher.InvokeAsync(() =>
        {
            items.Add(cursor.Current);
            turn.Status = TurnStatus.Working;
            IsAgentRunning = true;
        });

        // —— Token 批量更新：不逐 token Post 到 UI 线程（会假死），攒 50ms 批量刷新一次 ——
        var pendingTokens = new System.Text.StringBuilder();
        var pendingReasoning = new System.Text.StringBuilder();
        var flushTimer = new System.Threading.Timer(_ => _dispatcher.Post(() =>
        {
            if (pendingTokens.Length > 0)
            {
                EnsureTextSegment(cursor, items, senderName, isReasoningContent: false);
                cursor.Current.Content += pendingTokens.ToString();
                cursor.Current.MarkdownContent = cursor.Current.Content;
                pendingTokens.Clear();
            }
            if (pendingReasoning.Length > 0)
            {
                EnsureTextSegment(cursor, items, senderName, isReasoningContent: true);
                cursor.Current.Content += pendingReasoning.ToString();
                pendingReasoning.Clear();
            }
        }), null, Timeout.Infinite, Timeout.Infinite);

        ToolApprovalDecision? approvalResponse = null;
        var errored = false;
        try
        {
            while (true)
            {
                ToolApprovalRequest? pendingApproval = null;
                ChatMessageItem? approvalCard = null;
                // answerTokens 节流已由 50ms 批量 flush 替代

                var handlers = new StreamHandlers(
                    OnToken: (token, _) =>
                    {
                        fullText.Append(token);
                        pendingTokens.Append(token);
                        flushTimer.Change(50, Timeout.Infinite); // 50ms 后批量刷新
                        return Task.CompletedTask;
                    },
                    OnReasoning: (text, _) =>
                    {
                        pendingReasoning.Append(text);
                        flushTimer.Change(50, Timeout.Infinite);
                        return Task.CompletedTask;
                    },
                    OnToolEvent: (ev, _) =>
                    {
                        // todos_* 调用由 OnTodoList 渲染为计划卡，不重复出工具卡。
                        if (ev.Name.StartsWith("todo", StringComparison.OrdinalIgnoreCase))
                            return Task.CompletedTask;
                        _dispatcher.Post(() => HandleToolEventSegmented(cursor, items, ev, senderName));
                        return Task.CompletedTask;
                    },
                    OnApprovalRequest: (req, _) =>
                    {
                        pendingApproval = req;
                        var card = new ChatMessageItem
                        {
                            Side = ChatMessageSide.Left,
                            ItemType = "approval",
                            Sender = senderName,
                            ToolName = req.ToolName,
                            CallId = req.CallId,
                            Input = req.Input,
                            ApprovalState = 0,
                        };
                        approvalCard = card;
                        _dispatcher.Post(() =>
                        {
                            var idx = items.IndexOf(cursor.Current);
                            if (idx < 0) idx = items.Count - 1;
                            items.Insert(idx + 1, card);
                            cursor.Current = card;
                            turn.Status = TurnStatus.AwaitingApproval;
                        });
                        return Task.CompletedTask;
                    },
                    OnTodoList: (list, _) =>
                    {
                        _dispatcher.Post(() => HandleTodoList(items, list, senderName));
                        return Task.CompletedTask;
                    },
                    OnError: (text, _) =>
                    {
                        _dispatcher.Post(() =>
                        {
                            EnsureTextSegment(cursor, items, senderName, isReasoningContent: false);
                            cursor.Current.Content += $"\n\n[错误：{text}]";
                        });
                        return Task.CompletedTask;
                    },
                    OnUsage: info => _dispatcher.Post(() => TokenInfo = info));

                await runAsync(handlers, approvalResponse, cancellationToken).ConfigureAwait(false);

                if (pendingApproval is null) break;

                // 弹审批框等用户决定
                var decision = _approvalDialog is null ? null : await _approvalDialog.ShowAsync(pendingApproval).ConfigureAwait(false);
                if (decision is null)
                {
                    _dispatcher.Post(() => { if (approvalCard is not null) approvalCard.ApprovalState = 2; });
                    break; // 取消 → 终止本轮
                }
                approvalResponse = decision;
                var approved = decision.Approved;
                var cardRef = approvalCard;
                _dispatcher.Post(() =>
                {
                    if (cardRef is not null) cardRef.ApprovalState = approved ? 1 : 2;
                    turn.Status = TurnStatus.Working;
                    // 开新答案段承接恢复流
                    var seg = NewTextSegment(senderName, isReasoningContent: false);
                    var idx = items.IndexOf(cursor.Current);
                    if (idx < 0) idx = items.Count - 1;
                    items.Insert(idx + 1, seg);
                    cursor.Current = seg;
                });
            }
        }
        catch (Exception ex)
        {
            errored = true;
            fullText.Append($"\n\n[运行失败：{ex.Message}]");
            await _dispatcher.InvokeAsync(() =>
            {
                EnsureTextSegment(cursor, items, senderName, isReasoningContent: false);
                cursor.Current.Content += $"\n\n[运行失败：{ex.Message}]";
            });
        }
        finally
        {
            // 最后一次 flush（确保剩余 token 写入）+ 停止定时器
            flushTimer.Change(0, Timeout.Infinite);
            await Task.Delay(60); // 等 flush 完成
            flushTimer.Dispose();

            await _dispatcher.InvokeAsync(() =>
            {
                FinalizeSegment(cursor.Current, items);
                // 每轮一快照：turn 结束若有活动计划，把计划卡插到本轮首（用户消息之后、agent 工作之前），
                // 内联在对话里、出现在"制定计划"的位置（参考 Claude Code），而非堆在轮末与浮动面板叠底。
                // 借已有 kind=plan 持久化脚手架——历史会话恢复后仍能看到每轮的计划。
                if (HasActivePlan && ActiveTodos.Count > 0)
                {
                    var planCard = new ChatMessageItem { Side = ChatMessageSide.Left, ItemType = "plan", Sender = senderName, IsStreaming = false };
                    foreach (var t in ActiveTodos)
                        planCard.Todos.Add(new TodoItemVM { Title = t.Title, Description = t.Description, IsComplete = t.IsComplete });
                    items.Insert(1, planCard); // items[0] = 用户消息
                }
                turn.Finish(errored ? TurnStatus.Error : TurnStatus.Done);
                IsAgentRunning = false;
                _runCts?.Dispose();
                _runCts = null;
            });
        }
        return items.ToList();
    }

    private static ChatMessageItem NewTextSegment(string senderName, bool isReasoningContent) => new()
    {
        Side = ChatMessageSide.Left,
        ItemType = "text",
        Sender = senderName,
        Content = string.Empty,
        IsStreaming = true,
        // 只有 TextReasoningContent（真·思考）才 IsReasoning=true（折叠 💭）；
        // TextContent（可见叙述/答案）IsReasoning=false，冻结后渲染为展开的 markdown 文本，不折叠。
        IsReasoning = isReasoningContent,
        IsReasoningContent = isReasoningContent,
    };

    /// <summary>确保当前段是目标类型（TextContent 思考/答案 vs TextReasoningContent 推理）的文本段；否则冻结/移除当前并新开一段。串行在 UI 线程调用。</summary>
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

    /// <summary>工具事件处理：Call 冻结当前段并插一条工具步（带 CallId）；Result 按 CallId 回填到同一条，再开新答案段。</summary>
    private void HandleToolEventSegmented(StreamCursor cursor, IList<ChatMessageItem> items, ToolStreamEvent ev, string senderName)
    {
        if (ev.Kind == ToolStreamEventKind.Call)
        {
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
                Side = ChatMessageSide.Left,
                ItemType = "tool",
                Sender = senderName,
                ToolName = ev.Name,
                CallId = ev.CallId,
                Input = ev.Input,
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
            else if (cursor.Current.ItemType == "tool")
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

    /// <summary>todos_* 调用后更新浮动计划面板（ActiveTodos）。快照（每轮一卡、入对话流持久化）在 RunSegmentedStreamAsync 末尾插入。    /// 计划卡随 transcript 持久化、恢复，重启后仍在。</summary>
    private void HandleTodoList(IList<ChatMessageItem> items, IReadOnlyList<TodoItemInfo> list, string senderName)
    {
        // 计划只显示在浮动面板（输入框上方），不在对话流里重复显示
        ActiveTodos.Clear();
        foreach (var t in list)
            ActiveTodos.Add(new TodoItemVM { Title = t.Title, Description = t.Description, IsComplete = t.IsComplete });
        HasActivePlan = ActiveTodos.Count > 0;
    }

    /// <summary>运行结束：末段空则移除；否则停止流式。仅 TextContent 末段翻为最终回答（markdown 气泡），
    /// TextReasoningContent 段保持 💭 思考卡。中间各段保持思考态。</summary>
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

    private Guid _recentConversationId;

    /// <summary>收到远程 SignalR 消息回调（SignalR 线程）</summary>
    private async Task HandleMessageReceived(DeviceMessage msg)
    {
        _recentConversationId = msg.ConversationId;

        // 远程消息显示在右侧（与本机发送一致，参考 AlphaAgent），并起一轮
        ChatTurnItem remoteTurn = null!;
        await _dispatcher.InvokeAsync(() =>
        {
            remoteTurn = new ChatTurnItem { Header = msg.Content };
            remoteTurn.Children.Add(new ChatMessageItem
            {
                Side = ChatMessageSide.Right,
                ItemType = "text",
                Sender = msg.SenderName,
                Content = msg.Content
            });
            Messages.Add(remoteTurn);
        });

        // 选中已召唤的自定义专家时，用该专家流式回复
        if (SelectedAgentItem is ExpertItem expert)
        {
            var fullText = new System.Text.StringBuilder();
            var desiredMode = HasModeSupport ? SelectedMode : null;
            await RunSegmentedStreamAsync(remoteTurn, expert.Name,
                (handlers, approvalResponse, ct) => _agentRunner.RunStreamingAsync(msg.ConversationId, expert.Id, msg.Content, handlers, approvalResponse, desiredMode, ct),
                fullText);
            await RefreshModeAsync(msg.ConversationId);

            // 远程消息触发运行：把专家回复通过 SignalR 发回远程发送方（否则对方收不到 agent 结果）。
            // 回环安全：SignalRDeviceChatClient 用 _recentSentMessageIds 去重，服务端 echo 回本机的消息会被忽略。
            var replyText = fullText.ToString();
            if (!string.IsNullOrWhiteSpace(replyText) && _deviceChatClient.IsConnected)
            {
                try
                {
                    await _deviceChatClient.SendMessageAsync(msg.ConversationId, replyText, "Text");
                }
                catch
                {
                    // 发回失败不影响本地已显示的回复
                }
            }
            return;
        }

        // 未选用专家时本地简单回复（单独一轮）
        await _dispatcher.InvokeAsync(() =>
        {
            var t = new ChatTurnItem { Header = msg.Content };
            t.Children.Add(new ChatMessageItem
            {
                Side = ChatMessageSide.Left,
                ItemType = "text",
                Sender = "Aether",
                Content = $"收到来自 {msg.SenderName} 的消息：{msg.Content}",
                IsStreaming = false
            });
            t.Finish(TurnStatus.Done);
            Messages.Add(t);
        });
    }

    // —— 聊天消息列表 ——
    public ObservableCollection<ChatTurnItem> Messages { get; } = new();

    // —— 浮动计划面板（贴着输入框上方，实时显示最新计划，可展开/收起）——
    public ObservableCollection<TodoItemVM> ActiveTodos { get; } = new();
    [ObservableProperty] private bool _hasActivePlan;
    [ObservableProperty] private bool _isPlanExpanded = true;
    public string PlanSummary => HasActivePlan ? $"📋 计划 ({ActiveTodos.Count(t => t.IsComplete)}/{ActiveTodos.Count})" : "";
    partial void OnHasActivePlanChanged(bool value) => OnPropertyChanged(nameof(PlanSummary));
    partial void OnIsPlanExpandedChanged(bool value) => OnPropertyChanged(nameof(PlanSummary));
    [RelayCommand] private void TogglePlanExpanded() => IsPlanExpanded = !IsPlanExpanded;

    /// <summary>运行 Trace 查看 VM（桌面端注册；null = 无 trace 查看，如 sample console）。</summary>
    public TraceViewModel? Trace { get; }

    /// <summary>当前 agent 是否启用 AgentModeProvider（控制模式选择器/plan-accept 显隐）。</summary>
    [ObservableProperty]
    private bool _hasModeSupport;

    /// <summary>当前会话 AgentMode（plan/execute…）；null=未知/未启用。输入框选择器 + plan-accept 条绑定。</summary>
    [ObservableProperty]
    private string? _selectedMode = "plan";

    /// <summary>agent 是否正在流式运行（驱动 plan-accept 条显隐）。</summary>
    [ObservableProperty]
    private bool _isAgentRunning;

    /// <summary>plan 模式下、末轮产生了计划卡、且非运行中时显示"接受计划→执行"条（避免每轮盲弹）。</summary>
    public bool ShowPlanAccept => HasModeSupport && SelectedMode == "plan" && !IsAgentRunning && LastTurnHasPlan;

    /// <summary>末轮是否有计划卡（plan-accept 的触发信号）。</summary>
    private bool LastTurnHasPlan => Messages.Count > 0 && Messages[^1].Children.Any(c => c.IsPlanCard);

    /// <summary>输入框模式选择器选项（默认 plan/execute）。</summary>
    public ObservableCollection<string> ModeOptions { get; } = new() { "plan", "execute" };

    // —— 最近会话列表（侧边栏）——
    public ObservableCollection<ConversationItem> Conversations { get; } = new();

    /// <summary>是否有最近会话（控制空状态"暂无会话"显示）。</summary>
    [ObservableProperty] private bool _hasConversations;

    /// <summary>最近会话列表是否展开（ChatGPT 风，可收起）。</summary>
    [ObservableProperty] private bool _isRecentExpanded = true;

    public bool IsRecentEmptyVisible => IsRecentExpanded && !HasConversations;

    public string RecentCaret => IsRecentExpanded ? "▼" : "▶";

    partial void OnIsRecentExpandedChanged(bool value)
    {
        OnPropertyChanged(nameof(IsRecentEmptyVisible));
        OnPropertyChanged(nameof(RecentCaret));
        _preferences.SetAsync("recentExpanded", value ? "1" : "0").SafeFireAndForget();

    }

    partial void OnHasConversationsChanged(bool value) => OnPropertyChanged(nameof(IsRecentEmptyVisible));

    [RelayCommand]
    private void ToggleRecentExpanded() => IsRecentExpanded = !IsRecentExpanded;

    [RelayCommand]
    private void NewChat()
    {
        // 懒创建：仅进入待新聊天态，不在此时落库（避免空会话进列表）。
        _currentConversationId = Guid.Empty;
        Messages.Clear();
            ActiveTodos.Clear(); HasActivePlan = false;
        _pendingHistoryTurns = null;
        foreach (var c in Conversations) c.IsCurrent = false;
    }

    [RelayCommand]
    private async Task ResumeConversationAsync(ConversationItem item)
    {
        if (item is null) return;
        try
        {
            var conv = await _conversationStore.GetAsync(item.Id);
            _currentConversationId = item.Id;
            foreach (var c in Conversations) c.IsCurrent = c.Id == item.Id;

            _isInitializing = true;
            // 恢复时按绑定 id 判断是 workflow 还是 agent，切到对应目标（抑制变更回调）
            if (Guid.TryParse(item.AgentId, out var aid))
            {
                var wf = Workflows.Workflows.FirstOrDefault(w => w.Id == aid);
                if (wf is not null) { SelectedWorkflow = wf; SelectedAgentItem = null; }
                else { SelectedWorkflow = null; SelectedAgentItem = AgentSelectItems.OfType<ExpertItem>().FirstOrDefault(e => e.Id == aid); }
            }
            _isInitializing = false;

            Messages.Clear();
            ActiveTodos.Clear(); HasActivePlan = false;
            _pendingHistoryTurns = null;
            if (conv is not null)
            {
                // 一次性按 user 消息分组重建全部 turn（POCO 构建 + metadata 解析，开销小），
                // 但只把最近 N 轮加入 Messages 渲染——markdown 解析仅在绑定时发生，分批可避免卡顿。
                // 更早的轮暂存 _pendingHistoryTurns，待用户滚到顶部时由 View 触发 LoadMoreHistory 增量加载。
                var all = BuildTurns(conv.Messages, item.AgentName ?? "专家");
                if (all.Count <= HistoryInitialBatch)
                {
                    foreach (var t in all) Messages.Add(t);
                }
                else
                {
                    _pendingHistoryTurns = all.Take(all.Count - HistoryInitialBatch).ToList();
                    foreach (var t in all.Skip(all.Count - HistoryInitialBatch)) Messages.Add(t);
                }
            }
        }
        catch (Exception ex)
        {
            StatusText = $"加载会话失败：{ex.Message}";
        }
    }

    [RelayCommand]
    private async Task DeleteConversationAsync(ConversationItem item)
    {
        if (item is null) return;
        try
        {
            await _conversationStore.DeleteAsync(item.Id);
            Conversations.Remove(item);
            if (_currentConversationId == item.Id)
            {
                _currentConversationId = Guid.Empty;
                Messages.Clear();
            ActiveTodos.Clear(); HasActivePlan = false;
            }
        }
        catch (Exception ex)
        {
            StatusText = $"删除会话失败：{ex.Message}";
        }
    }

    /// <summary>从存储加载最近会话列表（按 UpdatedAt 倒序）。增量渲染：只把最近若干条加入列表，更早的滚到底部再加载。</summary>
    private async Task RefreshConversationsAsync()
    {
        try
        {
            var summaries = await _conversationStore.GetSummariesAsync(string.Empty);
            await _dispatcher.InvokeAsync(() =>
            {
                Conversations.Clear();
                // 摘要轻量（无 transcript），一次性构建全部 ConversationItem（POCO，开销小），
                // 但只渲染最近 N 条——侧边栏 ItemsControl 非虚拟化，全量渲染条目多时启动会卡。
                var all = new List<ConversationItem>(summaries.Count);
                foreach (var s in summaries)
                {
                    all.Add(new ConversationItem
                    {
                        Id = s.ConversationId,
                        Title = string.IsNullOrWhiteSpace(s.Title) ? "新会话" : s.Title,
                        Preview = s.LastMessagePreview ?? string.Empty,
                        DisplayTime = FormatTime(s.UpdatedAt),
                        AgentId = s.AgentId,
                        AgentName = s.AgentName,
                        IsCurrent = s.ConversationId == _currentConversationId,
                        ResumeCommand = ResumeConversationCommand,
                        DeleteCommand = DeleteConversationCommand,
                    });
                }
                foreach (var c in all) Conversations.Add(c);
            });
        }
        catch { /* 启动期失败不阻塞 */ }
    }

    /// <summary>侧边栏是否还有更早的会话未渲染（供 View 判断是否触发增量加载）。</summary>

    private static string FormatTime(DateTimeOffset t)
    {
        var local = t.LocalDateTime;
        if (local.Date == DateTime.Today) return local.ToString("HH:mm");
        return local.ToString("MM-dd HH:mm");
    }

    [ObservableProperty]
    private string _inputText = string.Empty;

    [ObservableProperty]
    private bool _canSendInput = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConnectionLabel))]
    private bool _isConnected;

    public string ConnectionLabel => IsConnected ? "已连接" : "未连接";

    [ObservableProperty]
    private string _statusText = string.Empty;

    /// <summary>本轮 token 用量摘要（UsageContent），绑定到顶栏 TokenInfo 槽。</summary>
    [ObservableProperty]
    private string _tokenInfo = string.Empty;

    public ObservableCollection<object> AgentSelectItems { get; } = new();
    public ObservableCollection<LlmConfigDto> LlmSelectItems { get; } = new();

    [ObservableProperty]
    private object? _selectedAgentItem;

    [ObservableProperty]
    private LlmConfigDto? _selectedLlmItem;

    // —— 侧边栏（原样：展开/收起）——
    [ObservableProperty]
    private double _sidebarWidth = 220;

    [ObservableProperty]
    private bool _isSidebarExpanded = true;

    [RelayCommand]
    private void ToggleSidebar()
    {
        IsSidebarExpanded = !IsSidebarExpanded;
        SidebarWidth = IsSidebarExpanded ? 220 : 48;
        _sidebarCollapsedForNotes = false;
    }

    private bool _sidebarCollapsedForNotes;

    // —— 导航 ——
    [ObservableProperty]
    private string _currentPage = "notes";

    /// <summary>是否是笔记页</summary>
    public bool IsNotesPage => CurrentPage == "notes";

    /// <summary>是否是专家页</summary>
    public bool IsExpertsPage => CurrentPage == "experts";

    /// <summary>是否是自动化页</summary>
    public bool IsAutomationPage => CurrentPage == "automation";

    /// <summary>是否是期货页（Stockapp 租户专属；Qifu 无此导航，恒 false）</summary>
    public bool IsFuturesPage => CurrentPage == "ctp";

    /// <summary>是否是回测/实盘页（Stockapp 租户专属；Qifu 无此导航，恒 false）</summary>
    public bool IsTradingPage => CurrentPage == "backtest";

    /// <summary>是否是股票交易页（Stockapp 租户专属；Qifu 无此导航，恒 false）。
    /// FlaUI 操控同花顺下单客户端（区别于 backtest 的 Lean 引擎）。</summary>
    public bool IsStockTradePage => CurrentPage == "stocktrade";

    /// <summary>笔记页 ViewModel</summary>
    public NotesViewModel Notes { get; }

    /// <summary>自动化页 ViewModel</summary>
    public AutomationViewModel Automation { get; }

    /// <summary>工作流页 ViewModel</summary>
    public WorkflowsViewModel Workflows { get; }

    /// <summary>当前召唤到聊天的 workflow（非空时聊天跑 workflow；为空时跑选中的 agent）。</summary>
    [ObservableProperty] private WorkflowItem? _selectedWorkflow;

    /// <summary>期货页 ViewModel（租户专属；由租户 MainViewModel 赋值，Qifu 为 null）。
    /// 类型为 object 以免共享层依赖 CTP。视图经 App.axaml DataTemplate 解析。</summary>
    public object? Futures { get; protected set; }

    /// <summary>回测/实盘页 ViewModel（租户专属；由租户 MainViewModel 赋值，Qifu 为 null）。
    /// 类型为 object 以免共享层依赖 Trading。视图经 App.axaml DataTemplate 解析。</summary>
    public object? Trading { get; protected set; }

    /// <summary>股票交易页 ViewModel（租户专属；由租户 MainViewModel 赋值，Qifu 为 null）。
    /// 类型为 object 以免共享层依赖 StockTrading。视图经 App.axaml DataTemplate 解析。</summary>
    public object? StockTrade { get; protected set; }

    /// <summary>专家页 ViewModel</summary>
    public ExpertsViewModel Experts { get; }

    /// <summary>导航项集合（租户填充）。</summary>
    public ObservableCollection<NavItem> NavItems { get; } = new();

    partial void OnCurrentPageChanged(string? value)
    {
        OnPropertyChanged(nameof(IsNotesPage));
        OnPropertyChanged(nameof(IsExpertsPage));
        OnPropertyChanged(nameof(IsAutomationPage));
        OnPropertyChanged(nameof(IsFuturesPage));
        OnPropertyChanged(nameof(IsTradingPage));
        OnPropertyChanged(nameof(IsStockTradePage));
    }

    [RelayCommand]
    private void NavigateTo(string pageKey)
    {
        // 离开笔记页时恢复侧边栏
        if (CurrentPage == "notes" && pageKey != "notes" && _sidebarCollapsedForNotes)
        {
            IsSidebarExpanded = true;
            SidebarWidth = 220;
            _sidebarCollapsedForNotes = false;
        }

        // 进入笔记页：自动收起侧边栏给笔记更大空间
        if (pageKey == "notes" && IsSidebarExpanded)
        {
            IsSidebarExpanded = false;
            SidebarWidth = 48;
            _sidebarCollapsedForNotes = true;
        }

        CurrentPage = pageKey;
        foreach (var item in NavItems)
            item.IsCurrent = item.Key == pageKey;
    }

    protected void AddNav(string key, string icon, string title)
    {
        NavItems.Add(new NavItem
        {
            Key = key,
            Icon = icon,
            Title = title,
            Command = NavigateToCommand,
        });
    }

    // —— 账户 / 登录态 ——
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UserInitial))]
    private bool _isLoggedIn;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UserInitial))]
    private string _userName = "未登录";

    /// <summary>账户头像首字母（未登录显示 ?）。</summary>
    public string UserInitial => IsLoggedIn
        ? (string.IsNullOrEmpty(UserName) ? "?" : UserName[..1])
        : "?";

    /// <summary>启动期初始化登录态：检查已缓存的应用 token，刷新账户显示。</summary>
    public virtual async Task InitializeAsync()
    {
        try
        {
            if (await _auth.IsLoggedInAsync())
            {
                IsLoggedIn = true;
                var username = await _tokenStore.GetUsernameAsync();
                UserName = string.IsNullOrWhiteSpace(username) ? "已登录" : username;
            }
            else
            {
                IsLoggedIn = false;
                UserName = "未登录";
            }
        }
        catch
        {
            IsLoggedIn = false;
            UserName = "未登录";
        }

        IsConnected = _deviceChatClient.IsConnected;
        StatusText = IsConnected ? "已连接" : "未连接";

        _isInitializing = true;
        try
        {
            await LoadLlmConfigsAsync();
            await Notes.LoadAsync();
            await Experts.LoadAsync();
            await Automation.LoadAsync();
            await Workflows.LoadAsync();
            await RestoreSummonedAndSelectedAsync();
            await RefreshConversationsAsync();
        }
        finally { _isInitializing = false; }

        // 启动自动化调度器（进程内定时触发；仅 App 打开时生效）。双启动守卫已在调度器内。
        _ = _serviceProvider.GetRequiredService<IAutomationService>()
            .StartSchedulerAsync(CancellationToken.None);

        // 启动 MCP server 连接（若 mcp-servers 目录配了 server）。后台连接 + 连好后把工具注册进 ToolRegistry。
        // 用 shared-task：编辑器打开时 EnsureLoadedAsync 会 await 同一连接任务，确保 MCP 区有最新工具。
        _ = Task.Run(async () =>
        {
            try
            {
                if (_serviceProvider.GetService<IMcpManagementService>() is { } mgmt)
                {
                    await mgmt.EnsureStartedAsync();
                    mgmt.StartHealthMonitoringAsync(TimeSpan.FromMinutes(2)).SafeFireAndForget(); // 每 2 分钟探活，掉线自动重连
                }
            }
            catch { /* MCP 启动失败不阻断主流程 */ }
        });

        await TryAutoConnectDeviceAsync();
    }

    /// <summary>从偏好恢复召唤状态与选中专家（跨重启保留），并保证启动时有一个默认选中。</summary>
    private async Task RestoreSummonedAndSelectedAsync()
    {
        try
        {
            // 召唤列表
            var summonedJson = await _preferences.GetAsync("summonedAgentIds");
            if (!string.IsNullOrWhiteSpace(summonedJson))
            {
                var ids = System.Text.Json.JsonSerializer.Deserialize<string[]>(summonedJson) ?? Array.Empty<string>();
                var set = new HashSet<string>(ids);
                foreach (var e in Experts.Experts)
                    e.IsSummoned = set.Contains(e.Id.ToString());
            }

            // 无任何召唤专家时，自动召唤第一个，保证下拉非空、有默认 agent
            if (Experts.GetSummonedExperts().Count == 0 && Experts.Experts.Count > 0)
            {
                Experts.Experts[0].IsSummoned = true;
                var ids = Experts.GetSummonedExperts().Select(e => e.Id.ToString()).ToArray();
                await _preferences.SetAsync("summonedAgentIds", System.Text.Json.JsonSerializer.Serialize(ids));
            }

            // 同步填充下拉（此时已在 UI 线程；不依赖后续的 RefreshAgentSelectItems）
            AgentSelectItems.Clear();
            foreach (var e in Experts.GetSummonedExperts())
                AgentSelectItems.Add(e);

            // 选中：已保存的 → 回退到第一个召唤的专家
            var selectedId = await _preferences.GetAsync("selectedAgentId");
            var saved = Guid.TryParse(selectedId, out var aid)
                ? AgentSelectItems.OfType<ExpertItem>().FirstOrDefault(e => e.Id == aid)
                : null;
            SelectedAgentItem = saved ?? AgentSelectItems.OfType<ExpertItem>().FirstOrDefault();

            // 恢复最近会话展开状态
            var expanded = await _preferences.GetAsync("recentExpanded");
            if (expanded == "0") IsRecentExpanded = false;
        }
        catch { /* 偏好恢复失败不阻塞 */ }
    }

    /// <summary>加载 LLM 配置列表到下拉框，并恢复上次选中的 LLM（跨重启保留）。</summary>
    private async Task LoadLlmConfigsAsync()
    {
        try
        {
            var configs = await _llmService.GetCachedAsync();
            LlmSelectItems.Clear();
            if (configs != null)
            {
                foreach (var c in configs)
                    LlmSelectItems.Add(c);

                // 选中：上次保存的 → IsDefault → 第一个
                var savedId = await _preferences.GetAsync("selectedLlmId");
                var saved = Guid.TryParse(savedId, out var lid)
                    ? configs.FirstOrDefault(c => c.Id == lid)
                    : null;
                SelectedLlmItem = saved ?? configs.FirstOrDefault(c => c.IsDefault) ?? configs.FirstOrDefault();
                // 启动时同步 holder（首次 agent 构建就能用对的 LLM）
                if (SelectedLlmItem is not null && _serviceProvider.GetService<Aether.AI.Harness.SelectedLlmHolder>() is { } holder)
                    holder.LlmConfigId = SelectedLlmItem.Id.ToString();
            }
        }
        catch
        {
        }
    }

    /// <summary>选中 LLM 变化时持久化（启动期 _isInitializing 抑制）。</summary>
    partial void OnSelectedLlmItemChanged(LlmConfigDto? value)
    {
        if (_isInitializing) return;
        if (value is not null)
        {
            _preferences.SetAsync("selectedLlmId", value.Id.ToString()).SafeFireAndForget();

            // 更新 LLM holder + invalidate 所有缓存的 agent → 下轮 run 用新 LLM 重建
            if (_serviceProvider.GetService<Aether.AI.Harness.SelectedLlmHolder>() is { } holder)
            {
                holder.LlmConfigId = value.Id.ToString();
                foreach (var expert in Experts.GetSummonedExperts())
                    _agentRunner.Invalidate(expert.Id);
            }
        }
    }

    /// <summary>召唤变化：刷新下拉 + 持久化召唤列表。</summary>
    private async void OnSummonChanged()
    {
        RefreshAgentSelectItems();
        // 编辑后 EnableAgentModeProvider 可能变了，重新评估模式选择器显隐
        if (SelectedAgentItem is ExpertItem ei)
            HasModeSupport = ei.EnableAgentModeProvider;
        try
        {
            var ids = Experts.GetSummonedExperts().Select(e => e.Id.ToString()).ToArray();
            await _preferences.SetAsync("summonedAgentIds", System.Text.Json.JsonSerializer.Serialize(ids));
        }
        catch { /* 偏好持久化失败不阻塞 */ }
    }

    /// <summary>召唤 workflow 到聊天：设为当前目标、开新会话、切到聊天页。</summary>
    private void OnWorkflowSummonRequested(WorkflowItem item)
    {
        _dispatcher.Post(() =>
        {
            SelectedWorkflow = item;
            SelectedAgentItem = null; // 走 workflow 分支
            _currentConversationId = Guid.Empty; // 新会话
            Messages.Clear();
            ActiveTodos.Clear(); HasActivePlan = false;
            StatusText = $"已召唤工作流：{item.Name}";
            IsConnected = _deviceChatClient.IsConnected;
        });
    }

    /// <summary>选中专家变化时：始终更新模式支持/选择器（UI 态）；启动期 _isInitializing 仅抑制持久化与 SetMode 副作用。</summary>
    partial void OnSelectedAgentItemChanged(object? value)
    {
        // 模式支持随选中 agent 变化（始终更新，否则启动期恢复选中时选择器/plan-accept 不显示）。
        HasModeSupport = value is ExpertItem e && e.EnableAgentModeProvider;
        _syncingMode = true;
        SelectedMode = HasModeSupport ? "plan" : null;
        _syncingMode = false;

        if (_isInitializing) return;
        if (value is ExpertItem expert)
        {
            _preferences.SetAsync("selectedAgentId", expert.Id.ToString()).SafeFireAndForget();

        }
        RefreshModeAsync().SafeFireAndForget();

    }

    partial void OnSelectedModeChanged(string? value)
    {
        OnPropertyChanged(nameof(ShowPlanAccept));
        OnPropertyChanged(nameof(IsPlanMode));
        if (_syncingMode || _isInitializing || value is null || !HasModeSupport) return;
        // 已存在会话：立即 SetMode；新会话：不改，由 run delegate 的 desiredMode 在首轮应用。
        if (_currentConversationId != Guid.Empty)
            SetModeOnSessionAsync(value).SafeFireAndForget();

    }

    /// <summary>是否在规划模式（ToggleButton 绑定用）。设值切换 plan/execute。</summary>
    public bool IsPlanMode
    {
        get => SelectedMode == "plan";
        set
        {
            var target = value ? "plan" : "execute";
            if (SelectedMode != target) SelectedMode = target;
        }
    }
    partial void OnIsAgentRunningChanged(bool value) => OnPropertyChanged(nameof(ShowPlanAccept));
    partial void OnHasModeSupportChanged(bool value) => OnPropertyChanged(nameof(ShowPlanAccept));

    /// <summary>刷新聊天页 Agent 下拉框（已召唤的专家），保留当前选中。</summary>
    private void RefreshAgentSelectItems()
    {
        _dispatcher.Post(() =>
        {
            var current = SelectedAgentItem as ExpertItem;
            AgentSelectItems.Clear();
            foreach (var e in Experts.GetSummonedExperts())
                AgentSelectItems.Add(e);

            // 当前仍在召唤列表 → 保持选中；否则回退第一个（避免清空导致选中丢失）
            SelectedAgentItem = (current is not null && AgentSelectItems.Contains(current))
                ? current
                : AgentSelectItems.OfType<ExpertItem>().FirstOrDefault();
        });
    }

    private async Task TryAutoConnectDeviceAsync()
    {
        try
        {
            await _autoConnect.LoadAndAutoConnectAsync();
        }
        catch
        {
        }
    }

    [RelayCommand]
    private async Task LoginAsync()
    {
        if (await _loginDialog.ShowLoginAsync())
            await InitializeAsync();
    }

    [RelayCommand]
    private async Task LogoutAsync()
    {
        await _auth.LogoutAsync();
        await InitializeAsync();
    }

    [RelayCommand]
    private async Task ShowSettingsAsync()
    {
        await _settingsDialog.ShowSettingsAsync();
    }

    /// <summary>停止当前流式生成（取消 CancellationToken）。</summary>
    [RelayCommand]
    private void StopGeneration()
    {
        _runCts?.Cancel();
    }

    /// <summary>用户在输入框切换 plan/execute 模式：新会话只记 SelectedMode（首轮 desiredMode 应用），已存在会话立即 SetMode。</summary>
    private async Task SetModeOnSessionAsync(string mode)
    {
        if (_currentConversationId == Guid.Empty) return;
        if (SelectedWorkflow is not null)
            await _workflowRunner.SetModeAsync(_currentConversationId, mode);
        else
            await _agentRunner.SetModeAsync(_currentConversationId, mode);
    }

    /// <summary>轮后从会话读实际模式（捕获 agent 自行 set_mode 的切换），带 guard 不回触 SetMode。</summary>
    private async Task RefreshModeAsync(Guid? conversationId = null)
    {
        var convId = conversationId ?? _currentConversationId;
        if (convId == Guid.Empty || !HasModeSupport) return;
        string? m = SelectedWorkflow is not null
            ? await _workflowRunner.GetModeAsync(convId).ConfigureAwait(false)
            : await _agentRunner.GetModeAsync(convId).ConfigureAwait(false);
        if (m is not null)
        {
            _syncingMode = true;
            SelectedMode = m;
            _syncingMode = false;
        }
    }

    /// <summary>接受计划：切 execute 模式 + 续跑一轮"已接受计划，进入执行"。</summary>
    [RelayCommand]
    private async Task AcceptPlanAsync()
    {
        if (!HasModeSupport || IsAgentRunning || _currentConversationId == Guid.Empty) return;
        await SetModeOnSessionAsync("execute");
        _syncingMode = true; SelectedMode = "execute"; _syncingMode = false;
        InputText = "（已接受计划，进入执行模式）";
        await SendInputAsync();
    }

    [RelayCommand]
    private async Task SendInputAsync()
    {
        if (!CanSendInput || string.IsNullOrWhiteSpace(InputText)) return;

        var content = InputText.Trim();
        InputText = string.Empty;
        _runCts = new CancellationTokenSource();

        // 本地显示用户消息（右侧）并起一轮
        var turn = new ChatTurnItem { Header = content };
        turn.Children.Add(new ChatMessageItem
        {
            Side = ChatMessageSide.Right,
            ItemType = "text",
            Sender = "我",
            Content = content
        });
        Messages.Add(turn);

        // 召唤了 workflow 时，本地流式运行 workflow（多 agent 转交）
        if (SelectedWorkflow is WorkflowItem wf)
        {
            var justCreated = false;
            if (_currentConversationId == Guid.Empty)
            {
                justCreated = true;
                _currentConversationId = Guid.NewGuid();
                var conv = new Conversation
                {
                    ConversationId = _currentConversationId,
                    AgentId = wf.Id.ToString(),
                    AgentName = wf.Name,
                    Title = content.Length > 20 ? content[..20] + "…" : content,
                };
                conv.Append(Aether.AI.Core.Messages.Message.User(content));
                await _conversationStore.SaveAsync(conv);
                await _dispatcher.InvokeAsync(() =>
                {
                    Conversations.Insert(0, new ConversationItem
                    {
                        Id = conv.ConversationId, Title = conv.Title, Preview = content,
                        DisplayTime = FormatTime(conv.UpdatedAt), AgentId = conv.AgentId, AgentName = conv.AgentName, IsCurrent = true,
                        ResumeCommand = ResumeConversationCommand,
                        DeleteCommand = DeleteConversationCommand,
                    });
                    foreach (var c in Conversations) c.IsCurrent = c.Id == _currentConversationId;
                });
            }

            var fullText = new System.Text.StringBuilder();
            var desiredMode = HasModeSupport ? SelectedMode : null;
            var runItems = await RunSegmentedStreamAsync(turn, wf.Name,
                (handlers, approvalResponse, ct) => _workflowRunner.RunStreamingAsync(_currentConversationId, wf.Id, content, handlers, approvalResponse, desiredMode, ct),
                fullText, _runCts?.Token ?? default);
            await PersistTurnAsync(content, runItems, fullText.ToString());
            await RefreshModeAsync();
            if (justCreated)
                GenerateAndApplyTitleAsync(content, fullText.ToString()).SafeFireAndForget();

            return;
        }

        // 选中已召唤的自定义专家时，本地流式运行（不走 SignalR）
        if (SelectedAgentItem is ExpertItem expert)
        {
            var justCreated = false;
            // 待新聊天 → 首次发送才落库创建会话
            if (_currentConversationId == Guid.Empty)
            {
                justCreated = true;
                _currentConversationId = Guid.NewGuid();
                var conv = new Conversation
                {
                    ConversationId = _currentConversationId,
                    AgentId = expert.Id.ToString(),
                    AgentName = expert.Name,
                    Title = content.Length > 20 ? content[..20] + "…" : content,
                };
                conv.Append(Aether.AI.Core.Messages.Message.User(content));
                await _conversationStore.SaveAsync(conv);
                await _dispatcher.InvokeAsync(() =>
                {
                    Conversations.Insert(0, new ConversationItem
                    {
                        Id = conv.ConversationId,
                        Title = conv.Title,
                        Preview = content,
                        DisplayTime = FormatTime(conv.UpdatedAt),
                        AgentId = conv.AgentId,
                        AgentName = conv.AgentName,
                        IsCurrent = true,
                        ResumeCommand = ResumeConversationCommand,
                        DeleteCommand = DeleteConversationCommand,
                    });
                    foreach (var c in Conversations) c.IsCurrent = c.Id == _currentConversationId;
                });
            }

            var fullText = new System.Text.StringBuilder();
            var desiredMode = HasModeSupport ? SelectedMode : null;
            var runItems = await RunSegmentedStreamAsync(turn, expert.Name,
                (handlers, approvalResponse, ct) => _agentRunner.RunStreamingAsync(_currentConversationId, expert.Id, content, handlers, approvalResponse, desiredMode, ct),
                fullText, _runCts?.Token ?? default);

            // 持久化 transcript（user + 思考段/工具步/回答）并乐观 bump 侧栏项
            await PersistTurnAsync(content, runItems, fullText.ToString());
            await RefreshModeAsync();

            // 首轮后异步用 LLM 生成会话标题（不阻塞；失败静默）
            if (justCreated)
                GenerateAndApplyTitleAsync(content, fullText.ToString()).SafeFireAndForget();

            return;
        }

        // 通过 SignalR 发送到服务端（若已连接）
        if (_deviceChatClient.IsConnected)
        {
            try
            {
                var conversationId = _recentConversationId != Guid.Empty
                    ? _recentConversationId
                    : Guid.NewGuid();
                await _deviceChatClient.SendMessageAsync(conversationId, content, "Text");
                _recentConversationId = conversationId;
            }
            catch (Exception ex)
            {
                turn.Children.Add(new ChatMessageItem
                {
                    Side = ChatMessageSide.Left,
                    ItemType = "text",
                    Sender = "系统",
                    Content = $"发送失败：{ex.Message}",
                    IsStreaming = false
                });
                turn.Finish(TurnStatus.Error);
            }
        }
        else
        {
            // 未连接时本地回复
            turn.Children.Add(new ChatMessageItem
            {
                Side = ChatMessageSide.Left,
                ItemType = "text",
                Sender = "Aether",
                Content = "设备未连接，请在设置中配置设备连接后重试。",
                IsStreaming = false
            });
            turn.Finish(TurnStatus.Done);
        }
    }

    /// <summary>异步用 LLM 生成标题并更新当前会话（持久化 + 侧栏项）。</summary>
    private async Task GenerateAndApplyTitleAsync(string userMessage, string assistantReply)
    {
        if (_currentConversationId == Guid.Empty) return;
        try
        {
            var title = await _titleService.GenerateTitleAsync(userMessage, assistantReply);
            if (string.IsNullOrWhiteSpace(title)) return;

            var conv = await _conversationStore.GetAsync(_currentConversationId);
            if (conv is null) return;
            conv.Title = title;
            await _conversationStore.SaveAsync(conv);

            await _dispatcher.InvokeAsync(() =>
            {
                var item = Conversations.FirstOrDefault(c => c.Id == _currentConversationId);
                if (item is not null) item.Title = title;
            });
        }
        catch { /* 标题生成失败不影响主流程 */ }
    }

    /// <summary>把本轮 transcript（user + 思考段/工具步/回答）追加到当前会话并持久化 + 乐观 bump 侧栏项。</summary>
    private async Task PersistTurnAsync(string userContent,
        IReadOnlyList<ChatMessageItem> runItems, string fullText)
    {
        if (_currentConversationId == Guid.Empty) return;
        try
        {
            var conv = await _conversationStore.GetAsync(_currentConversationId);
            if (conv is null) return;
            // user 已在创建时追加过；非首轮回补
            var last = conv.Messages.Count > 0 ? conv.Messages[^1] : null;
            if (last is null || last.Role != Aether.AI.Core.Messages.MessageRole.User || last.Content != userContent)
                conv.Append(Aether.AI.Core.Messages.Message.User(userContent));
            // 按时序持久化本轮 transcript（计划卡 / 思考段 / 工具步 / 回答）。用户消息已单独追加，审批卡运行期态不持久化。
            foreach (var item in runItems)
            {
                if (item.IsUser || item.IsApprovalCard) continue;
                if (item.IsText && item.IsReasoning && string.IsNullOrWhiteSpace(item.Content)) continue; // 跳过空思考段
                conv.Append(ToTranscriptMessage(item));
            }
            await _conversationStore.SaveAsync(conv);

            await _dispatcher.InvokeAsync(() =>
            {
                var item = Conversations.FirstOrDefault(c => c.Id == _currentConversationId);
                if (item is not null)
                {
                    item.Preview = fullText.Length > 40 ? fullText[..40] + "…" : fullText;
                    item.DisplayTime = FormatTime(conv.UpdatedAt);
                    // 移到顶部
                    Conversations.Remove(item);
                    Conversations.Insert(0, item);
                }
            });
        }
        catch { /* transcript 持久化失败不阻塞对话 */ }
    }

    /// <summary>ChatMessageItem → 持久化 Message。工具步用 metadata 存 input/output JSON；思考段 kind=reasoning；计划卡 kind=plan + todos JSON。</summary>
    private static Aether.AI.Core.Messages.Message ToTranscriptMessage(ChatMessageItem item)
    {
        var jsonOpts = new System.Text.Json.JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        if (item.IsPlanCard)
        {
            var todosJson = System.Text.Json.JsonSerializer.Serialize(
                item.Todos.Select(t => new { t.Title, Desc = t.Description, t.IsComplete }), jsonOpts);
            return new Aether.AI.Core.Messages.Message
            {
                Role = Aether.AI.Core.Messages.MessageRole.Assistant,
                Content = string.Empty,
                Metadata = new Dictionary<string, string> { ["kind"] = "plan", ["todos"] = todosJson },
            };
        }
        if (item.IsToolStep)
        {
            return new Aether.AI.Core.Messages.Message
            {
                Role = Aether.AI.Core.Messages.MessageRole.Tool,
                ToolName = item.ToolName ?? string.Empty,
                ToolCallId = item.CallId,
                Content = string.Empty,
                Metadata = new Dictionary<string, string>
                {
                    ["kind"] = "tool",
                    ["input"] = item.Input is null ? "" : System.Text.Json.JsonSerializer.Serialize(item.Input, jsonOpts),
                    ["output"] = item.Output is null ? "" : System.Text.Json.JsonSerializer.Serialize(item.Output, jsonOpts),
                },
            };
        }
        return new Aether.AI.Core.Messages.Message
        {
            Role = Aether.AI.Core.Messages.MessageRole.Assistant,
            Content = item.Content ?? string.Empty,
            Metadata = item.IsReasoning ? new Dictionary<string, string> { ["kind"] = "reasoning" } : null,
        };
    }

    /// <summary>是否还有更早的历史轮未渲染（供 View 判断是否触发增量加载）。</summary>
    public bool HasMoreHistory => _pendingHistoryTurns is { Count: > 0 };

    /// <summary>
    /// 增量加载更早的历史轮：从暂存队列取出最多 <see cref="HistoryLoadBatch"/> 轮，插入到 <see cref="Messages"/> 顶部。
    /// 返回实际加载的轮数（0 表示无更多历史）。markdown 解析仅在加入 Messages 绑定时发生，故分批可避免一次性卡顿。
    /// </summary>
    public int LoadMoreHistory()
    {
        if (_pendingHistoryTurns is null || _pendingHistoryTurns.Count == 0) return 0;
        int take = Math.Min(HistoryLoadBatch, _pendingHistoryTurns.Count);
        // 暂存按时间正序（最旧在前）；取最新的待加载批次（队列尾部），倒序 Insert(0) 以保持正序。
        int start = _pendingHistoryTurns.Count - take;
        for (int i = start + take - 1; i >= start; i--)
            Messages.Insert(0, _pendingHistoryTurns[i]);
        _pendingHistoryTurns.RemoveRange(start, take);
        return take;
    }

    /// <summary>按 user 消息分组重建全部 turn：每条 user 消息开新轮，其后 assistant/tool 归入该轮。历史轮标记 Done。</summary>
    private static List<ChatTurnItem> BuildTurns(IEnumerable<Aether.AI.Core.Messages.Message> messages, string agentName)
    {
        var turns = new List<ChatTurnItem>();
        ChatTurnItem? cur = null;
        foreach (var m in messages)
        {
            var ci = FromTranscriptMessage(m, agentName);
            if (m.Role == Aether.AI.Core.Messages.MessageRole.User)
            {
                cur = new ChatTurnItem { Header = m.Content };
                cur.Children.Add(ci);
                turns.Add(cur);
            }
            else
            {
                cur ??= new ChatTurnItem { Header = string.Empty };
                if (cur.Children.Count == 0) turns.Add(cur);
                cur.Children.Add(ci);
            }
        }
        foreach (var t in turns) t.Finish(TurnStatus.Done); // 历史轮已结束
        return turns;
    }

    /// <summary>持久化 Message → ChatMessageItem（还原历史时）。工具步/思考段从 metadata 还原。</summary>
    private static ChatMessageItem FromTranscriptMessage(Aether.AI.Core.Messages.Message m, string agentName)
    {
        if (m.Role == Aether.AI.Core.Messages.MessageRole.User)
            return new ChatMessageItem { Side = ChatMessageSide.Right, ItemType = "text", Sender = "我", Content = m.Content, IsStreaming = false };

        if (m.Role == Aether.AI.Core.Messages.MessageRole.Tool)
            return new ChatMessageItem
            {
                Side = ChatMessageSide.Left,
                ItemType = "tool",
                Sender = agentName,
                ToolName = m.ToolName ?? string.Empty,
                CallId = m.ToolCallId ?? string.Empty,
                Input = MetaDict(m, "input"),
                Output = MetaDict(m, "output"),
                IsToolCompleted = MetaHas(m, "output"),
            };

        var kind = MetaVal(m, "kind");
        if (kind == "plan")
        {
            var card = new ChatMessageItem { Side = ChatMessageSide.Left, ItemType = "plan", Sender = agentName, IsStreaming = false };
            var todosJson = MetaVal(m, "todos");
            if (!string.IsNullOrEmpty(todosJson))
            {
                try
                {
                    foreach (var t in System.Text.Json.JsonSerializer.Deserialize<List<TodoDto>>(todosJson)!)
                        card.Todos.Add(new TodoItemVM { Title = t.Title ?? string.Empty, Description = t.Desc, IsComplete = t.IsComplete });
                }
                catch { /* 计划反序列化失败不阻塞恢复 */ }
            }
            return card;
        }

        var isReasoning = kind == "reasoning";
        return new ChatMessageItem
        {
            Side = ChatMessageSide.Left,
            ItemType = "text",
            Sender = agentName,
            Content = m.Content,
            IsStreaming = false,
            IsReasoning = isReasoning,
        };
    }

    private static Dictionary<string, object>? MetaDict(Aether.AI.Core.Messages.Message m, string key)
    {
        if (m.Metadata is null || !m.Metadata.TryGetValue(key, out var json) || string.IsNullOrEmpty(json)) return null;
        try { return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(json); }
        catch { return null; }
    }

    private static bool MetaHas(Aether.AI.Core.Messages.Message m, string key)
        => m.Metadata is not null && m.Metadata.TryGetValue(key, out var v) && !string.IsNullOrEmpty(v);

    private static string? MetaVal(Aether.AI.Core.Messages.Message m, string key)
        => m.Metadata is not null && m.Metadata.TryGetValue(key, out var v) ? v : null;
}
