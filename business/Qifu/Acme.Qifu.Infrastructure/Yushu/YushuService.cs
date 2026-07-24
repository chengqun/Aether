using Acme.Qifu.Domain.Yushu;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Acme.Qifu.Infrastructure.Yushu;

/// <summary>
/// 毓数平台 BOLT MCP API 调用服务，通过 JSON-RPC 2.0 协议调用远程工具。
/// </summary>
public class YushuService : IYushuService
{
    private const string BoltMcpBaseUrl = "http://pro-bolt-mcp.daikuan.qihoo.net";
    private const string McpEndpoint = "/mcp/message";

    private readonly HttpClient _httpClient;
    private readonly IYushuAuthService _authService;
    private readonly string _resultsDir;
    private int _jsonRpcId = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// 创建 YushuService 实例。
    /// </summary>
    /// <param name="httpClient">HTTP 客户端</param>
    /// <param name="authService">毓数认证服务</param>
    /// <param name="resultsDir">查询结果文件的存储目录，为 null 时使用默认路径 ~/.yushu/results</param>
    public YushuService(HttpClient httpClient, IYushuAuthService authService, string? resultsDir = null)
    {
        _httpClient = httpClient;
        _authService = authService;
        _httpClient.Timeout = TimeSpan.FromSeconds(300);
        _resultsDir = resultsDir ?? Path.Combine(GetHomePath(), ".yushu", "results");
    }

    /// <inheritdoc />
    public async Task<YushuSqlQueryResult> AsyncSqlQueryAsync(string sql, string modelName, CancellationToken ct = default)
    {
        try
        {
            var result = await CallMcpToolAsync("asyncSqlQuery", new Dictionary<string, object>
            {
                ["sql"] = sql,
                ["model_name"] = modelName,
            }, ct);

            var text = ExtractResultText(result);
            return new YushuSqlQueryResult { JobId = text?.Trim() };
        }
        catch (McpException ex)
        {
            return new YushuSqlQueryResult { ErrorMessage = ex.Message };
        }
        catch (Exception ex)
        {
            return new YushuSqlQueryResult { ErrorMessage = $"SQL查询提交失败: {ex.Message}" };
        }
    }

    /// <inheritdoc />
    public async Task<YushuTaskStatusResult> FetchSqlTaskStatusAsync(string jobId, CancellationToken ct = default)
    {
        try
        {
            var result = await CallMcpToolAsync("fetchSqlTaskStatus", new Dictionary<string, object>
            {
                ["jobId"] = jobId,
            }, ct);

            var text = ExtractResultText(result);

            // 远程 MCP 返回的是原始状态，不包含自动轮询
            // 解析状态 JSON
            if (string.IsNullOrEmpty(text))
            {
                return new YushuTaskStatusResult { Status = "UNKNOWN", ErrorMessage = "返回内容为空" };
            }

            try
            {
                var statusData = JsonSerializer.Deserialize<JsonElement>(text, JsonOptions);
                return new YushuTaskStatusResult
                {
                    Status = statusData.TryGetProperty("status", out var s) ? s.GetString() ?? "UNKNOWN" : "UNKNOWN",
                    ResultUrl = statusData.TryGetProperty("resultUrl", out var u) ? u.GetString() : null,
                    TotalRows = statusData.TryGetProperty("totalRows", out var r) ? r.GetInt32() : 0,
                    ErrorMessage = statusData.TryGetProperty("errMsg", out var e) ? e.GetString() : null,
                    Message = statusData.TryGetProperty("message", out var m) ? m.GetString() : null,
                };
            }
            catch (JsonException)
            {
                // 如果不是 JSON，直接作为状态文本返回
                return new YushuTaskStatusResult { Status = text.Trim(), Message = text.Trim() };
            }
        }
        catch (McpException ex)
        {
            return new YushuTaskStatusResult { Status = "UNKNOWN", ErrorMessage = ex.Message };
        }
        catch (Exception ex)
        {
            return new YushuTaskStatusResult { Status = "UNKNOWN", ErrorMessage = $"查询状态失败: {ex.Message}" };
        }
    }

    /// <inheritdoc />
    public async Task<YushuDownloadResult> DownloadResultAsync(string resultUrl, string jobId, CancellationToken ct = default)
    {
        try
        {
            var token = await _authService.GetAccessTokenAsync(ct);

            using var request = new HttpRequestMessage(HttpMethod.Get, resultUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Add("X-Request-Channel", "AGENT");

            var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            // 检查自定义响应头
            var flag = response.Headers.Contains("flag") ? GetHeaderValue(response, "flag") : "S";
            if (flag == "F")
            {
                var msg = GetHeaderValue(response, "msg") ?? "下载失败";
                return new YushuDownloadResult { ErrorMessage = Uri.UnescapeDataString(msg) };
            }

            // 从 Content-Disposition 获取文件名
            var fileName = GetFileNameFromResponse(response, jobId);
            var filePath = Path.Combine(GetResultsDir(), fileName);

            // 确保目录存在
            var dir = Path.GetDirectoryName(filePath)!;
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            // 写入文件
            var bytes = await response.Content.ReadAsByteArrayAsync(ct);
            await File.WriteAllBytesAsync(filePath, bytes, ct);

            return new YushuDownloadResult
            {
                FilePath = filePath,
                FileSize = bytes.Length,
            };
        }
        catch (Exception ex)
        {
            return new YushuDownloadResult { ErrorMessage = $"下载结果失败: {ex.Message}" };
        }
    }

    /// <inheritdoc />
    public async Task<YushuPreviewResult> ReadResultAsync(string jobId, int maxRows = 10, int maxColumns = 10, int maxCellSize = 200, CancellationToken ct = default)
    {
        try
        {
            var resultsDir = GetResultsDir();
            if (!Directory.Exists(resultsDir))
            {
                return new YushuPreviewResult { ErrorMessage = "结果目录不存在，请先下载结果" };
            }

            var files = Directory.GetFiles(resultsDir, $"{jobId}.*");
            if (files.Length == 0)
            {
                return new YushuPreviewResult { ErrorMessage = $"未找到 jobId={jobId} 的结果文件" };
            }

            var filePath = files[0];
            var extension = Path.GetExtension(filePath).ToLowerInvariant();

            string csvContent;
            if (extension == ".zip")
            {
                using var archive = ZipFile.OpenRead(filePath);
                var csvEntry = archive.Entries.FirstOrDefault(e =>
                    e.Name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase));

                if (csvEntry == null)
                {
                    return new YushuPreviewResult { ErrorMessage = "ZIP 中未找到 CSV 文件" };
                }

                using var stream = csvEntry.Open();
                using var reader = new StreamReader(stream, Encoding.UTF8);
                csvContent = await reader.ReadToEndAsync(ct);
            }
            else
            {
                csvContent = await File.ReadAllTextAsync(filePath, Encoding.UTF8, ct);
            }

            return ParseCsvToPreview(csvContent, maxRows, maxColumns, maxCellSize);
        }
        catch (Exception ex)
        {
            return new YushuPreviewResult { ErrorMessage = $"预览结果失败: {ex.Message}" };
        }
    }

    /// <inheritdoc />
    public async Task<YushuCleanResult> CleanResultsAsync(int olderThanDays = 7, bool dryRun = false, CancellationToken ct = default)
    {
        try
        {
            var resultsDir = GetResultsDir();
            if (!Directory.Exists(resultsDir))
            {
                return new YushuCleanResult { RemovedCount = 0, RemovedSize = 0 };
            }

            var cutoff = DateTime.Now - TimeSpan.FromDays(olderThanDays);
            var removedFiles = new List<string>();
            long removedSize = 0;

            foreach (var file in new DirectoryInfo(resultsDir).EnumerateFiles())
            {
                ct.ThrowIfCancellationRequested();

                if (file.LastWriteTime < cutoff)
                {
                    removedFiles.Add(file.Name);
                    removedSize += file.Length;

                    if (!dryRun)
                    {
                        file.Delete();
                    }
                }
            }

            return new YushuCleanResult
            {
                RemovedCount = removedFiles.Count,
                RemovedSize = removedSize,
                RemovedFiles = removedFiles,
            };
        }
        catch (Exception ex)
        {
            return new YushuCleanResult { ErrorMessage = $"清理结果文件失败: {ex.Message}" };
        }
    }

    #region MCP JSON-RPC 调用

    private async Task<JsonElement> CallMcpToolAsync(string toolName, Dictionary<string, object> arguments, CancellationToken ct)
    {
        var token = await _authService.GetAccessTokenAsync(ct);

        var body = new
        {
            jsonrpc = "2.0",
            id = Interlocked.Increment(ref _jsonRpcId),
            method = "tools/call",
            @params = new { name = toolName, arguments },
        };

        var url = $"{BoltMcpBaseUrl}{McpEndpoint}";

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Add("X-Request-Channel", "AGENT");

        var jsonBody = JsonSerializer.Serialize(body, JsonOptions);
        request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

        var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

        // 处理 401 重新认证
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            if (_authService is YushuAuthService yushuAuth)
            {
                await yushuAuth.ReAuthenticateAsync(ct);
            }
            else
            {
                await _authService.GetAccessTokenAsync(ct);
            }

            // 重试一次
            token = await _authService.GetAccessTokenAsync(ct);
            using var retryRequest = new HttpRequestMessage(HttpMethod.Post, url);
            retryRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            retryRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            retryRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            retryRequest.Headers.Add("X-Request-Channel", "AGENT");
            retryRequest.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

            response = await _httpClient.SendAsync(retryRequest, HttpCompletionOption.ResponseHeadersRead, ct);
        }

        response.EnsureSuccessStatusCode();

        // 解析响应（支持 SSE 和 JSON）
        var result = await ParseMcpResponseAsync(response, ct);

        // 检查错误
        if (result.TryGetProperty("isError", out var isError) && isError.GetBoolean())
        {
            var errorTexts = new List<string>();
            if (result.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in content.EnumerateObject())
                {
                    // content 是数组，需要枚举数组元素
                }
                foreach (var item in content.EnumerateArray())
                {
                    if (item.TryGetProperty("type", out var type) && type.GetString() == "text")
                    {
                        errorTexts.Add(item.GetProperty("text").GetString() ?? "");
                    }
                }
            }
            throw new McpException(string.Join("; ", errorTexts));
        }

        return result;
    }

    private static async Task<JsonElement> ParseMcpResponseAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var contentType = response.Content.Headers.ContentType?.MediaType ?? "";

        if (contentType.Contains("event-stream"))
        {
            // SSE：读取所有行，取最后一条有效 data
            var stream = await response.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream);
            JsonElement? lastObj = null;

            while (!reader.EndOfStream)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line == null) break;

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

            if (lastObj.HasValue)
            {
                return lastObj.Value;
            }

            throw new McpException("SSE 响应中未找到有效数据");
        }
        else
        {
            // 普通 JSON
            var json = await response.Content.ReadAsStringAsync(ct);
            var envelope = JsonSerializer.Deserialize<JsonElement>(json, JsonOptions);

            // 检查 JSON-RPC 错误
            if (envelope.TryGetProperty("error", out var error) &&
                error.ValueKind != JsonValueKind.Null &&
                error.ValueKind != JsonValueKind.False)
            {
                var message = error.TryGetProperty("message", out var m) ? m.GetString() ?? "未知错误" : "未知错误";
                var code = error.TryGetProperty("code", out var c) ? c.GetInt32() : (int?)null;
                throw new McpException(message, code);
            }

            return envelope.TryGetProperty("result", out var result) ? result : envelope;
        }
    }

    private static string? ExtractResultText(JsonElement result)
    {
        if (result.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in content.EnumerateArray())
            {
                if (item.TryGetProperty("type", out var type) && type.GetString() == "text")
                {
                    return item.TryGetProperty("text", out var text) ? text.GetString() : null;
                }
            }
        }
        return null;
    }

    #endregion

    #region CSV 解析与 Markdown 预览

    private static YushuPreviewResult ParseCsvToPreview(string csvContent, int maxRows, int maxColumns = 10, int maxCellSize = 200)
    {
        var lines = csvContent.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0)
        {
            return new YushuPreviewResult { Content = "无数据", TotalRows = 0 };
        }

        var headers = ParseCsvLine(lines[0]);
        var totalRows = lines.Length - 1;
        var totalColumns = headers.Count;

        var sb = new StringBuilder();

        sb.Append("| ");
        sb.Append(string.Join(" | ", headers.Take(maxColumns).Select(h => TruncateCell(h, maxCellSize))));
        if (totalColumns > maxColumns) sb.Append(" | ...");
        sb.AppendLine(" |");

        sb.Append("| ");
        var separatorCount = Math.Min(totalColumns, maxColumns);
        sb.Append(string.Join(" | ", Enumerable.Repeat("---", separatorCount)));
        if (totalColumns > maxColumns) sb.Append(" | ...");
        sb.AppendLine(" |");

        var displayRows = Math.Min(maxRows, totalRows);
        for (var i = 1; i <= displayRows; i++)
        {
            var cells = ParseCsvLine(lines[i]);
            sb.Append("| ");
            sb.Append(string.Join(" | ", cells.Take(maxColumns).Select(c => TruncateCell(c, maxCellSize))));
            if (totalColumns > maxColumns) sb.Append(" | ...");
            sb.AppendLine(" |");
        }

        if (totalRows > maxRows)
        {
            sb.AppendLine($"\n... 共 {totalRows} 行，显示前 {maxRows} 行");
        }

        return new YushuPreviewResult
        {
            Content = sb.ToString(),
            TotalRows = totalRows,
            TotalColumns = totalColumns,
        };
    }

    private static List<string> ParseCsvLine(string line)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;

        foreach (var ch in line)
        {
            if (ch == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (ch == ',' && !inQuotes)
            {
                result.Add(current.ToString().Trim());
                current.Clear();
            }
            else
            {
                current.Append(ch);
            }
        }

        result.Add(current.ToString().Trim());
        return result;
    }

    private static string TruncateCell(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value)) return "";
        if (value.Length <= maxLength) return value;
        return value[..maxLength] + "...";
    }

    #endregion

    #region 辅助方法

    private static string GetHomePath() => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private string GetResultsDir() => _resultsDir;

    /// <inheritdoc />
    public string GetResultsDirectory() => _resultsDir;

    private static string GetFileNameFromResponse(HttpResponseMessage response, string jobId)
    {
        var contentDisposition = response.Content.Headers.ContentDisposition;
        if (contentDisposition?.FileName != null)
        {
            var fileName = contentDisposition.FileName.Trim('"');
            if (!string.IsNullOrEmpty(fileName))
            {
                // 使用 jobId 前缀 + 原始扩展名
                var ext = Path.GetExtension(fileName);
                return $"{jobId}{ext}";
            }
        }

        return $"{jobId}.csv";
    }

    private static string? GetHeaderValue(HttpResponseMessage response, string name)
    {
        if (response.Headers.TryGetValues(name, out var values))
        {
            return values.FirstOrDefault();
        }
        if (response.Content.Headers.TryGetValues(name, out var contentValues))
        {
            return contentValues.FirstOrDefault();
        }
        return null;
    }

    #endregion
}

/// <summary>
/// MCP 协议错误
/// </summary>
internal class McpException : Exception
{
    public int? Code { get; }

    public McpException(string message, int? code = null) : base(message)
    {
        Code = code;
    }
}
