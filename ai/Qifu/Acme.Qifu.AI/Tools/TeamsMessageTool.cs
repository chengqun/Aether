using System.ComponentModel;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Acme.Qifu.Domain.Yushu;

namespace Acme.Qifu.AI.Tools;

/// <summary>
/// 360Teams 消息通知工具，供 AI Agent 通过 AIFunctionFactory.Create() 调用。
/// 调用远程 QBI MCP 服务的 teams_im_message_sender 工具，把数据或分析结论以 Markdown 消息
/// 发送给指定域账号同事。认证复用毓数平台 OAuth（IYushuAuthService），与 QBI/毓数 BOLT MCP 共用同一 token。
/// </summary>
public class TeamsMessageTool
{
    private const string QbiMcpBaseUrl = "http://pro-qbi-mcp.daikuan.qihoo.net";
    private const string McpEndpoint = "/mcp/message";
    private const int MaxMessageLength = 8000;

    private readonly HttpClient _httpClient;
    private readonly IYushuAuthService _authService;
    private int _jsonRpcId = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public TeamsMessageTool(HttpClient httpClient, IYushuAuthService authService)
    {
        _httpClient = httpClient;
        _httpClient.Timeout = TimeSpan.FromSeconds(300);
        _authService = authService;
    }

    public class Output
    {
        public bool Success { get; set; }
        public string Result { get; set; } = string.Empty;
        public string? Error { get; set; }
    }

    [Description("360Teams-发送即时消息通知给指定域账号同事。消息支持 Markdown 格式，不超过 8000 字符。认证复用毓数平台登录态，未登录或过期会触发毓数 OAuth 登录。")]
    public async Task<Output> SendTeamsMessage(
        [Description("消息标题，必填，不能为空")] string title,
        [Description("消息内容，支持 Markdown 格式，不超过 8000 字符，必填")] string message,
        [Description("收信人域账号，多个用英文逗号分隔（如 zhangsan-jk,lisi-jk），必填")] string receiver="chengqun-jk",
        CancellationToken cancellationToken = default)
    {
        var validation = Validate(title, message, receiver);
        if (validation != null)
        {
            return new Output { Success = false, Error = validation, Result = validation };
        }

        var arguments = new Dictionary<string, object>
        {
            ["title"] = title.Trim(),
            ["message"] = message.Trim(),
            ["receiver"] = receiver.Trim(),
        };

        try
        {
            var text = await CallQbiToolAsync("teams_im_message_sender", arguments, cancellationToken);
            var receiverNames = string.Join("、", receiver.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            return new Output
            {
                Success = true,
                Result = string.IsNullOrEmpty(text)
                    ? $"已通知 {receiverNames}"
                    : $"已通知 {receiverNames}：{text}",
            };
        }
        catch (McpException ex)
        {
            return new Output { Success = false, Error = ex.Message, Result = $"发送失败: {ex.Message}" };
        }
        catch (Exception ex)
        {
            return new Output { Success = false, Error = ex.Message, Result = $"发送失败: {ex.Message}" };
        }
    }

    private static string? Validate(string title, string message, string receiver)
    {
        if (string.IsNullOrWhiteSpace(title))
            return "title 不能为空";
        if (string.IsNullOrWhiteSpace(message))
            return "message 不能为空";
        if (message.Length > MaxMessageLength)
            return $"message 超过 {MaxMessageLength} 字符（当前 {message.Length} 字符）";
        if (string.IsNullOrWhiteSpace(receiver))
            return "receiver 不能为空";

        var receivers = receiver.Split(',');
        foreach (var r in receivers)
        {
            if (string.IsNullOrWhiteSpace(r))
                return "receiver 列表格式错误（逗号之间存在空项）";
        }
        return null;
    }

    private async Task<string> CallQbiToolAsync(string toolName, Dictionary<string, object> arguments, CancellationToken ct)
    {
        var token = await _authService.GetAccessTokenAsync(ct);

        var body = new
        {
            jsonrpc = "2.0",
            id = Interlocked.Increment(ref _jsonRpcId),
            method = "tools/call",
            @params = new { name = toolName, arguments },
        };

        var url = $"{QbiMcpBaseUrl}{McpEndpoint}";
        var jsonBody = JsonSerializer.Serialize(body, JsonOptions);

        var response = await SendMcpRequestAsync(url, token, jsonBody, ct);

        // 401：重新认证后重试一次
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            await _authService.ReAuthenticateAsync(ct);

            token = await _authService.GetAccessTokenAsync(ct);
            response.Dispose();
            response = await SendMcpRequestAsync(url, token, jsonBody, ct);
        }

        try
        {
            response.EnsureSuccessStatusCode();

            var result = await ParseMcpResponseAsync(response, ct);

            // 远端工具级错误（isError=true）
            if (result.TryGetProperty("isError", out var isError) && isError.GetBoolean())
            {
                var errorTexts = new List<string>();
                if (result.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in content.EnumerateArray())
                    {
                        if (item.TryGetProperty("type", out var type) && type.GetString() == "text"
                            && item.TryGetProperty("text", out var text))
                        {
                            errorTexts.Add(text.GetString() ?? "");
                        }
                    }
                }
                throw new McpException(string.Join("; ", errorTexts));
            }

            return ExtractResultText(result) ?? string.Empty;
        }
        finally
        {
            response.Dispose();
        }
    }

    private async Task<HttpResponseMessage> SendMcpRequestAsync(string url, string token, string jsonBody, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Add("X-Request-Channel", "AGENT");
        request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

        return await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    private static async Task<JsonElement> ParseMcpResponseAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
        JsonElement envelope;

        if (contentType.Contains("event-stream"))
        {
            // SSE：取最后一条有效 data。每条 data: 是一整条 JSON-RPC envelope
            // （{"result":{content,isError},"jsonrpc","id"}），与普通 JSON 同形。
            var stream = await response.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream);
            JsonElement? lastObj = null;

            string? line;
            while ((line = await reader.ReadLineAsync(ct)) != null)
            {
                if (line.StartsWith("data:"))
                {
                    var dataStr = line[5..].Trim();
                    if (!string.IsNullOrEmpty(dataStr) && dataStr != "[DONE]")
                    {
                        try
                        {
                            lastObj = JsonSerializer.Deserialize<JsonElement>(dataStr, JsonOptions);
                        }
                        catch (JsonException)
                        {
                            // 忽略解析失败的行
                        }
                    }
                }
            }

            if (!lastObj.HasValue)
            {
                throw new McpException("SSE 响应中未找到有效数据");
            }
            envelope = lastObj.Value;
        }
        else
        {
            // 普通 JSON
            var json = await response.Content.ReadAsStringAsync(ct);
            envelope = JsonSerializer.Deserialize<JsonElement>(json, JsonOptions);
        }

        // 统一处理 JSON-RPC envelope（SSE 与 JSON 同形）。此前 SSE 分支直接返回整条 envelope：
        // ① 服务端以 error 应答时不查 → ExtractResultText 在顶层找不到 content → 返回 null →
        //    `?? ""` → 失败仍回「已通知 X」；② 未取 .result → 顶层找不到 content → 结果文本丢失。
        // 两条分支合流后统一查 error / 取 result，与 YushuService.ParseMcpResponseAsync 同构。
        if (envelope.TryGetProperty("error", out var error)
            && error.ValueKind != JsonValueKind.Null
            && error.ValueKind != JsonValueKind.False)
        {
            var message = error.TryGetProperty("message", out var m) ? m.GetString() ?? "未知错误" : "未知错误";
            var code = error.TryGetProperty("code", out var c) ? c.GetInt32() : (int?)null;
            throw new McpException(message, code);
        }

        return envelope.TryGetProperty("result", out var result) ? result : envelope;
    }

    private static string? ExtractResultText(JsonElement result)
    {
        if (result.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in content.EnumerateArray())
            {
                if (item.TryGetProperty("type", out var type) && type.GetString() == "text"
                    && item.TryGetProperty("text", out var text))
                {
                    return text.GetString();
                }
            }
        }
        return null;
    }
}

/// <summary>
/// QBI MCP 协议错误（TeamsMessageTool 内部使用）。
/// </summary>
internal class McpException : Exception
{
    public int? Code { get; }
    public McpException(string message, int? code = null) : base(message)
    {
        Code = code;
    }
}
