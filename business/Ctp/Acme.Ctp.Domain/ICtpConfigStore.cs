namespace Acme.Ctp.Domain;

/// <summary>
/// Persists the CTP connection profiles locally（多连接档案：连接即自动建档，键 = 前置地址 + 用户号）.
/// A concrete SQLite store is provided by Infrastructure.
/// </summary>
public interface ICtpConfigStore
{
    /// <summary>上次使用的 profile（is_last_used 行；无则 null——自动登录随之关闭）。</summary>
    Task<CtpConnectionConfig?> GetAsync(CancellationToken ct = default);

    /// <summary>全部 profile（last-used 优先，再按 Name 排序）。</summary>
    Task<List<CtpConnectionConfig>> GetAllAsync(CancellationToken ct = default);

    /// <summary>按 Id upsert；<paramref name="config"/> 的 IsLastUsed=true 时在同一事务内清掉其它行的旗标（唯一性不变量）。</summary>
    Task SaveAsync(CtpConnectionConfig config, CancellationToken ct = default);

    /// <summary>删除指定 profile（不影响当前连接）。</summary>
    Task DeleteAsync(Guid id, CancellationToken ct = default);

    /// <summary>删除全部 profile（旧 ctp_config 单行表不再触碰）。</summary>
    Task ClearAsync(CancellationToken ct = default);
}
