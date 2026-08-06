namespace Acme.StockTrading.Domain;

/// <summary>
/// 股票交易客户端配置端口（OS/机制无关，与 <see cref="IStockBrokerage"/> 同层）。
/// UI/AI 经此读写「客户端 exe 路径 / 窗口标题」，不直接依赖 FlaUI options（Infrastructure 类型，Windows-only）。
/// 实现：FlaUI 适配器侧的 <c>FlaUiStockBrokerageOptionsAdapter</c>（Windows，转发到 FlaUI 单例 options，
/// 改字段即时生效）；非 Windows 桩（字段可读写但不被消费）。
/// </summary>
public interface IStockTradingOptions
{
    /// <summary>同花顺下单客户端 exe 路径（启动按钮用）。空 = 仅 attach 已运行窗口。</summary>
    string ClientExecutablePath { get; set; }

    /// <summary>下单客户端窗口标题（FindWindow ByName 用）。</summary>
    string ClientWindowTitle { get; set; }

    /// <summary>操作间停顿毫秒（默认 100）。</summary>
    int StepDelayMs { get; set; }
}
