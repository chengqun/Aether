using System.Threading.Tasks;
using Volo.Abp.Data;
using Volo.Abp.DependencyInjection;
using Volo.Abp.FeatureManagement;
using Volo.Abp.MultiTenancy;
using Volo.Abp.TenantManagement;
using Volo.Abp.Uow;

namespace Aether.Server.Features;

/// <summary>
/// 按租户用途种入功能特性默认值，使既有 qifu/stockapp 租户在启用特性门控后不丢能力：
///   qifu     → 毓数 / 云文档 开
///   stockapp → 股票行情 / 分析 开
/// Chat.* 默认 true，回退即可，无需种入。
/// 仅在未显式设置时种入（GetOrNullAsync==null），管理员后续手改不会被覆盖。仅 host 上下文运行。
/// </summary>
public class TenantFeatureDefaultValueSeedContributor : IDataSeedContributor, ITransientDependency
{
    private readonly IFeatureManager _featureManager;
    private readonly ITenantRepository _tenantRepository;

    public TenantFeatureDefaultValueSeedContributor(
        IFeatureManager featureManager,
        ITenantRepository tenantRepository)
    {
        _featureManager = featureManager;
        _tenantRepository = tenantRepository;
    }

    [UnitOfWork]
    public virtual async Task SeedAsync(DataSeedContext context)
    {
        if (context.TenantId.HasValue)
        {
            return; // 仅 host 种入租户级特性
        }

        await SeedTenantAsync("qifu", new[]
        {
            ("Yushu.SqlQuery", "true"),
            ("Yushu.DataQuery", "true"),
            ("CloudDocs.Upload", "true"),
            ("CloudDocs.Query", "true")
        });

        await SeedTenantAsync("stockapp", new[]
        {
            ("StockApp.Quote", "true"),
            ("StockApp.Analysis", "true")
        });
    }

    private async Task SeedTenantAsync(string tenantName, (string Feature, string Value)[] features)
    {
        var tenant = await _tenantRepository.FindByNameAsync(tenantName);
        if (tenant == null)
        {
            return; // 租户尚未创建，下次 seed 再补
        }

        var providerKey = tenant.Id.ToString();
        foreach (var (feature, value) in features)
        {
            // 仅在未显式设置时种入，避免覆盖管理员手改
            var current = await _featureManager.GetOrNullAsync(feature, "T", providerKey);
            if (current == null)
            {
                await _featureManager.SetAsync(feature, value, "T", providerKey);
            }
        }
    }
}
