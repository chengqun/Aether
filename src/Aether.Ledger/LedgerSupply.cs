namespace Aether.Ledger;

/// <summary>
/// 链供给规则——**协议的第一不变量**（设计骨架）：全链余额合计 ≡ <see cref="TotalSupplyAtoms"/>，创世之后永真。
/// 21,000,000 币 × 10⁸ 原子单位（比特币式聪）；u64 上限 1.8×10¹⁹ ⇒ 余量四个数量级。
/// 刻意 <see langword="const"/> 而非配置：供给规则活在创世块里 = 任何增发都必然产生不同的创世哈希 = 一条不同的链。
/// </summary>
public static class LedgerSupply
{
    /// <summary>小数位（比特币式 8 位）。</summary>
    public const int Decimals = 8;

    /// <summary>原子单位/币。</summary>
    public const ulong AtomsPerCoin = 100_000_000UL;

    /// <summary>总量 21M 币（原子单位）——创世分配合计必须精确等于此值。</summary>
    public const ulong TotalSupplyAtoms = 21_000_000UL * AtomsPerCoin;

    /// <summary>显示格式化（验证器输出用）。</summary>
    public static string Format(ulong atoms)
        => (atoms / AtomsPerCoin).ToString("N0") + "." + (atoms % AtomsPerCoin).ToString("D8");
}
