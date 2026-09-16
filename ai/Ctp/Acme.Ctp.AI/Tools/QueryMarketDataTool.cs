using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Acme.Ctp.Application;
using Acme.Ctp.Domain;

namespace Acme.Ctp.AI.Tools;

public class QueryMarketDataTool
{
    private readonly ICtpTradingService _service;

    public QueryMarketDataTool(ICtpTradingService service)
    {
        _service = service;
    }

    public Task<QueryTickOutput> QueryLatestTickAsync(string? instrumentId)
    {
        // 先校验空合约号再落库层：底层 _latestTicks.TryGetValue(null) 直接抛 ArgumentNullException，
        // agent 会拿到裸堆栈而非可理解的错误提示。
        if (string.IsNullOrWhiteSpace(instrumentId))
            return Task.FromResult(new QueryTickOutput(null, null, "instrumentId 不能为空。"));

        try
        {
            var tick = _service.GetLatestTick(instrumentId);
            return Task.FromResult(new QueryTickOutput(instrumentId, tick, null));
        }
        catch (Exception ex)
        {
            return Task.FromResult(new QueryTickOutput(instrumentId, null,
                $"查询最新行情失败: {ex.GetType().Name}: {ex.Message}"));
        }
    }

    public Task<QueryAllTicksOutput> QueryAllLatestTicksAsync()
    {
        try
        {
            var ticks = _service.GetAllLatestTicks().Values.ToList();
            return Task.FromResult(new QueryAllTicksOutput(ticks, null));
        }
        catch (Exception ex)
        {
            return Task.FromResult(new QueryAllTicksOutput(null,
                $"查询全部最新行情失败: {ex.GetType().Name}: {ex.Message}"));
        }
    }
}

/// <summary>查询单合约最新行情：无数据时 Tick=null 且 Error=null（未订阅/尚无 tick 属正常态，非错误）。</summary>
public sealed record QueryTickOutput(string? InstrumentId, CtpTick? Tick, string? Error);

/// <summary>查询全部订阅合约最新行情（镜像 DataQuery ExecuteSqlOutput 约定）。</summary>
public sealed record QueryAllTicksOutput(IReadOnlyList<CtpTick>? Ticks, string? Error);
