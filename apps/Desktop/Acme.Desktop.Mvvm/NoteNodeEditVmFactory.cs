using System.Linq;
using System.Threading.Tasks;
using Acme.Notes.Domain;
using Acme.Notes.Domain.Knowledge;

namespace Acme.Desktop.Mvvm;

/// <summary>
/// 创建表单编辑 VM 的上下文：工厂需要的三方依赖——选中节点、Notes VM（保存/解析/状态）、
/// 主 VM（ Experts/Rules/Workflows 同级 VM）+ 视图回调 <see cref="SyncCodeEditor"/>（把落库后内容推回下栏 TextEditor）。
/// </summary>
public readonly record struct EditVmContext(
    NoteNodeVM Node,
    NotesViewModel Notes,
    IShellHost Main,
    System.Action SyncCodeEditor);

/// <summary>
/// 按节点类型创建右栏表单编辑 VM 的工厂。每个 form 类型一个实现，封装该类型的同级 VM 查找、
/// edit VM 构造、Save/AutoSave/Cancel 事件接线（含 CodeRule 的 C# body 回填、Knowledge 的 body 重解析 + link resolver 闭包）。
/// 经 <c>DesktopApp.Services</c> 定位器在 NotesView 惰性建字典派发，替代旧的 130 行 if 链。
/// 新增 form 类型 = 加一个工厂实现 + 注册——零改既有分派代码。
/// Note/策略族（Strategy/CodeStrategy）无工厂 → 字典未命中 → 不建表单（全宽代码编辑器）。
/// </summary>
public interface INoteNodeEditVmFactory
{
    NoteNodeType NodeType { get; }
    Task<object?> CreateAsync(EditVmContext ctx);
}

/// <summary>Agent（专家）表单工厂。</summary>
public sealed class AgentEditVmFactory : INoteNodeEditVmFactory
{
    public NoteNodeType NodeType => NoteNodeType.Agent;
    public async Task<object?> CreateAsync(EditVmContext ctx)
    {
        var main = ctx.Main;
        var notes = ctx.Notes;
        var expert = main.Experts.Experts.FirstOrDefault(e => e.Id == ctx.Node.Id);
        if (expert is null) return null;
        var editVm = main.Experts.CreateEditViewModel(expert);
        if (editVm is null) return null;
        editVm.SaveRequested += async saved =>
        {
            var result = await main.Experts.SaveAsync(saved);
            if (result.Success) { notes.StatusMessage = "已保存"; await notes.RefreshAsync(); }
            else editVm.Error = result.Error;
        };
        // 自动保存（含 per-tool 权限调整）：静默落库（不重载树）+ 同步下栏源码（含 tool_approvals）。
        editVm.AutoSaveRequested += async saved =>
        {
            var result = await notes.SaveFormItemSilentlyAsync(() => main.Experts.SaveAsync(saved), saved.Id);
            if (result.Success) ctx.SyncCodeEditor();
        };
        editVm.CancelRequested += () => { notes.IsPreviewOnly = false; };
        return editVm;
    }
}

/// <summary>Rule（YAML 规则）表单工厂。</summary>
public sealed class RuleEditVmFactory : INoteNodeEditVmFactory
{
    public NoteNodeType NodeType => NoteNodeType.Rule;
    public async Task<object?> CreateAsync(EditVmContext ctx)
    {
        var main = ctx.Main;
        var notes = ctx.Notes;
        var rule = main.Rules.Rules.FirstOrDefault(r => r.Id == ctx.Node.Id);
        if (rule is null) return null;
        var editVm = main.Rules.CreateEditViewModel(rule);
        if (editVm is null) return null;
        editVm.SaveRequested += async saved =>
        {
            var result = await main.Rules.SaveAsync(saved);
            if (result.Success) { notes.StatusMessage = "已保存"; await notes.RefreshAsync(); }
            else editVm.Error = result.Error;
        };
        editVm.AutoSaveRequested += async saved =>
        {
            var result = await notes.SaveFormItemSilentlyAsync(() => main.Rules.SaveAsync(saved), saved.Id);
            if (result.Success) ctx.SyncCodeEditor();
        };
        editVm.CancelRequested += () => { notes.IsPreviewOnly = false; };
        return editVm;
    }
}

/// <summary>CodeRule（C# 代码规则）表单工厂。保存前从下栏 live content 回填 C# body，避免表单快照覆盖下栏改动。</summary>
public sealed class CodeRuleEditVmFactory : INoteNodeEditVmFactory
{
    public NoteNodeType NodeType => NoteNodeType.CodeRule;
    public async Task<object?> CreateAsync(EditVmContext ctx)
    {
        var main = ctx.Main;
        var notes = ctx.Notes;
        var rule = main.Rules.Rules.FirstOrDefault(r => r.Id == ctx.Node.Id);
        if (rule is null) return null;
        var editVm = main.Rules.CreateCodeRuleEditViewModel(rule);
        editVm.SaveRequested += async saved =>
        {
            // 下栏源码可能改了 C# body：从当前节点 content 重新取 body，避免表单保存用 VM 快照覆盖下栏改动
            if (notes.SelectedNode?.Content is string c && !string.IsNullOrEmpty(c))
                saved.SystemPrompt = notes.CodeBodyFromContent(c);
            var result = await main.Rules.SaveAsync(saved);
            if (result.Success) { notes.StatusMessage = "已保存"; await notes.RefreshAsync(); }
            else editVm.Error = result.Error;
        };
        // 同样从 live content 回填 body——否则调工具/改权限触发的自动保存会把 C# 源码重置成旧快照。
        editVm.AutoSaveRequested += async saved =>
        {
            if (notes.SelectedNode?.Content is string c && !string.IsNullOrEmpty(c))
                saved.SystemPrompt = notes.CodeBodyFromContent(c);
            var result = await notes.SaveFormItemSilentlyAsync(() => main.Rules.SaveAsync(saved), saved.Id);
            if (result.Success) ctx.SyncCodeEditor();
        };
        editVm.CancelRequested += () => { notes.IsPreviewOnly = false; };
        return editVm;
    }
}

/// <summary>Workflow（专家团）表单工厂。</summary>
public sealed class WorkflowEditVmFactory : INoteNodeEditVmFactory
{
    public NoteNodeType NodeType => NoteNodeType.Workflow;
    public async Task<object?> CreateAsync(EditVmContext ctx)
    {
        var main = ctx.Main;
        var notes = ctx.Notes;
        var wf = main.Workflows.Workflows.FirstOrDefault(w => w.Id == ctx.Node.Id);
        if (wf is null) return null;
        var editVm = await main.Workflows.CreateEditViewModelAsync(wf);
        if (editVm is null) return null;
        editVm.SaveRequested += async saved =>
        {
            var result = await main.Workflows.SaveAsync(saved);
            if (result.Success) { notes.StatusMessage = "已保存"; await notes.RefreshAsync(); }
            else editVm.Error = result.Error;
        };
        // 自动保存：静默落库（不重载树）+ 同步下栏源码。
        editVm.AutoSaveRequested += async saved =>
        {
            var result = await notes.SaveFormItemSilentlyAsync(() => main.Workflows.SaveAsync(saved), saved.Id);
            if (result.Success) ctx.SyncCodeEditor();
        };
        editVm.CancelRequested += () => { notes.IsPreviewOnly = false; };
        return editVm;
    }
}

/// <summary>Strategy（YAML 声明式策略）表单工厂。内联构造 StrategyEditViewModel；保存前从 live content 重解析
/// 高级规则的 RawWhenYaml 回填（防表单快照覆盖下栏源码对 exotic 条件的改动），同 Knowledge 的 body 回填。</summary>
public sealed class StrategyEditVmFactory : INoteNodeEditVmFactory
{
    public NoteNodeType NodeType => NoteNodeType.Strategy;
    public async Task<object?> CreateAsync(EditVmContext ctx)
    {
        var notes = ctx.Notes;
        var (model, parseErr) = notes.TryParseStrategy(ctx.Node.Content ?? "", ctx.Node.Title ?? "");
        if (model is null)
        {
            notes.StatusMessage = "解析失败：" + parseErr;
            return null;
        }
        var editVm = new StrategyEditViewModel(ctx.Node.Id, ctx.Node.Title ?? "", model);
        editVm.SaveRequested += async saved =>
        {
            if (notes.SelectedNode?.Id != editVm.Id) return;
            MergeAdvancedFromLive(saved, notes);
            var result = await notes.SaveFormItemSilentlyAsync(() => notes.SaveStrategyAsync(saved, editVm.Name), editVm.Id);
            if (result.Success) { notes.StatusMessage = "已保存"; ctx.SyncCodeEditor(); }
            else editVm.Error = result.Error;
        };
        editVm.CancelRequested += () => { notes.IsPreviewOnly = false; };
        return editVm;
    }

    /// <summary>下栏源码可能改了高级规则的 when：从当前节点 content 重新解析，把高级规则的 RawWhenYaml 回填，
    /// 避免表单保存用 VM 快照覆盖下栏对 exotic 条件的改动（同 CodeRule 的 C# body 回填）。</summary>
    private static void MergeAdvancedFromLive(StrategyFormModel saved, NotesViewModel notes)
    {
        if (notes.SelectedNode?.Content is not string c || string.IsNullOrEmpty(c)) return;
        if (!saved.Rules.Any(r => r.IsAdvanced)) return;
        var (live, _) = notes.TryParseStrategy(c, saved.Name);
        if (live is null) return;
        for (int i = 0; i < saved.Rules.Count && i < live.Rules.Count; i++)
        {
            if (saved.Rules[i].IsAdvanced && live.Rules[i].IsAdvanced)
                saved.Rules[i].RawWhenYaml = live.Rules[i].RawWhenYaml;
        }
    }
}

/// <summary>Knowledge（知识对象）表单工厂。内联构造 KnowledgeEditViewModel + link resolver 闭包；保存前重解析 body。</summary>
public sealed class KnowledgeEditVmFactory : INoteNodeEditVmFactory
{
    public NoteNodeType NodeType => NoteNodeType.Knowledge;
    public async Task<object?> CreateAsync(EditVmContext ctx)
    {
        var notes = ctx.Notes;
        var (def, parseErr) = notes.TryParseKnowledge(ctx.Node.Content ?? "");
        if (def is null)
        {
            notes.StatusMessage = "解析失败：" + parseErr;
            return null;
        }
        var editVm = new KnowledgeEditViewModel(ctx.Node.Id, ctx.Node.Title, def,
            notes.GetLinkTargets(ctx.Node.Id),
            notes.GetLinkTargetTree(ctx.Node.Id),
            title => notes.FindNoteByTitle(title) is { } tn ? (tn.Id, tn.Title) : ((Guid Id, string Title)?)null,
            id => notes.FindNoteById(id)?.Title,
            title => notes.FindNoteByTitle(title) is { } tn2
                ? (tn2.Title, notes.TryParseKnowledge(tn2.Content ?? "").Def)
                : ((string Title, KnowledgeObjectDefinition? Def)?)null,
            raw => notes.NavigateToNoteCommand.Execute(raw));
        editVm.SaveRequested += async saved =>
        {
            // debounce 自动保存可能跨越节点切换：仅当当前选中节点仍是本对象时才写库
            if (notes.SelectedNode?.Id != editVm.Id) return;
            // 下栏源码可能改了 body：从当前节点 content 重新解析 body，避免表单保存覆盖下栏改动
            if (notes.SelectedNode?.Content is string c && !string.IsNullOrEmpty(c))
            {
                var (redef, _) = notes.TryParseKnowledge(c);
                if (redef is not null) saved.Body = redef.Body;
            }
            // 静默落库（不重载树）+ 同步下栏源码（含 kind/links/body 改动）
            var result = await notes.SaveFormItemSilentlyAsync(() => notes.SaveKnowledgeAsync(saved, editVm.Name), editVm.Id);
            if (result.Success) { notes.StatusMessage = "已保存"; ctx.SyncCodeEditor(); }
            else editVm.Error = result.Error;
        };
        editVm.CancelRequested += () => { notes.IsPreviewOnly = false; };
        return editVm;
    }
}
