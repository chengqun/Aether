using System;
using System.Threading;
using System.Threading.Tasks;

namespace Acme.Desktop.Mvvm;

/// <summary>活动报告生成服务（日报/周报）。接口 UI-agnostic（Mvvm）；impl 在 Shared/Shell——照
/// <see cref="Aether.AI.Hosting.IConversationSummaryService"/> 范式拉当天/当周 Agent runs + Git commits → LLM 汇总成 markdown 报告。
/// 数据底座=<see cref="Aether.AI.Memory.IConversationStore.GetRunsInRangeAsync"/>（Agent 维度）+
/// <see cref="Git.IGitService.GetHistory"/>（Git 维度，读文档页项目目录）。失败/无数据返回 null（调用方退化提示）。</summary>
public interface IActivityReportService
{
    /// <summary>生成日报/周报。kind=Day/Week；anchorDate=锚点日期（周报取该日所在 ISO 周一→周日）。
    /// modelId=null 表示用当前选中 LLM（镜像 TitleService 范式，日报非某 agent 产物）。返回 markdown 报告文本；失败/无数据返回 null。</summary>
    Task<string?> GenerateReportAsync(ActivityReportKind kind, DateOnly anchorDate, string? modelId = null, CancellationToken cancellationToken = default);
}

/// <summary>报告种类：日报（当天）/周报（锚点日所在周周一→周日）。</summary>
public enum ActivityReportKind { Day, Week }
