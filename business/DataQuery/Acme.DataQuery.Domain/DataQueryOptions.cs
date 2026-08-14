namespace Acme.DataQuery.Domain;

/// <summary>
/// MySQL 连接配置（可变 POCO，DI 单例）。
/// 连接器卡 VM（桌面）写它 + 持久化到 IPreferencesStore；<c>MySqlSqlExecutor</c> 每次查询读它建连接串（改字段即时生效，
/// 镜像 StockTrading「同 options 引用 per-op 重读」）。放在 Domain 让 business Infrastructure 与桌面卡 VM 共享同一实例，
/// 而无需 business 引用桌面 IPreferencesStore。
/// </summary>
public sealed class DataQueryOptions
{
    public string? Host { get; set; }
    public int Port { get; set; } = DataQueryDefaults.DefaultPort;
    public string? User { get; set; }
    public string? Password { get; set; }
    public string? Database { get; set; }

    /// <summary>配置是否完整（host/user 至少非空）——决定能否尝试连接。</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Host) && !string.IsNullOrWhiteSpace(User);
}

/// <summary>数据查询默认常量。</summary>
public static class DataQueryDefaults
{
    /// <summary>MySQL 默认端口。</summary>
    public const int DefaultPort = 3306;

    /// <summary>单条命令超时（秒）。</summary>
    public const int CommandTimeoutSec = 30;

    /// <summary>SELECT 结果集返回给 AI 的最大行数（防爆量；超出置 Truncated）。</summary>
    public const int MaxRows = 500;
}
