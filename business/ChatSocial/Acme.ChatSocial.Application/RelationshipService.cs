using Acme.ChatSocial.Domain;
using Acme.AppAuth.Domain;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace Acme.ChatSocial.Application;

public class RelationshipService : IRelationshipService
{
    private readonly IAppServerHttpClient _httpClient;

    public RelationshipService(IAppServerHttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<Relationship> CreateRelationshipAsync(int relationshipType, string targetId)
    {
        var response = await _httpClient.PostRawAsync($"api/app/relationship/relationship/{targetId}?type={relationshipType}", new object());
        return await ParseResponseAsync<Relationship>(response);
    }

    public async Task<Relationship> AcceptRelationshipAsync(int relationshipType, string relationshipId)
    {
        var response = await _httpClient.PostRawAsync($"api/app/relationship/accept-relationship/{relationshipId}?type={relationshipType}", new object());
        return await ParseResponseAsync<Relationship>(response);
    }

    public async Task<Relationship> RejectRelationshipAsync(int relationshipType, string relationshipId)
    {
        var response = await _httpClient.PostRawAsync($"api/app/relationship/reject-relationship/{relationshipId}?type={relationshipType}", new object());
        return await ParseResponseAsync<Relationship>(response);
    }

    public async Task RemoveRelationshipAsync(int relationshipType, string relationshipId)
    {
        // 用 GetRawAsync 检查状态码（GetAsync<object> 在空 body 时也会返回 null，无法区分失败与成功空响应）。
        // 失败抛异常——调用方（ContactDetailViewModel）已有 try/catch，设计意图即如此。
        var response = await _httpClient.GetRawAsync($"api/app/relationship/relationship/{relationshipId}?type={relationshipType}");
        if (response == null || !response.IsSuccessStatusCode)
            throw new HttpRequestException($"删除关系失败: {(int?)response?.StatusCode}");
    }

    public async Task<List<Relationship>?> GetAcceptedContactsAsync()
    {
        // 服务端返回 ContactBookDto（按类型分组的对象 {devices,friends,groups,stocks,serviceAccounts}），
        // 不是裸 List<Relationship>。按扁平 List 反序列化会失败 → 返回空。
        // 此处反序列化包装对象后展平为单一列表（VM 的 ContactBookBuilder 按 Type 再分类）。
        // book==null 表示 HTTP 失败 → 返回 null（不兜底空 list，否则缓存全量替换会误清空本地通讯录）。
        var book = await _httpClient.GetAsync<ContactBookDto>("api/app/relationship/accepted-contacts");
        return FlattenBook(book);
    }

    public async Task<List<Relationship>?> GetPendingRequestsAsync()
    {
        // pending-requests 同样返回按类型分组的 ContactBookDto。null=失败。
        var book = await _httpClient.GetAsync<ContactBookDto>("api/app/relationship/pending-requests");
        return FlattenBook(book);
    }

    /// <summary>把 ContactBookDto 各分组展平为单一 Relationship 列表。book==null（HTTP 失败）时返回 null，让调用方区分失败与空数据。</summary>
    private static List<Relationship>? FlattenBook(ContactBookDto? book)
    {
        if (book == null) return null;
        var all = new List<Relationship>(book.Devices.Count + book.Friends.Count + book.Groups.Count);
        all.AddRange(book.Devices);
        all.AddRange(book.Friends);
        all.AddRange(book.Groups);
        all.AddRange(book.Stocks);
        all.AddRange(book.ServiceAccounts);
        return all;
    }

    /// <summary>
    /// 服务端 accepted-contacts / pending-requests 端点的响应包装：按关系类型分组的列表。
    /// 镜像 AlphaAgent 服务端 ContactBookDto 契约。仅传输 DTO，不入 Domain。
    /// </summary>
    private sealed class ContactBookDto
    {
        public List<Relationship> Devices { get; set; } = new();
        public List<Relationship> Friends { get; set; } = new();
        public List<Relationship> Groups { get; set; } = new();
        public List<Relationship> Stocks { get; set; } = new();
        public List<Relationship> ServiceAccounts { get; set; } = new();
    }

    private static async Task<T> ParseResponseAsync<T>(HttpResponseMessage? response) where T : class
    {
        // 写操作（Create/Accept/Reject）失败抛异常——调用方已有 try/catch（注释"失败抛异常，由 catch 处理"）。
        // 之前兜底 new T() 把失败伪装成空对象，调用方 catch 永不触发 → 用户看不到错误。
        if (response == null || !response.IsSuccessStatusCode)
            throw new HttpRequestException($"请求失败: {(int?)response?.StatusCode}");

        var json = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<T>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new HttpRequestException("响应反序列化失败");
    }
}