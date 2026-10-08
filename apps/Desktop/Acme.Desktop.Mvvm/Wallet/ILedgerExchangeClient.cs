namespace Acme.Desktop.Mvvm;

/// <summary>
/// 交易所提币端口（钱包页用）：连交易所 JSON-RPC（`withdraw` op）——登录绑定账户（凭据即鉴权）→
/// 烧毁 venue 现货持仓 → custody 签名链上出金。返回链上交易哈希。
/// <para>实现住 Shared（<c>LedgerExchangeClient</c>，包 <c>SimRpcClient</c>）——Mvvm 刻意不引
/// <c>Acme.Ctp.Sim</c>（客户端 VM 库保持 sim 类型零泄漏的既定纪律），层化经端口反转。</para>
/// <para>交易所只绑回环（服务器形态）——远程交易所须先开 SSH 隧道（runbook §0）。</para>
/// </summary>
public interface ILedgerExchangeClient
{
    /// <summary>提币。业务失败抛 <c>CtpBusinessException</c>（90019=桥未配/持仓不足/链拒绝，消息带原因）。</summary>
    Task<string> WithdrawAsync(string exchangeEndpoint, string userId, string password,
        string toAddressHex, ulong amountAtoms, CancellationToken cancellationToken = default);
}

/// <summary>钱包种子文件位置（DI 单例——VM 与 agent 工具（AddLedgerTools）共用同一路径：单用户桌面，用户与 agent 同一个钱包）。</summary>
public sealed record LedgerWalletLocation(string SeedFilePath);
