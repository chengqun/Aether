using System.ComponentModel;
using Acme.DataQuery.Application;

namespace Acme.DataQuery.AI.Tools;

/// <summary>
/// SQL 查询工具：经 <see cref="ISqlQueryService"/> 对 MySQL 连接执行任意 SQL。
/// 注册为 Dangerous（任意 SQL 可含 DROP/DELETE 等不可逆操作）。
/// </summary>
public class SqlQueryTool
{
    private readonly ISqlQueryService _service;

    public SqlQueryTool(ISqlQueryService service) => _service = service;

    public class ExecuteSqlOutput
    {
        public bool Success { get; set; }
        public bool IsResultSet { get; set; }
        public List<string>? Columns { get; set; }
        public List<List<string?>>? Rows { get; set; }
        /// <summary>结果集总行数（封顶前）。</summary>
        public int RowCount { get; set; }
        /// <summary>非查询语句的受影响行数。</summary>
        public int AffectedRows { get; set; }
        /// <summary>结果集是否被行数封顶截断（默认上限 500 行）。</summary>
        public bool Truncated { get; set; }
        public string? Error { get; set; }
    }

    [Description("对已连接的 MySQL 数据库执行任意 SQL 语句（SELECT/DDL/DML 均可）。连接配置在「专家→连接器→MySQL」。结果集默认最多返回 500 行，超出会截断并置 truncated=true。")]
    public async Task<ExecuteSqlOutput> ExecuteSql(
        [Description("要执行的 SQL 语句（任意）")] string sql,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var r = await _service.ExecuteAsync(sql, cancellationToken);
            return new ExecuteSqlOutput
            {
                Success = r.Success,
                IsResultSet = r.IsResultSet,
                Columns = r.IsResultSet ? r.Columns.ToList() : null,
                Rows = r.IsResultSet ? r.Rows.Select(row => row.ToList()).ToList() : null,
                RowCount = r.RowCount,
                AffectedRows = r.AffectedRows,
                Truncated = r.Truncated,
                Error = r.Error,
            };
        }
        catch (Exception ex)
        {
            return new ExecuteSqlOutput { Success = false, Error = ex.Message };
        }
    }
}
