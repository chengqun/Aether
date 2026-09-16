using System;
using System.Threading;
using System.Threading.Tasks;
using Acme.Ctp.Application;
using Acme.Ctp.Domain;

namespace Acme.Ctp.AI.Tools;

public class QueryAccountTool
{
    private readonly ICtpTradingService _service;

    public QueryAccountTool(ICtpTradingService service)
    {
        _service = service;
    }

    public async Task<QueryAccountOutput> QueryAccountAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var account = await _service.QueryAccountAsync(cancellationToken);
            return new QueryAccountOutput(account, null);
        }
        catch (OperationCanceledException)
        {
            return new QueryAccountOutput(null, "查询资金账户已取消。");
        }
        catch (Exception ex)
        {
            // 不把原始堆栈透给 agent——返回结构化单行错误，便于其判断重试/换参
            //（如 CTP 未登录/前置未连时底层抛 native 异常）。
            return new QueryAccountOutput(null, $"查询资金账户失败: {ex.GetType().Name}: {ex.Message}");
        }
    }
}

/// <summary>查询资金账户结果：Account 或 Error 二择一（镜像 DataQuery ExecuteSqlOutput 约定）。</summary>
public sealed record QueryAccountOutput(CtpAccount? Account, string? Error);
