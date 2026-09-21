using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Threading;
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
    private readonly ConversationTitleApplier _titleApplier; // 会话标题生成+应用（持久化+侧栏项）——见 ConversationTitleApplier.cs
    private readonly IConversationSummaryService _summaryService; // 手机远程回复摘要（agent 跑完把 fullText 压成概览）
    private readonly IDeviceAutoConnectService _autoConnect;
    private readonly ChatStreamEngine _engine; // 流式渲染引擎（B5 抽出——审批/桌宠/文件编辑闭环/计划快照全在内），见 ChatStreamEngine.cs
    private readonly ChatTranscriptPersister _persister; // run+events 持久化（2026-09 ChatVM 拆分 B1 抽出），见 ChatTranscriptPersister.cs
    private readonly ChatLlmSelectionService _llmSelection; // LLM 下拉加载/持久化/invalidate（B2 抽出），见 ChatLlmSelectionService.cs
    private readonly ChatTargetRegistry _targets; // 目标池/逻辑键/偏好持久化（B3 抽出），见 ChatTargetRegistry.cs
    private readonly ConversationHistoryLoader _historyLoader; // 会话历史 DB 读+重建（B4 抽出），见 ConversationHistoryLoader.cs

    private readonly IDeviceChatClient _deviceChatClient;
    private readonly ILlmConfigService _llmService;
    private readonly IServiceProvider _serviceProvider;
    private readonly ISettingsDialogService _settingsDialog;
    private readonly RemoteMessagePump _remotePump; // 远程消息并发管线（Progress 过滤/去重/每会话串行）——见 RemoteMessagePump.cs

    private readonly ExpertsViewModel _experts;
    private readonly WorkflowsViewModel _workflows;
    private readonly RulesViewModel _rules;

    // —— 多会话并发：每会话独立 runtime，背景 run 往自己的 Messages 追加，切会话只换 _current 不取消 ——
    private readonly ConcurrentDictionary<Guid, ConversationRuntime> _runtimes = new();
    private ConversationRuntime _current; // 当前显示的会话 runtime（永不 null：待新聊天=Guid.Empty 占位 runtime）
    private Guid _currentConversationId = Guid.Empty; // _current.ConversationId 的镜像（30+ 读点免逐改）

    private bool _isInitializing;
    private bool _syncingMode; // 程序化刷新 SelectedChatMode 时置 true，避免 OnSelectedChatModeChanged 回触 SetMode

    // —— 历史增量加载：切换会话时只渲染最近若干轮，更早的暂存，滚到顶部再加载 ——
    // 避免长会话一次性重建全部消息 + 同步 markdown 解析导致的卡顿（全在 UI 线程）。
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
        _titleApplier = new ConversationTitleApplier(
            serviceProvider.GetRequiredService<IConversationTitleService>(), _conversationStore, _dispatcher);
        _summaryService = serviceProvider.GetRequiredService<IConversationSummaryService>();
        _autoConnect = serviceProvider.GetRequiredService<IDeviceAutoConnectService>();
        _experts = experts;
        _workflows = workflows;
        _rules = rules;

        // 流式渲染引擎（B5 抽出）：审批/桌宠/文件编辑闭环全在引擎内。委托缝——模式/模式支持**逐工具实时读**
        // （运行中切档立即生效）；isCurrentRuntime 决定背景会话计划变更是否上抛 PlanSummary（原 ReferenceEquals(_current, runtime)）。
        _engine = new ChatStreamEngine(_dispatcher,
            serviceProvider.GetService<IApprovalDialogService>(),
            serviceProvider.GetService<IToolApprovalLevelLookup>(),
            serviceProvider.GetService<IPetTokenStream>(),
            () => SelectedChatMode,
            () => HasModeSupport,
            rt => ReferenceEquals(_current, rt));
        _engine.PlanSummaryChanged += () => OnPropertyChanged(nameof(PlanSummary));
        _persister = new ChatTranscriptPersister(_conversationStore);
        // LLM 选择逻辑（B2 抽出）：invalidate 经委托注入——遍历 Experts 调 _agentRunner.Invalidate。
        _llmSelection = new ChatLlmSelectionService(_llmService, _preferences,
            serviceProvider.GetService<Aether.AI.Harness.SelectedLlmHolder>(),
            () => { foreach (var expert in _experts.Experts) _agentRunner.Invalidate(expert.Id); });
        _targets = new ChatTargetRegistry(_experts, _workflows, _rules, _preferences);
        _historyLoader = new ConversationHistoryLoader(_conversationStore);
        // processor 委托指向 ProcessRemoteMessageAsync（跑 agent/摘要/回复的编排，留本 VM）；
        // 泵只管过滤/去重/入队/串行调度。
        _remotePump = new RemoteMessagePump(ProcessRemoteMessageAsync);

        // 三类目标的可选集合变更（新建/删除/catalog reload）：刷新统一下拉池。选中是下拉选择的独立动作。
        _experts.TargetsChanged += OnChatTargetsChanged;
        _workflows.TargetsChanged += OnChatTargetsChanged;
        _rules.TargetsChanged += OnChatTargetsChanged;

        Conversations.CollectionChanged += (_, _) => HasConversations = Conversations.Count > 0;

        // 初始 runtime = 待新聊天（Guid.Empty 占位，不入 _runtimes；首次发送落库拿到真 Guid 后回填 + 注册）。
        _current = new ConversationRuntime(Guid.Empty);
        SetCurrentInternal(_current);

        SubscribeToConnectionStateChanges();
    }

    /// <summary>切换当前显示的会话 runtime：detach 旧 runtime（不停其 run——背景继续），attach 新 runtime 并
    /// 转发其 ObservableObject 变更到 VM 代理属性。切会话不取消任何 run。</summary>
    private void SetCurrentInternal(ConversationRuntime rt)
    {
        if (ReferenceEquals(_current, rt)) return;
        if (_current is not null) _current.PropertyChanged -= OnRuntimePropertyChanged;
        _current = rt;
        _currentConversationId = rt.ConversationId;
        if (rt is not null) rt.PropertyChanged += OnRuntimePropertyChanged;
        // 代理属性全量通知：Messages/ActiveTodos 换集合实例（视图重绑），标量重读。
        OnPropertyChanged(nameof(Messages));
        OnPropertyChanged(nameof(ActiveTodos));
        OnPropertyChanged(nameof(IsAgentRunning));
        OnPropertyChanged(nameof(HasActivePlan));
        OnPropertyChanged(nameof(PlanAwaitingAccept));
        OnPropertyChanged(nameof(TokenInfo));
        OnPropertyChanged(nameof(HasMessages));
        OnPropertyChanged(nameof(HasMoreHistory));
        OnPropertyChanged(nameof(PlanSummary));
        OnPropertyChanged(nameof(ShowPlanAccept));
        OnPropertyChanged(nameof(ShowPlanDismiss));
        // 切到的会话是否在跑 → 侧栏项角标
        UpdateConversationRunningFlag(rt);
    }

    /// <summary>转发 runtime 标量变更：当前 runtime 的变更→VM 代理属性（驱动 UI）；**任何** runtime 的
    /// IsRunning 变更→推侧栏角标（背景 run 起/止也刷新列表项 ⏳，不只当前会话）。
    /// 取代原 [ObservableProperty] 的 partial On*Changed 钩子（IsAgentRunning/HasActivePlan/PlanAwaitingAccept）。</summary>
    private void OnRuntimePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is not ConversationRuntime rt) return;
        // IsRunning 变更：任何 runtime（含背景）都推侧栏角标——用户能看到其它会话在后台跑。
        if (e.PropertyName == nameof(ConversationRuntime.IsRunning))
            UpdateConversationRunningFlag(rt);
        // 非当前 runtime 的标量变更不转发到 VM 代理属性——避免背景 run 的 plan/token 变更污染当前显示。
        if (!ReferenceEquals(sender, _current)) return;
        // runtime 属性名→VM 代理属性名映射：IsRunning→IsAgentRunning（其余同名）。
        // 不映射的话 XAML 绑的 !IsAgentRunning / IsAgentRunning 收不到 "IsRunning" 通知 →
        // 当前会话 run 起/止时输入框禁用态、发送/停止键显隐不刷新（SetCurrentInternal 切会话时
        // 显式通知了 IsAgentRunning 故切会话本身不受影响，但 run 起/止在 OnRuntimePropertyChanged 走此路径）。
        var proxyName = e.PropertyName == nameof(ConversationRuntime.IsRunning)
            ? nameof(IsAgentRunning)
            : e.PropertyName;
        OnPropertyChanged(proxyName!);
        switch (e.PropertyName)
        {
            case nameof(ConversationRuntime.IsRunning):
                OnPropertyChanged(nameof(ShowPlanAccept));
                OnPropertyChanged(nameof(ShowPlanDismiss));
                break;
            case nameof(ConversationRuntime.HasActivePlan):
                OnPropertyChanged(nameof(PlanSummary));
                OnPropertyChanged(nameof(ShowPlanDismiss));
                break;
            case nameof(ConversationRuntime.PlanAwaitingAccept):
                OnPropertyChanged(nameof(ShowPlanAccept));
                OnPropertyChanged(nameof(ShowPlanDismiss));
                break;
        }
    }

    /// <summary>取/建 runtime：已存在复用（切回仍在跑的会话见续接流式），否则新建并注册。</summary>
    private ConversationRuntime GetOrCreateRuntime(Guid conversationId)
    {
        if (conversationId == Guid.Empty) return _current; // 待新聊天：用占位 runtime
        return _runtimes.GetOrAdd(conversationId, id => new ConversationRuntime(id));
    }

    /// <summary>把 runtime.IsRunning 同步到侧栏会话项角标（后台 run 完成/切回时刷新）。</summary>
    private void UpdateConversationRunningFlag(ConversationRuntime rt)
    {
        var item = Conversations.FirstOrDefault(c => c.Id == rt.ConversationId);
        if (item is not null) item.IsRunning = rt.IsRunning;
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
            // 重连/首次连接成功后补拉离线期间手机发的消息（每会话最后一条未回复的用户消息）。
            // 初始化完成前不跑（目标未恢复，会对旧消息发「未选用专家」回执）——RunCatchUpAsync 内自守卫。
            if (status == "已连接" || status == "重新连接成功")
            {
                RunCatchUpAsync().SafeFireAndForget();
                // 上报当前选中专家名给服务端（手机只读状态用）。重连后服务端内存态可能已被清，需重新覆盖；
                // 未连接时 ReportSelectedTargetToServer 自静默，故此处直接调无妨。
                ReportSelectedTargetToServer();
            }
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

    // 最近一次远程消息/设备发送所在会话 id：SendInputAsync 设备发送分支据此把消息续进同一设备会话。
    private Guid _recentConversationId;

    /// <summary>重连/冷启动后补拉离线期间手机发的消息：取回每会话最后一条未回复的用户消息并回放。
    /// 回放走同一 HandleMessageReceived（含去重），已实时收到过的会被消息泵跳过。
    /// 仅在初始化完成后运行（目标已恢复）——否则会在 boot 期对未选目标的旧消息发「未选用专家」回执。</summary>
    private async Task RunCatchUpAsync()
    {
        if (_isInitializing) return;
        try
        {
            var messages = await _deviceChatClient.GetCatchUpMessagesAsync();
            if (messages.Count == 0) return;
            foreach (var msg in messages)
                await HandleMessageReceived(msg);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[Chat] 补拉离线消息失败: {ex.Message}");
        }
    }

    /// <summary>收到远程 SignalR 消息回调（SignalR 线程，须微秒级返回）。仅记录最近会话 id + 交消息泵
    /// （<see cref="RemoteMessagePump"/>：Progress 过滤/去重/按会话入队），实际处理见
    /// <see cref="ProcessRemoteMessageAsync"/>（每会话 drainer 串行调用）。</summary>
    private Task HandleMessageReceived(DeviceMessage msg)
    {
        _recentConversationId = msg.ConversationId;
        _remotePump.Accept(msg);
        return Task.CompletedTask;
    }

    /// <summary>实际处理一条远程消息（drainer 串行调用）：显示 + 跑 agent + 回复 / 未选目标回执。
    /// 操作 msg.ConversationId 对应的 runtime（可能与当前显示会话不同）——修掉旧路径写共享 Messages/
    /// IsAgentRunning 的 latent race：远程 run 往自己的 runtime.Messages 追加，不污染当前显示会话。</summary>
    private async Task ProcessRemoteMessageAsync(DeviceMessage msg)
    {
        // 远程会话的 runtime（按 msg.ConversationId 取/建）——可能与当前显示会话不同。
        var runtime = GetOrCreateRuntime(msg.ConversationId);
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
            runtime.Messages.Add(remoteTurn);
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
            await _engine.RunSegmentedStreamAsync(runtime, remoteTurn, targetName,
                (handlers, approvalResponse, ct) => remoteKind == ChatTargetKind.Workflow
                    ? _workflowRunner.RunStreamingAsync(convId, targetId, msg.Content, handlers, approvalResponse, desiredMode, ct)
                    : _agentRunner.RunStreamingAsync(convId, targetId, msg.Content, handlers, approvalResponse, desiredMode, ct),
                fullText,
                // 远程 run：老板手机只收最终概括，过程（进度心跳/审批）全不发——危险工具一律自动放行。
                autoApproveAll: true);
            if (remoteKind != ChatTargetKind.Rule) await RefreshModeAsync(msg.ConversationId);

            // 远程消息触发运行：把回复通过 SignalR 发回远程发送方（否则对方收不到结果）。
            // 回环安全：服务端对设备发送方用 GroupExcept 不回广播给本机（见 SignalRChatNotifier），
            // 桌面端不会收到自己的回复→不会重跑 agent。客户端 _recentSentMessageIds 仅作边缘竞态兜底。
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

        // 未选用专家时本地简单回复（单独一轮）——加到该远程会话的 runtime
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
            runtime.Messages.Add(t);
        });
        // 回执手机：未选用专家目标时告知，否则老板「发了没反应」。
        if (_deviceChatClient.IsConnected)
        {
            try
            {
                await _deviceChatClient.SendMessageAsync(msg.ConversationId,
                    "⚠️ 桌面端当前未选用专家目标，请先在桌面端选择目标后再发送指令。", "Text");
            }
            catch
            {
                // 回执失败不影响本地已显示的回复
            }
        }
    }

    // —— 代理属性：读当前会话 runtime 的状态。切会话时 SetCurrentInternal raise 这些属性 → 视图重绑/重读 ——
    // Messages/ActiveTodos 换集合实例（视图 ItemsControl 重绑新集合）；标量由 OnRuntimePropertyChanged 转发。
    public ObservableCollection<ChatTurnItem> Messages => _current.Messages;
    public ObservableCollection<TodoItemVM> ActiveTodos => _current.ActiveTodos;
    public bool HasActivePlan { get => _current.HasActivePlan; set => _current.HasActivePlan = value; }
    public bool PlanAwaitingAccept { get => _current.PlanAwaitingAccept; set => _current.PlanAwaitingAccept = value; }
    public string? TokenInfo { get => _current.TokenInfo; set => _current.TokenInfo = value; }
    public bool HasMessages => _current.HasMessages;

    [ObservableProperty] private bool _isPlanExpanded = true;
    public string PlanSummary => HasActivePlan ? $"📋 计划 ({ActiveTodos.Count(t => t.IsComplete)}/{ActiveTodos.Count})" : "";
    partial void OnIsPlanExpandedChanged(bool value) => OnPropertyChanged(nameof(PlanSummary));
    [RelayCommand] private void TogglePlanExpanded() => IsPlanExpanded = !IsPlanExpanded;
    /// <summary>确认执行完毕：收起浮动计划面板（HasActivePlan→false）。execute 完成后用户手动点。</summary>
    [RelayCommand]
    private void DismissPlan()
    {
        _current.ActiveTodos.Clear();
        _current.HasActivePlan = false;
    }

    /// <summary>当前 agent 是否启用 AgentModeProvider（控制模式选择器/plan-accept 显隐）。</summary>
    [ObservableProperty]
    private bool _hasModeSupport;

    /// <summary>当前会话运行模式（4 档：Plan/Manual/AutoEdit/Auto）。MAF 只存 plan/execute；
    /// execute 子档（Manual/AutoEdit/Auto）是纯 VM 状态，经审批拦截逐工具决定自动批/弹框。默认 AutoEdit。</summary>
    [ObservableProperty]
    private ChatMode _selectedChatMode = ChatMode.AutoEdit;

    /// <summary>agent 是否正在流式运行（代理当前会话 runtime.IsRunning；驱动 plan-accept 条显隐）。</summary>
    public bool IsAgentRunning => _current.IsRunning;

    /// <summary>plan 模式轮产出计划后（PlanAwaitingAccept）、非运行中时显示"接受计划→执行"条。
    /// 门控用「本轮是否待接受」而非「当前模式」，避免 execute 跑完 RefreshModeAsync 把模式回翻 plan 时误弹。</summary>
    public bool ShowPlanAccept => HasModeSupport && PlanAwaitingAccept && !IsAgentRunning;

    /// <summary>有计划、非运行中、且不在待接受态 → execute 已跑完，可点「确认执行完毕」收起面板。</summary>
    public bool ShowPlanDismiss => HasActivePlan && !IsAgentRunning && !PlanAwaitingAccept;

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
        // 切到新聊天**不取消**进行中的流式运行——背景 run 继续往自己的 runtime.Messages 追加，
        // 切回该会话即见流式续接。只换 _current 指针到新的 Guid.Empty 占位 runtime（SetCurrentInternal
        // 内部设 _current/_currentConversationId + raise 全代理属性变更 → 视图重绑到空集合）。
        System.Diagnostics.Trace.WriteLine(
            $"[NewChat] before: convId={_currentConversationId} IsAgentRunning={IsAgentRunning} _runtimes.Count={_runtimes.Count}");
        SetCurrentInternal(new ConversationRuntime(Guid.Empty));
        System.Diagnostics.Trace.WriteLine(
            $"[NewChat] after: convId={_currentConversationId} IsAgentRunning={IsAgentRunning} _current.IsRunning={_current.IsRunning}");
        // 懒创建：仅进入待新聊天态，不在此时落库（避免空会话进列表）。
        foreach (var c in Conversations) c.IsCurrent = false;
    }

    [RelayCommand]
    private async Task ResumeConversationAsync(ConversationItem item)
    {
        if (item is null) return;
        // 切会话**不取消**进行中的运行——背景 run 继续往自己的 runtime.Messages 追加，切回即见续接。
        // 取/建目标 runtime：若已有 live turns（切走再切回、或背景仍在跑）直接显，不重建（不踩流式）。
        var runtime = GetOrCreateRuntime(item.Id);
        var alreadyLoaded = runtime.Messages.Count > 0;
        SetCurrentInternal(runtime);
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

        // 首次切来（runtime 空）→ 从 typed 事件流重建 transcript 填进 runtime.Messages。
        // 已加载过（切走再切回）→ runtime.Messages 仍是上次的内容（背景 run 可能还在追加），直接显。
        // IsLoadingHistory：防切走又切回双重加载（Messages 仍空但上次加载 Task 未完）。
        if (!alreadyLoaded && !runtime.IsLoadingHistory)
        {
            runtime.IsLoadingHistory = true;
            try
            {
                // DB 读 + 轮次重建移后台线程——不阻塞 UI 线程（SQLite 读 + ChatTurnItem 构造 + 历史轮 markdown
                // 预解析集中在 Task.Run 内完成；runtime.Messages.Add 留 UI 线程触发渲染）。
                // 重建逻辑在 <see cref="ConversationHistoryLoader"/>（B4 抽出）；分批渲染留 VM（UI 策略）。
                var built = await Task.Run(() => _historyLoader.LoadAsync(item.Id, item.AgentName)).ConfigureAwait(true);

                if (built is { } result)
                {
                    runtime.NextRunSeq = result.NextRunSeq;
                    // 只把最近 N 轮加入 Messages 渲染（markdown 解析仅在绑定时发生，分批避免卡顿）；更早轮暂存待上滚增量加载。
                    if (result.Turns.Count <= HistoryInitialBatch)
                    {
                        foreach (var t in result.Turns) runtime.Messages.Add(t);
                    }
                    else
                    {
                        runtime.PendingHistoryTurns = result.Turns.Take(result.Turns.Count - HistoryInitialBatch).ToList();
                        foreach (var t in result.Turns.Skip(result.Turns.Count - HistoryInitialBatch)) runtime.Messages.Add(t);
                    }
                }
                else
                {
                    runtime.NextRunSeq = 1;
                }
            }
            finally
            {
                runtime.IsLoadingHistory = false;
            }
        }
    }

    [RelayCommand]
    private async Task DeleteConversationAsync(ConversationItem item)
    {
        if (item is null) return;
        // 删会话**要**取消其 run（删的会话 run 该杀），从 _runtimes 移除其 runtime。
        if (_runtimes.TryRemove(item.Id, out var delRt))
            CancelRunningRun(delRt);
        try
        {
            await _conversationStore.DeleteAsync(item.Id);
            // 清出运行时会话状态：丢弃 agent/workflow 路径缓存的 FrameworkAgentSession + 平台 session 字典，
            // 使 RestoringChatHistoryProvider 的 per-session 历史缓存随 GC 自动回收（防长会话累积内存泄漏）。
            _agentRunner.EvictConversation(item.Id);
            _workflowRunner.EvictConversation(item.Id);
            Conversations.Remove(item);
            // 删的正是当前显示会话 → 切到新聊天占位 runtime（不取消其它会话的背景 run）。
            if (_currentConversationId == item.Id)
                SetCurrentInternal(new ConversationRuntime(Guid.Empty));
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

    // TokenInfo 已下沉为 runtime 代理属性（_current.TokenInfo），见上方。

    /// <summary>聊天头统一下拉池：已召唤的 agent/规则/工作流（混合 object，含 ExpertItem + WorkflowItem）。</summary>
    public ObservableCollection<object> ChatTargetItems { get; } = new();
    public ObservableCollection<LlmConfigDto> LlmSelectItems { get; } = new();

    /// <summary>当前激活的对话目标（下拉选中）。agent/规则是 ExpertItem、工作流是 WorkflowItem。</summary>
    [ObservableProperty]
    private object? _selectedChatTarget;

    /// <summary>对话目标类型判别（薄包装——B3 拆分后实现在 <see cref="ChatTargetRegistry.KindOf"/>，~15 个调用点零改）。</summary>
    private static ChatTargetKind KindOf(object? t) => ChatTargetRegistry.KindOf(t);

    [ObservableProperty]
    private LlmConfigDto? _selectedLlmItem;

    /// <summary>从偏好恢复选中目标（跨重启保留），并保证启动时有一个默认选中。下拉池列全部目标，无召唤态。
    /// 偏好读取/解析在 <see cref="ChatTargetRegistry.RestoreAsync"/>（B3 抽出，含 legacy selectedAgentId 迁移）。
    /// 偏好恢复失败（state=null）只跳过「选中/展开态恢复」，**池照常填充**——偏好坏不该让下拉一整个会话没 agent。</summary>
    private async Task RestoreSelectedTargetAsync()
    {
        var state = await _targets.RestoreAsync();

        // ★ 下拉池 + 选中须在 UI 线程改：InitializeAsync 链经 ListAsync(ConfigureAwait(false)) 后续可能落在 threadpool，
        // ObservableCollection 跨线程修改 Avalonia ComboBox 不会反映（首轮下拉「明明有数据却为空」的隐患）。
        // 镜像 RefreshChatTargetItems 的 dispatcher 范式。InvokeAsync 返回 Task 可 await（Post 不可，会与后续
        // RefreshConversationsAsync 乱序）。当前虽实测在 UI 线程（thread=1），包 dispatcher 是防御 + 与既有范式一致。
        await _dispatcher.InvokeAsync(() =>
        {
            // 同步填充下拉（列全部 agent/规则/工作流；此时已在 UI 线程，不依赖后续的 RefreshChatTargetItems）
            ChatTargetItems.Clear();
            foreach (var t in _targets.BuildPool()) ChatTargetItems.Add(t);

            if (state is null)
            {
                // 偏好恢复失败 → 只跳过选中/展开态恢复，仍给默认选中（首项）。
                SetSelectedTargetSilently(ChatTargetItems.FirstOrDefault());
                return;
            }

            // 选中：已保存的 → 回退到池中首项
            var saved = _targets.ResolveSaved(state.Selected);
            SetSelectedTargetSilently(saved ?? ChatTargetItems.FirstOrDefault());
            if (state.RecentCollapsed) IsRecentExpanded = false;
        });
    }

    /// <summary>持久化当前选中目标（下拉切换时调）：写 Selected 字段（无召唤态）。实现在 <see cref="ChatTargetRegistry"/>。</summary>
    private async Task PersistSelectedTargetAsync()
    {
        await _targets.PersistSelectionAsync(SelectedChatTarget);
    }

    /// <summary>当前选中目标的显示名（供手机只读状态显示）；未选用返回空串。</summary>
    private string CurrentTargetDisplayName() => SelectedChatTarget switch
    {
        ExpertItem ei => ei.Name,
        WorkflowItem wi => wi.Name,
        _ => string.Empty,
    };

    /// <summary>把当前选中专家名上报给服务端（手机只读状态用）。未连接/上报失败静默丢弃，不影响主流程。
    /// 在目标切换 +（重）连接成功后调用——让手机能看「桌面当前会用哪个专家回」。</summary>
    private void ReportSelectedTargetToServer()
    {
        if (!_deviceChatClient.IsConnected) return;
        _deviceChatClient.ReportSelectedAgentAsync(CurrentTargetDisplayName()).SafeFireAndForget();
    }

    /// <summary>加载 LLM 配置列表到下拉框，并恢复上次选中的 LLM（跨重启保留）。
    /// 加载/选中解析/holder 同步逻辑在 <see cref="ChatLlmSelectionService"/>（B2 抽出）；
    /// 集合填充/赋值留 VM——保持原线程语义（无自带 dispatcher 包裹）。</summary>
    private async Task LoadLlmConfigsAsync()
    {
        var (configs, selected) = await _llmSelection.LoadAsync();
        LlmSelectItems.Clear();
        if (configs != null)
        {
            foreach (var c in configs)
                LlmSelectItems.Add(c);
            SelectedLlmItem = selected;
        }
    }

    /// <summary>选中 LLM 变化时持久化（启动期 _isInitializing 抑制）。逻辑在 <see cref="ChatLlmSelectionService"/>。</summary>
    partial void OnSelectedLlmItemChanged(LlmConfigDto? value)
    {
        if (_isInitializing) return;
        _llmSelection.OnSelectionChanged(value);
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
        SelectedChatMode = ChatMode.AutoEdit; // 默认自动编辑档（无选择器时值无意义——ChatApprovalDecider 不激活）
        _syncingMode = false;

        var newKey = ChatTargetRegistry.TargetKey(value, kind);
        var prevKey = _targets.SelectedKey;
        _targets.SelectedKey = newKey; // 始终跟踪（含启动期/resume，保证下一次真实切换判据正确）

        if (_isInitializing) return;

        // 同一逻辑目标（同 Id+Kind；实例可能因 reload 被替换）→ 非用户切换，不重置会话。
        // 程序化赋值（刷新池/恢复/启动）走 SetSelectedTargetSilently——它**先预置 _selectedTargetKey 再赋值**，
        // 故此处 newKey == prevKey 恒成立、必然早退。只有用户在下拉主动选了**另一个**目标（key 真变）才落到下面。
        if (newKey == prevKey) return;

        // 用户主动切换目标 → 开新会话（三类一致；会话已持久化，误切可从历史找回）。
        // 多会话并发：即便当前会话正在流式运行也允许切——SetCurrentInternal 只换 _current 指针不取消背景 run，
        // 旧会话的 run 继续往自己的 runtime.Messages 追加，切回即见续接。旧「运行中不能切」单会话守卫已废弃。
        // 切到新的 Guid.Empty 占位 runtime——旧会话 runtime（含可能的背景 run）不受影响、继续在后台跑。
        // 诊断日志：此切会话点应是切到新会话的**唯一**路径（程序赋值走 SetSelectedTargetSilently 已从机制上杜绝）。
        System.Diagnostics.Trace.WriteLine(
            $"[ChatTargetSwitch] new chat: prevKey=({prevKey.Id},{prevKey.Kind}) → newKey=({newKey.Id},{newKey.Kind}) kind={kind} IsAgentRunning={IsAgentRunning} IsInitializing={_isInitializing} thread={Environment.CurrentManagedThreadId}");

        SetCurrentInternal(new ConversationRuntime(Guid.Empty));

        PersistSelectedTargetAsync().SafeFireAndForget();
        RefreshModeAsync().SafeFireAndForget();
        // 用户切换了目标 → 上报新专家名给服务端（手机只读状态即时更新）。
        ReportSelectedTargetToServer();
    }

    /// <summary>程序化赋值 <see cref="SelectedChatTarget"/>（刷新池/启动恢复/恢复会话），**保证不触发会话重置**：
    /// 先把 <c>_selectedTargetKey</c> 预置为目标的 key 再赋值，使 <see cref="OnSelectedChatTargetChanged"/> 里
    /// <c>newKey == prevKey</c> 恒成立 → 早退。这样「重置会话」只可能由用户在 ComboBox 主动选别的目标触发——
    /// 任何程序赋值（改 agent/rule/workflow 文件、改知识对象、git 写 labs）从机制上都不可能清空会话。</summary>
    private void SetSelectedTargetSilently(object? target)
    {
        var kind = KindOf(target);
        _targets.SelectedKey = ChatTargetRegistry.TargetKey(target, kind);
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

    // OnIsAgentRunningChanged 已删——IsAgentRunning 现为 runtime 代理属性，派生通知在 OnRuntimePropertyChanged 统一转发。
    partial void OnHasModeSupportChanged(bool value) => OnPropertyChanged(nameof(ShowPlanAccept));

    /// <summary>刷新聊天头统一下拉池（全部 agent/规则/工作流），保留当前选中。
    /// 按 (Id,Kind) 匹配而非引用：reload 后实例被替换，引用匹配会丢选中并误切目标。
    /// 池构建在 <see cref="ChatTargetRegistry.BuildPool"/>（B3 抽出）；静默重指策略留 VM。</summary>
    private void RefreshChatTargetItems()
    {
        _dispatcher.Post(() =>
        {
            var current = SelectedChatTarget;
            var curKey = ChatTargetRegistry.TargetKey(current, KindOf(current));
            ChatTargetItems.Clear();
            foreach (var t in _targets.BuildPool()) ChatTargetItems.Add(t);
            // 诊断：池重建后的容量（experts/rules/workflows 三源合计）——「首轮下拉缺 agent」复发时
            // 在 dotnet run 控制台即可看到三源各自的条数，定位是哪个列表没就绪。
            System.Diagnostics.Trace.WriteLine(
                $"[ChatTargetPool] rebuilt: pool={ChatTargetItems.Count} experts={_experts.Experts.Count} rules={_rules.Rules.Count} workflows={_workflows.Workflows.Count} thread={Environment.CurrentManagedThreadId}");

            // 同一逻辑目标仍在池中 → 静默把 SelectedChatTarget 重定向到其新实例（仅同步引用，
            // 供 ComboBox 显示正确项 + 后续 KindOf 取值正确）。走 SetSelectedTargetSilently——它预置 key 再赋值，
            // 保证 OnSelectedChatTargetChanged 里 newKey==prevKey、绝不重置会话。labs 任何文件变更（含改 agent/
            // rule/workflow 文件本身、改知识对象）触发的 reload 都经此处，从机制上不可能清空当前会话。
            if (curKey.Kind != ChatTargetKind.None)
            {
                var next = ChatTargetItems.FirstOrDefault(t => ChatTargetRegistry.TargetKey(t, KindOf(t)) == curKey);
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

    /// <summary>停止当前显示会话的流式生成（取消 CancellationToken）。只影响当前会话，不影响其它并发会话。</summary>
    [RelayCommand]
    private void StopGeneration()
    {
        _current.RunCts?.Cancel();
    }

    /// <summary>取消指定会话的进行中流式运行（删会话前调）。
    /// 只 Cancel token，不阻塞等待——RunSegmentedStreamAsync 的 finally 会在 dispatcher 上
    /// 清 runtime.IsRunning 并 Dispose runtime.RunCts。CT 转发链路完整（→ agent.RunStreamingAsync(ct)），
    /// 故 Cancel 会让卡住的 LLM 流/工具调用抛 OCE 而结束本轮。
    /// 注意：新聊天/切会话**不再调此方法**——切会话只换 _current 指针不取消背景 run。仅删会话调。</summary>
    private void CancelRunningRun(ConversationRuntime? runtime = null)
    {
        var rt = runtime ?? _current;
        rt.RunCts?.Cancel();
    }

    /// <summary>取消**所有**会话的进行中流式运行（app 真退出时调）。
    /// 遍历 _runtimes + 当前占位 runtime，Cancel 各自 RunCts。进程即将退出，finally 的 dispatcher 清理
    /// 可能赶不上，但 Cancel 让 agent 工具调用抛 OCE 停止——防退出瞬间背景 run 执行危险工具的孤儿副作用。</summary>
    public void CancelAllRunning()
    {
        _current.RunCts?.Cancel();
        foreach (var rt in _runtimes.Values)
            rt.RunCts?.Cancel();
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

    /// <summary>轮后从会话读实际模式，带 guard 不回触 SetMode。<b>4 档全量同步</b>（MAF 1.22 自定义模式——
    /// 模式即完整状态，不再有「execute 子档保留 VM 值」的折衷）；未知值（如旧会话残留）保留现值。</summary>
    private async Task RefreshModeAsync(Guid? conversationId = null)
    {
        var convId = conversationId ?? _currentConversationId;
        if (convId == Guid.Empty || !HasModeSupport) return;
        string? m = KindOf(SelectedChatTarget) == ChatTargetKind.Workflow
            ? await _workflowRunner.GetModeAsync(convId).ConfigureAwait(false)
            : await _agentRunner.GetModeAsync(convId).ConfigureAwait(false);
        if (m is not null && ChatModeExtensions.FromMafMode(m) is { } synced)
        {
            _syncingMode = true;
            SelectedChatMode = synced;
            _syncingMode = false;
        }
    }

    /// <summary>接受计划：切 AutoEdit 档 + 续跑一轮"已接受计划，进入执行"。</summary>
    [RelayCommand]
    private async Task AcceptPlanAsync()
    {
        if (!HasModeSupport || IsAgentRunning || _currentConversationId == Guid.Empty) return;
        PlanAwaitingAccept = false; // 已接受→不再显示接受条（执行轮后也不会误弹）
        _syncingMode = true; SelectedChatMode = ChatMode.AutoEdit; _syncingMode = false;
        await SetModeOnSessionAsync(ChatMode.AutoEdit.ToDesiredMode());
        InputText = "（已接受计划，进入执行模式）";
        await SendInputAsync();
    }

    [RelayCommand]
    private async Task SendInputAsync()
    {
        var kind0 = KindOf(SelectedChatTarget);
        System.Diagnostics.Trace.WriteLine(
            $"[SendInput] enter: CanSendInput={CanSendInput} InputTextLen={InputText?.Length ?? -1} IsAgentRunning={IsAgentRunning} kind={kind0} convId={_currentConversationId}");
        if (!CanSendInput || string.IsNullOrWhiteSpace(InputText)) return;
        // 防重入：UI 已靠 IsAgentRunning 隐藏发送键/禁用输入框，这里兜底——
        // 上一轮未结束时（取消尚未落地）绝不再起一轮，避免 _runCts 被覆盖、两轮并发。
        if (IsAgentRunning) return;

        var content = InputText.Trim();
        InputText = string.Empty;
        // 当前会话 runtime：待新聊天态是 Guid.Empty 占位 runtime（首次发送落库拿到真 Guid 后提升为真 runtime）。
        var runtime = _current;
        runtime.RunCts = new CancellationTokenSource();

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
            // 待新聊天 → 首次发送才落库创建会话：把 Guid.Empty 占位 runtime 提升为真 runtime
            // （转移刚加的 turn + 承接 RunCts），注册进 _runtimes 并切为当前显示。
            if (_currentConversationId == Guid.Empty)
            {
                justCreated = true;
                var newId = Guid.NewGuid();
                var realRt = new ConversationRuntime(newId) { NextRunSeq = 1, RunCts = runtime.RunCts };
                runtime.RunCts = null;
                // 占位 runtime 是新聊天态（NewChat 已清空），只有刚加的这一个 turn 需转移。
                foreach (var t in runtime.Messages) realRt.Messages.Add(t);
                runtime.Messages.Clear();
                _runtimes[newId] = realRt;
                SetCurrentInternal(realRt);
                runtime = realRt;
                _currentConversationId = newId;
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
            runtime.TodosTouchedThisTurn = false; // 本轮 todos 信号重置（HandleTodoList 里置 true）
            var runItems = await _engine.RunSegmentedStreamAsync(runtime, turn, targetName,
                (handlers, approvalResponse, ct) => kind == ChatTargetKind.Workflow
                    ? _workflowRunner.RunStreamingAsync(_currentConversationId, targetId, content, handlers, approvalResponse, desiredMode, ct)
                    : _agentRunner.RunStreamingAsync(_currentConversationId, targetId, content, handlers, approvalResponse, desiredMode, ct),
                fullText, runtime.RunCts?.Token ?? default);

            // plan 模式轮、且本轮确实产出计划 → 待接受（驱动接受条）；execute 轮/无计划 → false。
            // 放在 RefreshModeAsync 之前：避免模式回翻干扰（门控已脱钩 SelectedMode，但语义上先定信号再读模式）。
            // 已开始执行则不再弹接受条：若本轮已有任意 todo 完成，说明 agent 在 plan 轮里已自行执行，
            // 此时再提示「计划已就绪，接受开始执行」与事实相悖（用户已看到执行结果）。
            runtime.PlanAwaitingAccept = desiredMode == "plan" && SelectedChatMode == ChatMode.Plan
                && runtime.TodosTouchedThisTurn && runtime.HasActivePlan
                && !runtime.ActiveTodos.Any(t => t.IsComplete);

            // 持久化本轮为 run+events（typed 事件流，CallId 写入时即配对）并乐观 bump 侧栏项。
            await PersistRunAsync(runtime, content, runItems, fullText.ToString());
            if (kind != ChatTargetKind.Rule) await RefreshModeAsync();

            // 首轮后异步用 LLM 生成会话标题（不阻塞；失败静默）
            if (justCreated)
                _titleApplier.ApplyAsync(_currentConversationId, content, fullText.ToString(), Conversations).SafeFireAndForget();

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

    /// <summary>
    /// 把本轮 transcript 持久化（委托 <see cref="ChatTranscriptPersister"/>——2026-09 ChatVM 拆分 B1 抽出，
    /// 方法体逐字搬入）并乐观 bump 侧栏项。bump 留在 VM（摸 Conversations 集合 + ConversationItem）；
    /// persister 返 null（空会话 id/会话缺失/持久化失败）时跳过 bump——保持原「失败即整体跳过」语义。
    /// </summary>
    private async Task PersistRunAsync(ConversationRuntime runtime,
        string userContent, IReadOnlyList<ChatMessageItem> runItems, string fullText)
    {
        var result = await _persister.PersistRunAsync(runtime, userContent, runItems);
        if (result is null) return;
        try
        {
            await _dispatcher.InvokeAsync(() =>
            {
                var item = Conversations.FirstOrDefault(c => c.Id == result.ConversationId);
                if (item is not null)
                {
                    var preview = string.IsNullOrWhiteSpace(fullText) ? userContent : fullText;
                    item.Preview = preview.Length > 40 ? preview[..40] + "…" : preview;
                    item.DisplayTime = FormatTime(result.FinishedAt);
                    // 移到顶部
                    Conversations.Remove(item);
                    Conversations.Insert(0, item);
                }
            });
        }
        catch { /* bump 失败不阻塞对话（原 catch 语义） */ }
    }

    /// <summary>是否还有更早的历史轮未渲染（供 View 判断是否触发增量加载）。代理当前会话 runtime。</summary>
    public bool HasMoreHistory => _current.PendingHistoryTurns is { Count: > 0 };

    /// <summary>
    /// 增量加载更早的历史轮：从暂存队列取出最多 <see cref="HistoryLoadBatch"/> 轮，插入到 <see cref="Messages"/> 顶部。
    /// 返回实际加载的轮数（0 表示无更多历史）。markdown 解析仅在加入 Messages 绑定时发生，故分批可避免一次性卡顿。
    /// 操作当前会话 runtime——历史增量加载只对当前显示的会话有意义。
    /// </summary>
    public int LoadMoreHistory()
    {
        var pending = _current.PendingHistoryTurns;
        if (pending is null || pending.Count == 0) return 0;
        int take = Math.Min(HistoryLoadBatch, pending.Count);
        // 暂存按时间正序（最旧在前）；取最新的待加载批次（队列尾部），倒序 Insert(0) 以保持正序。
        int start = pending.Count - take;
        for (int i = start + take - 1; i >= start; i--)
            Messages.Insert(0, pending[i]);
        pending.RemoveRange(start, take);
        return take;
    }
}

