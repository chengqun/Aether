namespace Acme.Qifu.Domain.CloudDocs;

/// <summary>
/// 360Teams 云文档上传服务：通过 web 通道（sid cookie）上传本地文件 / 文件夹到云文档空间。
/// </summary>
public interface ICloudDocsService
{
    /// <summary>列出指定文件夹下的子项（folderGuid=null 表示根目录）</summary>
    Task<CloudDocsListResult> ListChildrenAsync(string? folderGuid, CancellationToken ct = default);

    /// <summary>在指定父文件夹下创建子文件夹，返回新文件夹 guid</summary>
    Task<string?> CreateFolderAsync(string? parentGuid, string name, CancellationToken ct = default);

    /// <summary>解析目标父文件夹 guid：reference 可为 guid / 文件夹 URL / 根目录文件夹名（不存在则创建）</summary>
    Task<string?> ResolveParentFolderGuidAsync(string reference, bool createIfMissing = true, CancellationToken ct = default);

    /// <summary>上传单个本地文件到指定文件夹，返回上传结果</summary>
    Task<CloudDocsUploadFileResult> UploadFileAsync(string localPath, string folderGuid, string? displayName = null, CancellationToken ct = default);

    /// <summary>递归上传本地文件夹到云文档。dryRun=true 仅规划不实际上传。</summary>
    Task<CloudDocsUploadSummary> UploadFolderAsync(string root, string parentReference, string? rootName = null,
        bool dryRun = false, bool skipExisting = true, int uploadRetries = 2, CancellationToken ct = default);
}
