using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Aether.Server.Application.Contracts.Services.Security;
using Aether.Server.Domain.Entities;
using Aether.Server.Domain.Services.Moment;
using Aether.Server.Domain.Services.Securities;
using Aether.Server.Domain.Services.ServiceAccounts;
using Microsoft.Extensions.Logging;
using Volo.Abp.Application.Services;
using Volo.Abp.Domain.Repositories;
using Volo.Abp.TenantManagement;

namespace Aether.Server.Application.Services.Security;

public class SecuritySyncService : ApplicationService, ISecuritySyncService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ISecurityManager _securityManager;
    private readonly IMomentManager _momentManager;
    private readonly IRepository<AppMoment, Guid> _momentRepository;
    private readonly IServiceAccountManager _serviceAccountManager;
    private readonly IServiceAccountPostManager _serviceAccountPostManager;
    private readonly IRepository<AppServiceAccountPost, Guid> _postRepository;
    private readonly ITenantRepository _tenantRepository;

    public SecuritySyncService(
        IHttpClientFactory httpClientFactory,
        ISecurityManager securityManager,
        IMomentManager momentManager,
        IRepository<AppMoment, Guid> momentRepository,
        IServiceAccountManager serviceAccountManager,
        IServiceAccountPostManager serviceAccountPostManager,
        IRepository<AppServiceAccountPost, Guid> postRepository,
        ITenantRepository tenantRepository)
    {
        _httpClientFactory = httpClientFactory;
        _securityManager = securityManager;
        _momentManager = momentManager;
        _momentRepository = momentRepository;
        _serviceAccountManager = serviceAccountManager;
        _serviceAccountPostManager = serviceAccountPostManager;
        _postRepository = postRepository;
        _tenantRepository = tenantRepository;
    }

    public async Task<SecuritySyncResult> SyncFromExternalAsync()
    {
        // 东方财富期货合约列表（返回 {list:[{dm,name,sc,...}]}，sc=交易所码 113/114/115/142/220/225）。
        // 与 stockPickingUrl 同样写死，不走 appsettings 配置。
        const string futuresUrl = "https://futsseapi.eastmoney.com/list/trans/block/risk/mk0830?orderBy=&sort=&pageSize=999&pageIndex=0&specificContract=true&platform=zbPC&field=name,p,zdf,vol,ccl,rz,tjd,cje,zde,o,h,l,zf,zjsj,zt,dt,dm,sc,tag,uid,zsjd";

        try
        {
            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(30);
            var response = await client.GetStringAsync(futuresUrl);
            var securities = ParseFutureData(response);

            if (securities.Count == 0)
            {
                Logger.LogInformation("SecuritySync: 外部数据源返回0条记录");
                return new SecuritySyncResult { Total = 0 };
            }

            var (added, updated) = await _securityManager.UpsertRangeAsync(securities);
            Logger.LogInformation("SecuritySync: 同步完成，共{Total}条，新增{Added}条，更新{Updated}条",
                securities.Count, added, updated);

            return new SecuritySyncResult
            {
                Total = securities.Count,
                Added = added,
                Updated = updated
            };
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "SecuritySync: 同步失败 - {Message}", ex.Message);
            return new SecuritySyncResult();
        }
    }

    public async Task<StockPickingMomentResult> SyncStockPickingMomentsAsync()
    {
        const string stockPickingUrl = "http://ai.10jqka.com.cn/transfer/index/index?app=19";

        // 选股策略数据（朋友圈 + 「明日策略」服务号文章）归 stockapp 租户——服务号为 stockapp 专属。
        // DailySyncBackgroundService 在 host 上下文调用本方法，这里切换到 stockapp 租户，
        // 使新建的 AppMoment/AppServiceAccountPost 带 stockapp TenantId，并被 stockapp 租户可见。
        var stockappTenant = await _tenantRepository.FindByNameAsync("stockapp");
        if (stockappTenant == null)
        {
            Logger.LogWarning("StockPickingSync: 未找到 stockapp 租户，跳过选股策略同步");
            return new StockPickingMomentResult();
        }

        using (CurrentTenant.Change(stockappTenant.Id))
        {
            try
            {
            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(30);
            var response = await client.GetStringAsync(stockPickingUrl);

            var strategies = ParseStockPickingData(response);
            if (strategies.Count == 0)
            {
                Logger.LogInformation("StockPickingSync: 外部数据源返回0条策略");
                return new StockPickingMomentResult();
            }

            // 取策略日期（所有策略同一天），设定发布时间为当天 20:00 上海时间（UTC+8 = 12:00 UTC）
            var pickingDate = strategies.First().Date;
            var publishTime = DateTime.SpecifyKind(pickingDate.Date.AddHours(12), DateTimeKind.Utc); // 20:00 CST = 12:00 UTC
            var dateStart = pickingDate.Date;
            var dateEnd = dateStart.AddDays(1);

            // 查询该日期已有的股票朋友圈，用于去重（内容相同则跳过）
            var existingMoments = await _momentRepository.GetListAsync(m =>
                m.Type == "Stock" && m.CreatedAt >= dateStart && m.CreatedAt < dateEnd);
            // 用内容做去重集合：同一天内容已存在则跳过
            var existingContents = existingMoments.Select(m => m.Content).ToHashSet();

            var result = new StockPickingMomentResult { TotalStrategies = strategies.Count };

            // 批量预加载所有策略涉及的股票代码，避免 N+1 查询
            var allStockCodes = strategies
                .SelectMany(s => s.Stocks)
                .Select(s => s.StockCode)
                .Distinct()
                .ToList();
            var securityDict = await _securityManager.GetByCodeAndTypeBatchAsync(allStockCodes, "股票");

            // 每个策略的每只股票各发一条朋友圈
            foreach (var strategy in strategies)
            {
                var content = $"🎯 击中策略：{strategy.StrategyName}";

                // 去重：同一天内容相同则跳过
                if (existingContents.Contains(content))
                {
                    continue;
                }

                foreach (var stock in strategy.Stocks)
                {
                    // 从预加载字典中查找，不再逐个查询数据库
                    if (!securityDict.TryGetValue(stock.StockCode, out var security))
                    {
                        Logger.LogWarning("StockPickingSync: 股票代码 {Code} (Type=股票) 未在 AppSecurities 中找到，跳过", stock.StockCode);
                        result.SkippedStocks++;
                        continue;
                    }

                    await _momentManager.CreateStockMomentAsync(security.Id, content, publishTime);
                    result.PublishedMoments++;
                }

                // 记录已发布的内容，防止同一策略在同一次运行中重复
                existingContents.Add(content);
            }

            // 发布到"明日策略"服务号
            await PublishToServiceAccountAsync(strategies, publishTime);

            Logger.LogInformation("StockPickingSync: 同步完成，{Strategies}个策略，发布{Published}条朋友圈，跳过{Skipped}只股票",
                result.TotalStrategies, result.PublishedMoments, result.SkippedStocks);

            return result;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "StockPickingSync: 同步失败 - {Message}", ex.Message);
            return new StockPickingMomentResult();
        }
        }
    }

    /// <summary>
    /// 将选股策略发布到"明日策略"服务号
    /// </summary>
    private async Task PublishToServiceAccountAsync(List<StockPickingStrategy> strategies, DateTime publishTime)
    {
        const string serviceAccountName = "明日策略";

        try
        {
            // 按名称查找服务号
            var accounts = await _serviceAccountManager.SearchAsync(serviceAccountName);
            var serviceAccount = accounts.FirstOrDefault(sa => sa.Name == serviceAccountName);
            if (serviceAccount == null)
            {
                Logger.LogWarning("StockPickingSync: 未找到名为「{Name}」的服务号，跳过服务号发布", serviceAccountName);
                return;
            }

            // 查询该服务号同一天已有的文章，用于去重
            var dateStart = publishTime.Date;
            var dateEnd = dateStart.AddDays(1);
            var existingPosts = await _postRepository.GetListAsync(p =>
                p.ServiceAccountId == serviceAccount.Id
                && p.PublishedAt >= dateStart && p.PublishedAt < dateEnd);
            var existingTitles = existingPosts.Select(p => p.Title).ToHashSet();

            foreach (var strategy in strategies)
            {
                var title = strategy.StrategyName;

                // 去重：同一天同名文章已存在则跳过
                if (existingTitles.Contains(title))
                {
                    continue;
                }

                // 正文：策略名称 + 股票列表
                var stockList = string.Join("\n", strategy.Stocks.Select(s => $"- {s.StockName}（{s.StockCode}）"));
                var content = $"## {strategy.StrategyName}\n\n{stockList}";
                var summary = $"共 {strategy.Stocks.Count} 只股票：{string.Join("、", strategy.Stocks.Take(5).Select(s => s.StockName))}{(strategy.Stocks.Count > 5 ? "等" : "")}";

                await _serviceAccountPostManager.CreatePostAsync(
                    serviceAccount.Id, title, content, publishTime, summary, contentType: "Analysis");

                Logger.LogInformation("StockPickingSync: 已发布服务号文章「{Title}」", title);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "StockPickingSync: 发布服务号文章失败 - {Message}", ex.Message);
        }
    }

    private List<AppSecurity> ParseFutureData(string response)
    {
        using var doc = JsonDocument.Parse(response);

        if (!doc.RootElement.TryGetProperty("list", out var listElement))
        {
            return new List<AppSecurity>();
        }

        return listElement.EnumerateArray()
            .Select(item =>
            {
                var code = item.TryGetProperty("dm", out var dm) ? dm.GetString()?.ToLower() ?? "" : "";
                var name = item.TryGetProperty("name", out var nameEl) ? nameEl.GetString() ?? "" : "";
                var sc = item.TryGetProperty("sc", out var scEl) ? scEl.GetInt32() : 0;

                return new AppSecurity(code, name, "期货", sc.ToString(), ExtractBaseCode(code));
            })
            .Where(s => !string.IsNullOrEmpty(s.Code))
            .ToList();
    }

    private static string ExtractBaseCode(string code)
    {
        if (string.IsNullOrEmpty(code)) return code;

        var i = code.Length - 1;
        while (i >= 0 && char.IsDigit(code[i])) i--;

        return i < code.Length - 1 ? code[..(i + 1)] : code;
    }

    private List<StockPickingStrategy> ParseStockPickingData(string response)
    {
        using var doc = JsonDocument.Parse(response);

        if (!doc.RootElement.TryGetProperty("data", out var dataArray))
        {
            return new List<StockPickingStrategy>();
        }

        var result = new List<StockPickingStrategy>();

        foreach (var item in dataArray.EnumerateArray())
        {
            var strategyName = item.TryGetProperty("strategy_name", out var sn) ? sn.GetString() ?? "" : "";
            var dateStr = item.TryGetProperty("stockpicking_date", out var d) ? d.GetString() ?? "" : "";

            if (!item.TryGetProperty("stock_info", out var stockInfoArray))
                continue;

            var stocks = new List<StockPickingStock>();
            foreach (var stockItem in stockInfoArray.EnumerateArray())
            {
                var code = stockItem.TryGetProperty("stock_code", out var sc) ? sc.GetString() ?? "" : "";
                var name = stockItem.TryGetProperty("stock_name", out var sn2) ? sn2.GetString() ?? "" : "";
                if (!string.IsNullOrEmpty(code))
                    stocks.Add(new StockPickingStock { StockCode = code, StockName = name });
            }

            // 解析日期：yyyyMMdd → DateTime
            DateTime date = default;
            if (dateStr.Length == 8 && int.TryParse(dateStr, out _))
            {
                date = new DateTime(
                    int.Parse(dateStr[..4]),
                    int.Parse(dateStr[4..6]),
                    int.Parse(dateStr[6..8]));
            }
            else
            {
                date = DateTime.UtcNow;
            }

            if (!string.IsNullOrEmpty(strategyName) && stocks.Count > 0)
            {
                result.Add(new StockPickingStrategy
                {
                    StrategyName = strategyName,
                    Date = date,
                    Stocks = stocks
                });
            }
        }

        return result;
    }
}

/// <summary>
/// 选股策略数据
/// </summary>
internal class StockPickingStrategy
{
    public string StrategyName { get; set; } = string.Empty;
    public DateTime Date { get; set; }
    public List<StockPickingStock> Stocks { get; set; } = new();
}

/// <summary>
/// 选股策略中的股票
/// </summary>
internal class StockPickingStock
{
    public string StockCode { get; set; } = string.Empty;
    public string StockName { get; set; } = string.Empty;
}
