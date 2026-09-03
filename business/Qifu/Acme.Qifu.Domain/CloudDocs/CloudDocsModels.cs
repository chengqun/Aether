namespace Acme.Qifu.Domain.CloudDocs;

/// <summary>
/// 云文档环境（prod / sit）。
/// </summary>
public enum CloudDocsEnv
{
    /// <summary>生产环境：doc.360teams.com</summary>
    Prod,
    /// <summary>测试环境：sit-doc.sk.360shuke.com</summary>
    Sit
}

/// <summary>
/// 云文档登录结果。
/// </summary>
public class CloudDocsLoginResult
{
    /// <summary>是否成功捕获并验证 sid</summary>
    public bool Success { get; set; }

    /// <summary>捕获到的 sid（已存入缓存，通常不外露）</summary>
    public string? Sid { get; set; }

    /// <summary>结果消息</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>错误信息</summary>
    public string? Error { get; set; }
}

/// <summary>
/// 云文档目录项（文件或文件夹）。
/// </summary>
public class CloudDocsItem
{
    public string? Guid { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool IsFolder { get; set; }
    public long FileSize { get; set; }
}

/// <summary>
/// 云文档目录列表结果。
/// </summary>
public class CloudDocsListResult
{
    public bool Success { get; set; }
    public List<CloudDocsItem> Items { get; set; } = new();
    public string? Error { get; set; }
    public bool IsSuccess => string.IsNullOrEmpty(Error);
}

/// <summary>
/// 单个文件上传结果。
/// </summary>
public class CloudDocsUploadFileResult
{
    public bool Success { get; set; }
    public string? Name { get; set; }
    public string? Guid { get; set; }
    public string? RelativePath { get; set; }
    /// <summary>ok / skipped / error</summary>
    public string Status { get; set; } = string.Empty;
    public long Size { get; set; }
    public string? Error { get; set; }
}

/// <summary>
/// 文件夹递归上传汇总结果。
/// </summary>
public class CloudDocsUploadSummary
{
    public bool Success { get; set; }
    public string? RootFolderGuid { get; set; }
    public int TotalFiles { get; set; }
    public int Uploaded { get; set; }
    public int Skipped { get; set; }
    public int Failed { get; set; }
    public int FoldersCreated { get; set; }
    public List<CloudDocsUploadFileResult> Files { get; set; } = new();
    public string? Error { get; set; }
}
