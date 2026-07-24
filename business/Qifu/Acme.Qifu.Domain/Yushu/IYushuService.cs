namespace Acme.Qifu.Domain.Yushu;

/// <summary>
/// 毓数平台 BOLT MCP API 调用服务，支持异步 SQL 查询、状态轮询、结果下载与预览。
/// </summary>
public interface IYushuService
{
    /// <summary>提交异步 SQL 查询，返回 jobId。</summary>
    Task<YushuSqlQueryResult> AsyncSqlQueryAsync(string sql, string modelName, CancellationToken ct = default);

    /// <summary>查询异步任务状态（单次调用，不含轮询）。</summary>
    Task<YushuTaskStatusResult> FetchSqlTaskStatusAsync(string jobId, CancellationToken ct = default);

    /// <summary>下载查询结果文件到本地。</summary>
    Task<YushuDownloadResult> DownloadResultAsync(string resultUrl, string jobId, CancellationToken ct = default);

    /// <summary>预览本地已下载的结果文件内容。</summary>
    Task<YushuPreviewResult> ReadResultAsync(string jobId, int maxRows = 10, int maxColumns = 10, int maxCellSize = 200, CancellationToken ct = default);

    /// <summary>获取查询结果文件的本地存储目录路径。</summary>
    string GetResultsDirectory();

    /// <summary>清理过期的查询结果文件。</summary>
    Task<YushuCleanResult> CleanResultsAsync(int olderThanDays = 7, bool dryRun = false, CancellationToken ct = default);
}
