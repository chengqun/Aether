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

    public async Task<List<Moment>> GetFriendsMomentsAsync(int limit = 50, int offset = 0, DateTime? since = null, CancellationToken ct = default)
    {
        var url = $"api/app/moment/friends-moments?limit={limit}&offset={offset}";
        if (since.HasValue)
            url += $"&since={since.Value:O}";
        return await _httpClient.GetAsync<List<Moment>>(url, ct) ?? new List<Moment>();
    }

    public async Task<Moment> CreateMomentAsync(string content, string? imageUrl = null, string type = "Text", string visibility = "Friends", CancellationToken ct = default)
    {
        var body = new { Content = content, ImageUrl = imageUrl, Type = type, Visibility = visibility };
        var response = await _httpClient.PostRawAsync("api/app/moment", body, ct);
        return await ParseResponseAsync<Moment>(response, ct);
    }

    public async Task DeleteMomentAsync(string momentId, CancellationToken ct = default)
    {
        await _httpClient.GetAsync<object>($"api/app/moment/{momentId}", ct);
    }

    private static async Task<T> ParseResponseAsync<T>(HttpResponseMessage? response, CancellationToken ct) where T : new()
    {
        if (response == null || !response.IsSuccessStatusCode)
            return new T();

        var json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<T>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new T();
    }
}
