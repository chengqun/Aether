namespace Acme.Qifu.Domain.Yushu;

/// <summary>异步 SQL 查询提交结果</summary>
public class YushuSqlQueryResult
{
    /// <summary>查询任务编号</summary>
    public string? JobId { get; set; }
    /// <summary>错误信息</summary>
    public string? ErrorMessage { get; set; }
    /// <summary>是否成功</summary>
    public bool IsSuccess => string.IsNullOrEmpty(ErrorMessage);
}

/// <summary>异步任务状态查询结果</summary>
public class YushuTaskStatusResult
{
    /// <summary>任务状态：SUCCESS, RUNNING, CANCEL, FAIL, UNKNOWN</summary>
    public string Status { get; set; } = string.Empty;
    /// <summary>结果下载地址（SUCCESS 时有值）</summary>
    public string? ResultUrl { get; set; }
    /// <summary>结果总行数</summary>
    public int TotalRows { get; set; }
    /// <summary>错误信息</summary>
    public string? ErrorMessage { get; set; }
    /// <summary>状态消息</summary>
    public string? Message { get; set; }
    /// <summary>是否完成（终态）</summary>
    public bool IsCompleted => Status is "SUCCESS" or "CANCEL" or "FAIL";
    /// <summary>是否成功</summary>
    public bool IsSuccess => Status == "SUCCESS";
}

/// <summary>查询结果下载结果</summary>
public class YushuDownloadResult
{
    /// <summary>本地文件路径</summary>
    public string? FilePath { get; set; }
    /// <summary>文件大小（字节）</summary>
    public long FileSize { get; set; }
    /// <summary>错误信息</summary>
    public string? ErrorMessage { get; set; }
    /// <summary>是否成功</summary>
    public bool IsSuccess => string.IsNullOrEmpty(ErrorMessage);
}

/// <summary>查询结果预览</summary>
public class YushuPreviewResult
{
    /// <summary>预览内容（Markdown 表格格式）</summary>
    public string Content { get; set; } = string.Empty;
    /// <summary>总行数</summary>
    public int TotalRows { get; set; }
    /// <summary>总列数</summary>
    public int TotalColumns { get; set; }
    /// <summary>错误信息</summary>
    public string? ErrorMessage { get; set; }
    /// <summary>是否成功</summary>
    public bool IsSuccess => string.IsNullOrEmpty(ErrorMessage);
}

/// <summary>清理过期结果文件结果</summary>
public class YushuCleanResult
{
    /// <summary>删除的文件数量</summary>
    public int RemovedCount { get; set; }
    /// <summary>删除的文件总大小（字节）</summary>
    public long RemovedSize { get; set; }
    /// <summary>删除的文件列表</summary>
    public List<string> RemovedFiles { get; set; } = new();
    /// <summary>错误信息</summary>
    public string? ErrorMessage { get; set; }
    /// <summary>是否成功</summary>
    public bool IsSuccess => string.IsNullOrEmpty(ErrorMessage);
}

/// <summary>任务执行日志拉取结果（失败诊断用）</summary>
public class YushuSqlLogResult
{
    /// <summary>本地日志文件路径（<jobId>.log）</summary>
    public string? FilePath { get; set; }
    /// <summary>日志总行数</summary>
    public int TotalLines { get; set; }
    /// <summary>日志全部行</summary>
    public List<string> Lines { get; set; } = new();
    /// <summary>错误信息</summary>
    public string? ErrorMessage { get; set; }
    /// <summary>是否成功</summary>
    public bool IsSuccess => string.IsNullOrEmpty(ErrorMessage);
}

/// <summary>权限工单发起结果（模型安全等级申请 / 表权限申请共用）</summary>
public class YushuApplyResult
{
    /// <summary>远端返回原文</summary>
    public string? Result { get; set; }
    /// <summary>错误信息</summary>
    public string? ErrorMessage { get; set; }
    /// <summary>是否成功</summary>
    public bool IsSuccess => string.IsNullOrEmpty(ErrorMessage);
}
