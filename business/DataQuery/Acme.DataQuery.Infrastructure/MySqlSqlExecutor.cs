using System.Text;
using Acme.DataQuery.Domain;
using MySqlConnector;

namespace Acme.DataQuery.Infrastructure;

/// <summary>
/// <see cref="ISqlExecutor"/> 的 MySQL 实现：从 <see cref="DataQueryOptions"/> 建连接串，经 MySqlConnector 执行。
/// 每次查询重新读 options → 改连接器配置即时生效（无需重启）。options 不全 → 返回清晰错误。
/// 用 <c>ExecuteReaderAsync</c> 统一执行：FieldCount&gt;0 = 结果集（读列+行），否则 = 非查询（取 RecordsAffected）。
/// </summary>
public sealed class MySqlSqlExecutor : ISqlExecutor
{
    private readonly DataQueryOptions _options;

    public MySqlSqlExecutor(DataQueryOptions options) => _options = options;

    public async Task<SqlRawResult> ExecuteAsync(string sql, CancellationToken cancellationToken = default)
    {
        if (!_options.IsConfigured)
            return new SqlRawResult { Error = "MySQL 未配置：请在「专家→连接器」填 host/user 等连接信息" };

        try
        {
            await using var conn = new MySqlConnection(BuildConnectionString());
            await conn.OpenAsync(cancellationToken);

            await using var cmd = new MySqlCommand(sql, conn) { CommandTimeout = DataQueryDefaults.CommandTimeoutSec };
            using var reader = await cmd.ExecuteReaderAsync(cancellationToken);

            // FieldCount>0 = 有结果集（SELECT/SHOW/DESC…）；=0 = 非查询（INSERT/UPDATE/DELETE/DDL）。
            if (reader.FieldCount > 0)
            {
                var columns = new List<string>(reader.FieldCount);
                for (int i = 0; i < reader.FieldCount; i++)
                    columns.Add(reader.GetName(i));

                var rows = new List<IReadOnlyList<string?>>();
                while (await reader.ReadAsync(cancellationToken))
                {
                    var row = new string?[reader.FieldCount];
                    for (int i = 0; i < reader.FieldCount; i++)
                        row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i)?.ToString();
                    rows.Add(row);
                }
                return new SqlRawResult { IsResultSet = true, Columns = columns, Rows = rows };
            }
            else
            {
                // 必须先关闭 reader 才能稳定拿到 RecordsAffected（MySqlConnector 在 reader 活跃时可能未填充）。
                var affected = reader.RecordsAffected;
                await reader.CloseAsync();
                return new SqlRawResult { IsResultSet = false, AffectedRows = affected };
            }
        }
        catch (Exception ex)
        {
            return new SqlRawResult { Error = ex.Message };
        }
    }

    private string BuildConnectionString()
    {
        var sb = new StringBuilder();
        sb.Append($"Server={_options.Host};Port={_options.Port};User ID={_options.User};");
        if (!string.IsNullOrEmpty(_options.Password))
            sb.Append($"Password={_options.Password};");
        if (!string.IsNullOrWhiteSpace(_options.Database))
            sb.Append($"Database={_options.Database};");
        sb.Append("AllowUserVariables=true;SslMode=Preferred;");
        return sb.ToString();
    }
}
