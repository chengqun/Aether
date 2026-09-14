using System.Net;
using System.Text;
using System.Text.Json;
using Acme.AppAuth.Application.Auth;
using Acme.AppAuth.Domain;

namespace Acme.AppAuth.Infrastructure.Tests;

/// <summary>
/// AppAuthService.RefreshAsync single-flight 回归测试——锁「并发 401 → N 个并发 refresh_token
/// grant → 令牌轮换互踩 → 败者 ClearAsync 抹掉胜者刚存的 token → 会话被登出」的修复
/// （见 non-apps 审计 P2 惊群项）。
/// </summary>
public class AppAuthSingleFlightTests
{
    private sealed class FakeHandler : HttpMessageHandler
    {
        public int Hits;
        public bool Fail;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // 模拟服务端刷新有网络往返——保证 N 个调用真实重叠在同一轮内
            await Task.Delay(100, cancellationToken);
            Interlocked.Increment(ref Hits);
            if (Fail)
                return new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent("{\"error\":\"invalid_grant\"}", Encoding.UTF8, "application/json")
                };
            var body = JsonSerializer.Serialize(new
            {
                access_token = $"access-{Hits}",
                refresh_token = $"rotated-{Hits}",
                expires_in = 3600
            });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class FakeTokenStore : IAppTokenStore
    {
        private AppToken? _token;
        public int ClearCount;

        public Task<string?> GetAccessTokenAsync(CancellationToken ct = default) => Task.FromResult(_token?.AccessToken);
        public Task<string?> GetRefreshTokenAsync(CancellationToken ct = default) => Task.FromResult(_token?.RefreshToken);
        public Task<string?> GetUsernameAsync(CancellationToken ct = default) => Task.FromResult(_token?.Username);
        public Task<DateTimeOffset?> GetExpiresAtAsync(CancellationToken ct = default) => Task.FromResult(_token?.ExpiresAt);

        public Task SaveAsync(AppToken token, CancellationToken ct = default)
        {
            _token = token;
            return Task.CompletedTask;
        }

        public Task ClearAsync(CancellationToken ct = default)
        {
            Interlocked.Increment(ref ClearCount);
            _token = null;
            return Task.CompletedTask;
        }
    }

    private static AppAuthService CreateService(FakeHandler handler, FakeTokenStore store) => new(
        new HttpClient(handler),
        store,
        new AppServerOptions { ClientId = "client", ClientSecret = "secret", Tenant = "default", BaseUrl = "https://server.example" });

    [Fact]
    public async Task ConcurrentRefresh_CoalescesIntoSingleTokenRequest()
    {
        var handler = new FakeHandler();
        var store = new FakeTokenStore();
        store.SaveAsync(new AppToken { AccessToken = "old", RefreshToken = "refresh-1", ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(1) }).GetAwaiter().GetResult();
        var svc = CreateService(handler, store);

        // 启动期 6 路并行（证券同步 + 聊天 + 动态等全 401 → 各调 RefreshAsync）
        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => svc.RefreshAsync()));

        Assert.All(results, r => Assert.True(r)); // 全部成功（无败者 ClearAsync）
        Assert.Equal(1, handler.Hits);            // single-flight：6 调用只发了 1 次 HTTP
        Assert.Equal(0, store.ClearCount);        // 刷新成功，无人清空 token
        Assert.Equal("rotated-1", (await store.GetRefreshTokenAsync())!); // 胜者 token 存活
    }

    [Fact]
    public async Task SequentialRefresh_StillAllowsNewRefreshAfterLeaderCompletes()
    {
        var handler = new FakeHandler();
        var store = new FakeTokenStore();
        store.SaveAsync(new AppToken { AccessToken = "old", RefreshToken = "refresh-1", ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(1) }).GetAwaiter().GetResult();
        var svc = CreateService(handler, store);

        Assert.True(await svc.RefreshAsync());
        // 上一轮 leader 已退出、位已清——下一轮 401 重新发起刷新（不吞掉后续刷新）
        Assert.True(await svc.RefreshAsync());
        Assert.Equal(2, handler.Hits);
        Assert.Equal(0, store.ClearCount);
    }

    [Fact]
    public async Task FailedRefresh_StillClearsOnceAndFollowersDoNotDoubleClear()
    {
        var handler = new FakeHandler { Fail = true };
        var store = new FakeTokenStore();
        store.SaveAsync(new AppToken { AccessToken = "old", RefreshToken = "refresh-1", ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(1) }).GetAwaiter().GetResult();
        var svc = CreateService(handler, store);

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => svc.RefreshAsync()));

        Assert.All(results, r => Assert.False(r));
        Assert.Equal(1, handler.Hits);
        Assert.Equal(1, store.ClearCount); // 只清一次（leader），无重复清空
    }
}
