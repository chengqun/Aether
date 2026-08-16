using System.Reflection;
using Acme.DataQuery.AI.Tools;
using Aether.AI.Hosting;
using Aether.AI.Core.Agents;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.DataQuery.AI;

public static class DataQueryAIBuilderExtensions
{
    public static readonly string[] ToolNames = { "ExecuteSql" };

    /// <summary>注册数据查询工具（仅工具）。业务层（DataQuery.Infrastructure/Application）由宿主接线。
    /// ExecuteSql 标 Dangerous——任意 SQL 可含 DROP/DELETE/UPDATE（不可逆），专家作者可在编辑器 per-tool 覆盖。</summary>
    public static AetherAIBuilder AddDataQueryTools(this AetherAIBuilder builder)
    {
        var s = builder.Services;
        s.AddTransient<SqlQueryTool>();

        const string cat = "数据查询";
        builder.ConfigureToolRegistry(reg =>
        {
            reg.Register("ExecuteSql", "执行任意 SQL 语句（MySQL，含查询/DDL/DML）", cat,
                sp => CreateTool<SqlQueryTool>(sp, "ExecuteSql"), ToolApprovalLevel.Dangerous);
        });
        return builder;
    }

    private static AIFunction CreateTool<T>(IServiceProvider sp, string method) where T : class
    {
        var tool = sp.GetRequiredService<T>();
        var m = typeof(T).GetMethod(method, BindingFlags.Public | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"Tool method '{method}' not found on {typeof(T).Name}.");
        return AIFunctionFactory.Create(m, tool);
    }
}
