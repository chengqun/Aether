using Acme.Notes.Domain;
using System.Text.Json;

namespace Acme.Desktop.Mvvm;

/// <summary>对话目标类型（原 ChatWorkspaceViewModel 私有嵌套枚举提升为顶层——B3 拆分；VM 留 KindOf 薄包装，调用点零改）。</summary>
public enum ChatTargetKind { None, Agent, Rule, Workflow }

/// <summary>恢复出的目标选中态（chatTargets 偏好 + recentExpanded 展开态）。</summary>
public sealed record ChatTargetRestoreState(ChatTargetSelected? Selected, bool RecentCollapsed);

/// <summary>chatTargets 偏好 JSON（camelCase；仅 Selected 字段——旧 Summoned 字段被忽略）。</summary>
public sealed class ChatTargetPrefs
{
    public ChatTargetSelected? Selected { get; set; }
}

/// <summary>单条选中目标（Kind: Agent/Rule/Workflow + Id 字符串）。</summary>
public sealed class ChatTargetSelected
{
    public string Kind { get; set; } = "Agent";
    public string Id { get; set; } = string.Empty;
}

/// <summary>
/// 聊天目标注册表：从 ChatWorkspaceViewModel 抽出（2026-09 ChatVM 拆分 B3），逻辑逐字搬入。
/// 拥有「目标池构建（experts+rules+workflows 全列）/ (Id,Kind) 逻辑键 / chatTargets 偏好读写（含
/// legacy selectedAgentId 迁移）/ 恢复态解析」。**不含策略**——选中变化触发的会话重置/模式支持/
/// 服务端上报留在 VM（<c>OnSelectedChatTargetChanged</c>/<c>SetSelectedTargetSilently</c>），
/// 池集合（ChatTargetItems）/选中（SelectedChatTarget）bindable 也留 VM。
/// <para><see cref="SelectedKey"/> 是「同一逻辑目标」的判据状态（原 VM 的 <c>_selectedTargetKey</c> 字段）——
/// SetSelectedTargetSilently 先预置它再赋值，使 OnSelectedChatTargetChanged 里 newKey==prevKey 恒成立、
/// 程序化赋值从机制上不可能触发会话重置（该互锁钉死在 VM 侧，本类只存取）。</para>
/// </summary>
public sealed class ChatTargetRegistry
{
    private readonly ExpertsViewModel _experts;
    private readonly WorkflowsViewModel _workflows;
    private readonly RulesViewModel _rules;
    private readonly IPreferencesStore _preferences;

    private static readonly JsonSerializerOptions PrefsJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public ChatTargetRegistry(ExpertsViewModel experts, WorkflowsViewModel workflows,
        RulesViewModel rules, IPreferencesStore preferences)
    {
        _experts = experts;
        _workflows = workflows;
        _rules = rules;
        _preferences = preferences;
    }

    /// <summary>当前选中目标的逻辑键（原 VM _selectedTargetKey）。SetSelectedTargetSilently 先预置再赋值。</summary>
    public (Guid Id, ChatTargetKind Kind) SelectedKey { get; set; } = (Guid.Empty, ChatTargetKind.None);

    /// <summary>对话目标类型判别（集中所有分支点的类型歧视）。</summary>
    public static ChatTargetKind KindOf(object? t) => t switch
    {
        WorkflowItem => ChatTargetKind.Workflow,
        ExpertItem e when e.NodeType == NoteNodeType.Rule => ChatTargetKind.Rule,
        ExpertItem => ChatTargetKind.Agent,
        _ => ChatTargetKind.None,
    };

    /// <summary>目标的逻辑标识（Id+Kind）。reload 后实例被替换，按引用匹配会丢选中，
    /// 故用 (Id,Kind) 作为「同一目标」的判据。</summary>
    public static (Guid Id, ChatTargetKind Kind) TargetKey(object? t, ChatTargetKind kind) => t switch
    {
        ExpertItem e => (e.Id, kind),
        WorkflowItem w => (w.Id, ChatTargetKind.Workflow),
        _ => (Guid.Empty, ChatTargetKind.None),
    };

    /// <summary>构建统一下拉池：全部 agent/规则/工作流（无召唤门控，创建即可在下拉选用）。</summary>
    public List<object> BuildPool()
    {
        var pool = new List<object>();
        foreach (var e in _experts.Experts) pool.Add(e);
        foreach (var r in _rules.Rules) pool.Add(r);
        foreach (var w in _workflows.Workflows) pool.Add(w);
        return pool;
    }

    /// <summary>从偏好恢复（chatTargets 优先；缺失时迁移旧键 selectedAgentId）+ 最近会话展开态。
    /// 返回 null = 偏好读取失败（吞异常）——VM 据此跳过整个恢复块（原语义）。</summary>
    public async Task<ChatTargetRestoreState?> RestoreAsync()
    {
        try
        {
            // 读结构化偏好 chatTargets（仅 Selected 字段；旧 Summoned 字段被忽略）；缺失时迁移旧键（仅 selectedAgentId）
            ChatTargetSelected? savedSel = null;
            var json = await _preferences.GetAsync("chatTargets");
            if (!string.IsNullOrWhiteSpace(json))
            {
                var prefs = JsonSerializer.Deserialize<ChatTargetPrefs>(json, PrefsJson);
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
            return new ChatTargetRestoreState(savedSel, expanded == "0");
        }
        catch
        {
            return null; // 偏好恢复失败不阻塞
        }
    }

    /// <summary>把保存的选中目标解析到当前池中的实例（Kind → 对应列表按 Id 匹配；找不到返 null）。</summary>
    public object? ResolveSaved(ChatTargetSelected? sel)
    {
        if (sel is { } s && Guid.TryParse(s.Id, out var sid))
        {
            return s.Kind switch
            {
                "Rule" => _rules.Rules.FirstOrDefault(r => r.Id == sid),
                "Agent" => _experts.Experts.FirstOrDefault(e => e.Id == sid),
                "Workflow" => (object?)_workflows.Workflows.FirstOrDefault(w => w.Id == sid),
                _ => null,
            };
        }
        return null;
    }

    /// <summary>当前选中目标的偏好形状（None/无 Id → null，即清空保存）。</summary>
    public ChatTargetSelected? CurrentSelectedPrefs(object? selected)
    {
        var kind = KindOf(selected);
        if (kind == ChatTargetKind.None) return null;
        var id = selected switch
        {
            ExpertItem ei => ei.Id.ToString(),
            WorkflowItem wi => wi.Id.ToString(),
            _ => null,
        };
        return id is null ? null : new ChatTargetSelected { Kind = kind.ToString(), Id = id };
    }

    /// <summary>持久化选中目标（下拉切换时调）：写 Selected 字段（无召唤态）。失败吞。</summary>
    public async Task PersistSelectionAsync(object? selected)
    {
        try
        {
            var prefs = new ChatTargetPrefs { Selected = CurrentSelectedPrefs(selected) };
            await _preferences.SetAsync("chatTargets", JsonSerializer.Serialize(prefs, PrefsJson));
        }
        catch { /* 偏好持久化失败不阻塞 */ }
    }
}
