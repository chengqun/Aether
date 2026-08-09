using Acme.Moments.Domain;
using Acme.AppAuth.Domain;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Acme.Moments.Application;

public class MomentService : IMomentService
{
    private readonly IAppServerHttpClient _httpClient;

    public MomentService(IAppServerHttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<List<Moment>?> GetFriendsMomentsAsync(int limit = 50, int offset = 0, DateTime? since = null, CancellationToken ct = default)
    {
        // null=HTTP 失败（GetAsync 返 null），不兜底空 list——否则缓存增量同步分不清失败与空数据。
        var url = $"api/app/moment/friends-moments?limit={limit}&offset={offset}";
        if (since.HasValue)
            url += $"&since={since.Value:O}";
        return await _httpClient.GetAsync<List<Moment>>(url, ct);
    }

    public async Task<List<Moment>?> GetMomentsAsync(string targetId, string type, int limit = 50, int offset = 0, DateTime? since = null, CancellationToken ct = default)
    {
        // 服务端 [HttpGet("moments/{targetId}")]，按 type 解析 targetId（user/stock/device/group）。null=HTTP 失败。
        var url = $"api/app/moment/moments/{Uri.EscapeDataString(targetId)}?type={Uri.EscapeDataString(type)}&limit={limit}&offset={offset}";
        if (since.HasValue)
            url += $"&since={since.Value:O}";
        return await _httpClient.GetAsync<List<Moment>>(url, ct);
    }

    public async Task<Moment?> CreateMomentAsync(string content, string? imageUrl = null, string type = "Text", string visibility = "Friends", CancellationToken ct = default)
    {
        // null=HTTP 失败——不兜底 new Moment()，否则 VM 的 if(created!=null) 永真 → 发布失败仍显示成功+插空白卡片。
        var body = new { Content = content, ImageUrl = imageUrl, Type = type, Visibility = visibility };
        var response = await _httpClient.PostRawAsync("api/app/moment", body, ct);
        if (response == null || !response.IsSuccessStatusCode) return null;
        var json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<Moment>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    }

    public async Task DeleteMomentAsync(string momentId, CancellationToken ct = default)
    {
        // 服务端 [HttpDelete("moment/{id}")]——用 DELETE verb + 检查状态码，失败抛异常。
        // 之前用 GET 凑合不命中 DELETE 路由，删除永远不执行。
        var response = await _httpClient.DeleteRawAsync($"api/app/moment/{momentId}", ct);
        if (response == null || !response.IsSuccessStatusCode)
            throw new System.Net.Http.HttpRequestException($"删除朋友圈失败: {(int?)response?.StatusCode}");
    }
}
