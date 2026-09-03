using Acme.Qifu.Domain.CloudDocs;

namespace Acme.Qifu.Mobile.Services;

/// <summary>
/// 手机端云文档 sid 双向同步适配器：实现 <see cref="Acme.Mobile.Mvvm.Services.ICloudDocsTokenSyncService"/>
/// （手机 VM 层本地端口），委托给业务 <see cref="Acme.Qifu.Domain.CloudDocs.ICloudDocsTokenSyncService"/>
/// （<c>CloudDocsTokenSyncService</c>，Acme.Qifu.Application，经 <c>AddQifuApplication</c> 注册）。
/// </summary>
internal sealed class MobileCloudDocsTokenSyncService : Acme.Mobile.Mvvm.Services.ICloudDocsTokenSyncService
{
    private readonly ICloudDocsTokenSyncService _businessSync;

    public MobileCloudDocsTokenSyncService(ICloudDocsTokenSyncService businessSync)
    {
        _businessSync = businessSync;
    }

    public Task<bool> SyncToServerAsync(CancellationToken ct = default)
        => _businessSync.SyncToServerAsync(ct);

    public Task<bool> SyncFromServerAsync(CancellationToken ct = default)
        => _businessSync.SyncFromServerAsync(ct);
}
