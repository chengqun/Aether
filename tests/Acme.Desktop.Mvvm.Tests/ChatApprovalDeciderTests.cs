using Aether.AI.Core.Agents;
using Acme.Desktop.Mvvm;

namespace Acme.Desktop.Mvvm.Tests;

/// <summary>
/// <see cref="ChatApprovalDecider"/> 模式矩阵回归——CLAUDE.md「Chat 4-mode selector」表格的机器化：
/// 每档对 non-Safe 工具的放行决定 + lookup 缺失安全降级 + 无选择器会话的旧行为保留。
/// 这里锁的是 VM 层审批拦截的唯一决策点（审批 cooperative loop 的宿主半边）。
/// </summary>
public class ChatApprovalDeciderTests
{
    private const string RequestId = "req-001";
    private const string ToolName = "place_order";

    private sealed class FixedLevelLookup(ToolApprovalLevel level) : IToolApprovalLevelLookup
    {
        public ToolApprovalLevel LevelOf(string toolName) => level;
    }

    private static ToolApprovalDecision? Decide(
        ChatMode mode,
        ToolApprovalLevel level,
        bool hasModeSupport = true,
        IToolApprovalLevelLookup? lookup = null) =>
        ChatApprovalDecider.TryDecide(mode, hasModeSupport, ToolName, lookup ?? new FixedLevelLookup(level), RequestId);

    // ── 无 4 档选择器的会话：任何模式全弹框（RequireToolApproval-only 旧语义） ──

    [Theory]
    [InlineData(ChatMode.Plan)]
    [InlineData(ChatMode.Manual)]
    [InlineData(ChatMode.AutoEdit)]
    [InlineData(ChatMode.Auto)]
    public void NoModeSupport_always_defers_to_dialog(ChatMode mode)
    {
        Assert.Null(Decide(mode, ToolApprovalLevel.Dangerous, hasModeSupport: false));
    }

    // ── Auto 档：全部自动批（含 Dangerous），不弹框 ──

    [Theory]
    [InlineData(ToolApprovalLevel.Safe)]
    [InlineData(ToolApprovalLevel.Confirm)]
    [InlineData(ToolApprovalLevel.Dangerous)]
    public void Auto_approves_everything_including_dangerous(ToolApprovalLevel level)
    {
        var d = Decide(ChatMode.Auto, level);
        Assert.NotNull(d);
        Assert.True(d!.Approved);
        Assert.Equal(RequestId, d.RequestId);
        Assert.False(d.AlwaysApprove); // 一次性批：模式可变，不设 session 级 standing rule
    }

    // ── AutoEdit 档：自动批非 Dangerous；Dangerous 弹框 ──

    [Theory]
    [InlineData(ToolApprovalLevel.Safe)]
    [InlineData(ToolApprovalLevel.Confirm)]
    public void AutoEdit_approves_non_dangerous(ToolApprovalLevel level)
    {
        var d = Decide(ChatMode.AutoEdit, level);
        Assert.NotNull(d);
        Assert.True(d!.Approved);
        Assert.False(d.AlwaysApprove);
    }

    [Fact]
    public void AutoEdit_defers_dangerous_to_dialog()
    {
        Assert.Null(Decide(ChatMode.AutoEdit, ToolApprovalLevel.Dangerous));
    }

    /// <summary>lookup 未注册（null）→ 退化为 Dangerous 全弹——即使工具实际声明 Confirm 也不自动批（安全降级）。</summary>
    [Fact]
    public void AutoEdit_missing_lookup_degrades_to_defer()
    {
        // 直接调 TryDecide 传 null lookup（经 Decide 助手会被 ?? 替换回 FixedLevelLookup）
        Assert.Null(ChatApprovalDecider.TryDecide(
            ChatMode.AutoEdit, hasModeSupport: true, ToolName, levelLookup: null, RequestId));
    }

    // ── Manual / Plan 档：始终弹框 ──

    [Theory]
    [InlineData(ChatMode.Manual)]
    [InlineData(ChatMode.Plan)]
    public void Manual_and_Plan_always_defer(ChatMode mode)
    {
        Assert.Null(Decide(mode, ToolApprovalLevel.Confirm));
    }

    [Theory]
    [InlineData(ToolApprovalLevel.Safe)]
    [InlineData(ToolApprovalLevel.Confirm)]
    [InlineData(ToolApprovalLevel.Dangerous)]
    public void Manual_defers_all_levels(ToolApprovalLevel level)
    {
        Assert.Null(Decide(ChatMode.Manual, level));
    }

    /// <summary>Reason 文案区分档位（日志/审批回执可读性）。</summary>
    [Fact]
    public void Reason_distinguishes_mode()
    {
        Assert.Contains("自动模式", Decide(ChatMode.Auto, ToolApprovalLevel.Confirm)!.Reason);
        Assert.Contains("自动编辑模式", Decide(ChatMode.AutoEdit, ToolApprovalLevel.Confirm)!.Reason);
    }
}
