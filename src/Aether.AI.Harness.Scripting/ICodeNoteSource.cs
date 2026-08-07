namespace Aether.AI.Harness.Scripting;

/// <summary>
/// 代码规则笔记源端口：提供 CodeRule 笔记（由 host 实现，桥接 Notes 上下文）。
/// 让 Harness.Scripting 不直接依赖 Notes.Application，通过端口隔离——对称于 Trading 的
/// <c>IStrategyNoteSource</c>。host 负责按 <c>NoteNodeType.CodeRule</c> 过滤后返回。
/// </summary>
public interface ICodeNoteSource
{
    /// <summary>获取所有代码规则笔记（host 已按节点类型过滤）。</summary>
    Task<IReadOnlyList<CodeNote>> GetAllAsync(CancellationToken cancellationToken = default);
}

/// <summary>代码规则笔记（从 Notes 桥接的最小字段）。<see cref="Id"/> = 笔记 Guid 字符串，作注册表键。</summary>
public sealed record CodeNote(string Id, string Title, string Content);
