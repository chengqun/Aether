using Acme.Qifu.Domain.Yushu;

namespace Acme.Qifu.Mobile.Services;

/// <summary>
/// 手机端毓数 token 双向同步适配器：实现 <see cref="Acme.Mobile.Mvvm.Services.IYushuTokenSyncService"/>
/// （手机 VM 层本地端口），委托给业务 <see cref="Acme.Qifu.Domain.Yushu.IYushuTokenSyncService"/>
/// （<c>YushuTokenSyncService</c>，Acme.Qifu.Application，经 <c>AddQifuApplication</c> 注册）。
/// 两接口方法签名一致（SyncToServerAsync/SyncFromServerAsync），仅类型不同——本类做桥接。
/// </summary>
internal sealed class MobileYushuTokenSyncService : Acme.Mobile.Mvvm.Services.IYushuTokenSyncService
{
    private readonly IYushuTokenSyncService _businessSync;

    public MobileYushuTokenSyncService(IYushuTokenSyncService businessSync)
    {
        _businessSync = businessSync;
    }

    public Task<bool> SyncToServerAsync(CancellationToken ct = default)
        => _businessSync.SyncToServerAsync(ct);

    public Task<bool> SyncFromServerAsync(CancellationToken ct = default)
        => _businessSync.SyncFromServerAsync(ct);
}
