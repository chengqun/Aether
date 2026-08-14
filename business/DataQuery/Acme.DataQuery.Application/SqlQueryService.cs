using Acme.DataQuery.Domain;

namespace Acme.DataQuery.Application;

/// <summary>
/// <see cref="ISqlQueryService"/> 实现：经 <see cref="ISqlExecutor"/> 端口执行，对结果集做行数封顶（防爆量）。
/// 驱动无关——所有 DB 访问委托给 Infrastructure 的 executor。
/// </summary>
public sealed class SqlQueryService : ISqlQueryService
{
    private readonly ISqlExecutor _executor;

    public SqlQueryService(ISqlExecutor executor) => _executor = executor;

    public async Task<SqlQueryResult> ExecuteAsync(string sql, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sql))
            return new SqlQueryResult { Success = false, Error = "SQL 语句为空" };

        try
        {
            var raw = await _executor.ExecuteAsync(sql, cancellationToken);
            if (raw.Error is not null)
                return new SqlQueryResult { Success = false, Error = raw.Error };

            if (!raw.IsResultSet)
                return new SqlQueryResult { Success = true, IsResultSet = false, AffectedRows = raw.AffectedRows };

            // 结果集封顶：超出 MaxRows 的行丢弃，置 Truncated，但 RowCount 记真实总数。
            var total = raw.Rows.Count;
            var capped = total > DataQueryDefaults.MaxRows ? raw.Rows.Take(DataQueryDefaults.MaxRows).ToList() : raw.Rows;
            return new SqlQueryResult
            {
                Success = true,
                IsResultSet = true,
                Columns = raw.Columns,
                Rows = capped,
                RowCount = total,
                Truncated = total > DataQueryDefaults.MaxRows,
            };
        }
        catch (Exception ex)
        {
            return new SqlQueryResult { Success = false, Error = ex.Message };
        }
    }
}
