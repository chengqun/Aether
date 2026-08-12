using System.Collections.ObjectModel;
using System.Windows.Input;
using Acme.Agents.AI;
using Acme.DeviceConnect.Domain;
using Acme.Llm.Application;
using Acme.Llm.Domain;
using Acme.Notes.Domain;
using Aether.AI.Core.Agents;
using Aether.AI.Core.Conversations;
using Aether.AI.Hosting;
using Aether.AI.Memory;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.Desktop.Mvvm;

/// <summary>
/// 聊天工作台 ViewModel：从 <see cref="MainViewModelBase"/> 抽出的完整聊天引擎（流式分段/转写持久化/会话管理/
/// 对话目标/模式/计划/审批/历史增量）。行为保持——原 MainViewModelBase 的聊天方法体逐字搬入。
/// 可独立组合：Stockapp/Qifu 桌面端经 MainViewModelBase 持有；Codex 工作台直接作为 chat panel。
/// ChatView 绑定此类型（<c>DataContext="{Binding Chat}"</c>），code-behind 经 <see cref="IChatViewHost"/> 访问。
/// </summary>
public partial class ChatWorkspaceViewModel : ObservableObject, IChatViewHost
{
    private readonly ICustomAgentRunner _agentRunner;
    private readonly ICustomWorkflowRunner _workflowRunner;
    private readonly IUiDispatcher _dispatcher;
    private readonly IConversationStore _conversationStore;
    private readonly IPreferencesStore _preferences;
    private readonly IConversationTitleService _titleService;
    private readonly IDeviceAutoConnectService _autoConnect;
    private readonly IApprovalDialogService? _approvalDialog;
    private readonly IDeviceChatClient _deviceChatClient;
    private readonly ILlmConfigService _llmService;
    private readonly IServiceProvider _serviceProvider;
    private readonly ISettingsDialogService _settingsDialog;

    private readonly ExpertsViewModel _experts;
    private readonly WorkflowsViewModel _workflows;
    private readonly RulesViewModel _rules;

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

    public ChatWorkspaceViewModel(
        IServiceProvider serviceProvider,
        IDeviceChatClient deviceChatClient,
        ILlmConfigService llmService,
        ISettingsDialogService settingsDialog,
        ExpertsViewModel experts,
        WorkflowsViewModel workflows,
        RulesViewModel rules)
    {
        _serviceProvider = serviceProvider;
        _deviceChatClient = deviceChatClient;
        _llmService = llmService;
        _settingsDialog = settingsDialog;
        _agentRunner = serviceProvider.GetRequiredService<ICustomAgentRunner>();
        _workflowRunner = serviceProvider.GetRequiredService<ICustomWorkflowRunner>();
        _dispatcher = serviceProvider.GetRequiredService<IUiDispatcher>();
        _conversationStore = serviceProvider.GetRequiredService<IConversationStore>();
        _preferences = serviceProvider.GetRequiredService<IPreferencesStore>();
        _titleService = serviceProvider.GetRequiredService<IConversationTitleService>();
        _autoConnect = serviceProvider.GetRequiredService<IDeviceAutoConnectService>();
        _approvalDialog = serviceProvider.GetService<IApprovalDialogService>();
        _experts = experts;
        _workflows = workflows;
        _rules = rules;

        // 三类目标的召唤状态变化（toggle 入池/出池）：刷新统一下拉池 + 持久化召唤列表。
        // 召唤只入池，不自动激活——激活是下拉选择的独立动作（与 agent 模型一致）。
        _experts.SummonChanged += OnChatTargetsChanged;
        _workflows.SummonChanged += OnChatTargetsChanged;
        _rules.SummonChanged += OnChatTargetsChanged;

        Conversations.CollectionChanged += (_, _) => HasConversations = Conversations.Count > 0;

        SubscribeToConnectionStateChanges();
    }

    /// <summary>设置对话框服务（ChatView 头部「设备连接配置」按钮调用）。</summary>
    public ISettingsDialogService? SettingsDialog => _settingsDialog;

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

    /// <summary>由 shell 在设备连接变更后调用（SettingsViewModel/ISettingsDialogService 经 MainViewModelBase 透传）。</summary>
    public void RefreshConnectionState()
    {
        _dispatcher.Post(() =>
        {
            IsConnected = _deviceChatClient.IsConnected;
            StatusText = IsConnected ? "已连接" : "未连接";
        });
    }

    /// <summary>LLM 配置变更后刷新聊天页的模型下拉（设置页新增/编辑/删除后经 MainViewModelBase 透传调用）。</summary>
    public async Task RefreshLlmConfigsAsync()
    {
        await _dispatcher.InvokeAsync(async () => await LoadLlmConfigsAsync());
    }

    /// <summary>聊天引擎启动期初始化：LLM 配置加载 + 召唤/选中目标恢复 + 最近会话加载 + 连接态 + 自动连接。
    /// 须在 shell 已加载 Experts/Workflows/Rules 之后调用（RestoreSummonedAndSelectedAsync 读这三类列表）。</summary>
    public async Task InitializeAsync()
    {
        IsConnected = _deviceChatClient.IsConnected;
        StatusText = IsConnected ? "已连接" : "未连接";

        _isInitializing = true;
        try
        {
            await LoadLlmConfigsAsync();
            await RestoreSummonedAndSelectedAsync();
            await RefreshConversationsAsync();
        }
        finally { _isInitializing = false; }

        await TryAutoConnectDeviceAsync();
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
        // 本轮 todos 信号重置：只有本轮真的 todos_* 了才在末尾插计划快照卡，避免上一轮的计划状态
        // （HasActivePlan/ActiveTodos 跨轮保留供浮动面板显示）被误当作本轮产出 → 计划卡逐轮累积。
        // SendInputAsync 处也重置一次（留作语义注释），这里覆盖远程消息路径（不经过 SendInputAsync）。
        _todosTouchedThisTurn = false;
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
                // 每轮一快照：本轮确有 todos 更新（_todosTouchedThisTurn）且仍有活动计划时，
                // 把计划卡插到本轮首（用户消息之后、agent 工作之前），内联在对话里、出现在"制定计划"的位置
                // （参考 Claude Code），而非堆在轮末与浮动面板叠底。借已有 kind=plan 持久化脚手架——
                // 历史会话恢复后仍能看到每轮的计划。须用 _todosTouchedThisTurn 门控：HasActivePlan/ActiveTodos
                // 跨轮保留供浮动面板显示，若不门控则每一轮都会用陈旧计划插一张快照 → 计划卡逐轮累积。
                if (_todosTouchedThisTurn && HasActivePlan && ActiveTodos.Count > 0)
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
            // 审批恢复轮 MAF 会重新发出已批准工具的 Call（同 CallId）。第一轮已建过同 CallId 的工具卡
            // （在审批卡之前），若不处理会留下 orphan 卡（无结果、显"卡住/在跑"）+ 恢复轮再建一张 →
            // 同一逻辑调用两张卡、显示错乱。这里移除旧 orphan，在当前位置（审批卡之后）新建一张，
            // 保证后续 Result 落在审批卡之后、时序正确。
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

    /// <summary>todos_* 调用后更新浮动计划面板（ActiveTodos）。快照（每轮一卡、入对话流持久化）在 RunSegmentedStreamAsync 末尾插入。
    /// 计划卡随 transcript 持久化、恢复，重启后仍在。</summary>
    private void HandleTodoList(IList<ChatMessageItem> items, IReadOnlyList<TodoItemInfo> list, string senderName)
    {
        // 计划只显示在浮动面板（输入框上方），不在对话流里重复显示。
        // 去重：agent 跨轮重新规划时有时对同一事项重复 todos_add（旧项仍在 TodoProvider 列表里又被加一遍），
        // 致一份计划里出现两条内容相同的条目。按 Title（忽略大小写/首尾空白）去重，保留首次出现顺序；
        // 若任一副本已完成，则合并为已完成。
        ActiveTodos.Clear();
        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var merged = new List<(string Title, string? Description, bool IsComplete)>();
        foreach (var t in list)
        {
            var key = (t.Title ?? string.Empty).Trim();
            if (key.Length == 0)
            {
                merged.Add((t.Title ?? string.Empty, t.Description, t.IsComplete));
                continue;
            }
            if (seen.TryGetValue(key, out var i))
            {
                if (t.IsComplete) merged[i] = (merged[i].Title, merged[i].Description, true);
            }
            else
            {
                seen[key] = merged.Count;
                merged.Add((t.Title ?? string.Empty, t.Description, t.IsComplete));
            }
        }
        foreach (var m in merged)
            ActiveTodos.Add(new TodoItemVM { Title = m.Title, Description = m.Description ?? string.Empty, IsComplete = m.IsComplete });
        HasActivePlan = ActiveTodos.Count > 0;
        // PlanSummary（"X/Y" 计数）是计算属性，仅依赖 HasActivePlan/IsPlanExpanded 变化才上抛。
        // 完成 todo 时 HasActivePlan 不变（仍 true）→ 计数不刷新，用户须点表头展开/收起才触发 OnIsPlanExpandedChanged
        // 间接刷新计数。这里在集合变更后显式上抛，让计数随完成进度实时更新。
        OnPropertyChanged(nameof(PlanSummary));
        _todosTouchedThisTurn = true; // 本轮确有 todos 更新（SendInputAsync 末尾据此置 PlanAwaitingAccept）
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

        // 选中对话目标时，用该目标流式回复（agent/规则走 _agentRunner、工作流走 _workflowRunner）
        var remoteKind = KindOf(SelectedChatTarget);
        if (remoteKind != ChatTargetKind.None)
        {
            var (targetId, targetName) = SelectedChatTarget switch
            {
                ExpertItem ei => (ei.Id, ei.Name),
                WorkflowItem wi => (wi.Id, wi.Name),
                _ => (Guid.Empty, "目标"),
            };
            var fullText = new System.Text.StringBuilder();
            var desiredMode = HasModeSupport ? SelectedMode : null;
            await RunSegmentedStreamAsync(remoteTurn, targetName,
                (handlers, approvalResponse, ct) => remoteKind == ChatTargetKind.Workflow
                    ? _workflowRunner.RunStreamingAsync(msg.ConversationId, targetId, msg.Content, handlers, approvalResponse, desiredMode, ct)
                    : _agentRunner.RunStreamingAsync(msg.ConversationId, targetId, msg.Content, handlers, approvalResponse, desiredMode, ct),
                fullText);
            if (remoteKind != ChatTargetKind.Rule) await RefreshModeAsync(msg.ConversationId);

            // 远程消息触发运行：把回复通过 SignalR 发回远程发送方（否则对方收不到结果）。
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
    /// <summary>plan 模式轮、且本轮产出计划后置 true（待用户接受）；execute 轮/接受后即 false。
    /// 脱钩于 SelectedMode——避免 RefreshModeAsync 把模式回翻成 plan 时接受条误弹。</summary>
    [ObservableProperty] private bool _planAwaitingAccept;
    /// <summary>本轮是否调过 todos_*（区分「本轮新计划」vs「上轮残留计划」）。每轮开始重置。</summary>
    private bool _todosTouchedThisTurn;
    public string PlanSummary => HasActivePlan ? $"📋 计划 ({ActiveTodos.Count(t => t.IsComplete)}/{ActiveTodos.Count})" : "";
    partial void OnHasActivePlanChanged(bool value)
    {
        OnPropertyChanged(nameof(PlanSummary));
        OnPropertyChanged(nameof(ShowPlanDismiss));
    }
    partial void OnIsPlanExpandedChanged(bool value) => OnPropertyChanged(nameof(PlanSummary));
    partial void OnPlanAwaitingAcceptChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowPlanAccept));
        OnPropertyChanged(nameof(ShowPlanDismiss));
    }
    [RelayCommand] private void TogglePlanExpanded() => IsPlanExpanded = !IsPlanExpanded;
    /// <summary>确认执行完毕：收起浮动计划面板（HasActivePlan→false）。execute 完成后用户手动点。</summary>
    [RelayCommand]
    private void DismissPlan()
    {
        ActiveTodos.Clear();
        HasActivePlan = false;
    }

    /// <summary>当前 agent 是否启用 AgentModeProvider（控制模式选择器/plan-accept 显隐）。</summary>
    [ObservableProperty]
    private bool _hasModeSupport;

    /// <summary>当前会话 AgentMode（plan/execute…）；null=未知/未启用。输入框选择器 + plan-accept 条绑定。</summary>
    [ObservableProperty]
    private string? _selectedMode = "plan";

    /// <summary>agent 是否正在流式运行（驱动 plan-accept 条显隐）。</summary>
    [ObservableProperty]
    private bool _isAgentRunning;

    /// <summary>plan 模式轮产出计划后（PlanAwaitingAccept）、非运行中时显示"接受计划→执行"条。
    /// 门控用「本轮是否待接受」而非「当前模式」，避免 execute 跑完 RefreshModeAsync 把模式回翻 plan 时误弹。</summary>
    public bool ShowPlanAccept => HasModeSupport && PlanAwaitingAccept && !IsAgentRunning;

    /// <summary>有计划、非运行中、且不在待接受态 → execute 已跑完，可点「确认执行完毕」收起面板。</summary>
    public bool ShowPlanDismiss => HasActivePlan && !IsAgentRunning && !PlanAwaitingAccept;

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
            ActiveTodos.Clear(); HasActivePlan = false; PlanAwaitingAccept = false;
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
            // 恢复时按绑定 id 在三类列表里找目标（规则/agent 都是 ExpertItem，按所在列表区分）。
            // 会话目标可能已取消召唤 → 自动入池，保证下拉可见且选中（抑制 OnSelectedChatTargetChanged 重置副作用）。
            if (Guid.TryParse(item.AgentId, out var aid))
            {
                ExpertItem? ei = _rules.Rules.FirstOrDefault(r => r.Id == aid)
                              ?? _experts.Experts.FirstOrDefault(e => e.Id == aid);
                WorkflowItem? wi = ei is null ? _workflows.Workflows.FirstOrDefault(w => w.Id == aid) : null;
                object? target = (object?)ei ?? wi;
                if (target is ExpertItem e2 && !e2.IsSummoned) e2.IsSummoned = true;
                else if (target is WorkflowItem w2 && !w2.IsSummoned) w2.IsSummoned = true;
                if (target is not null && !ChatTargetItems.Contains(target)) ChatTargetItems.Add(target);
                SelectedChatTarget = target;
            }
            _isInitializing = false;

            Messages.Clear();
            ActiveTodos.Clear(); HasActivePlan = false; PlanAwaitingAccept = false;
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
            ActiveTodos.Clear(); HasActivePlan = false; PlanAwaitingAccept = false;
            }
        }
        catch (Exception ex)
        {
            StatusText = $"删除会话失败：{ex.Message}";
        }
    }

    /// <summary>从存储加载最近会话列表（按 UpdatedAt 倒序）。增量渲染：只把最近若干条加入列表，更早的滚到底部再加载。</summary>
    public async Task RefreshConversationsAsync()
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

    /// <summary>聊天头统一下拉池：已召唤的 agent/规则/工作流（混合 object，含 ExpertItem + WorkflowItem）。</summary>
    public ObservableCollection<object> ChatTargetItems { get; } = new();
    public ObservableCollection<LlmConfigDto> LlmSelectItems { get; } = new();

    /// <summary>当前激活的对话目标（下拉选中）。agent/规则是 ExpertItem、工作流是 WorkflowItem。</summary>
    [ObservableProperty]
    private object? _selectedChatTarget;

    /// <summary>对话目标类型判别（集中所有分支点的类型歧视）。</summary>
    private enum ChatTargetKind { None, Agent, Rule, Workflow }
    private static ChatTargetKind KindOf(object? t) => t switch
    {
        WorkflowItem => ChatTargetKind.Workflow,
        ExpertItem e when e.NodeType == NoteNodeType.Rule => ChatTargetKind.Rule,
        ExpertItem => ChatTargetKind.Agent,
        _ => ChatTargetKind.None,
    };

    /// <summary>目标的逻辑标识（Id+Kind）。reload 后实例被替换，按引用匹配会丢选中，
    /// 故用 (Id,Kind) 作为「同一目标」的判据。</summary>
    private static (Guid Id, ChatTargetKind Kind) TargetKey(object? t, ChatTargetKind kind) => t switch
    {
        ExpertItem e => (e.Id, kind),
        WorkflowItem w => (w.Id, ChatTargetKind.Workflow),
        _ => (Guid.Empty, ChatTargetKind.None),
    };
    private (Guid Id, ChatTargetKind Kind) _selectedTargetKey = (Guid.Empty, ChatTargetKind.None);

    [ObservableProperty]
    private LlmConfigDto? _selectedLlmItem;

    /// <summary>从偏好恢复三类目标的召唤状态与选中目标（跨重启保留），并保证启动时有一个默认选中。</summary>
    private async Task RestoreSummonedAndSelectedAsync()
    {
        try
        {
            // 读结构化偏好 chatTargets；缺失时迁移旧键（仅 agent）
            var json = await _preferences.GetAsync("chatTargets");
            ChatTargetPrefs? prefs = null;
            if (!string.IsNullOrWhiteSpace(json))
            {
                prefs = System.Text.Json.JsonSerializer.Deserialize<ChatTargetPrefs>(json, _prefsJson);
            }
            else
            {
                var legacySummoned = await _preferences.GetAsync("summonedAgentIds");
                var legacySelected = await _preferences.GetAsync("selectedAgentId");
                if (!string.IsNullOrWhiteSpace(legacySummoned))
                {
                    var ids = System.Text.Json.JsonSerializer.Deserialize<string[]>(legacySummoned) ?? Array.Empty<string>();
                    prefs = new ChatTargetPrefs
                    {
                        Summoned = new() { Agent = ids.ToList() },
                        Selected = !string.IsNullOrWhiteSpace(legacySelected)
                            ? new ChatTargetSelected { Kind = "Agent", Id = legacySelected } : null,
                    };
                }
            }

            // 按已保存 id 集合恢复三类 IsSummoned
            if (prefs is not null)
            {
                var ag = prefs.Summoned?.Agent is { Count: > 0 } ? new HashSet<string>(prefs.Summoned.Agent) : null;
                var rg = prefs.Summoned?.Rule is { Count: > 0 } ? new HashSet<string>(prefs.Summoned.Rule) : null;
                var wg = prefs.Summoned?.Workflow is { Count: > 0 } ? new HashSet<string>(prefs.Summoned.Workflow) : null;
                foreach (var e in _experts.Experts) e.IsSummoned = ag?.Contains(e.Id.ToString()) ?? false;
                foreach (var r in _rules.Rules) r.IsSummoned = rg?.Contains(r.Id.ToString()) ?? false;
                foreach (var w in _workflows.Workflows) w.IsSummoned = wg?.Contains(w.Id.ToString()) ?? false;
            }

            // 三类全空时自动召唤首个 agent，保证下拉非空、有默认目标
            if (_experts.GetSummonedExperts().Count == 0
                && _rules.GetSummonedRules().Count == 0
                && _workflows.GetSummonedWorkflows().Count == 0
                && _experts.Experts.Count > 0)
            {
                _experts.Experts[0].IsSummoned = true;
                await PersistSummonedTargetsAsync();
            }

            // 同步填充下拉（此时已在 UI 线程；不依赖后续的 RefreshChatTargetItems）
            ChatTargetItems.Clear();
            foreach (var e in _experts.GetSummonedExperts()) ChatTargetItems.Add(e);
            foreach (var r in _rules.GetSummonedRules()) ChatTargetItems.Add(r);
            foreach (var w in _workflows.GetSummonedWorkflows()) ChatTargetItems.Add(w);

            // 选中：已保存的 → 回退到池中首项
            object? saved = null;
            if (prefs?.Selected is { } sel && Guid.TryParse(sel.Id, out var sid))
            {
                saved = sel.Kind switch
                {
                    "Rule" => _rules.Rules.FirstOrDefault(r => r.Id == sid),
                    "Agent" => _experts.Experts.FirstOrDefault(e => e.Id == sid),
                    "Workflow" => (object?)_workflows.Workflows.FirstOrDefault(w => w.Id == sid),
                    _ => null,
                };
                // 保存的目标可能已取消召唤 → 自动入池，保证下拉可见且选中
                if (saved is ExpertItem e2 && !e2.IsSummoned) { e2.IsSummoned = true; ChatTargetItems.Add(e2); }
                else if (saved is WorkflowItem w2 && !w2.IsSummoned) { w2.IsSummoned = true; ChatTargetItems.Add(w2); }
            }
            SelectedChatTarget = saved ?? ChatTargetItems.FirstOrDefault();

            // 恢复最近会话展开状态
            var expanded = await _preferences.GetAsync("recentExpanded");
            if (expanded == "0") IsRecentExpanded = false;
        }
        catch { /* 偏好恢复失败不阻塞 */ }
    }

    /// <summary>持久化三类目标的召唤列表（toggle 入池/出池时调）。</summary>
    private async Task PersistSummonedTargetsAsync()
    {
        try
        {
            var prefs = new ChatTargetPrefs
            {
                Summoned = new()
                {
                    Agent = _experts.GetSummonedExperts().Select(e => e.Id.ToString()).ToList(),
                    Rule = _rules.GetSummonedRules().Select(r => r.Id.ToString()).ToList(),
                    Workflow = _workflows.GetSummonedWorkflows().Select(w => w.Id.ToString()).ToList(),
                },
                Selected = CurrentSelectedPrefs(),
            };
            await _preferences.SetAsync("chatTargets", System.Text.Json.JsonSerializer.Serialize(prefs, _prefsJson));
        }
        catch { /* 偏好持久化失败不阻塞 */ }
    }

    /// <summary>持久化当前选中目标（下拉切换时调）：读现有 prefs 只更 Selected 再写回。</summary>
    private async Task PersistSelectedTargetAsync()
    {
        try
        {
            var json = await _preferences.GetAsync("chatTargets");
            var prefs = string.IsNullOrWhiteSpace(json)
                ? new ChatTargetPrefs()
                : (System.Text.Json.JsonSerializer.Deserialize<ChatTargetPrefs>(json, _prefsJson) ?? new ChatTargetPrefs());
            prefs.Selected = CurrentSelectedPrefs();
            await _preferences.SetAsync("chatTargets", System.Text.Json.JsonSerializer.Serialize(prefs, _prefsJson));
        }
        catch { /* 偏好持久化失败不阻塞 */ }
    }

    private ChatTargetSelected? CurrentSelectedPrefs()
    {
        var kind = KindOf(SelectedChatTarget);
        if (kind == ChatTargetKind.None) return null;
        var id = SelectedChatTarget switch
        {
            ExpertItem ei => ei.Id.ToString(),
            WorkflowItem wi => wi.Id.ToString(),
            _ => null,
        };
        return id is null ? null : new ChatTargetSelected { Kind = kind.ToString(), Id = id };
    }

    private static readonly System.Text.Json.JsonSerializerOptions _prefsJson = new()
    {
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };


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
                foreach (var expert in _experts.GetSummonedExperts())
                    _agentRunner.Invalidate(expert.Id);
            }
        }
    }

    /// <summary>三类目标的召唤状态变化（toggle 入池/出池）：刷新统一下拉池 + 持久化召唤列表。
    /// 召唤只入池，不自动激活——激活是下拉选择的独立动作（与 agent 模型一致）。</summary>
    private async void OnChatTargetsChanged()
    {
        RefreshChatTargetItems();
        await PersistSummonedTargetsAsync();
    }

    /// <summary>下拉选中目标变化：更新模式支持；非启动期统一开新会话（三类一致）+ 持久化选中。</summary>
    partial void OnSelectedChatTargetChanged(object? value)
    {
        var kind = KindOf(value);
        // 模式支持：工作流支持；agent 看 EnableAgentModeProvider；规则无 mode。始终更新（启动期恢复选中也要显选择器）。
        HasModeSupport = kind == ChatTargetKind.Workflow
                      || (kind == ChatTargetKind.Agent && value is ExpertItem e && e.EnableAgentModeProvider);
        _syncingMode = true;
        SelectedMode = HasModeSupport ? "plan" : null;
        _syncingMode = false;

        var newKey = TargetKey(value, kind);
        var prevKey = _selectedTargetKey;
        _selectedTargetKey = newKey; // 始终跟踪（含启动期/resume，保证下一次真实切换判据正确）

        if (_isInitializing) return;

        // 同一逻辑目标（同 Id+Kind；实例可能因 reload 被替换）→ 非用户切换，不重置会话。
        if (newKey == prevKey) return;

        // 统一行为变更：切换目标 → 开新会话（三类一致；会话已持久化，误切可从历史找回）。
        _currentConversationId = Guid.Empty;
        Messages.Clear();
        ActiveTodos.Clear(); HasActivePlan = false; PlanAwaitingAccept = false;
        _pendingHistoryTurns = null;

        PersistSelectedTargetAsync().SafeFireAndForget();
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
    partial void OnIsAgentRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowPlanAccept));
        OnPropertyChanged(nameof(ShowPlanDismiss));
    }
    partial void OnHasModeSupportChanged(bool value) => OnPropertyChanged(nameof(ShowPlanAccept));

    /// <summary>刷新聊天头统一下拉池（已召唤的 agent/规则/工作流），保留当前选中。
    /// 按 (Id,Kind) 匹配而非引用：reload 后实例被替换，引用匹配会丢选中并误切目标。</summary>
    private void RefreshChatTargetItems()
    {
        _dispatcher.Post(() =>
        {
            var current = SelectedChatTarget;
            var curKey = TargetKey(current, KindOf(current));
            ChatTargetItems.Clear();
            foreach (var e in _experts.GetSummonedExperts()) ChatTargetItems.Add(e);
            foreach (var r in _rules.GetSummonedRules()) ChatTargetItems.Add(r);
            foreach (var w in _workflows.GetSummonedWorkflows()) ChatTargetItems.Add(w);

            // 同一逻辑目标仍在池中 → 选中其新实例；否则回退第一个（避免清空导致选中丢失）
            object? next = null;
            if (curKey.Kind != ChatTargetKind.None)
                next = ChatTargetItems.FirstOrDefault(t => TargetKey(t, KindOf(t)) == curKey);
            SelectedChatTarget = next ?? ChatTargetItems.FirstOrDefault();
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
        var kind = KindOf(SelectedChatTarget);
        if (kind == ChatTargetKind.Rule) return; // 规则不支持 mode
        if (kind == ChatTargetKind.Workflow)
            await _workflowRunner.SetModeAsync(_currentConversationId, mode);
        else
            await _agentRunner.SetModeAsync(_currentConversationId, mode);
    }

    /// <summary>轮后从会话读实际模式（捕获 agent 自行 set_mode 的切换），带 guard 不回触 SetMode。</summary>
    private async Task RefreshModeAsync(Guid? conversationId = null)
    {
        var convId = conversationId ?? _currentConversationId;
        if (convId == Guid.Empty || !HasModeSupport) return;
        string? m = KindOf(SelectedChatTarget) == ChatTargetKind.Workflow
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
        PlanAwaitingAccept = false; // 已接受→不再显示接受条（execute 轮后也不会误弹）
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

        // 选中对话目标时，本地流式运行（agent/规则走 _agentRunner、工作流走 _workflowRunner）
        var kind = KindOf(SelectedChatTarget);
        if (kind != ChatTargetKind.None)
        {
            var (targetId, targetName) = SelectedChatTarget switch
            {
                ExpertItem ei => (ei.Id, ei.Name),
                WorkflowItem wi => (wi.Id, wi.Name),
                _ => (Guid.Empty, "目标"),
            };

            var justCreated = false;
            // 待新聊天 → 首次发送才落库创建会话
            if (_currentConversationId == Guid.Empty)
            {
                justCreated = true;
                _currentConversationId = Guid.NewGuid();
                var conv = new Conversation
                {
                    ConversationId = _currentConversationId,
                    AgentId = targetId.ToString(),
                    AgentName = targetName,
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
            // 规则无 mode（desiredMode 传 null）；agent/工作流传 HasModeSupport?SelectedMode:null。
            var desiredMode = kind == ChatTargetKind.Rule ? null : (HasModeSupport ? SelectedMode : null);
            _todosTouchedThisTurn = false; // 本轮 todos 信号重置（HandleTodoList 里置 true）
            var runItems = await RunSegmentedStreamAsync(turn, targetName,
                (handlers, approvalResponse, ct) => kind == ChatTargetKind.Workflow
                    ? _workflowRunner.RunStreamingAsync(_currentConversationId, targetId, content, handlers, approvalResponse, desiredMode, ct)
                    : _agentRunner.RunStreamingAsync(_currentConversationId, targetId, content, handlers, approvalResponse, desiredMode, ct),
                fullText, _runCts?.Token ?? default);

            // plan 模式轮、且本轮确实产出计划 → 待接受（驱动接受条）；execute 轮/无计划 → false。
            // 放在 RefreshModeAsync 之前：避免模式回翻干扰（门控已脱钩 SelectedMode，但语义上先定信号再读模式）。
            // 已开始执行则不再弹接受条：若本轮已有任意 todo 完成，说明 agent 在 plan 轮里已自行执行，
            // 此时再提示「计划已就绪，接受开始执行」与事实相悖（用户已看到执行结果）。
            PlanAwaitingAccept = desiredMode == "plan" && _todosTouchedThisTurn && HasActivePlan
                && !ActiveTodos.Any(t => t.IsComplete);

            // 持久化 transcript（user + 思考段/工具步/回答）并乐观 bump 侧栏项
            await PersistTurnAsync(content, runItems, fullText.ToString());
            if (kind != ChatTargetKind.Rule) await RefreshModeAsync();

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

/// <summary>聊天目标偏好（chatTargets 键）：三类召唤 id 集合 + 当前选中目标。camelCase JSON 持久化。</summary>
sealed record ChatTargetPrefs
{
    public ChatTargetSummoned? Summoned { get; set; }
    public ChatTargetSelected? Selected { get; set; }
}

sealed record ChatTargetSummoned
{
    public List<string> Agent { get; set; } = new();
    public List<string> Rule { get; set; } = new();
    public List<string> Workflow { get; set; } = new();
}

sealed record ChatTargetSelected
{
    public string? Kind { get; set; } // "Agent" | "Rule" | "Workflow"
    public string? Id { get; set; }
}
