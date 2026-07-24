namespace Acme.Qifu.Domain.CloudDocs;

/// <summary>
/// 360Teams 云文档 sid 跨端同步：桌面端上传到服务器，手机端从服务器拉取。
/// 手机端无法自行跑 Playwright 登录，依赖桌面端已上传的 sid。
/// </summary>
public interface ICloudDocsTokenSyncService
{
    /// <summary>将本地缓存的云文档 sid 上传到服务器（桌面端登录成功后调用）。</summary>
    Task<bool> SyncToServerAsync(CancellationToken ct = default);

    /// <summary>从服务器拉取云文档 sid 到本地缓存（手机端登录后调用）。</summary>
    Task<bool> SyncFromServerAsync(CancellationToken ct = default);
}
