using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Acme.Moments.Domain;

public interface IMomentService
{
    /// <summary>朋友朋友圈聚合 feed。null=HTTP 失败，空列表=成功无数据。</summary>
    Task<List<Moment>?> GetFriendsMomentsAsync(int limit = 50, int offset = 0, DateTime? since = null, CancellationToken ct = default);

    /// <summary>指定联系人/股票/设备/群组的朋友圈。targetId + type 过滤（服务端 moments/{targetId} 端点）。
    /// type 取值：friendship/user/stock/device/group。null=HTTP 失败。</summary>
    Task<List<Moment>?> GetMomentsAsync(string targetId, string type, int limit = 50, int offset = 0, DateTime? since = null, CancellationToken ct = default);

    /// <summary>发朋友圈。null=HTTP 失败（不兜底空 Moment，否则 VM 误报发布成功 + 插空白卡片）。</summary>
    Task<Moment?> CreateMomentAsync(string content, string? imageUrl = null, string type = "Text", string visibility = "Friends", CancellationToken ct = default);

    /// <summary>删除朋友圈（P1：当前用 GET 凑合，服务端是 [HttpDelete]——待 IAppServerHttpClient 加 DeleteRawAsync 后改 verb）。</summary>
    Task DeleteMomentAsync(string momentId, CancellationToken ct = default);
}
