using Acme.Ctp.Domain;
using Acme.Ctp.Sim;
using Acme.Desktop.Mvvm;

namespace Acme.Desktop.Shared.Hosting;

/// <summary>
/// 交易所提币端口实现（钱包页用）：每调用一连接的薄封装——Connect + Hello → Login（绑定账户 = 出金账户，
/// 凭据即鉴权）→ <c>withdraw</c> op（先烧 venue 现货持仓，custody 签名链上出金）→ 返回 tx hash。
/// <para>镜像钱包 CLI 的 withdraw 路径；业务错误（90019 等）经 <see cref="CtpBusinessException"/> 上抛
/// （ErrorId + 消息带原因——桥未配/持仓不足/链拒绝）。</para>
/// </summary>
public sealed class LedgerExchangeClient : ILedgerExchangeClient
{
    public async Task<string> WithdrawAsync(string exchangeEndpoint, string userId, string password,
        string toAddressHex, ulong amountAtoms, string asset = "coin", CancellationToken cancellationToken = default)
    {
        var (host, port) = ParseEndpoint(exchangeEndpoint);
        using var client = new SimRpcClient(host, port);
        await client.ConnectAsync(cancellationToken);
        await client.LoginAsync(new LoginArg("SIM", userId, password), cancellationToken);
        return await client.WithdrawAsync(toAddressHex, amountAtoms, asset, cancellationToken);
    }

    /// <summary>[host:]port 解析（同钱包 CLI——空/垃圾值抛 <see cref="FormatException"/>）。</summary>
    private static (string Host, int Port) ParseEndpoint(string endpoint)
    {
        var addr = endpoint.Trim();
        var colon = addr.LastIndexOf(':');
        if (colon > 0 && int.TryParse(addr[(colon + 1)..], out var port))
            return (addr[..colon], port);
        if (int.TryParse(addr, out var portOnly))
            return ("127.0.0.1", portOnly);
        throw new FormatException($"交易所地址格式非法：\"{endpoint}\"（应为 [host:]port，如 127.0.0.1:17800）");
    }
}
