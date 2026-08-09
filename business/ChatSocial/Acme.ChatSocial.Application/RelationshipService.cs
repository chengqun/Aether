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
        await _httpClient.GetAsync<object>($"api/app/relationship/relationship/{relationshipId}?type={relationshipType}");
    }

    public async Task<List<Relationship>> GetAcceptedContactsAsync()
    {
        // 服务端返回 ContactBookDto（按类型分组的对象 {devices,friends,groups,stocks,serviceAccounts}），
        // 不是裸 List<Relationship>。按扁平 List 反序列化会失败 → 返回空。
        // 此处反序列化包装对象后展平为单一列表（VM 的 ContactBookBuilder 按 Type 再分类）。
        var book = await _httpClient.GetAsync<ContactBookDto>("api/app/relationship/accepted-contacts");
        return FlattenBook(book);
    }

    public async Task<List<Relationship>> GetPendingRequestsAsync()
    {
        // pending-requests 同样返回按类型分组的 ContactBookDto。
        var book = await _httpClient.GetAsync<ContactBookDto>("api/app/relationship/pending-requests");
        return FlattenBook(book);
    }

    /// <summary>把 ContactBookDto 各分组展平为单一 Relationship 列表（空数组分量为空，无副作用）。</summary>
    private static List<Relationship> FlattenBook(ContactBookDto? book)
    {
        if (book == null) return new List<Relationship>();
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

    private static async Task<T> ParseResponseAsync<T>(HttpResponseMessage? response) where T : new()
    {
        if (response == null || !response.IsSuccessStatusCode)
            return new T();

        var json = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<T>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new T();
    }
}