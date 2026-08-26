namespace Acme.DataQuery.Application;

/// <summary>
/// SQL 查询服务契约（驱动无关）。AI 工具适配器注入此契约，不依赖 Infrastructure/驱动。
/// 镜像 Notes 的分层模式：端口在 Domain，驱动实现在 Infrastructure。
/// </summary>
public interface ISqlQueryService
{
    /// <summary>执行任意 SQL。SELECT 返回结果集（封顶 <see cref="Domain.DataQueryDefaults.MaxRows"/> 行，超出置 Truncated）；DDL/DML 返回受影响行数。</summary>
    Task<SqlQueryResult> ExecuteAsync(string sql, CancellationToken cancellationToken = default);
}

/// <summary>
/// 给 AI 的查询结果（已整形：行数封顶 + 截断标记）。
/// </summary>
public sealed class SqlQueryResult
{
    public bool Success { get; init; }
    public bool IsResultSet { get; init; }
    public IReadOnlyList<string> Columns { get; init; } = Array.Empty<string>();
    public IReadOnlyList<IReadOnlyList<string?>> Rows { get; init; } = Array.Empty<IReadOnlyList<string?>>();
    /// <summary>结果集总行数（封顶前的真实数，供 AI 判断规模）。</summary>
    public int RowCount { get; init; }
    public int AffectedRows { get; init; }
    /// <summary>结果集是否被行数封顶截断。</summary>
    public bool Truncated { get; init; }
    public string? Error { get; init; }
}
