using System.Text;
using Acme.Desktop.Mvvm;
using Aether.AI.Core.Agents;
using Xunit;

namespace Acme.Desktop.Mvvm.Tests;

/// <summary>
/// ChatStreamEngine（ChatVM 拆分 B5）单测：用假 runAsync 驱动 StreamHandlers（Core DTO，无需 MEAI）锁定
/// 引擎语义——分段交错（reasoning/answer 分卡）、工具 call+result 配对单卡、审批协作循环三路
/// （autoApproveAll / 模式自动批 / 弹框批准 / 弹框取消）、计划快照插入 + PlanSummaryChanged 门控、
/// 错误路径、IsRunning/RunCts 生命周期。
/// </summary>
public class ChatStreamEngineTests
{
    private sealed class InlineDispatcher : IUiDispatcher
    {
        public Task InvokeAsync(Action action) { action(); return Task.CompletedTask; }
        public void Post(Action action) => action();
    }

    private sealed class ScriptedDialog : IApprovalDialogService
    {
        public ToolApprovalDecision? Response { get; set; }
        public int Calls { get; private set; }
        public Task<ToolApprovalDecision?> ShowAsync(ToolApprovalRequest request)
        {
            Calls++;
            return Task.FromResult(Response);
        }
    }

    private sealed class EngineHarness
    {
        public ChatMode Mode = ChatMode.AutoEdit;
        public bool HasModeSupport;
        public bool IsCurrent = true;
        public int PlanSummaryRaises;
        public ChatStreamEngine Engine = null!;

        public EngineHarness(IApprovalDialogService? dialog = null)
        {
            Engine = new ChatStreamEngine(new InlineDispatcher(), dialog, null, null,
                () => Mode, () => HasModeSupport, rt => IsCurrent);
            Engine.PlanSummaryChanged += () => PlanSummaryRaises++;
        }
    }

    private static ChatTurnItem UserTurn(string content = "user msg")
    {
        var turn = new ChatTurnItem { Header = content };
        turn.Children.Add(new ChatMessageItem
        {
            Side = ChatMessageSide.Right, ItemType = "text", Sender = "我", Content = content
        });
        return turn;
    }

    /// <summary>跑一次引擎：runAsync 收到 handlers 后按脚本回调，记录每次调用的 approvalResponse。</summary>
    private static async Task<(List<ChatMessageItem> Items, List<ToolApprovalDecision?> Responses)> RunAsync(
        ChatStreamEngine engine, ConversationRuntime runtime, ChatTurnItem turn,
        Func<StreamHandlers, Task> script)
    {
        var fullText = new StringBuilder();
        var responses = new List<ToolApprovalDecision?>();
        var items = await engine.RunSegmentedStreamAsync(runtime, turn, "agent",
            (handlers, approvalResponse, ct) =>
            {
                responses.Add(approvalResponse);
                return script(handlers);
            },
            fullText);
        return (items, responses);
    }

    private static ToolStreamEvent Call(string callId, string name) =>
        new(ToolStreamEventKind.Call, name, callId, new Dictionary<string, object>(), null);

    private static ToolStreamEvent Result(string callId, string name) =>
        new(ToolStreamEventKind.Result, name, callId, null, new Dictionary<string, object> { ["ok"] = true });

    // —— 分段交错 ——

    [Fact]
    public async Task Reasoning_And_Answer_Split_Into_Separate_Segments()
    {
        var h = new EngineHarness();
        var runtime = new ConversationRuntime(Guid.NewGuid());
        var turn = UserTurn();

        var (items, _) = await RunAsync(h.Engine, runtime, turn, async handlers =>
        {
            await handlers.OnReasoning!("thinking hard", default);
            await handlers.OnToken!("the answer", default);
        });

        // 用户气泡 + reasoning 卡 + answer 卡；初始空段被 EnsureTextSegment 移除
        Assert.Equal(3, items.Count);
        var reasoning = items[1];
        var answer = items[2];
        Assert.True(reasoning.IsText && reasoning.IsReasoning && reasoning.Content == "thinking hard");
        Assert.True(answer.IsText && !answer.IsReasoning && answer.Content == "the answer");
        Assert.False(answer.IsStreaming);                     // 末段 TextContent 冻结为最终回答
        Assert.Equal("the answer", answer.MarkdownContent);   // markdown 安全网赋值
    }

    [Fact]
    public async Task Tool_Call_And_Result_Merge_Into_One_Card()
    {
        var h = new EngineHarness();
        var runtime = new ConversationRuntime(Guid.NewGuid());
        var turn = UserTurn();

        var (items, _) = await RunAsync(h.Engine, runtime, turn, async handlers =>
        {
            await handlers.OnToolEvent!(Call("c1", "grep"), default);
            await handlers.OnToolEvent!(Result("c1", "grep"), default);
        });

        // 用户气泡 + 单张工具卡（call+result 按 CallId 配对）；result 后新开的空答案段被 FinalizeSegment 移除
        Assert.Equal(2, items.Count);
        var tool = items[1];
        Assert.True(tool.IsToolStep);
        Assert.Equal("grep", tool.ToolName);
        Assert.True(tool.IsToolCompleted);
        Assert.NotNull(tool.Output);
    }

    // —— 审批协作循环 ——

    [Fact]
    public async Task AutoApproveAll_Bypasses_Dialog_And_Approves_Everything()
    {
        var dialog = new ScriptedDialog(); // 不应被调用
        var h = new EngineHarness(dialog) { Mode = ChatMode.Manual, HasModeSupport = true };
        var runtime = new ConversationRuntime(Guid.NewGuid());
        var turn = UserTurn();
        var fullText = new StringBuilder();
        var responses = new List<ToolApprovalDecision?>();

        var items = await h.Engine.RunSegmentedStreamAsync(runtime, turn, "agent",
            (handlers, approvalResponse, ct) =>
            {
                responses.Add(approvalResponse);
                if (approvalResponse is null)
                {
                    return handlers.OnApprovalRequest!(new ToolApprovalRequest("r1", "PlaceOrder", "c1", null), default);
                }
                return handlers.OnToken!("approved and done", default);
            },
            fullText, autoApproveAll: true);

        Assert.Equal(0, dialog.Calls);            // 远程 run 一律自动批——不弹框
        Assert.Equal(2, responses.Count);          // 协作循环：第一轮触发审批，第二轮带决定恢复
        Assert.NotNull(responses[1]);
        Assert.True(responses[1]!.Approved);
        Assert.Empty(items.Where(i => i.IsApprovalCard)); // 自动批不插审批卡
    }

    [Fact]
    public async Task Manual_Mode_Defers_To_Dialog_And_Resumes_With_Decision()
    {
        var dialog = new ScriptedDialog
        {
            Response = new ToolApprovalDecision("r1", Approved: true, Reason: "ok", AlwaysApprove: false),
        };
        var h = new EngineHarness(dialog) { Mode = ChatMode.Manual, HasModeSupport = true };
        var runtime = new ConversationRuntime(Guid.NewGuid());
        var turn = UserTurn();
        var responses = new List<ToolApprovalDecision?>();
        var fullText = new StringBuilder();

        var items = await h.Engine.RunSegmentedStreamAsync(runtime, turn, "agent",
            (handlers, approvalResponse, ct) =>
            {
                responses.Add(approvalResponse);
                if (approvalResponse is null)
                    return handlers.OnApprovalRequest!(new ToolApprovalRequest("r1", "PlaceOrder", "c1", null), default);
                return handlers.OnToken!("resumed", default);
            },
            fullText);

        Assert.Equal(1, dialog.Calls);                    // 弹过一次框
        Assert.Equal(2, responses.Count);                 // 协作循环：第一轮触发审批，第二轮带决定恢复
        Assert.True(responses[1]!.Approved);              // 恢复轮带用户批准
        var card = Assert.Single(items.Where(i => i.IsApprovalCard));
        Assert.Equal(1, card.ApprovalState);              // 批准态 ✅
    }

    [Fact]
    public async Task Dialog_Cancel_Terminates_Turn_And_Marks_Card_Rejected()
    {
        var dialog = new ScriptedDialog { Response = null }; // 取消
        var h = new EngineHarness(dialog) { Mode = ChatMode.Manual, HasModeSupport = true };
        var runtime = new ConversationRuntime(Guid.NewGuid());
        var turn = UserTurn();

        var (items, responses) = await RunAsync(h.Engine, runtime, turn, handlers =>
        {
            return handlers.OnApprovalRequest!(new ToolApprovalRequest("r1", "PlaceOrder", "c1", null), default);
        });

        Assert.Single(responses);                          // 取消 → 终止本轮，无第二轮
        var card = Assert.Single(items.Where(i => i.IsApprovalCard));
        Assert.Equal(2, card.ApprovalState);              // ❌
    }

    [Fact]
    public async Task Auto_Mode_Auto_Approves_Without_Dialog()
    {
        var dialog = new ScriptedDialog();
        var h = new EngineHarness(dialog) { Mode = ChatMode.Auto, HasModeSupport = true };
        var runtime = new ConversationRuntime(Guid.NewGuid());
        var turn = UserTurn();
        var responses = new List<ToolApprovalDecision?>();
        var fullText = new StringBuilder();

        // 仅首轮请求审批（真实 MAF 恢复轮不重发审批请求）
        await h.Engine.RunSegmentedStreamAsync(runtime, turn, "agent",
            (handlers, approvalResponse, ct) =>
            {
                responses.Add(approvalResponse);
                return approvalResponse is null
                    ? handlers.OnApprovalRequest!(new ToolApprovalRequest("r1", "DeleteFile", "c1", null), ct)
                    : Task.CompletedTask;
            },
            fullText);

        Assert.Equal(0, dialog.Calls);       // Auto 全自动批（含 Dangerous）
        Assert.Equal(2, responses.Count);    // 恢复轮
        Assert.True(responses[1]!.Approved);
    }

    [Fact]
    public async Task Without_Mode_Support_Always_Defers_To_Dialog()
    {
        // hasModeSupport=false（RequireToolApproval-only agent，无 4 档选择器）→ 退化为全弹（legacy 行为）
        var dialog = new ScriptedDialog
        {
            Response = new ToolApprovalDecision("r1", Approved: false, Reason: "no", AlwaysApprove: false),
        };
        var h = new EngineHarness(dialog) { Mode = ChatMode.Auto, HasModeSupport = false };
        var runtime = new ConversationRuntime(Guid.NewGuid());
        var turn = UserTurn();

        var responses = new List<ToolApprovalDecision?>();
        var fullText = new StringBuilder();
        var items = await h.Engine.RunSegmentedStreamAsync(runtime, turn, "agent",
            (handlers, approvalResponse, ct) =>
            {
                responses.Add(approvalResponse);
                // 仅首轮请求审批（真实 MAF 恢复轮不重发审批请求）
                return approvalResponse is null
                    ? handlers.OnApprovalRequest!(new ToolApprovalRequest("r1", "DeleteFile", "c1", null), ct)
                    : Task.CompletedTask;
            },
            fullText);

        Assert.Equal(1, dialog.Calls);         // 即便 Auto 档也弹框（无选择器语义）
        Assert.Equal(2, responses.Count);
        Assert.True(responses[1] is { Approved: false }); // 用户拒绝 → 恢复轮带拒绝
    }

    // —— 计划 ——

    [Fact]
    public async Task TodoList_Updates_Panel_And_Inserts_Plan_Snapshot_At_Turn_Top()
    {
        var h = new EngineHarness();
        var runtime = new ConversationRuntime(Guid.NewGuid());
        var turn = UserTurn();

        var (items, _) = await RunAsync(h.Engine, runtime, turn, async handlers =>
        {
            await handlers.OnTodoList!(new List<TodoItemInfo>
            {
                new("1", "step-1", "d1", false),
                new("2", "step-2", null, true),
            }, default);
            await handlers.OnToken!("done", default);
        });

        // 面板：去重合并后的 todos + HasActivePlan
        Assert.True(runtime.HasActivePlan);
        Assert.Equal(2, runtime.ActiveTodos.Count);
        Assert.True(runtime.ActiveTodos[1].IsComplete);
        Assert.True(runtime.TodosTouchedThisTurn);

        // 快照卡插到本轮首（用户气泡之后）
        var planCard = items.FirstOrDefault(i => i.IsPlanCard);
        Assert.NotNull(planCard);
        Assert.Equal(1, items.IndexOf(planCard!));         // items[0] = 用户消息
        Assert.Equal(2, planCard!.Todos.Count);

        // 当前会话 → PlanSummary 上抛（原 ReferenceEquals(_current, runtime) 门控）
        Assert.Equal(1, h.PlanSummaryRaises);
    }

    [Fact]
    public async Task Background_Runtime_Plan_Change_Does_Not_Raise_PlanSummary()
    {
        var h = new EngineHarness { IsCurrent = false };   // 模拟背景会话
        var runtime = new ConversationRuntime(Guid.NewGuid());
        var turn = UserTurn();

        var (items, _) = await RunAsync(h.Engine, runtime, turn, handlers =>
        {
            return handlers.OnTodoList!(new List<TodoItemInfo> { new("1", "s", null, false) }, default);
        });

        Assert.True(runtime.HasActivePlan);       // 面板/快照照常（写自己 runtime）
        Assert.NotNull(items.FirstOrDefault(i => i.IsPlanCard));
        Assert.Equal(0, h.PlanSummaryRaises);     // 但不上抛当前面板计数
    }

    // —— 错误 / 生命周期 ——

    [Fact]
    public async Task RunError_Appends_Error_Text_And_Marks_Turn_Error()
    {
        var h = new EngineHarness();
        var runtime = new ConversationRuntime(Guid.NewGuid());
        var turn = UserTurn();
        var fullText = new StringBuilder();

        var items = await h.Engine.RunSegmentedStreamAsync(runtime, turn, "agent",
            (_, _, _) => throw new InvalidOperationException("boom"),
            fullText);

        Assert.Contains("[运行失败：boom]", fullText.ToString());
        Assert.Contains(items, i => i.IsText && i.Content.Contains("[运行失败：boom]"));
        Assert.Equal(TurnStatus.Error, turn.Status);
        Assert.False(runtime.IsRunning);
        Assert.Null(runtime.RunCts);
    }

    [Fact]
    public async Task IsRunning_True_During_Run_Cleared_After()
    {
        var h = new EngineHarness();
        var runtime = new ConversationRuntime(Guid.NewGuid()) { RunCts = new CancellationTokenSource() };
        var turn = UserTurn();
        var observedDuringRun = false;

        await RunAsync(h.Engine, runtime, turn, _ =>
        {
            observedDuringRun = runtime.IsRunning;
            return Task.CompletedTask;
        });

        Assert.True(observedDuringRun);
        Assert.False(runtime.IsRunning);
        Assert.Null(runtime.RunCts);   // finally 里 Dispose + 置 null
    }

    [Fact]
    public async Task Usage_Updates_Runtime_TokenInfo()
    {
        var h = new EngineHarness();
        var runtime = new ConversationRuntime(Guid.NewGuid());
        var turn = UserTurn();

        await RunAsync(h.Engine, runtime, turn, handlers =>
        {
            handlers.OnUsage?.Invoke("tokens: 123");
            return Task.CompletedTask;
        });

        Assert.Equal("tokens: 123", runtime.TokenInfo);
    }

    [Fact]
    public async Task FullText_Accumulates_Answer_Tokens()
    {
        var h = new EngineHarness();
        var runtime = new ConversationRuntime(Guid.NewGuid());
        var turn = UserTurn();
        var fullText = new StringBuilder();

        await h.Engine.RunSegmentedStreamAsync(runtime, turn, "agent",
            (handlers, _, _) =>
            {
                handlers.OnToken!("Hello ", default);
                handlers.OnToken!("world", default);
                return Task.CompletedTask;
            },
            fullText);

        Assert.Equal("Hello world", fullText.ToString());
    }
}
