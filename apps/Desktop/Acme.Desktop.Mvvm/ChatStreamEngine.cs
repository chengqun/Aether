using System.Text;
using Aether.AI.Core.Agents;

namespace Acme.Desktop.Mvvm;

/// <summary>
/// 聊天流式渲染引擎：从 ChatWorkspaceViewModel 抽出（2026-09 ChatVM 拆分 B5），方法体逐字搬入。
/// 分段交错运行：agent 推理→调工具→再推理→… 的过程中，把每段推理(<c>TextReasoningContent</c>)、
/// 答案(<c>TextContent</c>)、工具步、审批卡、计划卡按真实时序交错插入聊天流。
/// 推理进 💭 思考卡、答案进 markdown 气泡（建段时即区分 IsReasoning，不再靠末段翻转）。
/// 审批请求冒出时本轮流结束 → 弹审批框 → 带 <see cref="ToolApprovalDecision"/> 再次 runAsync 恢复执行（协作循环）。
/// <para>
/// 委托缝（原 VM 纠缠点，行为契约）：<see cref="_selectedMode"/>/<see cref="_hasModeSupport"/> 是**逐工具实时读**
/// （运行中切档立即影响下一个工具——文档化行为）；<see cref="_isCurrentRuntime"/> 决定背景会话的计划变更
/// 是否上抛 <see cref="PlanSummaryChanged"/>（VM 转发 PlanSummary 计算属性通知）。
/// <see cref="AgentFileEditTracker"/> 一并搬入（全 VM 只有引擎用）。
/// </para>
/// </summary>
public sealed class ChatStreamEngine
{
    private readonly IUiDispatcher _dispatcher;
    private readonly IApprovalDialogService? _approvalDialog;
    private readonly IToolApprovalLevelLookup? _levelLookup; // AutoEdit 模式按工具等级区分自动批/弹框；null 退化为全弹（Manual 语义）
    private readonly IPetTokenStream? _petTokens;    // 桌宠专属逐字答案流（可空：未注册则桌宠无打字机）
    private readonly AgentFileEditTracker _fileEdits; // 文件编辑闭环（快照/diff/撤销）——自持小类
    private readonly Func<ChatMode> _selectedMode;
    private readonly Func<bool> _hasModeSupport;
    private readonly Func<ConversationRuntime, bool> _isCurrentRuntime;

    /// <summary>当前显示会话的计划计数变化（原 VM 在 HandleTodoList 里按 ReferenceEquals(_current, runtime) 上抛 PlanSummary）。</summary>
    public event Action? PlanSummaryChanged;

    public ChatStreamEngine(IUiDispatcher dispatcher, IApprovalDialogService? approvalDialog,
        IToolApprovalLevelLookup? levelLookup, IPetTokenStream? petTokens,
        Func<ChatMode> selectedMode, Func<bool> hasModeSupport, Func<ConversationRuntime, bool> isCurrentRuntime)
    {
        _dispatcher = dispatcher;
        _approvalDialog = approvalDialog;
        _levelLookup = levelLookup;
        _petTokens = petTokens;
        _selectedMode = selectedMode;
        _hasModeSupport = hasModeSupport;
        _isCurrentRuntime = isCurrentRuntime;
        _fileEdits = new AgentFileEditTracker(dispatcher);
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
    /// 分段交错运行：把每段推理/答案/工具步/审批卡/计划卡按真实时序交错插入聊天流，返回本轮产生的
    /// transcript 项（按时序），供持久化。
    /// </summary>
    public async Task<List<ChatMessageItem>> RunSegmentedStreamAsync(ConversationRuntime runtime, ChatTurnItem turn,
        string senderName,
        Func<StreamHandlers, ToolApprovalDecision?, CancellationToken, Task> runAsync,
        System.Text.StringBuilder fullText, CancellationToken cancellationToken = default,
        // 远程路径传 true：老板手机只收最终概括，过程（计划/工具/思考/审批）全不发——审批也自动放行所有工具（含 Dangerous）。
        // 本地路径传 false（默认）→ 走 4 档模式选择器 ChatApprovalDecider + 桌面本地审批框 _approvalDialog。
        bool autoApproveAll = false)
    {
        var items = turn.Children; // 本轮工作列表 = turn 的 Children（用户气泡为第一项，agent 产出随后）
        var cursor = new StreamCursor(NewTextSegment(senderName, isReasoningContent: false));
        // 本轮 todos 信号重置：只有本轮真的 todos_* 了才在末尾插计划快照卡，避免上一轮的计划状态
        // （HasActivePlan/ActiveTodos 跨轮保留供浮动面板显示）被误当作本轮产出 → 计划卡逐轮累积。
        // SendInputAsync 处也重置一次（留作语义注释），这里覆盖远程消息路径（不经过 SendInputAsync）。
        runtime.TodosTouchedThisTurn = false;
        await _dispatcher.InvokeAsync(() =>
        {
            items.Add(cursor.Current);
            turn.Status = TurnStatus.Working;
            runtime.IsRunning = true;
        });

        // —— Token 批量更新：不逐 token Post 到 UI 线程（会假死），攒批量刷新一次 ——
        // 用「有序 chunk 队列」而非两个独立 StringBuilder。原因：MAF 一个 update 可含混合 TextContent +
        // TextReasoningContent，且 reasoning→answer 边界常落在同一 flush 窗口。若分两个 StringBuilder 且
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
                // answerTokens 节流已由批量 flush 替代

                var handlers = new StreamHandlers(
                    OnToken: (token, _) =>
                    {
                        fullText.Append(token);
                        lock (pendingLock) pendingChunks.Add((token, false));
                        flushTimer.Change(150, Timeout.Infinite); // 批量刷新（节流 Content 增量→TextBlock 重排频率）
                        _petTokens?.AppendToken(token); // 桌宠专属打字机（逐字）；关键节点信号由 Harness 侧发射
                        return Task.CompletedTask;
                    },
                    OnReasoning: (text, _) =>
                    {
                        lock (pendingLock) pendingChunks.Add((text, true));
                        flushTimer.Change(150, Timeout.Infinite);
                        return Task.CompletedTask;
                    },
                    OnToolEvent: (ev, _) =>
                    {
                        // todos_* 调用由 OnTodoList 渲染为计划卡，不重复出工具卡。
                        if (ev.Name.StartsWith("todo", StringComparison.OrdinalIgnoreCase))
                            return Task.CompletedTask;
                        // 关键节点信号（工具起止）不再由 VM 发射——已下沉 Harness，轮询折叠在
                        // RunProgressFanout 内统一处理（含 PollingToolNames 名单）。
                        _dispatcher.Post(() => HandleToolEventSegmented(cursor, items, ev, senderName));
                        return Task.CompletedTask;
                    },
                    OnApprovalRequest: (req, _) =>
                    {
                        pendingApproval = req;
                        // 审批请求的关键节点信号由 Harness 统一发射（见 HarnessRuntime.RunStreamingCoreAsync）。
                        // 远程 run（autoApproveAll）一律自动批准所有工具（含 Dangerous）——老板只收最终概括，不交互；
                        // 本地 run 按当前模式算自动决定（Auto 全自动批；AutoEdit 自动批非 Dangerous；Manual/Plan 弹框）。
                        // 读模式经委托**逐工具即时读**——运行中切档立即影响下一个工具（解决中途切模式）。
                        autoDecision = autoApproveAll
                            ? new ToolApprovalDecision(req.RequestId, Approved: true, "远程自动批准", AlwaysApprove: false)
                            : ChatApprovalDecider.TryDecide(_selectedMode(), _hasModeSupport(), req.ToolName, _levelLookup, req.RequestId);
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
                        _dispatcher.Post(() => HandleTodoList(runtime, items, list, senderName));
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
                    OnUsage: info => _dispatcher.Post(() => runtime.TokenInfo = info));

                await runAsync(handlers, approvalResponse, cancellationToken).ConfigureAwait(false);

                if (pendingApproval is null) break;

                ToolApprovalDecision decision;
                if (autoDecision is not null)
                {
                    // 自动批（远程 autoApproveAll / 本地 Auto/AutoEdit-非Dangerous）：无对话框、无审批卡，直接走与用户批准相同的再调用路径恢复执行。
                    decision = autoDecision;
                }
                else
                {
                    // 非自动批：弹桌面本地框（本地路径）。远程 run 已由 autoApproveAll 保证恒批、不会走到这。
                    var userDecision = _approvalDialog is null ? null : await _approvalDialog.ShowAsync(pendingApproval).ConfigureAwait(false);
                    if (userDecision is null)
                    {
                        _dispatcher.Post(() => { if (approvalCard is not null) approvalCard.ApprovalState = 2; });
                        break; // 取消（本地框关闭）→ 终止本轮
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
                // 每轮一快照：本轮确有 todos 更新（runtime.TodosTouchedThisTurn）且仍有活动计划时，
                // 把计划卡插到本轮首（用户消息之后、agent 工作之前），内联在对话里、出现在"制定计划"的位置
                // （参考 Claude Code），而非堆在轮末与浮动面板叠底。借已有 kind=plan 持久化脚手架——
                // 历史会话恢复后仍能看到每轮的计划。须用 TodosTouchedThisTurn 门控：HasActivePlan/ActiveTodos
                // 跨轮保留供浮动面板显示，若不门控则每一轮都会用陈旧计划插一张快照 → 计划卡逐轮累积。
                if (runtime.TodosTouchedThisTurn && runtime.HasActivePlan && runtime.ActiveTodos.Count > 0)
                {
                    var planCard = new ChatMessageItem { Side = ChatMessageSide.Left, ItemType = "plan", Sender = senderName, IsStreaming = false };
                    foreach (var t in runtime.ActiveTodos)
                        planCard.Todos.Add(new TodoItemVM { Title = t.Title, Description = t.Description, IsComplete = t.IsComplete });
                    items.Insert(1, planCard); // items[0] = 用户消息
                }
                turn.Finish(errored ? TurnStatus.Error : TurnStatus.Done);
                runtime.IsRunning = false;
                runtime.RunCts?.Dispose();
                runtime.RunCts = null;
            });

            // 桌宠逐字流收尾（打字机 flush 后停止）。RUN 不考虑 remote——桌宠本地/远程都收逐字。
            _petTokens?.EndAnswer();
            // 终态关键节点（RunDone/RunError）不再由 VM 发射——Harness 的两个 RunStreamingAsync 重载
            // 已在正常返回后/异常收敛时统一 Post，Automation/工作流路径同样受益。
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

    /// <summary>该工具名是否属于轮询型名单（<see cref="PollingToolNames"/>）。工具卡渲染据此把连续重复调用
    /// 合并为一张卡；进度的轮询折叠由 Harness 的 <c>RunProgressFanout</c> 独立处理（同一名单语义）。</summary>
    private static bool IsPollingTool(string toolName)
    {
        return !string.IsNullOrEmpty(toolName) && PollingToolNames.Contains(toolName);
    }

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

            // 文件编辑闭环（快照注入 + 撤销按钮）：守卫在 tracker 内部，非编辑工具 no-op。
            _fileEdits.OnToolCall(tool, ev.Name, ev.Input);
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

            // 文件编辑闭环：读盘 new + 快照 old 算事后并排 diff 挂卡（后台读盘）。无快照的卡 no-op。
            _fileEdits.OnToolResult(tool);

            var idx = items.IndexOf(tool);
            if (idx < 0) idx = items.Count - 1;
            var next = NewTextSegment(senderName, isReasoningContent: false);
            items.Insert(idx + 1, next);
            cursor.Current = next;
        }
    }

    /// <summary>todos_* 调用后更新浮动计划面板（runtime.ActiveTodos）。快照（每轮一卡、入对话流持久化）在 RunSegmentedStreamAsync 末尾插入。
    /// 计划卡随 transcript 持久化、恢复，重启后仍在。操作指定 runtime——背景会话的计划不污染当前显示会话的面板。</summary>
    private void HandleTodoList(ConversationRuntime runtime, IList<ChatMessageItem> items, IReadOnlyList<TodoItemInfo> list, string senderName)
    {
        // 计划只显示在浮动面板（输入框上方），不在对话流里重复显示。
        // 去重：agent 跨轮重新规划时有时对同一事项重复 todos_add（旧项仍在 TodoProvider 列表里又被加一遍），
        // 致一份计划里出现两条内容相同的条目。按 Title（忽略大小写/首尾空白）去重，保留首次出现顺序；
        // 若任一副本已完成，则合并为已完成。
        runtime.ActiveTodos.Clear();
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
            runtime.ActiveTodos.Add(new TodoItemVM { Title = m.Title, Description = m.Description ?? string.Empty, IsComplete = m.IsComplete });
        runtime.HasActivePlan = runtime.ActiveTodos.Count > 0;
        // PlanSummary（"X/Y" 计数）是 VM 计算属性。仅当此 runtime 是当前显示会话时才上抛——背景会话的计划
        // 变更不应刷新当前面板计数（VM 的 runtime 代理层已隔离标量通知，但 PlanSummary 是 VM 计算属性，
        // 需显式转发）。完成 todo 时 HasActivePlan 不变（仍 true）→ 计数不刷新，这里在集合变更后显式上抛。
        if (_isCurrentRuntime(runtime))
            PlanSummaryChanged?.Invoke();
        runtime.TodosTouchedThisTurn = true; // 本轮确有 todos 更新（SendInputAsync 末尾据此置 PlanAwaitingAccept）
    }

    /// <summary>运行结束：末段空则移除；否则停止流式。仅 TextContent 末段翻为最终回答（markdown 气泡），
    /// TextReasoningContent 段保持 💭 思考卡。中间各段保持思考态。</summary>
    private static void FinalizeSegment(ChatMessageItem last, IList<ChatMessageItem> items)
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
}
