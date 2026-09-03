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

    /// <summary>拉取任务执行日志（失败诊断用），全量写入 <jobId>.log 并返回全部行。</summary>
    /// <param name="jobId">任务编号</param>
    Task<YushuSqlLogResult> SqlLogAsync(string jobId, CancellationToken ct = default);

    /// <summary>发起【毓数表查询-模型安全等级申请】工单（表缺少大模型查询(*)授权 / 模型等级不足时使用）。</summary>
    /// <param name="modelName">当前大模型名称（如 claude-opus-4-7 / glm-4）；经 mcphub 网关带 _model_id_，模型安全等级申请必填。</param>
    Task<YushuApplyResult> ApplyModelUsageAuthorityAsync(
        string dataSource, string tables, string modelLevel, string purpose, int selectLimit,
        string? modelName = null, CancellationToken ct = default);

    /// <summary>发起【毓数表权限申请】工单（库表未授权 / 缺少表查询权限时使用）。</summary>
    /// <param name="modelName">当前大模型名称（可选，经 mcphub 网关带 _model_id_）。</param>
    Task<YushuApplyResult> ApplyTableAuthorityAsync(
        string tables, string authType, string? dataSource = null, string? reason = null,
        bool hdfsAuth = false, string? modelName = null, CancellationToken ct = default);
}
