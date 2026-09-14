using System.Text;
using System.Text.Json;
using Acme.AppAuth.Domain;
using Acme.AppAuth.Infrastructure.Auth;

namespace Acme.AppAuth.Infrastructure.Tests;

public class CurrentUserResolverTests
{
    private sealed class FakeTokenStore : IAppTokenStore
    {
        public string? AccessToken { get; set; }
        public string? Username { get; set; }

        public Task<string?> GetAccessTokenAsync(CancellationToken ct = default) => Task.FromResult(AccessToken);
        public Task<string?> GetRefreshTokenAsync(CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<string?> GetUsernameAsync(CancellationToken ct = default) => Task.FromResult(Username);
        public Task<DateTimeOffset?> GetExpiresAtAsync(CancellationToken ct = default) => Task.FromResult<DateTimeOffset?>(null);
        public Task SaveAsync(AppToken token, CancellationToken ct = default) => Task.CompletedTask;
        public Task ClearAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    /// <summary>用 base64url（RFC 7515，省略 `=` 填充）编码 payload 造一个假 JWT。</summary>
    private static string MakeJwt(object payload)
    {
        static string B64Url(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

        var header = B64Url("{\"alg\":\"none\",\"typ\":\"JWT\"}");
        var body = B64Url(JsonSerializer.Serialize(payload));
        return $"{header}.{body}.sig";
    }

    [Fact]
    public async Task Extracts_sub_from_base64url_payload()
    {
        // 回归守卫：sub 值刻意含能产生 `-`/`_` 的字符组合，使 base64url payload 里出现
        // `-`/`_`（旧 PadBase64 只补 `=` 不换字符 → Convert.FromBase64String 抛
        // FormatException 被吞 → 永远回退 MD5(username)）。
        var sub = "user-????>>>>>___~~~";
        var token = MakeJwt(new { sub, name = "alice" });
        // 前置断言：这个 payload 确实含 base64url 专有字符，否则测试没测到目标路径。
        var payload = token.Split('.')[1];
        Assert.True(payload.Contains('-') || payload.Contains('_'),
            $"test fixture did not produce a base64url-only payload: {payload}");

        var resolver = new CurrentUserResolver(new FakeTokenStore { AccessToken = token, Username = "alice" });

        Assert.Equal(sub, await resolver.GetCurrentUserIdAsync());
    }

    [Fact]
    public async Task Falls_back_to_md5_username_when_payload_has_no_sub()
    {
        var token = MakeJwt(new { name = "alice" });
        var resolver = new CurrentUserResolver(new FakeTokenStore { AccessToken = token, Username = "alice" });

        var id = await resolver.GetCurrentUserIdAsync();

        Assert.NotNull(id);
        Assert.NotEqual("", id);
        // MD5(username) 作为 Guid 字符串（8-4-4-4-12），不是 sub。
        Assert.Matches("^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$", id!);
    }

    [Fact]
    public async Task Falls_back_to_md5_username_when_token_malformed()
    {
        var resolver = new CurrentUserResolver(new FakeTokenStore { AccessToken = "not-a-jwt", Username = "bob" });

        var id = await resolver.GetCurrentUserIdAsync();

        Assert.Matches("^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$", id!);
    }

    [Fact]
    public async Task Returns_null_when_no_token_and_no_username()
    {
        var resolver = new CurrentUserResolver(new FakeTokenStore());

        Assert.Null(await resolver.GetCurrentUserIdAsync());
    }
}
