namespace Acme.Trading.Runtime.Domain;

/// <summary>策略笔记源端口：提供策略候选笔记（由 host 实现，桥接 Notes 上下文）。
/// 让 Trading.Scripting 不直接依赖 Notes.Application，通过 Domain 端口隔离。</summary>
public interface IStrategyNoteSource
{
    /// <summary>获取所有策略笔记（host 已按 CodeStrategy 节点类型预过滤，Content 为 IStrategy 实现类 C# 源码）。</summary>
    Task<IReadOnlyList<StrategyNote>> GetAllAsync(CancellationToken cancellationToken = default);
}

/// <summary>策略候选笔记（从 Notes 桥接的最小字段）。</summary>
public sealed record StrategyNote(string Id, string Title, string Content);
