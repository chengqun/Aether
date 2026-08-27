using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using Acme.DeviceConnect.Domain;
using Acme.Llm.Application;
using Acme.Llm.Domain;
using Acme.Notes.Domain;
using Aether.AI.Core.Agents;
using Aether.AI.Core.Conversations;
using Aether.AI.Core.Conversations.Runs;
using Aether.AI.Hosting;
using Aether.AI.Memory;
using Acme.Desktop.Mvvm.Git;
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
    private readonly IConversationSummaryService _summaryService; // 手机远程回复摘要（agent 跑完把 fullText 压成概览）
    private readonly IDeviceAutoConnectService _autoConnect;
    private readonly IApprovalDialogService? _approvalDialog;
    private readonly IToolApprovalLevelLookup? _levelLookup; // AutoEdit 模式按工具等级区分自动批/弹框；null 退化为全弹（Manual 语义）
    private readonly IRunProgressSink? _progressSink; // 运行关键节点信号出口（可空：未注册则不广播，桌宠/手机端行为不变）
    private readonly IPetTokenStream? _petTokens;    // 桌宠专属逐字答案流（可空：未注册则桌宠无打字机，关键节点仍走 _progressSink）

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
    private bool _syncingMode; // 程序化刷新 SelectedChatMode 时置 true，避免 OnSelectedChatModeChanged 回触 SetMode
    private CancellationTokenSource? _runCts; // 当前流式运行的取消令牌（停止生成用）

    // 远程审批 pending 注册表：convId → (审批请求, TCS)。远程 run 撞审批时 onApprovalRequest 建 TCS 注册于此；
    // 老板手机端发回 "ApprovalDecision" 消息 → HandleRemoteApprovalDecisionAsync 解 TCS → run 协作循环恢复。
    // 每会话至多一个 pending（MAF 审批结束本轮）；新聊天取消旧 pending（TrySetResult(null) → loop break）。
    private readonly Dictionary<Guid, RemotePendingApproval> _remoteApprovals = new();
    /// <summary>一条 pending 远程审批：审批请求（带 RequestId）+ 等老板决策的 TCS。null 决策=取消。</summary>
    private sealed record RemotePendingApproval(ToolApprovalRequest Req, TaskCompletionSource<ToolApprovalDecision?> Tcs);

    // —— 历史增量加载：切换会话时只渲染最近若干轮，更早的暂存，滚到顶部再加载 ——
    // 避免长会话一次性重建全部消息 + 同步 markdown 解析导致的卡顿（全在 UI 线程）。
    private List<ChatTurnItem>? _pendingHistoryTurns; // 暂存的更早轮（时间正序，最旧在前；尚未加入 Messages 渲染）
    private const int HistoryInitialBatch = 10;       // 切换会话时初始渲染的最近轮数
    private const int HistoryLoadBatch = 10;          // 每次滚到顶部增量加载的轮数
    private int _nextRunSeq = 1;                      // 当前会话下一个 run 的序号（1-based；新建=1，恢复=最大 seq+1）

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
        _summaryService = serviceProvider.GetRequiredService<IConversationSummaryService>();
        _autoConnect = serviceProvider.GetRequiredService<IDeviceAutoConnectService>();
        _approvalDialog = serviceProvider.GetService<IApprovalDialogService>();
        _levelLookup = serviceProvider.GetService<IToolApprovalLevelLookup>();
        _progressSink = serviceProvider.GetService<IRunProgressSink>();
        _petTokens = serviceProvider.GetService<IPetTokenStream>();
        _experts = experts;
        _workflows = workflows;
        _rules = rules;

        // 三类目标的可选集合变更（新建/删除/catalog reload）：刷新统一下拉池。选中是下拉选择的独立动作。
        _experts.TargetsChanged += OnChatTargetsChanged;
        _workflows.TargetsChanged += OnChatTargetsChanged;
        _rules.TargetsChanged += OnChatTargetsChanged;

        Conversations.CollectionChanged += (_, _) => HasConversations = Conversations.Count > 0;

        // 当前会话是否有消息：控制 ChatPageView 全屏页空态 hero ↔ 非空态（消息流+输入），对齐 ChatGPT/WorkBuddy 每会话空态。
        // 与 HasConversations（有无任何历史记录）区分——有历史记录的新会话仍显空态 hero（输入框垂直居中）。
        Messages.CollectionChanged += (_, _) => HasMessages = Messages.Count > 0;

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
    /// 须在 shell 已加载 Experts/Workflows/Rules 之后调用（RestoreSelectedTargetAsync 读这三类列表）。</summary>
    public async Task InitializeAsync()
    {
        IsConnected = _deviceChatClient.IsConnected;
        StatusText = IsConnected ? "已连接" : "未连接";

        _isInitializing = true;
        try
        {
            await LoadLlmConfigsAsync();
            await RestoreSelectedTargetAsync();
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
        // 轮询型工具去重：连续重复调用同一轮询工具（如 PollSqlTaskStatus/FetchSqlTaskStatus）时复用同一张工具卡，
        // 而非每轮新建一张（否则「轮询」工具卡刷屏）。记录最近一次该工具的工具卡 + 已轮询次数。
        public ChatMessageItem? LastPollingCard;
        public string? LastPollingTool;
        public int PollCount;
        public StreamCursor(ChatMessageItem initial) => Current = initial;
    }

    // 轮询型工具名单：这些工具语义上就是「检查当前状态/等待完成」，agent 会反复调用直到终态。
    // 连续重复调用时合并为一张卡（刷新状态 + 轮询计数），避免刷屏。名字按工具注册时的 key 匹配。
    private static readonly HashSet<string> PollingToolNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "PollSqlTaskStatus",
        "FetchSqlTaskStatus",
    };

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
        System.Text.StringBuilder fullText, CancellationToken cancellationToken = default,
        // 远程路径传：关键节点信号（计划/审批/完成/错误）序列化成 RunProgress JSON 经 SignalR 以 "Progress" type 发回手机
        // （老板视角：只收结论性信号）。本地路径不传（默认 null）→ 不发 Progress。注意：本地桌宠走 IRunProgressSink（广播），
        // 与手机端这条互不相干——手机端是 remote-only，桌宠是 local+remote 都可见。逐字 token 两边都不传（噪音）。
        Action<RunProgress>? onRemoteProgress = null,
        // 远程路径传：撞审批时把请求 park 给手机（建 TCS + 注册 _remoteApprovals，等手机端 "ApprovalDecision" 消息解）。
        // 本地路径不传（默认 null）→ 走桌面本地审批框 _approvalDialog。Auto 模式由 TryAutoApprove 自动批、不经此回调。
        Func<ToolApprovalRequest, Task<ToolApprovalDecision?>>? onApprovalRequest = null)
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
        // 用「有序 chunk 队列」而非两个独立 StringBuilder。原因：MAF 一个 update 可含混合 TextContent +
        // TextReasoningContent，且 reasoning→answer 边界常落在同一 50ms flush 窗口。若分两个 StringBuilder 且
        // flush 先处理 answer 再处理 reasoning，会把边界倒置——answer 先 EnsureTextSegment(false) 冻结当前
        // reasoning 卡、新建 answer 卡，随后 reasoning 又 EnsureTextSegment(true) 冻结 answer、新建第二张
        // reasoning 卡 → 一段连续 reasoning 被劈成两卡（上方「思考了」+ 下方「思考中」）、answer 夹中间，
        // 用户看到「多个思考中、乱了、流式跑上面的思考中去了」。有序队列保留 token 真实时序，连续同类型
        // chunk 合并进同一卡，仅在类型真切换时换段——消除劈裂。
        // 流式期只更 Content（TextBlock 廉价、无 markdown 重解析，避免假死）；完成时 FinalizeSegment 切
        // markdown viewer 一次性渲染格式。
        var pendingChunks = new List<(string Text, bool IsReasoning)>();
        var pendingLock = new object();
        var flushTimer = new System.Threading.Timer(_ => _dispatcher.Post(() =>
        {
            List<(string Text, bool IsReasoning)> snapshot;
            lock (pendingLock)
            {
                if (pendingChunks.Count == 0) return;
                snapshot = pendingChunks;          // 整表换出：drain 原子，流式线程继续往新表 append 不互斥
                pendingChunks = new List<(string, bool)>();
            }
            // 按真实时序处理；连续同类型 chunk 合并成一个字符串再追加（O(段数) 次拼接，非 O(chunk 数) 次）。
            var buf = new System.Text.StringBuilder();
            bool? segType = null;
            foreach (var (text, isReasoning) in snapshot)
            {
                if (segType is { } prev && prev != isReasoning)
                {
                    EnsureTextSegment(cursor, items, senderName, isReasoningContent: prev);
                    cursor.Current.Content += buf.ToString();
                    buf.Clear();
                }
                segType = isReasoning;
                buf.Append(text);
            }
            if (buf.Length > 0 && segType is { } last)
            {
                EnsureTextSegment(cursor, items, senderName, isReasoningContent: last);
                cursor.Current.Content += buf.ToString();
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
                ToolApprovalDecision? autoDecision = null; // 模式自动批（Auto/AutoEdit-非Dangerous）；非 null 则跳过对话框+审批卡
                // answerTokens 节流已由 50ms 批量 flush 替代

                var handlers = new StreamHandlers(
                    OnToken: (token, _) =>
                    {
                        fullText.Append(token);
                        lock (pendingLock) pendingChunks.Add((token, false));
                        flushTimer.Change(50, Timeout.Infinite); // 50ms 后批量刷新
                        _petTokens?.AppendToken(token); // 桌宠专属打字机（逐字）；关键节点另走 _progressSink
                        return Task.CompletedTask;
                    },
                    OnReasoning: (text, _) =>
                    {
                        lock (pendingLock) pendingChunks.Add((text, true));
                        flushTimer.Change(50, Timeout.Infinite);
                        EmitProgress(new RunProgress(RunProgressKind.Reasoning), onRemoteProgress);
                        return Task.CompletedTask;
                    },
                    OnToolEvent: (ev, _) =>
                    {
                        // todos_* 调用由 OnTodoList 渲染为计划卡，不重复出工具卡。
                        if (ev.Name.StartsWith("todo", StringComparison.OrdinalIgnoreCase))
                            return Task.CompletedTask;
                        var isPollingTool = IsPollingTool(ev.Name);
                        var isPollingRepeat = isPollingTool && cursor.LastPollingTool == ev.Name;
                        _dispatcher.Post(() => HandleToolEventSegmented(cursor, items, ev, senderName));
                        // 关键节点广播：工具起止。轮询型工具连续重复调用时跳过心跳（合并为单卡，避免「轮询」刷屏）。
                        if (!isPollingRepeat)
                        {
                            EmitProgress(ev.Kind == ToolStreamEventKind.Call
                                ? new RunProgress(RunProgressKind.ToolStarted, ToolName: ev.Name, ToolArgument: ExtractToolArgument(ev.Input))
                                : new RunProgress(RunProgressKind.ToolFinished, ToolName: ev.Name), onRemoteProgress);
                        }
                        return Task.CompletedTask;
                    },
                    OnApprovalRequest: (req, _) =>
                    {
                        pendingApproval = req;
                        // 关键节点广播：审批请求（下单/删除/推送等危险操作需确认）。
                        EmitProgress(new RunProgress(RunProgressKind.ApprovalRequested, ToolName: req.ToolName), onRemoteProgress);
                        // 按当前模式算自动决定（Auto 全自动批；AutoEdit 自动批非 Dangerous；Manual/Plan 弹框）。
                        // 读 SelectedChatMode 是逐工具调用即时生效——运行中切档立即影响下一个工具（解决中途切模式）。
                        autoDecision = TryAutoApprove(req);
                        if (autoDecision is not null)
                            return Task.CompletedTask; // 自动批：不插审批卡，工具 call+result 仍经 OnToolEvent 正常渲染
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
                        // 关键节点广播：计划更新（含手机端进度心跳——须带 Todos 供手机端逐条渲染）。
                        EmitProgress(new RunProgress(RunProgressKind.PlanUpdated, PlanStepCount: list.Count, Todos: list), onRemoteProgress);
                        return Task.CompletedTask;
                    },
                    OnError: (text, _) =>
                    {
                        _dispatcher.Post(() =>
                        {
                            EnsureTextSegment(cursor, items, senderName, isReasoningContent: false);
                            cursor.Current.Content += $"\n\n[错误：{text}]";
                        });
                        EmitProgress(new RunProgress(RunProgressKind.RunError, Text: text), onRemoteProgress);
                        return Task.CompletedTask;
                    },
                    OnUsage: info => _dispatcher.Post(() => TokenInfo = info));

                await runAsync(handlers, approvalResponse, cancellationToken).ConfigureAwait(false);

                if (pendingApproval is null) break;

                ToolApprovalDecision decision;
                if (autoDecision is not null)
                {
                    // 模式自动批（Auto/AutoEdit-非Dangerous）：无对话框、无审批卡，直接走与用户批准相同的再调用路径恢复执行。
                    decision = autoDecision;
                }
                else
                {
                    // 非自动批：远程路径把请求 park 给手机（onApprovalRequest 建 TCS 等老板决策），否则弹桌面本地框。
                    var userDecision = onApprovalRequest is not null
                        ? await onApprovalRequest(pendingApproval).ConfigureAwait(false)
                        : (_approvalDialog is null ? null : await _approvalDialog.ShowAsync(pendingApproval).ConfigureAwait(false));
                    if (userDecision is null)
                    {
                        _dispatcher.Post(() => { if (approvalCard is not null) approvalCard.ApprovalState = 2; });
                        break; // 取消（本地框关闭 / 远程被新聊天取消）→ 终止本轮
                    }
                    decision = userDecision;
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
                approvalResponse = decision;
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

            // 桌宠逐字流收尾（打字机 flush 后停止）。RUN 不考虑 remote——桌宠本地/远程都收逐字。
            _petTokens?.EndAnswer();
            // 终态关键节点广播：本轮收敛为 Done 或 Error。不带原文/摘要——桌宠只冒固定短话「搞定啦」，
            // 手机端最终结果由 ConversationSummaryService 的 LLM 概览单独发（Text type），Progress 心跳不发 long-form。
            EmitProgress(errored
                ? new RunProgress(RunProgressKind.RunError)
                : new RunProgress(RunProgressKind.RunDone), onRemoteProgress);
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

    /// <summary>从工具入参提取主参数摘要（取第一个字符串/数值值，截断 ~20 字符），供关键节点信号展示。
    /// 拿不到则返回 null（消费方退化为只显工具名）。</summary>
    private static bool IsPollingTool(string toolName)
    {
        return !string.IsNullOrEmpty(toolName) && PollingToolNames.Contains(toolName);
    }

    private static string? ExtractToolArgument(Dictionary<string, object>? input)
    {
        if (input is null) return null;
        foreach (var v in input.Values)
        {
            if (v is null) continue;
            var s = v.ToString();
            if (string.IsNullOrWhiteSpace(s)) continue;
            s = s.Trim();
            return s.Length > 20 ? s[..20] + "…" : s;
        }
        return null;
    }

    /// <summary>把一条关键节点信号广播给本地消费者（IRunProgressSink，桌宠等）+ 可选地转发给手机端
    /// （onRemoteProgress，remote-only 路径）。两通道互不相干：本地桌宠 local+remote 都可见，手机端仅 remote 触发。
    /// 逐字 token 不走此处（见 RunProgress 粒度约定）。</summary>
    private void EmitProgress(RunProgress signal, Action<RunProgress>? onRemoteProgress)
    {
        _progressSink?.Post(signal);
        onRemoteProgress?.Invoke(signal);
    }    /// <summary>确保当前段是目标类型（TextContent 思考/答案 vs TextReasoningContent 推理）的文本段；否则冻结/移除当前并新开一段。串行在 UI 线程调用。</summary>
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

            // 轮询型工具去重：连续重复调用同一工具时复用上一张卡（刷新 callId/input/轮询计数），不新建。
            // 「轮询」工具（PollSqlTaskStatus/FetchSqlTaskStatus）agent 会反复调用直到终态，每轮新建会刷屏。
            if (IsPollingTool(ev.Name))
            {
                if (cursor.PendingTools.TryGetValue(ev.CallId, out var p)) cursor.PendingTools.Remove(ev.CallId);
                if (cursor.LastPollingCard is not null && cursor.LastPollingTool == ev.Name)
                {
                    // 复用：拿掉刚插入的新卡，复用旧卡（刷新 CallId/Input）。轮询计数 +1。
                    items.Remove(tool);
                    cursor.Current = cursor.LastPollingCard;
                    cursor.LastPollingCard.CallId = ev.CallId;
                    cursor.LastPollingCard.Input = ev.Input;
                    // 复用卡时旧卡的 result 已落，这里置为「再轮询中」——保持单卡形态，result 到达再置完成。
                    cursor.LastPollingCard.IsToolCompleted = false;
                    cursor.PollCount++;
                    if (!string.IsNullOrEmpty(ev.CallId)) cursor.PendingTools[ev.CallId] = cursor.LastPollingCard;
                    cursor.LastPollingCard.ToolName = $"{ev.Name}（第 {cursor.PollCount} 次轮询）";
                    return; // 复用卡不重复走文件编辑快照等分支
                }
                cursor.LastPollingCard = tool;
                cursor.LastPollingTool = ev.Name;
                cursor.PollCount = 1;
            }
            else
            {
                // 非轮询工具打断了轮询序列 → 清状态（后续若再轮询，重新计一张）。
                cursor.LastPollingCard = null;
                cursor.LastPollingTool = null;
                cursor.PollCount = 0;
            }

            // 文件编辑工具：call 阶段读盘快照 old（供事后算 diff + 一键撤销写回）。后台读盘避免大文件阻塞 UI。
            // 撤销按钮建卡即注入（点按 → UndoFileEdit 写回 old）。
            if (AgentEditPreview.IsFileEditTool(ev.Name))
            {
                var input = ev.Input;
                var cardRef = tool;
                cardRef.UndoCommand = new RelayCommand(() => UndoFileEdit(cardRef));
                _ = Task.Run(() =>
                {
                    var fp = AgentEditPreview.TryGetFilePath(input);
                    if (fp is null) return;
                    bool existed = File.Exists(fp);
                    string? old = existed ? TryReadAllText(fp) : null;
                    _dispatcher.Post(() => cardRef.FileEdit = new FileEditSnapshot(fp, old, existed));
                });
            }
            // 多文件批量编辑工具：call 阶段对每个 patch path 读盘快照 old。撤销逆序写回全部。
            else if (AgentEditPreview.IsApplyPatchTool(ev.Name))
            {
                var input = ev.Input;
                var cardRef = tool;
                cardRef.UndoCommand = new RelayCommand(() => UndoFileEdit(cardRef));
                _ = Task.Run(() =>
                {
                    var paths = AgentEditPreview.TryGetFilePaths(input);
                    if (paths.Count == 0) return;
                    var snaps = new List<FileEditSnapshot>(paths.Count);
                    foreach (var fp in paths)
                    {
                        bool existed = File.Exists(fp);
                        string? old = existed ? TryReadAllText(fp) : null;
                        snaps.Add(new FileEditSnapshot(fp, old, existed));
                    }
                    _dispatcher.Post(() => cardRef.FileEdits = snaps);
                });
            }
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

            // 文件编辑工具：result 阶段读盘 new + 快照 old 算事后并排 diff 挂卡。后台读盘。
            if (tool.FileEdit is { } snap)
            {
                var cardRef = tool;
                _ = Task.Run(() =>
                {
                    bool newExists = File.Exists(snap.FilePath);
                    string? newText = newExists ? TryReadAllText(snap.FilePath) : null;
                    DiffModel? diff = null;
                    // old 或 new 任一存在即有可展示 diff（新建文件 old="" → 全增；删除结果 new="" → 全删）。
                    if (snap.OldExisted || newExists)
                    {
                        var syntax = GitService.SyntaxForExtension(Path.GetExtension(snap.FilePath));
                        diff = LineDiffBuilder.Build(snap.OldContent ?? "", newText ?? "", snap.FilePath, syntax);
                    }
                    _dispatcher.Post(() => cardRef.DiffPreview = diff);
                });
            }
            // 多文件批量编辑：result 阶段对每个 path 读盘 new + 快照 old 算 diff，列表挂卡。
            else if (tool.FileEdits is { Count: > 0 } snaps)
            {
                var cardRef = tool;
                _ = Task.Run(() =>
                {
                    var diffs = new List<DiffModel>(snaps.Count);
                    foreach (var s in snaps)
                    {
                        bool newExists = File.Exists(s.FilePath);
                        string? newText = newExists ? TryReadAllText(s.FilePath) : null;
                        // old 或 new 任一存在即算 diff（新建→全增；删除→全删）。
                        if (s.OldExisted || newExists)
                        {
                            var syntax = GitService.SyntaxForExtension(Path.GetExtension(s.FilePath));
                            diffs.Add(LineDiffBuilder.Build(s.OldContent ?? "", newText ?? "", s.FilePath, syntax));
                        }
                    }
                    _dispatcher.Post(() => cardRef.DiffPreviews = diffs);
                });
            }

            var idx = items.IndexOf(tool);
            if (idx < 0) idx = items.Count - 1;
            var next = NewTextSegment(senderName, isReasoningContent: false);
            items.Insert(idx + 1, next);
            cursor.Current = next;
        }
    }

    /// <summary>撤销一步文件编辑：写回 call 阶段快照的 old 内容（工具新建的文件 → 撤销 = 删除）。
    /// 写盘后 <c>DocumentsViewModel</c> 的 FileSystemWatcher 自动重载编辑器。朴素单步语义——写回该步 old；
    /// 多步同文件撤销中间步会破坏后续步基准（边缘情况，用户多半撤销最后一步或整体重来）。
    /// 多文件批量编辑（ApplyPatch）：逆序写回所有快照（后改的先恢复，尽量还原中间状态）。</summary>
    private void UndoFileEdit(ChatMessageItem card)
    {
        if (card.IsUndone) return;

        // 多文件批量编辑：逆序写回全部快照
        if (card.FileEdits is { Count: > 0 } snaps)
        {
            for (int i = snaps.Count - 1; i >= 0; i--)
            {
                var snap = snaps[i];
                try
                {
                    if (snap.OldExisted)
                        File.WriteAllText(snap.FilePath, snap.OldContent ?? "");
                    else if (File.Exists(snap.FilePath))
                        File.Delete(snap.FilePath);
                }
                catch
                {
                    // 单文件撤销失败不中断后续（用户可去 SCM 页手动恢复）。
                }
            }
            card.IsUndone = true;
            return;
        }

        // 单文件编辑
        var single = card.FileEdit;
        if (single is null) return;
        try
        {
            if (single.OldExisted)
                File.WriteAllText(single.FilePath, single.OldContent ?? "");
            else if (File.Exists(single.FilePath))
                File.Delete(single.FilePath); // 工具新建的文件 → 撤销 = 删除
            card.IsUndone = true;
        }
        catch
        {
            // 撤销失败（文件锁定/权限）忽略，用户可去 SCM 页手动恢复。
        }
    }

    /// <summary>读文本文件全文，失败返回 null（二进制/锁定/权限）。</summary>
    private static string? TryReadAllText(string path)
    {
        try { return File.ReadAllText(path); }
        catch { return null; }
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
                // IsStreaming=false 切到完成态：ChatView 完成态 viewer 绑 Content 一次性渲染 markdown 格式。
                // MarkdownContent 同步赋值仅作安全网（保持属性一致，供未来/非展示消费者），不驱动桌面展示。
                last.MarkdownContent = last.Content;
            }
        }
    }

    private Guid _recentConversationId;

    /// <summary>处理老板手机端发回的审批决策（messageType="ApprovalDecision"）。
    /// 反序列化 RemoteApprovalDecision → 查 _remoteApprovals 取 pending → 补上原 RequestId 建 ToolApprovalDecision → 解 TCS。
    /// run 协作循环的 onApprovalRequest 被 TCS 唤醒，带 decision 重入 harness 同会话恢复（HarnessRuntime:272-299）。
    /// 无 pending → 忽略（stale：已取消/超时/重复）。</summary>
    private void HandleRemoteApprovalDecision(DeviceMessage msg)
    {
        RemoteApprovalDecision? dto = null;
        try
        {
            dto = System.Text.Json.JsonSerializer.Deserialize<RemoteApprovalDecision>(msg.Content, ProgressJsonOpts);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[RemoteApproval] 反序列化失败（conv {msg.ConversationId}）: {ex.Message}");
        }
        if (dto is null) return;

        lock (_remoteApprovals)
        {
            if (_remoteApprovals.TryGetValue(msg.ConversationId, out var pending))
            {
                _remoteApprovals.Remove(msg.ConversationId);
                var decision = new ToolApprovalDecision(pending.Req.RequestId, dto.Approved, dto.Reason, AlwaysApprove: false);
                pending.Tcs.TrySetResult(decision); // 唤醒 onApprovalRequest → run 协作循环带 approvalResponse 重入
            }
            // 无 pending → stale，忽略
        }
    }

    /// <summary>取消同会话 pending 远程审批（新聊天起 turn 前调）。
    /// TrySetResult(null) → onApprovalRequest 返回 null → run 协作循环 break 终止旧轮，避免新旧 turn 抢同 framework session。</summary>
    private void CancelRemoteApproval(Guid convId)
    {
        lock (_remoteApprovals)
        {
            if (_remoteApprovals.TryGetValue(convId, out var pending))
            {
                _remoteApprovals.Remove(convId);
                pending.Tcs.TrySetResult(null);
            }
        }
    }

    /// <summary>收到远程 SignalR 消息回调（SignalR 线程）</summary>
    private async Task HandleMessageReceived(DeviceMessage msg)
    {
        _recentConversationId = msg.ConversationId;

        // 控制消息按 messageType 路由（不建可见 turn）：
        // ApprovalDecision = 老板手机端审批决策（解 _remoteApprovals 的 pending TCS 恢复 run）；
        // Progress = 桌面是发送方，不消费自己发出的心跳。
        var mt = msg.MessageType;
        if (string.Equals(mt, "ApprovalDecision", StringComparison.OrdinalIgnoreCase))
        {
            HandleRemoteApprovalDecision(msg);
            return;
        }
        if (string.Equals(mt, "Progress", StringComparison.OrdinalIgnoreCase)) return;

        // 新聊天取消同会话 pending 远程审批（否则旧 run 挂着 TCS、新 turn 抢同 framework session → hang）。
        CancelRemoteApproval(msg.ConversationId);

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
            // 摘要用与 agent 同款 LLM：取当前专家 ModelId（WorkflowItem 无 ModelId → null 退宿主默认/聊天页选中 LLM）。
            var agentModelId = (SelectedChatTarget as ExpertItem)?.ModelId;
            var convId = msg.ConversationId;
            var fullText = new System.Text.StringBuilder();
            var desiredMode = HasModeSupport ? SelectedChatMode.ToDesiredMode() : null;
            await RunSegmentedStreamAsync(remoteTurn, targetName,
                (handlers, approvalResponse, ct) => remoteKind == ChatTargetKind.Workflow
                    ? _workflowRunner.RunStreamingAsync(convId, targetId, msg.Content, handlers, approvalResponse, desiredMode, ct)
                    : _agentRunner.RunStreamingAsync(convId, targetId, msg.Content, handlers, approvalResponse, desiredMode, ct),
                fullText,
                // 进度心跳：关键节点信号序列化成 RunProgress JSON 经 SignalR 以 "Progress" type 发回手机。
                // 老板视角=只收结论性信号：计划/审批/完成/错误；过程噪音（思考/工具起止）不发手机（桌宠本端经
                // _progressSink 仍收全集）。手机反序列化回 RunProgress 按 kind 富渲染（与桌宠同款）。心跳丢失不影响主流程；不 await。
                onRemoteProgress: signal =>
                {
                    if (signal.Kind is not (RunProgressKind.PlanUpdated or RunProgressKind.ApprovalRequested
                                            or RunProgressKind.RunDone or RunProgressKind.RunError)) return;
                    if (!_deviceChatClient.IsConnected) return;
                    var json = System.Text.Json.JsonSerializer.Serialize(signal, ProgressJsonOpts);
                    _deviceChatClient.SendMessageAsync(convId, json, "Progress").SafeFireAndForget();
                },
                // 远程审批：远程 run 撞审批时把请求 park 给手机（建 TCS + 注册到 _remoteApprovals），
                // 等老板手机端发回 "ApprovalDecision" 消息解 TCS。Auto 模式由 TryAutoApprove 自动批、不经此回调。
                onApprovalRequest: req =>
                {
                    var tcs = new TaskCompletionSource<ToolApprovalDecision?>(TaskCreationOptions.RunContinuationsAsynchronously);
                    lock (_remoteApprovals) _remoteApprovals[convId] = new RemotePendingApproval(req, tcs);
                    return tcs.Task;
                });
            if (remoteKind != ChatTargetKind.Rule) await RefreshModeAsync(msg.ConversationId);

            // 远程消息触发运行：把回复通过 SignalR 发回远程发送方（否则对方收不到结果）。
            // 回环安全：SignalRDeviceChatClient 用 _recentSentMessageIds 去重，服务端 echo 回本机的消息会被忽略。
            // 老板视角：不回传全量过程，只发一段 LLM 概览摘要（用与 agent 同款模型）。失败/Rule 退化为截断全量。
            var full = fullText.ToString();
            string replyText;
            if (remoteKind == ChatTargetKind.Rule || string.IsNullOrWhiteSpace(full))
            {
                replyText = full; // Rule 无 LLM，直发全量
            }
            else
            {
                try
                {
                    var summary = await _summaryService.GenerateSummaryAsync(full, agentModelId);
                    replyText = !string.IsNullOrWhiteSpace(summary)
                        ? summary
                        : (full.Length > 2000 ? full[..2000] + "\n…（完整回复见桌面端）" : full);
                }
                catch
                {
                    replyText = full.Length > 2000 ? full[..2000] + "\n…（完整回复见桌面端）" : full;
                }
            }
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
    /// 脱钩于 SelectedChatMode——避免 RefreshModeAsync 把模式回翻成 plan 时接受条误弹。</summary>
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

    /// <summary>当前会话运行模式（4 档：Plan/Manual/AutoEdit/Auto）。MAF 只存 plan/execute；
    /// execute 子档（Manual/AutoEdit/Auto）是纯 VM 状态，经审批拦截逐工具决定自动批/弹框。默认 AutoEdit。</summary>
    [ObservableProperty]
    private ChatMode _selectedChatMode = ChatMode.AutoEdit;

    /// <summary>agent 是否正在流式运行（驱动 plan-accept 条显隐）。</summary>
    [ObservableProperty]
    private bool _isAgentRunning;

    /// <summary>plan 模式轮产出计划后（PlanAwaitingAccept）、非运行中时显示"接受计划→执行"条。
    /// 门控用「本轮是否待接受」而非「当前模式」，避免 execute 跑完 RefreshModeAsync 把模式回翻 plan 时误弹。</summary>
    public bool ShowPlanAccept => HasModeSupport && PlanAwaitingAccept && !IsAgentRunning;

    /// <summary>有计划、非运行中、且不在待接受态 → execute 已跑完，可点「确认执行完毕」收起面板。</summary>
    public bool ShowPlanDismiss => HasActivePlan && !IsAgentRunning && !PlanAwaitingAccept;

    // —— 最近会话列表（侧边栏）——
    public ObservableCollection<ConversationItem> Conversations { get; } = new();

    /// <summary>是否有最近会话（控制空状态"暂无会话"显示）。</summary>
    [ObservableProperty] private bool _hasConversations;

    /// <summary>当前会话是否有消息（ChatPageView 全屏页空态 hero ↔ 非空态切换）。</summary>
    [ObservableProperty] private bool _hasMessages;

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
        // 切到新聊天前先取消进行中的流式运行，否则后台 run 孤儿化继续跑（IsAgentRunning 不归零、
        // 工具持续重试、孤儿进程堆积 → CPU/内存耗尽）。StopGeneration 走的是同一条取消路径。
        CancelRunningRun();
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
        // 切会话前先取消进行中的运行，避免后台孤儿 run 继续消耗资源（同 NewChat）。
        CancelRunningRun();
        try
        {
            var conv = await _conversationStore.GetConversationAsync(item.Id);
            _currentConversationId = item.Id;
            foreach (var c in Conversations) c.IsCurrent = c.Id == item.Id;

            _isInitializing = true;
            // 恢复时按绑定 id 在三类列表里找目标（规则/agent 都是 ExpertItem，按所在列表区分）。
            // 静默选中（预置 key 再赋值），不触发会话重置——恢复会话本就不该清空正在恢复的 transcript。
            if (Guid.TryParse(item.AgentId, out var aid))
            {
                ExpertItem? ei = _rules.Rules.FirstOrDefault(r => r.Id == aid)
                              ?? _experts.Experts.FirstOrDefault(e => e.Id == aid);
                WorkflowItem? wi = ei is null ? _workflows.Workflows.FirstOrDefault(w => w.Id == aid) : null;
                SetSelectedTargetSilently((object?)ei ?? wi);
            }
            _isInitializing = false;

            Messages.Clear();
            ActiveTodos.Clear(); HasActivePlan = false; PlanAwaitingAccept = false;
            _pendingHistoryTurns = null;
            if (conv is not null)
            {
                // 从 typed 事件流重建 transcript：每条 run 一轮（user 消息在 run.UserMessage），events 还原思考/工具/答案/计划卡。
                // 只把最近 N 轮加入 Messages 渲染（markdown 解析仅在绑定时发生，分批避免卡顿）；更早轮暂存待上滚增量加载。
                var runs = await _conversationStore.GetRunsAsync(item.Id);
                var eventsByRun = new Dictionary<Guid, IReadOnlyList<AgentEvent>>();
                foreach (var run in runs)
                    eventsByRun[run.Id] = await _conversationStore.GetEventsAsync(run.Id);
                _nextRunSeq = runs.Count == 0 ? 1 : runs.Max(r => r.Seq) + 1;

                var all = TurnRebuilder.BuildTurnsFromRuns(runs, eventsByRun, item.AgentName ?? "专家");
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
            else
            {
                _nextRunSeq = 1;
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
        // 删会话前先取消进行中的运行（若删的正是当前会话，后台 run 会孤立写入已删会话）。
        CancelRunningRun();
        try
        {
            await _conversationStore.DeleteAsync(item.Id);
            // 清出运行时会话状态：丢弃 agent/workflow 路径缓存的 FrameworkAgentSession + 平台 session 字典，
            // 使 RestoringChatHistoryProvider 的 per-session 历史缓存随 GC 自动回收（防长会话累积内存泄漏）。
            _agentRunner.EvictConversation(item.Id);
            _workflowRunner.EvictConversation(item.Id);
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

    /// <summary>从偏好恢复选中目标（跨重启保留），并保证启动时有一个默认选中。下拉池列全部目标，无召唤态。</summary>
    private async Task RestoreSelectedTargetAsync()
    {
        try
        {
            // 读结构化偏好 chatTargets（仅 Selected 字段；旧 Summoned 字段被忽略）；缺失时迁移旧键（仅 selectedAgentId）
            ChatTargetSelected? savedSel = null;
            var json = await _preferences.GetAsync("chatTargets");
            if (!string.IsNullOrWhiteSpace(json))
            {
                var prefs = System.Text.Json.JsonSerializer.Deserialize<ChatTargetPrefs>(json, _prefsJson);
                savedSel = prefs?.Selected;
            }
            else
            {
                var legacySelected = await _preferences.GetAsync("selectedAgentId");
                if (!string.IsNullOrWhiteSpace(legacySelected))
                    savedSel = new ChatTargetSelected { Kind = "Agent", Id = legacySelected };
            }

            // 读最近会话展开态（UI 无关，可与上面偏好一起在后台读）。
            var expanded = await _preferences.GetAsync("recentExpanded");

            // ★ 下拉池 + 选中须在 UI 线程改：InitializeAsync 链经 ListAsync(ConfigureAwait(false)) 后续可能落在 threadpool，
            // ObservableCollection 跨线程修改 Avalonia ComboBox 不会反映（首轮下拉「明明有数据却为空」的隐患）。
            // 镜像 RefreshChatTargetItems 的 dispatcher 范式。InvokeAsync 返回 Task 可 await（Post 不可，会与后续
            // RefreshConversationsAsync 乱序）。当前虽实测在 UI 线程（thread=1），包 dispatcher 是防御 + 与既有范式一致。
            await _dispatcher.InvokeAsync(() =>
            {
                // 同步填充下拉（列全部 agent/规则/工作流；此时已在 UI 线程，不依赖后续的 RefreshChatTargetItems）
                ChatTargetItems.Clear();
                foreach (var e in _experts.Experts) ChatTargetItems.Add(e);
                foreach (var r in _rules.Rules) ChatTargetItems.Add(r);
                foreach (var w in _workflows.Workflows) ChatTargetItems.Add(w);

                // 选中：已保存的 → 回退到池中首项
                object? saved = null;
                if (savedSel is { } sel && Guid.TryParse(sel.Id, out var sid))
                {
                    saved = sel.Kind switch
                    {
                        "Rule" => _rules.Rules.FirstOrDefault(r => r.Id == sid),
                        "Agent" => _experts.Experts.FirstOrDefault(e => e.Id == sid),
                        "Workflow" => (object?)_workflows.Workflows.FirstOrDefault(w => w.Id == sid),
                        _ => null,
                    };
                }
                SetSelectedTargetSilently(saved ?? ChatTargetItems.FirstOrDefault());
                if (expanded == "0") IsRecentExpanded = false;
            });
        }
        catch { /* 偏好恢复失败不阻塞 */ }
    }

    /// <summary>持久化当前选中目标（下拉切换时调）：写 Selected 字段（无召唤态）。</summary>
    private async Task PersistSelectedTargetAsync()
    {
        try
        {
            var prefs = new ChatTargetPrefs { Selected = CurrentSelectedPrefs() };
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

    /// <summary>RunProgress/RemoteApprovalDecision 的 wire 序列化选项：camelCase + 枚举字符串 +
    /// 保留中文（UnsafeRelaxed）。PropertyNameCaseInsensitive 使反序列化手机端发来的
    /// RemoteApprovalDecision（可能 PascalCase/camelCase）稳健。勿复用 _prefsJson——它无 JsonStringEnumConverter。</summary>
    private static readonly System.Text.Json.JsonSerializerOptions ProgressJsonOpts = new()
    {
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
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
                foreach (var expert in _experts.Experts)
                    _agentRunner.Invalidate(expert.Id);
            }
        }
    }

    /// <summary>三类目标的可选集合变化（新建/删除/catalog reload）：刷新统一下拉池（列全部目标，按 Id+Kind 保留选中）。
    /// 选中是下拉选择的独立动作（与 agent 模型一致）；此处只刷新池，不自动激活、不持久化召唤态。</summary>
    private void OnChatTargetsChanged()
    {
        RefreshChatTargetItems();
    }

    /// <summary>下拉选中目标变化：更新模式支持；非启动期统一开新会话（三类一致）+ 持久化选中。</summary>
    partial void OnSelectedChatTargetChanged(object? value)
    {
        var kind = KindOf(value);
        // 模式支持：工作流支持；agent 看 EnableAgentModeProvider；规则无 mode。始终更新（启动期恢复选中也要显选择器）。
        HasModeSupport = kind == ChatTargetKind.Workflow
                      || (kind == ChatTargetKind.Agent && value is ExpertItem e && e.EnableAgentModeProvider);
        _syncingMode = true;
        SelectedChatMode = ChatMode.AutoEdit; // 默认自动编辑档（无选择器时值无意义——TryAutoApprove 不激活）
        _syncingMode = false;

        var newKey = TargetKey(value, kind);
        var prevKey = _selectedTargetKey;
        _selectedTargetKey = newKey; // 始终跟踪（含启动期/resume，保证下一次真实切换判据正确）

        if (_isInitializing) return;

        // 运行中的流式不能被中断/清空（兜底：切目标本就是用户主动动作，运行中不可能合法切换）。
        if (IsAgentRunning) return;

        // 同一逻辑目标（同 Id+Kind；实例可能因 reload 被替换）→ 非用户切换，不重置会话。
        // 程序化赋值（刷新池/恢复/启动）走 SetSelectedTargetSilently——它**先预置 _selectedTargetKey 再赋值**，
        // 故此处 newKey == prevKey 恒成立、必然早退。只有用户在下拉主动选了**另一个**目标（key 真变）才落到下面。
        if (newKey == prevKey) return;

        // 用户主动切换目标 → 开新会话（三类一致；会话已持久化，误切可从历史找回）。
        // 诊断日志：此清空点应是清空会话的**唯一**路径（程序赋值走 SetSelectedTargetSilently 已从机制上杜绝）。
        // 若运行时观察到「会话被清空但用户并未主动切目标」，Trace 里的 prevKey/newKey/kind 可立即锁定触发源。
        System.Diagnostics.Trace.WriteLine(
            $"[ChatTargetSwitch] Messages cleared: prevKey=({prevKey.Id},{prevKey.Kind}) → newKey=({newKey.Id},{newKey.Kind}) kind={kind} IsAgentRunning={IsAgentRunning} IsInitializing={_isInitializing} thread={Environment.CurrentManagedThreadId}");

        _currentConversationId = Guid.Empty;
        Messages.Clear();
        ActiveTodos.Clear(); HasActivePlan = false; PlanAwaitingAccept = false;
        _pendingHistoryTurns = null;

        PersistSelectedTargetAsync().SafeFireAndForget();
        RefreshModeAsync().SafeFireAndForget();
    }

    /// <summary>程序化赋值 <see cref="SelectedChatTarget"/>（刷新池/启动恢复/恢复会话），**保证不触发会话重置**：
    /// 先把 <c>_selectedTargetKey</c> 预置为目标的 key 再赋值，使 <see cref="OnSelectedChatTargetChanged"/> 里
    /// <c>newKey == prevKey</c> 恒成立 → 早退。这样「重置会话」只可能由用户在 ComboBox 主动选别的目标触发——
    /// 任何程序赋值（改 agent/rule/workflow 文件、改知识对象、git 写 labs）从机制上都不可能清空会话。</summary>
    private void SetSelectedTargetSilently(object? target)
    {
        var kind = KindOf(target);
        _selectedTargetKey = TargetKey(target, kind);
        SelectedChatTarget = target;
    }

    partial void OnSelectedChatModeChanged(ChatMode value)
    {
        OnPropertyChanged(nameof(ShowPlanAccept));
        if (_syncingMode || _isInitializing || !HasModeSupport) return;
        // 已存在会话：立即 SetMode（plan/execute）；新会话：不改，由 run delegate 的 desiredMode 在首轮应用。
        // 审批子档（Manual/AutoEdit/Auto）不经 SetMode——它们是 VM 状态，在 OnApprovalRequest 逐工具读取。
        if (_currentConversationId != Guid.Empty)
            SetModeOnSessionAsync(value.ToDesiredMode()).SafeFireAndForget();
    }

    partial void OnIsAgentRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowPlanAccept));
        OnPropertyChanged(nameof(ShowPlanDismiss));
    }
    partial void OnHasModeSupportChanged(bool value) => OnPropertyChanged(nameof(ShowPlanAccept));

    /// <summary>刷新聊天头统一下拉池（全部 agent/规则/工作流），保留当前选中。
    /// 按 (Id,Kind) 匹配而非引用：reload 后实例被替换，引用匹配会丢选中并误切目标。</summary>
    private void RefreshChatTargetItems()
    {
        _dispatcher.Post(() =>
        {
            var current = SelectedChatTarget;
            var curKey = TargetKey(current, KindOf(current));
            ChatTargetItems.Clear();
            // 列全部 agent/规则/工作流——无召唤门控，创建即可在下拉选用。
            foreach (var e in _experts.Experts) ChatTargetItems.Add(e);
            foreach (var r in _rules.Rules) ChatTargetItems.Add(r);
            foreach (var w in _workflows.Workflows) ChatTargetItems.Add(w);

            // 同一逻辑目标仍在池中 → 静默把 SelectedChatTarget 重定向到其新实例（仅同步引用，
            // 供 ComboBox 显示正确项 + 后续 KindOf 取值正确）。走 SetSelectedTargetSilently——它预置 key 再赋值，
            // 保证 OnSelectedChatTargetChanged 里 newKey==prevKey、绝不重置会话。labs 任何文件变更（含改 agent/
            // rule/workflow 文件本身、改知识对象）触发的 reload 都经此处，从机制上不可能清空当前会话。
            if (curKey.Kind != ChatTargetKind.None)
            {
                var next = ChatTargetItems.FirstOrDefault(t => TargetKey(t, KindOf(t)) == curKey);
                if (next is not null && !ReferenceEquals(next, SelectedChatTarget))
                    SetSelectedTargetSilently(next);
            }
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

    /// <summary>取消进行中的流式运行（新聊天/切会话/删会话前调）。
    /// 只 Cancel token，不阻塞等待——RunSegmentedStreamAsync 的 finally 会在 dispatcher 上
    /// 清 IsAgentRunning 并 Dispose _runCts。CT 转发链路完整（→ agent.RunStreamingAsync(ct)），
    /// 故 Cancel 会让卡住的 LLM 流/工具调用抛 OCE 而结束本轮。</summary>
    private void CancelRunningRun()
    {
        _runCts?.Cancel();
    }

    /// <summary>用户在输入框切模式：新会话只记 SelectedChatMode（首轮 desiredMode 应用），已存在会话立即 SetMode（plan/execute）。</summary>
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

    /// <summary>按当前 <see cref="SelectedChatMode"/> 算工具审批的自动决定。
    /// 返回非 null = 自动批准（跳过对话框+审批卡，走与用户批准相同的再调用路径恢复执行）；返回 null = 需弹框。
    /// - Auto：全部自动批（含 Dangerous）。
    /// - AutoEdit：自动批非 Dangerous（Safe/Confirm）；Dangerous 弹框。lookup 为 null（未注册）退化为全弹（安全降级）。
    /// - Manual/Plan：始终弹框（Plan 轮一般不调写工具，防御性弹框）。
    /// AlwaysApprove=false：一次性批，不设 session 级 standing rule（模式可变，不能永久放行）。</summary>
    private ToolApprovalDecision? TryAutoApprove(ToolApprovalRequest req)
    {
        // 无模式选择器（RequireToolApproval-only agent 或规则）→ 不自动批，走对话框（保留旧行为）。
        // 4 档自动批策略仅对有选择器（EnableAgentModeProvider/工作流）的会话生效。
        if (!HasModeSupport) return null;
        switch (SelectedChatMode)
        {
            case ChatMode.Auto:
                return new ToolApprovalDecision(req.RequestId, Approved: true, "自动批准（自动模式）", AlwaysApprove: false);
            case ChatMode.AutoEdit:
                // lookup 缺失 → 视为 Dangerous（弹框，安全降级）。
                var level = _levelLookup?.LevelOf(req.ToolName) ?? ToolApprovalLevel.Dangerous;
                return level != ToolApprovalLevel.Dangerous
                    ? new ToolApprovalDecision(req.RequestId, Approved: true, "自动批准（自动编辑模式）", AlwaysApprove: false)
                    : null;
            default: // Manual / Plan
                return null;
        }
    }

    /// <summary>轮后从会话读实际模式（捕获 agent 自行 set_mode 的切换），带 guard 不回触 SetMode。
    /// 只同步 plan/execute（MAF 唯一存的两态）；execute 子档（Manual/AutoEdit/Auto）保留 VM 当前值——
    /// set_mode 经 ToolFilteringChatClient 过滤，agent 不能自切，子档只由用户改。</summary>
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
            // MAF=plan → Plan；MAF=execute → 若当前是 Plan 则置默认 AutoEdit，否则保留 execute 子档。
            SelectedChatMode = ChatModeExtensions.IsPlanMode(m)
                ? ChatMode.Plan
                : (SelectedChatMode == ChatMode.Plan ? ChatMode.AutoEdit : SelectedChatMode);
            _syncingMode = false;
        }
    }

    /// <summary>接受计划：切 AutoEdit（execute 子档）+ 续跑一轮"已接受计划，进入执行"。</summary>
    [RelayCommand]
    private async Task AcceptPlanAsync()
    {
        if (!HasModeSupport || IsAgentRunning || _currentConversationId == Guid.Empty) return;
        PlanAwaitingAccept = false; // 已接受→不再显示接受条（execute 轮后也不会误弹）
        _syncingMode = true; SelectedChatMode = ChatMode.AutoEdit; _syncingMode = false;
        await SetModeOnSessionAsync("execute");
        InputText = "（已接受计划，进入执行模式）";
        await SendInputAsync();
    }

    [RelayCommand]
    private async Task SendInputAsync()
    {
        if (!CanSendInput || string.IsNullOrWhiteSpace(InputText)) return;
        // 防重入：UI 已靠 IsAgentRunning 隐藏发送键/禁用输入框，这里兜底——
        // 上一轮未结束时（取消尚未落地）绝不再起一轮，避免 _runCts 被覆盖、两轮并发。
        if (IsAgentRunning) return;

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
                _nextRunSeq = 1;
                var conv = new Conversation
                {
                    ConversationId = _currentConversationId,
                    AgentId = targetId.ToString(),
                    AgentName = targetName,
                    Title = content.Length > 20 ? content[..20] + "…" : content,
                };
                // 用户消息不再挂在 Conversation 上——它随 AgentRun.UserMessage 在本轮结束时持久化。
                await _conversationStore.SaveConversationAsync(conv);
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
            // 规则无 mode（desiredMode 传 null）；agent/工作流传 HasModeSupport?SelectedChatMode.ToDesiredMode():null。
            var desiredMode = kind == ChatTargetKind.Rule ? null : (HasModeSupport ? SelectedChatMode.ToDesiredMode() : null);
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
            PlanAwaitingAccept = desiredMode == "plan" && SelectedChatMode == ChatMode.Plan
                && _todosTouchedThisTurn && HasActivePlan
                && !ActiveTodos.Any(t => t.IsComplete);

            // 持久化本轮为 run+events（typed 事件流，CallId 写入时即配对）并乐观 bump 侧栏项。
            await PersistRunAsync(content, runItems, fullText.ToString());
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

            var conv = await _conversationStore.GetConversationAsync(_currentConversationId);
            if (conv is null) return;
            conv.Title = title;
            await _conversationStore.SaveConversationAsync(conv);

            await _dispatcher.InvokeAsync(() =>
            {
                var item = Conversations.FirstOrDefault(c => c.Id == _currentConversationId);
                if (item is not null) item.Title = title;
            });
        }
        catch { /* 标题生成失败不影响主流程 */ }
    }

    /// <summary>
    /// 把本轮 transcript 持久化为一个 <see cref="AgentRun"/> + 其 <see cref="AgentEvent"/> 事件流（typed、append-only，
    /// CallId 在 ToolCall/ToolResult 写入时即配对），调 <see cref="IConversationStore.AppendRunAsync"/> 原子写入并
    /// 反范式化更新会话 preview/updatedAt。乐观 bump 侧栏项。用户消息在 <see cref="AgentRun.UserMessage"/> 上，不另存。
    /// 审批卡运行期态不持久化（与旧行为一致）。
    /// </summary>
    private async Task PersistRunAsync(string userContent,
        IReadOnlyList<ChatMessageItem> runItems, string fullText)
    {
        if (_currentConversationId == Guid.Empty) return;
        try
        {
            var conv = await _conversationStore.GetConversationAsync(_currentConversationId);
            if (conv is null) return;

            var now = DateTimeOffset.UtcNow;
            var hasAnswer = runItems.Any(i => i.IsText && !i.IsReasoning && !string.IsNullOrWhiteSpace(i.Content));
            var status = hasAnswer ? RunStatus.Completed : RunStatus.Interrupted;

            // 原子取号再构建——关闭 _nextRunSeq 跨 await AppendRunAsync 的并发窗口（两轮 persist 并发会撞 Seq）。
            // 失败时已消耗的序号留空隙无害（seq 仅用于排序，不要求稠密）。
            var runSeq = System.Threading.Interlocked.Increment(ref _nextRunSeq) - 1;
            var run = new AgentRun
            {
                ConversationId = _currentConversationId,
                Seq = runSeq,
                Status = status,
                AgentId = conv.AgentId,
                AgentName = conv.AgentName,
                UserMessage = userContent,
                StartedAt = now,   // 近似（实际开始更早）；持久化/排序用
                FinishedAt = now,
            };

            var events = new List<AgentEvent>();
            var seq = 1;
            foreach (var item in runItems)
            {
                // 用户消息已在 run.UserMessage；审批卡运行期态不持久化；跳过空思考段。
                if (item.IsUser || item.IsApprovalCard) continue;
                if (item.IsText && item.IsReasoning && string.IsNullOrWhiteSpace(item.Content)) continue;

                if (item.IsPlanCard)
                {
                    var todos = item.Todos.Select(t => new TodoItemInfo(t.Title, t.Title, t.Description, t.IsComplete)).ToList();
                    events.Add(MakeEvent(run.Id, ref seq, AgentEventType.PlanUpdated, new AgentEventPayload.PlanUpdated(todos)));
                }
                else if (item.IsToolStep)
                {
                    events.Add(MakeEvent(run.Id, ref seq, AgentEventType.ToolCall,
                        new AgentEventPayload.ToolCall(item.CallId, item.ToolName ?? "tool", item.Input)));
                    // 未完成的工具步（中断）只存 call，无 result——重建时为孤儿 call（UI 仍展示 call 卡），
                    // LLM 历史重建时由 EventHistoryRebuilder.DropOrphanToolCalls 丢弃。
                    if (item.IsToolCompleted)
                        events.Add(MakeEvent(run.Id, ref seq, AgentEventType.ToolResult,
                            new AgentEventPayload.ToolResult(item.CallId, item.ToolName ?? "tool", item.Output)));
                }
                else if (item.IsReasoning)
                    events.Add(MakeEvent(run.Id, ref seq, AgentEventType.ReasoningChunk,
                        new AgentEventPayload.ReasoningChunk(item.Content ?? string.Empty)));
                else
                    events.Add(MakeEvent(run.Id, ref seq, AgentEventType.TextChunk,
                        new AgentEventPayload.TextChunk(item.Content ?? string.Empty)));
            }

            await _conversationStore.AppendRunAsync(run, events);

            await _dispatcher.InvokeAsync(() =>
            {
                var item = Conversations.FirstOrDefault(c => c.Id == _currentConversationId);
                if (item is not null)
                {
                    var preview = string.IsNullOrWhiteSpace(fullText) ? userContent : fullText;
                    item.Preview = preview.Length > 40 ? preview[..40] + "…" : preview;
                    item.DisplayTime = FormatTime(now);
                    // 移到顶部
                    Conversations.Remove(item);
                    Conversations.Insert(0, item);
                }
            });
        }
        catch { /* run 持久化失败不阻塞对话 */ }
    }

    /// <summary>构造一个事件：Seq 自增分配，Payload 序列化为紧凑 JSON。</summary>
    private static AgentEvent MakeEvent(Guid runId, ref int seq, AgentEventType type, object payload)
    {
        var e = new AgentEvent { RunId = runId, Seq = seq, EventType = type, Payload = AgentEventPayload.Serialize(payload) };
        seq++;
        return e;
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
}

/// <summary>聊天目标偏好（chatTargets 键）：当前选中目标。camelCase JSON 持久化（旧 Summoned 字段被忽略）。</summary>
sealed record ChatTargetPrefs
{
    public ChatTargetSelected? Selected { get; set; }
}

sealed record ChatTargetSelected
{
    public string? Kind { get; set; } // "Agent" | "Rule" | "Workflow"
    public string? Id { get; set; }
}
