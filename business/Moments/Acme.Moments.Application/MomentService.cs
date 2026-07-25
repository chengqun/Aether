using Acme.Moments.Domain;
using Acme.AppAuth.Domain;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace Acme.Moments.Application;

public class MomentService : IMomentService
{
    private readonly IAppServerHttpClient _httpClient;

    public MomentService(IAppServerHttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<List<Moment>> GetFriendsMomentsAsync(int limit = 50, int offset = 0, DateTime? since = null)
    {
        var url = $"api/app/moment/friends-moments?limit={limit}&offset={offset}";
        if (since.HasValue)
            url += $"&since={since.Value:O}";
        return await _httpClient.GetAsync<List<Moment>>(url) ?? new List<Moment>();
    }

    public async Task<Moment> CreateMomentAsync(string content, string? imageUrl = null, string type = "Text", string visibility = "Friends")
    {
        var body = new { Content = content, ImageUrl = imageUrl, Type = type, Visibility = visibility };
        var response = await _httpClient.PostRawAsync("api/app/moment", body);
        return await ParseResponseAsync<Moment>(response);
    }

    public async Task DeleteMomentAsync(string momentId)
    {
        await _httpClient.GetAsync<object>($"api/app/moment/{momentId}");
    }

    private static async Task<T> ParseResponseAsync<T>(HttpResponseMessage? response) where T : new()
    {
        if (response == null || !response.IsSuccessStatusCode)
            return new T();

        var json = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<T>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new T();
    }
}