using System.Net;
using System.Text;
using Acme.Qifu.AI.Tools;
using Acme.Qifu.Domain.Yushu;

namespace Acme.Qifu.AI.Tests;

public class TeamsMessageToolTests
{
    private sealed class FakeAuth : IYushuAuthService
    {
        public Task<string> GetAccessTokenAsync(CancellationToken ct = default) => Task.FromResult("tok");
        public Task<bool> IsAuthenticatedAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task<string?> LoginAsync(CancellationToken ct = default) => Task.FromResult<string?>("tok");
        public Task<string> ReAuthenticateAsync(CancellationToken ct = default) => Task.FromResult("tok");
    }

    /// <summary>回放一段固定 body / content-type 的假 handler。</summary>
    private sealed class FixedHandler : HttpMessageHandler
    {
        private readonly string _body;
        private readonly string _contentType;

        public FixedHandler(string body, string contentType)
        {
            _body = body;
            _contentType = contentType;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var content = new StringContent(_body, Encoding.UTF8);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(_contentType);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    private static TeamsMessageTool Build(string body, string contentType)
        => new(new HttpClient(new FixedHandler(body, contentType)), new FakeAuth());

    private const string SseJson = "text/event-stream";
    private const string PlainJson = "application/json";

    [Fact]
    public async Task Sse_error_envelope_reports_failure_instead_of_claiming_sent()
    {
        // 回归守卫：SSE 分支旧实现直接返回整条 envelope、不查 JSON-RPC error →
        // ExtractResultText 在顶层找不到 content → null → `?? ""` → 失败仍回「已通知 X」。
        const string body = "event: message\n" +
                            "data: {\"jsonrpc\":\"2.0\",\"id\":1,\"error\":{\"code\":-32000,\"message\":\"receiver not found\"}}\n\n";
        var tool = Build(body, SseJson);

        var output = await tool.SendTeamsMessage("标题", "内容", "zhangsan-jk");

        Assert.False(output.Success);
        Assert.Contains("receiver not found", output.Result);
        Assert.DoesNotContain("已通知", output.Result);
    }

    [Fact]
    public async Task Sse_result_envelope_is_unwrapped_so_result_text_survives()
    {
        // 回归守卫：SSE 分支旧实现不取 .result → 顶层无 content → 结果文本丢失（只剩「已通知 X」）。
        const string body = "event: message\n" +
                            "data: {\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{\"content\":[{\"type\":\"text\",\"text\":\"msg-123\"}]}}\n\n";
        var tool = Build(body, SseJson);

        var output = await tool.SendTeamsMessage("标题", "内容", "zhangsan-jk");

        Assert.True(output.Success);
        Assert.Contains("已通知", output.Result);
        Assert.Contains("msg-123", output.Result);
    }

    [Fact]
    public async Task Json_error_envelope_still_reports_failure()
    {
        // 既有路径（普通 JSON）不得回归。
        const string body = "{\"jsonrpc\":\"2.0\",\"id\":1,\"error\":{\"code\":-32000,\"message\":\"bad token\"}}";
        var tool = Build(body, PlainJson);

        var output = await tool.SendTeamsMessage("标题", "内容", "zhangsan-jk");

        Assert.False(output.Success);
        Assert.Contains("bad token", output.Result);
        Assert.DoesNotContain("已通知", output.Result);
    }

    [Fact]
    public async Task Sse_tool_level_isError_reports_failure()
    {
        const string body = "data: {\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{\"isError\":true," +
                            "\"content\":[{\"type\":\"text\",\"text\":\"quota exceeded\"}]}}\n\n";
        var tool = Build(body, SseJson);

        var output = await tool.SendTeamsMessage("标题", "内容", "zhangsan-jk");

        Assert.False(output.Success);
        Assert.Contains("quota exceeded", output.Result);
        Assert.DoesNotContain("已通知", output.Result);
    }
}
