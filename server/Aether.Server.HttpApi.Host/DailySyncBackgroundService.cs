using System;
using System.Threading;
using System.Threading.Tasks;
using Aether.Server.Application.Contracts.Services.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Aether.Server.HttpApi.Host;

/// <summary>
/// 每日定时同步任务：上海时间 20:01 执行同步期货数据和同步选股策略发朋友圈
/// </summary>
public class DailySyncBackgroundService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<DailySyncBackgroundService> _logger;

    public DailySyncBackgroundService(
        IServiceProvider serviceProvider,
        ILogger<DailySyncBackgroundService> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("DailySync: 定时同步服务已启动，每天上海时间 20:01 执行");

        while (!stoppingToken.IsCancellationRequested)
        {
            var now = DateTime.Now; // 服务器本地时间
            var todayTarget = now.Date.AddHours(20).AddMinutes(1); // 今天 20:01

            TimeSpan delay;
            if (now >= todayTarget)
            {
                // 已过今天 20:01，等明天 20:01
                delay = todayTarget.AddDays(1) - now;
            }
            else
            {
                // 还没到今天 20:01
                delay = todayTarget - now;
            }

            _logger.LogInformation("DailySync: 下次执行时间 {NextRun}，等待 {Delay}",
                (now + delay).ToString("yyyy-MM-dd HH:mm:ss"), delay);

            await Task.Delay(delay, stoppingToken);

            if (stoppingToken.IsCancellationRequested) break;

            _logger.LogInformation("DailySync: 开始执行每日同步任务...");

            try
            {
                using var scope = _serviceProvider.CreateScope();
                var syncService = scope.ServiceProvider.GetRequiredService<ISecuritySyncService>();

                // 1. 同步期货数据
                _logger.LogInformation("DailySync: 开始同步期货数据...");
                var syncResult = await syncService.SyncFromExternalAsync();
                _logger.LogInformation("DailySync: 期货数据同步完成，共{Total}条，新增{Added}条，更新{Updated}条",
                    syncResult.Total, syncResult.Added, syncResult.Updated);

                // 2. 同步选股策略发朋友圈 + 服务号文章
                _logger.LogInformation("DailySync: 开始同步选股策略...");
                var pickingResult = await syncService.SyncStockPickingMomentsAsync();
                _logger.LogInformation("DailySync: 选股策略同步完成，{Strategies}个策略，发布{Published}条朋友圈，跳过{Skipped}只股票",
                    pickingResult.TotalStrategies, pickingResult.PublishedMoments, pickingResult.SkippedStocks);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "DailySync: 每日同步任务执行失败");
            }
        }

        _logger.LogInformation("DailySync: 定时同步服务已停止");
    }
}
