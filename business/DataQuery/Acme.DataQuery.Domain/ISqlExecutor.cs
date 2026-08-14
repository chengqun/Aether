namespace Acme.DataQuery.Domain;

/// <summary>
/// SQL 执行端口（驱动无关）。实现放 Infrastructure（持有 MySQL 驱动），Application 服务经此端口调用。
/// 镜像 Notes 的 <c>INoteRepository</c> 分层：端口在 Domain，驱动隔离在 Infrastructure。
/// </summary>
public interface ISqlExecutor
{
    /// <summary>执行任意 SQL。查询语句返回结果集（IsResultSet=true）；DDL/DML 返回受影响行数。</summary>
    Task<SqlRawResult> ExecuteAsync(string sql, CancellationToken cancellationToken = default);
}

/// <summary>
/// 驱动返回的原始结果（未做行数封顶/整形）。
/// </summary>
public sealed record SqlRawResult
{
    /// <summary>是否为结果集（SELECT）——true 时 Columns/Rows 有值；false 时看 AffectedRows。</summary>
    public bool IsResultSet { get; init; }

    /// <summary>列名集合（IsResultSet=true 时）。</summary>
    public IReadOnlyList<string> Columns { get; init; } = Array.Empty<string>();

    /// <summary>行数据（每行 = 每列的字符串值，null 表示 DB NULL）。未封顶，调用方负责截断。</summary>
    public IReadOnlyList<IReadOnlyList<string?>> Rows { get; init; } = Array.Empty<IReadOnlyList<string?>>();

    /// <summary>非查询语句的受影响行数（IsResultSet=false 时）。</summary>
    public int AffectedRows { get; init; }

    /// <summary>执行错误（非 null = 失败，此时其余字段无意义）。</summary>
    public string? Error { get; init; }
}
