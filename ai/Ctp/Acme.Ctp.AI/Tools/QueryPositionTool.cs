using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Acme.Ctp.Application;
using Acme.Ctp.Domain;

namespace Acme.Ctp.AI.Tools;

public class QueryPositionTool
{
    private readonly ICtpTradingService _service;

    public QueryPositionTool(ICtpTradingService service)
    {
        _service = service;
    }

    public async Task<QueryPositionsOutput> QueryPositionsAsync(string? instrumentId = null, CancellationToken cancellationToken = default)
    {
        try
        {
            var positions = await _service.QueryPositionAsync(instrumentId, cancellationToken);
            return new QueryPositionsOutput(positions, null);
        }
        catch (OperationCanceledException)
        {
            return new QueryPositionsOutput(null, "查询持仓已取消。");
        }
        catch (Exception ex)
        {
            // 不把原始堆栈透给 agent——返回结构化单行错误，便于其判断重试/换参。
            return new QueryPositionsOutput(null, $"查询持仓失败: {ex.GetType().Name}: {ex.Message}");
        }
    }
}

/// <summary>查询持仓结果：Positions 或 Error 二择一（镜像 DataQuery ExecuteSqlOutput 约定）。</summary>
public sealed record QueryPositionsOutput(IReadOnlyList<CtpPosition>? Positions, string? Error);
