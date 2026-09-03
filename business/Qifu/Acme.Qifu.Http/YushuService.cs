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

namespace Acme.Qifu.Http.Yushu;

/// <summary>
/// 毓数平台 BOLT MCP API 调用服务，通过 JSON-RPC 2.0 协议调用远程工具。
/// </summary>
public class YushuService : IYushuService
{
    // 全部经 mcphub 网关：查询工具 boltv2-* + 权限工单 dcpv2-*。鉴权用 mcphub dashboard JWT
    //（IMcphubAuthService 管理，与 Python skill 共享 ~/.local/share/mcphub/credentials.json，走 x-auth-token 头）。
    // 旧 bolt 网关（pro-bolt-mcp）已弃用——查询工具一并迁到 mcphub。
    private const string McphubBaseUrl = "https://mcphub.daikuan.qihoo.net";
    private const string McphubEndpoint = "/mcp";

    /// <summary>mcphub 网关要求所有 tools/call 带 _model_id_；工具未显式提供时用此默认值（fetch/sqlLog 等）。</summary>
    private const string DefaultModelId = "360/360-kimi-k2.5";

    /// <summary>上报给 mcphub 的 agent 工具标识（对照 sql_query.py --platform）。</summary>
    private const string ClientPlatform = "Aether";
    /// <summary>客户端版本号；与 sql-query skill 对齐，避免远端返回"请升级到最新版本"提示（对照 sql_query.py VERSION）。</summary>
    private const string ClientVersion = "1.1.1";

    private readonly HttpClient _httpClient;
    private readonly IMcphubAuthService _mcphubAuth;
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
    /// <param name="mcphubAuth">mcphub 网关鉴权服务</param>
    /// <param name="resultsDir">查询结果文件的存储目录，为 null 时使用默认路径 ~/.yushu/results</param>
    public YushuService(HttpClient httpClient, IMcphubAuthService mcphubAuth, string? resultsDir = null)
    {
        _httpClient = httpClient;
        _mcphubAuth = mcphubAuth;
        _httpClient.Timeout = TimeSpan.FromSeconds(300);
        _resultsDir = resultsDir ?? Path.Combine(GetHomePath(), ".yushu", "results");
    }

    /// <inheritdoc />
    public async Task<YushuSqlQueryResult> AsyncSqlQueryAsync(string sql, string modelName, CancellationToken ct = default)
    {
        try
        {
            // 对照 sql_query.py cmd_async_sql_query：线协议入参含 platform + version（上报 agent 工具与
            // 客户端版本；version 缺失时远端会回 "请升级到最新版本" 提示）。_model_id_ 由 CallMcphubToolAsync 注入。
            var result = await CallMcphubToolAsync("boltv2-asyncSqlQuery", new Dictionary<string, object>
            {
                ["sql"] = sql,
                ["model_name"] = modelName,
                ["platform"] = ClientPlatform,
                ["version"] = ClientVersion,
            }, modelName, ct);

            // 远端 text 为 JSON：{"jobId":"...","modelName":"...","modelLevel":"...","errMsg":null,"tip":"..."}
            // 提取 jobId（旧实现把整段 JSON 当 jobId 返回，会污染后续 FetchSqlTaskStatus(jobId)）；errMsg 非空视为失败。
            var text = ExtractResultText(result);
            return ParseSqlQueryResult(text);
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
            var result = await CallMcphubToolAsync("boltv2-fetchSqlTaskStatus", new Dictionary<string, object>
            {
                ["jobId"] = jobId,
            }, null, ct);

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
            // resultUrl 为直连 https 下载地址（短链已下线，服务端不 30x），自认证——无需 auth 头
            //（对照 sql_query.py _open_download_response：仅 Accept: */*）。
            using var request = new HttpRequestMessage(HttpMethod.Get, resultUrl);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));

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

    /// <inheritdoc />
    public async Task<YushuSqlLogResult> SqlLogAsync(string jobId, CancellationToken ct = default)
    {
        try
        {
            var result = await CallMcphubToolAsync("boltv2-sqlLog", new Dictionary<string, object>
            {
                ["jobId"] = jobId,
            }, null, ct);

            var text = ExtractResultText(result) ?? string.Empty;
            var lines = ParseLogLines(text);

            // 全量覆盖写 <jobId>.log（随 CleanResults 按 mtime 一并清理）
            Directory.CreateDirectory(_resultsDir);
            var filePath = Path.Combine(_resultsDir, $"{jobId}.log");
            await File.WriteAllTextAsync(filePath, string.Join('\n', lines) + (lines.Count > 0 ? "\n" : ""), Encoding.UTF8, ct);

            return new YushuSqlLogResult
            {
                FilePath = filePath,
                TotalLines = lines.Count,
                Lines = lines,
            };
        }
        catch (McpException ex)
        {
            return new YushuSqlLogResult { ErrorMessage = ex.Message };
        }
        catch (Exception ex)
        {
            return new YushuSqlLogResult { ErrorMessage = $"拉取任务日志失败: {ex.Message}" };
        }
    }

    /// <inheritdoc />
    public async Task<YushuApplyResult> ApplyModelUsageAuthorityAsync(
        string dataSource, string tables, string modelLevel, string purpose, int selectLimit,
        string? modelName = null, CancellationToken ct = default)
    {
        try
        {
            var arguments = new Dictionary<string, object>
            {
                ["dataSource"] = dataSource,
                ["tables"] = tables,
                ["modelLevel"] = modelLevel,
                ["purpose"] = purpose,
                ["selectLimit"] = selectLimit,
            };
            // _model_id_ 由 CallMcphubToolAsync 统一注入；模型安全等级申请尤其需要（记录用哪个模型查表）。

            var result = await CallMcphubToolAsync("dcpv2-applyModelUsageAuthority", arguments, modelName, ct);

            return new YushuApplyResult { Result = ExtractResultText(result) };
        }
        catch (McpException ex)
        {
            return new YushuApplyResult { ErrorMessage = ex.Message };
        }
        catch (Exception ex)
        {
            return new YushuApplyResult { ErrorMessage = $"发起模型安全等级申请失败: {ex.Message}" };
        }
    }

    /// <inheritdoc />
    public async Task<YushuApplyResult> ApplyTableAuthorityAsync(
        string tables, string authType, string? dataSource = null, string? reason = null,
        bool hdfsAuth = false, string? modelName = null, CancellationToken ct = default)
    {
        try
        {
            var arguments = new Dictionary<string, object>
            {
                ["tables"] = tables,
                ["authType"] = authType,
            };
            if (!string.IsNullOrEmpty(dataSource)) arguments["dataSource"] = dataSource;
            if (!string.IsNullOrEmpty(reason)) arguments["reason"] = reason;
            if (hdfsAuth) arguments["hdfsAuth"] = true;

            var result = await CallMcphubToolAsync("dcpv2-applyTableAuthority", arguments, modelName, ct);

            return new YushuApplyResult { Result = ExtractResultText(result) };
        }
        catch (McpException ex)
        {
            return new YushuApplyResult { ErrorMessage = ex.Message };
        }
        catch (Exception ex)
        {
            return new YushuApplyResult { ErrorMessage = $"发起表权限申请失败: {ex.Message}" };
        }
    }

    /// <summary>解析 boltv2-sqlLog 返回的日志行：JSON 数组 → 逐项转 str；JSON 字符串 → splitlines；否则原文本 splitlines。</summary>
    private static List<string> ParseLogLines(string text)
    {
        if (string.IsNullOrEmpty(text)) return new List<string>();
        try
        {
            var parsed = JsonSerializer.Deserialize<JsonElement>(text, JsonOptions);
            if (parsed.ValueKind == JsonValueKind.Array)
            {
                var list = new List<string>(parsed.GetArrayLength());
                foreach (var item in parsed.EnumerateArray())
                    list.Add(item.ValueKind == JsonValueKind.Null ? string.Empty : item.ToString());
                return list;
            }
            if (parsed.ValueKind == JsonValueKind.String)
                return parsed.GetString()?.Split('\n', StringSplitOptions.None).ToList() ?? new List<string>();
        }
        catch (JsonException) { /* 落到原文本分支 */ }
        return text.Split('\n', StringSplitOptions.None).ToList();
    }

    #region MCP JSON-RPC 调用

    /// <summary>
    /// 调用 mcphub 网关工具（查询 boltv2-* + 权限工单 dcpv2-* 全经此网关）。鉴权用 mcphub
    /// dashboard JWT（IMcphubAuthService，走 x-auth-token 头——网关 Bearer 校验器不认 dashboard JWT
    /// 会以 invalid_token 拒绝）。所有 tools/call 统一注入 _model_id_（modelName ?? DefaultModelId，
    /// 网关要求）。token 过期(401) → ReAuthenticateAsync 重登并重试一次（对照 call_remote oauth 分支）；
    /// 403 视为权限/凭据失效，报错引导重新登录。
    /// </summary>
    private async Task<JsonElement> CallMcphubToolAsync(
        string remoteToolName, Dictionary<string, object> arguments, string? modelName, CancellationToken ct)
    {
        // 网关要求所有 tools/call 带 _model_id_；调用方未显式提供时用默认值。
        arguments["_model_id_"] = string.IsNullOrEmpty(modelName) ? DefaultModelId : modelName;

        var token = await _mcphubAuth.GetAccessTokenAsync(ct);

        var body = new
        {
            jsonrpc = "2.0",
            id = Interlocked.Increment(ref _jsonRpcId),
            method = "tools/call",
            @params = new { name = remoteToolName, arguments },
        };
        var url = $"{McphubBaseUrl}{McphubEndpoint}";
        var jsonBody = JsonSerializer.Serialize(body, JsonOptions);

        using var request = BuildMcphubRequest(url, jsonBody, token);
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

        // 401 → 强制重登并重试一次（对照 call_remote oauth 分支）。
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            token = await _mcphubAuth.ReAuthenticateAsync(ct);
            using var retryRequest = BuildMcphubRequest(url, jsonBody, token);
            using var retryResponse = await _httpClient.SendAsync(retryRequest, HttpCompletionOption.ResponseHeadersRead, ct);
            retryResponse.EnsureSuccessStatusCode();
            var retryResult = await ParseMcpResponseAsync(retryResponse, ct);
            ThrowIfMcpError(retryResult);
            return retryResult;
        }

        if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
        {
            throw new McpException(
                "mcphub 鉴权被拒（HTTP 403）。token 可能已失效或权限不足，请在「专家→连接器」" +
                "重新登录 mcphub 后重试。");
        }

        response.EnsureSuccessStatusCode();

        var result = await ParseMcpResponseAsync(response, ct);
        ThrowIfMcpError(result);
        return result;
    }

    private static HttpRequestMessage BuildMcphubRequest(string url, string jsonBody, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Add("x-auth-token", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Add("X-Request-Channel", "AGENT");
        request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        return request;
    }

    /// <summary>远端 tools/call 返回 isError=true 时，聚合 content[].text 抛 McpException。</summary>
    private static void ThrowIfMcpError(JsonElement result)
    {
        if (result.TryGetProperty("isError", out var isError) && isError.GetBoolean())
        {
            var errorTexts = new List<string>();
            if (result.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in content.EnumerateArray())
                {
                    if (item.TryGetProperty("type", out var type) && type.GetString() == "text")
                        errorTexts.Add(item.GetProperty("text").GetString() ?? "");
                }
            }
            throw new McpException(string.Join("; ", errorTexts));
        }
    }

    private static async Task<JsonElement> ParseMcpResponseAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
        JsonElement envelope;

        if (contentType.Contains("event-stream"))
        {
            // mcphub /mcp 是 MCP streamable-HTTP 端点，tools/call 以 SSE 返回：每条 data: 是一整条
            // JSON-RPC envelope（{"result":{content,isError},"jsonrpc","id"}）。取最后一条有效 data。
            var stream = await response.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream);
            JsonElement? lastObj = null;

            while (true)
            {
                // ReadLineAsync 在流结束时返回 null（避免在 async 方法中读 EndOfStream —— CA2024）。
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

        // 统一处理 JSON-RPC envelope（SSE 与 JSON 同形）。对照 sql_query.py：_parse_mcp_payload 得到整条
        // envelope，call_remote 再 envelope.get("result", envelope) 取 result。之前 SSE 分支直接返回 envelope
        // （未取 .result）→ ExtractResultText 在顶层找不到 content → 返回 null → asyncSqlQuery 的 jobId 为空。
        // 此处在两条分支后统一提取 .result / 检查 .error。
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

    /// <summary>解析 asyncSqlQuery 返回的 JSON 文本为 jobId；errMsg 非空时返回错误。</summary>
    private static YushuSqlQueryResult ParseSqlQueryResult(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new YushuSqlQueryResult { ErrorMessage = "提交成功但返回内容为空" };
        }

        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (root.TryGetProperty("errMsg", out var err) && err.ValueKind == JsonValueKind.String)
            {
                var msg = err.GetString();
                if (!string.IsNullOrEmpty(msg))
                {
                    return new YushuSqlQueryResult { ErrorMessage = msg };
                }
            }
            var jobId = root.TryGetProperty("jobId", out var j) ? j.GetString() : null;
            return new YushuSqlQueryResult { JobId = jobId?.Trim() };
        }
        catch (JsonException)
        {
            // 非预期格式：当作裸 jobId 回退（兼容旧 bolt 网关直接返回 jobId 字符串的形态）。
            return new YushuSqlQueryResult { JobId = text.Trim() };
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
