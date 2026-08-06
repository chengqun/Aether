using Acme.StockTrading.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Acme.StockTrading.Infrastructure.DependencyInjection;

/// <summary>
/// FlaUI 适配器 DI 入口（Windows-only）。注册 IStockBrokerage → FlaUiStockBrokerage。
/// 配置（窗口标题/exe 路径）通过回调设置；宿主可选不传回调用默认值。
/// 同时注册 <see cref="IStockTradingOptions"/>（跨平台端口）→ <see cref="FlaUiStockBrokerageOptionsAdapter"/>，
/// 让 UI 经端口读写配置，不直接依赖 Infrastructure 类型；改端口字段即时生效。
/// </summary>
public static class StockTradingInfrastructureExtensions
{
    public static IServiceCollection AddStockTradingFlaUi(
        this IServiceCollection services,
        Action<FlaUiStockBrokerageOptions>? configure = null)
    {
        var options = new FlaUiStockBrokerageOptions();
        configure?.Invoke(options);
        services.AddSingleton(options);
        services.AddSingleton<IStockBrokerage>(sp =>
            new FlaUiStockBrokerage(options, sp.GetService<ILogger<FlaUiStockBrokerage>>()));
        services.AddSingleton<IStockTradingOptions>(new FlaUiStockBrokerageOptionsAdapter(options));
        return services;
    }
}
