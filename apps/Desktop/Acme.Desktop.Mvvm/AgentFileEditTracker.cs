using System.IO;
using Acme.Desktop.Mvvm.Git;
using CommunityToolkit.Mvvm.Input;

namespace Acme.Desktop.Mvvm;

/// <summary>
/// Agent 文件编辑闭环的聊天侧执行器：工具 Call 阶段后台读盘快照 old（供事后算 diff + 一键撤销写回）、
/// Result 阶段读盘 new 算事后并排 diff、Undo 写回快照 old（工具新建的文件 → 撤销 = 删除）。
/// <para>从 <c>ChatWorkspaceViewModel</c> 抽出——文件编辑闭环是自持关注点（仅依赖 <see cref="IUiDispatcher"/>
/// 与 Git/ 层静态 helper + <see cref="ChatMessageItem"/> 卡片局部状态），与分段流的时序编排无关。
/// 读盘一律 <c>Task.Run</c> 后台（大文件不阻塞 UI 线程），结果经 <c>Post</c> 回 UI 线程挂卡。</para>
/// <para>非文件编辑工具的 Call/Result 是 no-op（守卫在内部），调用方无须预判工具类型。</para>
/// </summary>
public sealed class AgentFileEditTracker
{
    private readonly IUiDispatcher _dispatcher;

    public AgentFileEditTracker(IUiDispatcher dispatcher)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    /// <summary>工具 Call 阶段：文件编辑/批量编辑工具注入撤销按钮 + 后台读盘快照 old。其余工具 no-op。
    /// MAF 流式 <c>FunctionCallContent</c>(call) 先于工具执行，故此时文件未改——快照即编辑前内容。</summary>
    public void OnToolCall(ChatMessageItem card, string toolName, Dictionary<string, object>? input)
    {
        // 文件编辑工具：call 阶段读盘快照 old（供事后算 diff + 一键撤销写回）。后台读盘避免大文件阻塞 UI。
        // 撤销按钮建卡即注入（点按 → Undo 写回 old）。
        if (AgentEditPreview.IsFileEditTool(toolName))
        {
            card.UndoCommand = new RelayCommand(() => Undo(card));
            _ = Task.Run(() =>
            {
                var fp = AgentEditPreview.TryGetFilePath(input);
                if (fp is null) return;
                bool existed = File.Exists(fp);
                string? old = existed ? TryReadAllText(fp) : null;
                _dispatcher.Post(() => card.FileEdit = new FileEditSnapshot(fp, old, existed));
            });
        }
        // 多文件批量编辑工具：call 阶段对每个 patch path 读盘快照 old。撤销逆序写回全部。
        else if (AgentEditPreview.IsApplyPatchTool(toolName))
        {
            card.UndoCommand = new RelayCommand(() => Undo(card));
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
                _dispatcher.Post(() => card.FileEdits = snaps);
            });
        }
    }

    /// <summary>工具 Result 阶段：后台读盘 new + 快照 old 算事后并排 diff 挂卡（单文件/多文件）。
    /// Result 后于工具执行，此时文件已改。无快照的卡 no-op（非编辑工具）。</summary>
    public void OnToolResult(ChatMessageItem card)
    {
        if (card.FileEdit is { } snap)
        {
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
                _dispatcher.Post(() => card.DiffPreview = diff);
            });
        }
        else if (card.FileEdits is { Count: > 0 } snaps)
        {
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
                _dispatcher.Post(() => card.DiffPreviews = diffs);
            });
        }
    }

    /// <summary>撤销一步文件编辑：写回 call 阶段快照的 old 内容（工具新建的文件 → 撤销 = 删除）。
    /// 写盘后 <c>DocumentsViewModel</c> 的 FileSystemWatcher 自动重载编辑器。朴素单步语义——写回该步 old；
    /// 多步同文件撤销中间步会破坏后续步基准（边缘情况，用户多半撤销最后一步或整体重来）。
    /// 多文件批量编辑（ApplyPatch）：逆序写回所有快照（后改的先恢复，尽量还原中间状态）。</summary>
    public void Undo(ChatMessageItem card)
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
}
