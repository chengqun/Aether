using System.Text.Json;
using Acme.Desktop.Mvvm;

namespace Acme.Desktop.Mvvm.Tests;

/// <summary>
/// <see cref="AgentFileEditTracker"/> 文件编辑闭环回归——锁从 ChatWorkspaceViewModel 抽出的三段语义：
/// Call 阶段快照 old（撤销按钮同步注入）、Result 阶段读盘 new 算事后并排 diff、Undo 写回 old
/// （存在过的写回；工具新建的删除；多文件逆序恢复；IsUndone 幂等守卫）。
/// 非内容编辑工具全程 no-op——守卫在 tracker 内部，调用方无须预判工具类型。
/// 后台读盘走 Task.Run + Post：用 InlineDispatcher 同步直跑 + SpinUntil 轮询锁定异步路径。
/// </summary>
public class AgentFileEditTrackerTests
{
    /// <summary>同步直跑的 UI 调度器假体：Post/InvokeAsync 在调用线程立即执行（单测无真实 UI 线程可回）。</summary>
    private sealed class InlineDispatcher : IUiDispatcher
    {
        public Task InvokeAsync(Action action)
        {
            action();
            return Task.CompletedTask;
        }

        public void Post(Action action) => action();
    }

    private static AgentFileEditTracker NewTracker() => new(new InlineDispatcher());

    /// <summary>建临时目录跑测试体（工具编辑的目标文件都放里面），退出递归清理。</summary>
    private static void WithTempDir(Action<string> body)
    {
        var dir = Directory.CreateTempSubdirectory("aether-edit-test-").FullName;
        try { body(dir); }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    private static ChatMessageItem ToolCard() => new() { ItemType = "tool", ToolName = "EditFile" };

    // ── 撤销：写回 call 阶段快照的 old（存在过→写回；工具新建→删除） ──

    [Fact]
    public void Undo_single_edit_restores_old_content()
    {
        WithTempDir(dir =>
        {
            var path = Path.Combine(dir, "a.txt");
            File.WriteAllText(path, "改后内容");
            var card = ToolCard();
            card.FileEdit = new FileEditSnapshot(path, "改前内容", OldExisted: true);

            NewTracker().Undo(card);

            Assert.Equal("改前内容", File.ReadAllText(path));
            Assert.True(card.IsUndone);
            Assert.False(card.HasUndo); // 已撤销 → 撤销按钮隐藏
        });
    }

    [Fact]
    public void Undo_created_file_deletes_it()
    {
        WithTempDir(dir =>
        {
            var path = Path.Combine(dir, "new.txt");
            File.WriteAllText(path, "工具刚创建的内容");
            var card = ToolCard();
            card.FileEdit = new FileEditSnapshot(path, null, OldExisted: false);

            NewTracker().Undo(card);

            Assert.False(File.Exists(path)); // 新建文件 → 撤销 = 删除
            Assert.True(card.IsUndone);
        });
    }

    [Fact]
    public void Undo_multi_file_patch_restores_every_snapshot()
    {
        WithTempDir(dir =>
        {
            var edited = Path.Combine(dir, "a.txt");
            var created = Path.Combine(dir, "b.txt");
            File.WriteAllText(edited, "改后1");
            File.WriteAllText(created, "工具创建的2");
            var card = ToolCard();
            card.FileEdits = new List<FileEditSnapshot>
            {
                new(edited, "改前1", OldExisted: true),
                new(created, null, OldExisted: false),
            };

            NewTracker().Undo(card); // 多文件分支：逆序写回全部快照

            Assert.Equal("改前1", File.ReadAllText(edited)); // 存在过的写回 old
            Assert.False(File.Exists(created));              // patch 新建的删除
            Assert.True(card.IsUndone);
        });
    }

    [Fact]
    public void Undo_is_idempotent_after_first_undo()
    {
        WithTempDir(dir =>
        {
            var path = Path.Combine(dir, "a.txt");
            File.WriteAllText(path, "改后内容");
            var card = ToolCard();
            card.FileEdit = new FileEditSnapshot(path, "改前内容", OldExisted: true);
            var tracker = NewTracker();

            tracker.Undo(card);
            File.WriteAllText(path, "撤销后又被外部改了"); // 模拟后续编辑

            tracker.Undo(card); // 幂等守卫：IsUndone 已置 → 早返，不再写盘

            Assert.Equal("撤销后又被外部改了", File.ReadAllText(path));
        });
    }

    // ── no-op 守卫：非内容编辑工具 / 无快照的卡 ──

    [Theory]
    [InlineData("RunBacktest")]
    [InlineData("DeleteFile")]  // 不可逆删除走审批，不进内容编辑闭环
    [InlineData("MoveFile")]
    [InlineData("edit_symbol")] // 路径由 Roslyn 执行期定位，call 阶段无 filePath
    [InlineData("run_rule")]
    public void OnToolCall_non_content_edit_tool_is_noop(string toolName)
    {
        var card = ToolCard();

        NewTracker().OnToolCall(card, toolName, new Dictionary<string, object> { ["filePath"] = "/nonexistent/a.txt" });

        Assert.Null(card.UndoCommand);
        Assert.Null(card.FileEdit);
        Assert.Null(card.FileEdits);
    }

    [Fact]
    public void OnToolResult_without_snapshot_is_noop()
    {
        var card = ToolCard();

        NewTracker().OnToolResult(card); // 非 editing 卡：无 FileEdit/FileEdits → 不抛、不产出

        Assert.Null(card.DiffPreview);
        Assert.Null(card.DiffPreviews);
    }

    [Fact]
    public void Undo_without_snapshot_is_noop()
    {
        var card = ToolCard();

        NewTracker().Undo(card);

        Assert.False(card.IsUndone);
    }

    // ── Call 阶段：撤销按钮同步注入 + 后台读盘快照 old（Task.Run + Post 落卡） ──

    [Fact]
    public void OnToolCall_edit_file_injects_undo_and_snapshots_old()
    {
        WithTempDir(dir =>
        {
            var path = Path.Combine(dir, "a.txt");
            File.WriteAllText(path, "改前内容");
            var card = ToolCard();
            var input = new Dictionary<string, object> { ["filePath"] = path };

            NewTracker().OnToolCall(card, "EditFile", input);

            Assert.NotNull(card.UndoCommand); // 撤销按钮同步注入（不等后台读盘）
            Assert.True(SpinWait.SpinUntil(() => card.FileEdit is not null, 5000), "后台快照未落卡");
            Assert.Equal(Path.GetFullPath(path), card.FileEdit!.FilePath);
            Assert.Equal("改前内容", card.FileEdit.OldContent);
            Assert.True(card.FileEdit.OldExisted);
        });
    }

    [Fact]
    public void OnToolCall_apply_patch_snapshots_every_path()
    {
        WithTempDir(dir =>
        {
            var p1 = Path.Combine(dir, "a.txt");
            var p2 = Path.Combine(dir, "b.txt");
            File.WriteAllText(p1, "改前1");
            File.WriteAllText(p2, "改前2");
            var card = ToolCard();
            // MEAI 反序列化路径：patches 是 JsonElement 数组，每项含 filePath
            var patches = JsonSerializer.SerializeToElement(new object[]
            {
                new { filePath = p1 },
                new { filePath = p2 },
            });
            var input = new Dictionary<string, object> { ["patches"] = patches };

            NewTracker().OnToolCall(card, "ApplyPatch", input);

            Assert.NotNull(card.UndoCommand);
            Assert.True(SpinWait.SpinUntil(() => card.FileEdits is { Count: > 0 }, 5000), "多文件快照未落卡");
            Assert.Equal(2, card.FileEdits!.Count);
            Assert.Contains(card.FileEdits, s => s.FilePath == Path.GetFullPath(p1) && s.OldContent == "改前1");
            Assert.Contains(card.FileEdits, s => s.FilePath == Path.GetFullPath(p2) && s.OldContent == "改前2");
        });
    }

    // ── Result 阶段：快照 old + 磁盘 new → 事后并排 diff 挂卡 ──

    [Fact]
    public void OnToolResult_builds_diff_from_old_snapshot_and_new_disk_content()
    {
        WithTempDir(dir =>
        {
            var path = Path.Combine(dir, "a.txt");
            File.WriteAllText(path, "改前内容");
            var card = ToolCard();
            card.FileEdit = new FileEditSnapshot(path, "改前内容", OldExisted: true); // call 阶段快照
            File.WriteAllText(path, "改后内容"); // 模拟工具已执行（result 先于本调用落盘）

            NewTracker().OnToolResult(card);

            Assert.True(SpinWait.SpinUntil(() => card.DiffPreview is not null, 5000), "diff 未落卡");
            Assert.Equal("改前内容", card.DiffPreview!.OldText);
            Assert.Equal("改后内容", card.DiffPreview.NewText);
            Assert.True(card.HasFileDiff);
        });
    }
}
