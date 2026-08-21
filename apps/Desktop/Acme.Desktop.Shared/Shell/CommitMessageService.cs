using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Acme.Desktop.Mvvm;
using Acme.Desktop.Mvvm.Git;
using Aether.AI.Harness.Agents;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.Desktop.Shared.Shell;

/// <summary>提交消息生成服务 impl：取当前项目目录 git 变更的 diff 摘要 → LLM 生成提交消息。
/// 照 <see cref="Aether.AI.Hosting.ConversationSummaryService"/> 范式：ctor 注 <see cref="IServiceProvider"/> 懒解析
/// （<see cref="IGitService"/> + <see cref="ICurrentProjectDirectoryProvider"/> + <see cref="IChatClientFactory"/>），
/// 失败 silent 返回 null。素材 = staged 变更（无 staged 用全部变更）的文件列表 + 每文件 Added/Removed 行摘要（截断防溢出）。
/// ModelId 传 null（用当前选中 LLM——提交消息非某 agent 产物，镜像 TitleService）。</summary>
public sealed class CommitMessageService : ICommitMessageService
{
    private readonly IServiceProvider _services;

    public CommitMessageService(IServiceProvider services)
        => _services = services ?? throw new ArgumentNullException(nameof(services));

    public async Task<string?> GenerateAsync(string? modelId = null, CancellationToken cancellationToken = default)
    {
        var git = _services.GetService<IGitService>();
        var projectDir = _services.GetService<ICurrentProjectDirectoryProvider>()?.CurrentDirectory;
        if (git is null || string.IsNullOrEmpty(projectDir) || !git.IsRepository(projectDir))
            return null;

        // 取变更 + 拼 diff 素材（同步 LibGit2Sharp → 后台线程，避免大仓库卡 UI）。
        var material = await Task.Run(() =>
        {
            var status = git.GetStatus(projectDir);
            if (status.Count == 0) return (string?)null;
            // staged 优先；无 staged 用全部变更（提交会自动 stage 全部，符合 SCM CommitCommand 语义）。
            var changes = status.Where(c => c.IsStaged).ToList();
            if (changes.Count == 0) changes = status.ToList();
            return BuildMaterial(git, projectDir, changes);
        }, cancellationToken).ConfigureAwait(false);
        if (material is null) return null; // 无变更

        // 懒解析 LLM 客户端（照 ConversationSummaryService：factory 优先，fallback 容器 IChatClient，全 catch silent）。
        IChatClient? chatClient = null;
        try
        {
            var factory = _services.GetService<IChatClientFactory>();
            if (factory is not null) chatClient = factory.Create(modelId); // null=当前选中 LLM
            chatClient ??= _services.GetService<IChatClient>();
        }
        catch { return null; }
        if (chatClient is null) return null;

        const int MaxInput = 6000; // diff 素材可能长（多文件），上限防上下文溢出（SummaryService 用 8000，diff 更冗长故更紧）。
        var input = material.Length > MaxInput ? material[..MaxInput] + "\n\n…（后续 diff 省略）" : material;
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, CommitMessageSystemPrompt),
            new(ChatRole.User, input),
        };
        try
        {
            var response = await chatClient.GetResponseAsync(messages, cancellationToken: cancellationToken).ConfigureAwait(false);
            var msg = response.Text?.Trim();
            if (string.IsNullOrWhiteSpace(msg)) return null;
            // 去掉模型可能裹的引号 / 书名号 / 代码围栏。
            msg = msg.Trim('"', '\'', '「', '」', '“', '”', '`');
            if (msg.StartsWith("```")) msg = StripCodeFence(msg);
            msg = msg.Trim();
            return string.IsNullOrWhiteSpace(msg) ? null : msg;
        }
        catch { return null; } // 生成失败不影响主流程——调用方退化提示"生成失败"。
    }

    /// <summary>拼 diff 素材：文件列表（状态字母 + 路径）+ 每文件 Added/Removed 行摘要（截断防溢出）。
    /// Removed 从 <see cref="DiffModel.OldLines"/> 取、Added 从 <see cref="DiffModel.NewLines"/> 取——
    /// diff 行按对齐填充 Blank，故 removed 行只在旧侧有 Text、added 行只在新侧有 Text，须各取各的。</summary>
    private static string BuildMaterial(IGitService git, string projectDir, IReadOnlyList<GitChange> changes)
    {
        var sb = new StringBuilder();
        sb.Append("## 变更文件（").Append(changes.Count).Append("）\n");
        foreach (var c in changes)
            sb.Append("- ").Append(Letter(c.Kind)).Append(' ').Append(c.Path).Append('\n');

        sb.Append("\n## diff 摘要（Added/Removed 行）\n");
        const int MaxDiffLinesPerFile = 40;
        const int MaxTotalDiffLines = 300;
        var totalLines = 0;
        foreach (var c in changes)
        {
            if (totalLines >= MaxTotalDiffLines) { sb.Append("…（更多文件 diff 省略）\n"); break; }
            var diff = git.GetDiff(projectDir, c.Path);
            sb.Append("--- ").Append(c.Path);
            if (diff is null) { sb.Append("（无 diff）\n"); continue; }
            if (diff.IsBinary) { sb.Append("（二进制）\n"); continue; }
            sb.Append('\n');

            var n = 0;
            // Removed 行（旧侧有 Text，新侧补 Blank）。
            if (diff.OldLines is not null)
                foreach (var line in diff.OldLines)
                {
                    if (line.Kind != DiffLineKind.Removed) continue;
                    if (n >= MaxDiffLinesPerFile) { sb.Append("…（本文件 diff 省略）\n"); break; }
                    sb.Append("- ").Append(line.Text).Append('\n');
                    n++; totalLines++;
                }
            // Added 行（新侧有 Text，旧侧补 Blank）。
            if (diff.NewLines is not null && n < MaxDiffLinesPerFile)
                foreach (var line in diff.NewLines)
                {
                    if (line.Kind != DiffLineKind.Added) continue;
                    if (n >= MaxDiffLinesPerFile) { sb.Append("…（本文件 diff 省略）\n"); break; }
                    sb.Append("+ ").Append(line.Text).Append('\n');
                    n++; totalLines++;
                }
        }
        return sb.ToString();
    }

    /// <summary>去首行 <c>```lang</c> 与末行 <c>```</c> 代码围栏（模型偶把消息裹进围栏）。</summary>
    private static string StripCodeFence(string msg)
    {
        var lines = msg.Split('\n');
        if (lines.Length < 2) return msg;
        var start = lines[0].StartsWith("```") ? 1 : 0;
        var end = lines[^1].Trim().StartsWith("```") ? lines.Length - 1 : lines.Length;
        return end > start ? string.Join('\n', lines[start..end]).Trim() : msg;
    }

    private static string Letter(ChangeKind kind) => kind switch
    {
        ChangeKind.Modified => "M",
        ChangeKind.Added => "A",
        ChangeKind.Deleted => "D",
        ChangeKind.Untracked => "U",
        ChangeKind.Renamed => "R",
        _ => "?",
    };

    private const string CommitMessageSystemPrompt =
        """"
        你是 git 提交消息撰写助手。用户会给你当前 git 变更的文件列表与 diff 摘要（Added/Removed 行）。
        请据此生成一条提交消息。要求：
        - 首行简明中文描述本次改动（动词开头，如「修复…」「新增…」「重构…」「更新…」），不超过 50 字
        - 不强制加前缀标签；改动明显属单一类型时可加 feat/fix/refactor 等小写前缀
        - 若改动跨多个方面，首行概括后空一行，再用要点列出关键改动（每条一行）
        - 只输出提交消息本身，不要任何解释、前缀说明或寒暄
        - 不要包含代码块围栏或引号
        """";
}
