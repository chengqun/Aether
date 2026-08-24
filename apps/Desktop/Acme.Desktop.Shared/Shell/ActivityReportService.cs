using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Aether.AI.Core.Conversations.Runs;
using Aether.AI.Harness.Agents;
using Aether.AI.Memory;
using Acme.Desktop.Mvvm;
using Acme.Desktop.Mvvm.Git;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.Desktop.Shared.Shell;

/// <summary>活动报告生成服务 impl：拉当天/当周 Agent runs + Git commits + 知识对象变更（知识库目录 git 提交）→ 聚合成 markdown 素材 → LLM 汇总成日报/周报。
/// 照 <c>ConversationSummaryService</c> 范式：ctor 注 <see cref="IServiceProvider"/> 懒解析（IConversationStore/IGitService/IChatClientFactory），
/// 失败 silent 返回 null。ModelId 传 null（镜像 TitleService——用当前选中 LLM；日报非某 agent 产物，不绑 agent 模型）。
/// 时段边界用硬编码 <see cref="ChinaOffset"/>（+8）零点 → UTC（started_at/When 存 UTC ISO，与 ActivityViewModel 一致）。</summary>
public sealed class ActivityReportService : IActivityReportService
{
    private readonly IServiceProvider _services;

    /// <summary>中国标准时区（UTC+8）——与 <see cref="ActivityViewModel"/> 一致，硬编码不依赖系统时区（出差/海外开发机也准）。</summary>
    private static readonly TimeSpan ChinaOffset = TimeSpan.FromHours(8);

    public ActivityReportService(IServiceProvider services)
        => _services = services ?? throw new ArgumentNullException(nameof(services));

    public async Task<string?> GenerateReportAsync(ActivityReportKind kind, DateOnly anchorDate, string? modelId = null, CancellationToken cancellationToken = default)
    {
        // 1. 时段边界：周报取 anchor 所在 ISO 周一→下周一；日报取当天→次日。中国时区零点转 UTC（与 ActivityViewModel 一致）。
        var from = kind == ActivityReportKind.Week ? MondayOf(anchorDate) : anchorDate;
        var to = from.AddDays(kind == ActivityReportKind.Week ? 7 : 1);
        var fromDto = new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), ChinaOffset).ToUniversalTime();
        var toDto = new DateTimeOffset(to.ToDateTime(TimeOnly.MinValue), ChinaOffset).ToUniversalTime();

        // 2. 懒解析数据源（与 ActivityViewModel 同：IConversationStore 跨会话查 run + IGitService 读文档页项目目录）。
        var store = _services.GetService<IConversationStore>();
        var git = _services.GetService<IGitService>();
        var projectDir = _services.GetService<ICurrentProjectDirectoryProvider>()?.CurrentDirectory;

        // 3. 拉 runs（async I/O，不阻塞 UI 线程）。
        var runs = store is null
            ? new List<RunSummary>()
            : (await store.GetRunsInRangeAsync(string.Empty, fromDto, toDto, cancellationToken).ConfigureAwait(false)).ToList();

        // 4. 拉 commits + 聚合素材（同步 LibGit2Sharp 遍历 + CPU → 后台线程，避免大仓库 5000 commits 卡 UI）。
        var knowledgeDir = _services.GetService<IKnowledgeDirectoryProvider>()?.CurrentDirectory;
        var material = await Task.Run(() =>
        {
            var commits = new List<CommitInfo>();
            if (git is not null && !string.IsNullOrEmpty(projectDir) && git.IsRepository(projectDir))
            {
                var history = git.GetHistory(projectDir, 5000);
                commits = history.Select(n => n.Info).Where(c => c.When >= fromDto && c.When < toDto).ToList();
            }

            // 知识对象变更：知识库目录（.ok 文件所在，独立于项目目录）的 git 提交历史。同路径防双计。
            var knowledgeCommits = new List<CommitInfo>();
            if (git is not null && !string.IsNullOrEmpty(knowledgeDir)
                && !string.Equals(knowledgeDir, projectDir, StringComparison.OrdinalIgnoreCase)
                && git.IsRepository(knowledgeDir))
            {
                var khistory = git.GetHistory(knowledgeDir, 5000);
                knowledgeCommits = khistory.Select(n => n.Info).Where(c => c.When >= fromDto && c.When < toDto).ToList();
            }
            return runs.Count == 0 && commits.Count == 0 && knowledgeCommits.Count == 0
                ? null : BuildMaterial(kind, from, to, runs, commits, knowledgeCommits);
        }, cancellationToken).ConfigureAwait(false);
        if (material is null) return null; // 无活动数据

        // 5. 懒解析 LLM 客户端（照 ConversationSummaryService：factory 优先，fallback 容器 IChatClient，全 catch silent）。
        IChatClient? chatClient = null;
        try
        {
            var factory = _services.GetService<IChatClientFactory>();
            if (factory is not null) chatClient = factory.Create(modelId); // 日报传 null = 当前选中 LLM
            chatClient ??= _services.GetService<IChatClient>();
        }
        catch { return null; }
        if (chatClient is null) return null;

        // 6. LLM 汇总成报告（system+user 两条消息，照 ConversationSummaryService 的 GetResponseAsync 范式）。
        const int MaxInput = 12000; // 报告素材可能长（多 run/commit），上限防上下文溢出（SummaryService 用 8000）。
        var input = material.Length > MaxInput ? material[..MaxInput] + "\n\n…（后续内容省略）" : material;
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, ReportSystemPrompt),
            new(ChatRole.User, input),
        };
        try
        {
            var response = await chatClient.GetResponseAsync(messages, cancellationToken: cancellationToken).ConfigureAwait(false);
            var report = response.Text?.Trim();
            return string.IsNullOrWhiteSpace(report) ? null : report;
        }
        catch { return null; } // 报告失败不影响主流程——调用方退化提示"生成失败"。
    }

    /// <summary>把 runs + commits + 知识对象变更聚合成机器可读的 markdown 素材（LLM 据此重组为结构化报告）。</summary>
    private static string BuildMaterial(ActivityReportKind kind, DateOnly from, DateOnly to,
        List<RunSummary> runs, List<CommitInfo> commits, List<CommitInfo> knowledgeCommits)
    {
        var sb = new StringBuilder();
        sb.Append("# 活动数据（").Append(kind == ActivityReportKind.Week ? "周报" : "日报").Append('，')
          .Append(from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        if (kind == ActivityReportKind.Week)
            sb.Append(" ~ ").Append(to.AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        sb.Append("）\n\n");

        sb.Append("## Agent 执行（").Append(runs.Count).Append("）\n");
        foreach (var r in runs)
        {
            var t = r.StartedAt.ToOffset(ChinaOffset).ToString("HH:mm", CultureInfo.InvariantCulture);
            sb.Append("- [").Append(t).Append("] ").Append(r.AgentName)
              .Append(" (").Append(r.Status).Append("): ")
              .Append(string.IsNullOrEmpty(r.UserMessage) ? "（无消息）" : r.UserMessage.Replace('\n', ' ')).Append('\n');
        }

        sb.Append("\n## Git 提交（").Append(commits.Count).Append("）\n");
        foreach (var c in commits)
        {
            var t = c.When.ToOffset(ChinaOffset).ToString("MM-dd HH:mm", CultureInfo.InvariantCulture);
            sb.Append("- ").Append(c.ShaShort).Append(' ').Append(c.Message.Replace('\n', ' ')).Append(" (").Append(t).Append(")\n");
        }

        sb.Append("\n## 知识对象变更（").Append(knowledgeCommits.Count).Append("）\n");
        foreach (var c in knowledgeCommits)
        {
            var t = c.When.ToOffset(ChinaOffset).ToString("MM-dd HH:mm", CultureInfo.InvariantCulture);
            sb.Append("- ").Append(c.ShaShort).Append(' ').Append(c.Message.Replace('\n', ' ')).Append(" (").Append(t).Append(")\n");
        }
        return sb.ToString();
    }

    /// <summary>anchorDate 所在 ISO 周的周一（周一始；Sunday 回退 6 天）。</summary>
    private static DateOnly MondayOf(DateOnly anchor)
    {
        int dow = (int)anchor.DayOfWeek; // 0=Sunday..6=Saturday
        int diff = dow == 0 ? -6 : 1 - dow;
        return anchor.AddDays(diff);
    }

    private const string ReportSystemPrompt =
        """"
        你是日报/周报撰写助手。用户会给你一段时间内的 Agent 执行记录、Git 提交记录与知识对象变更记录（机器汇总的活动数据，其中「知识对象变更」是知识库目录的 git 提交，反映对 .ok 知识对象的增改）。
        请基于这些数据生成一份结构化中文报告：

        ## 今日完成（或本周完成）
        按主题归类列出关键任务，标注 Agent 名与大致时间。

        ## 进行中 / 待办
        从未完成（Failed/Interrupted 状态）或明显半截的执行推断。

        ## 问题与风险
        执行失败、报错、重复尝试同一任务等异常信号。

        ## 明日计划（或下周计划）
        基于进行中/待办给出合理下一步。

        要求：
        - 要点式，不寒暄，只输出报告本身。
        - 保留关键引用：Agent 名、提交 sha（前 7 位）、工具/命令名。
        - 数据为空的小节可省略。
        - 若几乎没有活动，如实说明"本期活动较少"。
        """";
}
