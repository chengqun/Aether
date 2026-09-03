namespace Acme.Qifu.Domain.Yushu;

/// <summary>
/// 毓数平台 Token 跨端同步：桌面端上传到服务器，手机端从服务器拉取。
/// </summary>
public interface IYushuTokenSyncService
{
    /// <summary>将本地缓存中的毓数 Token 上传到服务器（桌面端认证成功后调用）。</summary>
    Task<bool> SyncToServerAsync(CancellationToken ct = default);

    /// <summary>从服务器拉取毓数 Token 到本地缓存（手机端登录后调用）。</summary>
    Task<bool> SyncFromServerAsync(CancellationToken ct = default);
}
