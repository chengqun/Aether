namespace Acme.Ctp.Domain;

// —— 客户端专属契约类型（2026-10-04 契约分家：三方共享的交易域 DTO 已拆至交易所契约内核
//    Acme.CtpExchange.Domain——本项目经共享内核引用它们，AppAuth 模式；本文件只留客户端会话形状的类型）——

/// <summary>CTP 登录连接参数（交易前置 + 行情前置 + 凭据）。</summary>
public sealed class CtpLoginRequest
{
    public required string FrontAddress { get; init; }   // 交易前置 tcp://ip:port
    public required string MdFrontAddress { get; init; } // 行情前置 tcp://ip:port
    public required string BrokerId { get; init; }
    public required string UserId { get; init; }
    public required string Password { get; init; }
    public required string AppId { get; init; }
    public required string AuthCode { get; init; }
}

/// <summary>行情前置连接参数。CTP 行情前置是独立连接，不依赖交易登录即可订阅行情。</summary>
public sealed class CtpMarketDataConnect
{
    public required string MdFrontAddress { get; init; }   // 行情前置 tcp://ip:port
    public required string BrokerId { get; init; }
    public required string UserId { get; init; }
    public required string Password { get; init; }
}

/// <summary>
/// CTP 连接状态（比单纯 bool 更细，承载重连/闭市语义）。
/// </summary>
public enum CtpConnectionStatus
{
    /// <summary>未登录 / 已登出。</summary>
    Disconnected,
    /// <summary>已连接且已登录（可交易/查询）。</summary>
    Connected,
    /// <summary>盘中断线，重连 + 重新登录中。</summary>
    Reconnecting,
    /// <summary>闭市（交易时段外），等待下个开盘自动重连。</summary>
    Closed,
}
