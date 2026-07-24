using System.ComponentModel;
using Acme.Qifu.Domain.Yushu;

namespace Acme.Qifu.AI.Tools;

/// <summary>
/// 毓数平台 SQL 查询工具，供 AI Agent 通过 AIFunctionFactory.Create() 调用。
/// </summary>
public class YushuSqlQueryTool
{
    public class Output
    {
        public bool Success { get; set; }
        public string Result { get; set; } = string.Empty;
        public string? JobId { get; set; }
        public string? ResultUrl { get; set; }
        public string? FilePath { get; set; }
        public long? FileSize { get; set; }
        public int? TotalRows { get; set; }
        public string? Content { get; set; }
        public string? Error { get; set; }
    }

    private readonly IYushuService _yushuService;

    public YushuSqlQueryTool(IYushuService yushuService)
    {
        _yushuService = yushuService;
    }

    [Description("毓数-异步SQL查询，提交SQL后立即返回jobId。第一行通过注释指定引擎（如 --spark safe_lycc）。查询必须加limit，默认1000。")]
    public async Task<Output> AsyncSqlQuery(
        [Description("SQL内容，第一行通过注释指定引擎和集群，如 --doris REPORT_DORIS 或 --spark safe_lycc；查询需要加limit，不超过50000; sql中有union时,limit要加在union外层")] string sql,
        [Description("当前 AI Agent 实际使用的精确模型 ID，如 claude-opus-4-7、360/glm-5.1；不要使用通俗名称")] string modelName="360/360-kimi-k2.5",
        CancellationToken cancellationToken = default)
    {
        var result = await _yushuService.AsyncSqlQueryAsync(sql, modelName, cancellationToken);

        if (!result.IsSuccess)
        {
            return new Output { Success = false, Error = result.ErrorMessage };
        }

        return new Output { Success = true, JobId = result.JobId, Result = $"查询已提交，任务编号: {result.JobId}" };
    }

    [Description("毓数-查询异步SQL任务当前状态（单次查询，不轮询）。返回当前状态，若未完成可稍后再次调用查询。")]
    public async Task<Output> FetchSqlTaskStatus(
        [Description("任务编号")] string jobId,
        CancellationToken cancellationToken = default)
    {
        var status = await _yushuService.FetchSqlTaskStatusAsync(jobId, cancellationToken);

        if (!status.IsCompleted)
        {
            return new Output
            {
                Success = false,
                JobId = jobId,
                Result = $"任务仍在运行中，jobId: {jobId}，当前状态: {status.Status}。可稍后再次使用 FetchSqlTaskStatus 查询，或使用 PollSqlTaskStatus 自动轮询等待完成。"
            };
        }

        return FormatTerminalStatus(status, jobId);
    }

    [Description("毓数-自动轮询SQL任务状态直到完成（最长3分钟）。适合用户希望等待查询结果的场景。")]
    public async Task<Output> PollSqlTaskStatus(
        [Description("任务编号")] string jobId,
        CancellationToken cancellationToken = default)
    {
        // 轮询策略：前3次间隔5s，4-10次间隔10s，11次起间隔30s，最长180s
        var maxTotalSeconds = 180;
        var startTime = DateTime.UtcNow;
        var pollCount = 0;

        while (true)
        {
            var status = await _yushuService.FetchSqlTaskStatusAsync(jobId, cancellationToken);

            if (!status.IsCompleted)
            {
                pollCount++;
                var elapsed = (DateTime.UtcNow - startTime).TotalSeconds;
                if (elapsed > maxTotalSeconds)
                {
                    return new Output
                    {
                        Success = false,
                        JobId = jobId,
                        Result = $"轮询超时（{maxTotalSeconds}s），任务仍在运行，jobId: {jobId}，请稍后使用 FetchSqlTaskStatus 再次查询"
                    };
                }

                var delay = pollCount <= 3 ? 5 : pollCount <= 10 ? 10 : 30;
                await Task.Delay(TimeSpan.FromSeconds(delay), cancellationToken);
                continue;
            }

            return FormatTerminalStatus(status, jobId);
        }
    }

    [Description("下载查询结果文件到本地。需要提供 fetchSqlTaskStatus 返回的 resultUrl 和 jobId。")]
    public async Task<Output> DownloadResult(
        [Description("结果下载地址（来自fetchSqlTaskStatus返回的resultUrl）")] string resultUrl,
        [Description("任务编号，用作本地文件名")] string jobId,
        CancellationToken cancellationToken = default)
    {
        var result = await _yushuService.DownloadResultAsync(resultUrl, jobId, cancellationToken);

        if (!result.IsSuccess)
        {
            return new Output { Success = false, Error = result.ErrorMessage };
        }

        return new Output
        {
            Success = true,
            JobId = jobId,
            FilePath = result.FilePath,
            FileSize = result.FileSize,
            Result = $"结果已下载到: {result.FilePath}（大小: {FormatFileSize(result.FileSize)}）"
        };
    }

    [Description("预览本地已下载的查询结果文件，以Markdown表格展示。")]
    public async Task<Output> ReadResult(
        [Description("任务编号")] string jobId,
        [Description("最多预览行数，默认10")] int maxRows = 10,
        [Description("最多预览列数，默认10")] int maxColumns = 10,
        [Description("单元格值最大字符数，超出截断并提示，默认200")] int maxCellSize = 200,
        CancellationToken cancellationToken = default)
    {
        var result = await _yushuService.ReadResultAsync(jobId, maxRows, maxColumns, maxCellSize, cancellationToken);

        if (!result.IsSuccess)
        {
            return new Output { Success = false, Error = result.ErrorMessage };
        }

        return new Output
        {
            Success = true,
            JobId = jobId,
            Content = result.Content,
            TotalRows = result.TotalRows,
            Result = $"{result.Content}\n\n总行数: {result.TotalRows}，总列数: {result.TotalColumns}"
        };
    }

    [Description("清理过期的查询结果文件。按天数删除~/.yushu/results/下超过指定天数的文件。")]
    public async Task<Output> CleanResults(
        [Description("删除多少天前的文件，默认7")] int olderThanDays = 7,
        [Description("仅列出将删除的文件，不实际删除")] bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        var result = await _yushuService.CleanResultsAsync(olderThanDays, dryRun, cancellationToken);

        if (!result.IsSuccess)
        {
            return new Output { Success = false, Error = result.ErrorMessage };
        }

        var sb = new System.Text.StringBuilder();
        if (result.RemovedCount == 0)
        {
            sb.Append("没有超过").Append(olderThanDays).Append("天的文件需要清理");
        }
        else
        {
            sb.Append(dryRun ? "将删除" : "已删除").Append(result.RemovedCount).Append("个文件");
            sb.Append("（大小: ").Append(FormatFileSize(result.RemovedSize)).Append("）");
            if (result.RemovedFiles.Count > 0)
            {
                sb.Append("\n文件列表:\n");
                foreach (var file in result.RemovedFiles)
                {
                    sb.Append("- ").Append(file).Append("\n");
                }
            }
        }

        return new Output { Success = true, Result = sb.ToString() };
    }

    /// <summary>
    /// 格式化终态（SUCCESS/CANCEL/FAIL）结果，FetchSqlTaskStatus 和 PollSqlTaskStatus 共用。
    /// </summary>
    private static Output FormatTerminalStatus(YushuTaskStatusResult status, string jobId)
    {
        return status.Status switch
        {
            "SUCCESS" => new Output
            {
                Success = true,
                JobId = jobId,
                TotalRows = status.TotalRows,
                ResultUrl = status.ResultUrl,
                Result = $"查询成功！{status.TotalRows} 行{(!string.IsNullOrEmpty(status.ResultUrl) ? $"，结果下载地址: {status.ResultUrl}" : "")}"
            },
            "CANCEL" => new Output
            {
                Success = false,
                JobId = jobId,
                Result = "任务被取消（可能超时）"
            },
            "FAIL" => new Output
            {
                Success = false,
                JobId = jobId,
                Error = status.ErrorMessage ?? status.Message,
                Result = $"查询失败: {status.ErrorMessage ?? status.Message ?? "未知错误"}"
            },
            _ => new Output
            {
                Success = false,
                JobId = jobId,
                Error = status.ErrorMessage ?? status.Message,
                Result = $"未知状态: {status.Status}，{status.ErrorMessage ?? status.Message ?? ""}"
            }
        };
    }

    private static string FormatFileSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        return $"{bytes / (1024.0 * 1024.0):F1} MB";
    }
}
