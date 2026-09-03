using System.ComponentModel;
using Acme.Qifu.Domain.CloudDocs;

namespace Acme.Qifu.AI.Tools;

/// <summary>
/// 360Teams 云文档工具，供 AI Agent 通过 AIFunctionFactory.Create() 调用。
/// 包含浏览器登录（捕获 sid）与本地文件夹/文件上传到云文档空间。
/// </summary>
public class CloudDocsUploadTool
{
    public class Output
    {
        public bool Success { get; set; }
        public string Result { get; set; } = string.Empty;
        public string? Error { get; set; }
        // 上传汇总
        public int? TotalFiles { get; set; }
        public int? Uploaded { get; set; }
        public int? Skipped { get; set; }
        public int? Failed { get; set; }
        public int? FoldersCreated { get; set; }
        public string? RootFolderGuid { get; set; }
        // 单文件/登录
        public string? Guid { get; set; }
        public string? Sid { get; set; }
    }

    private readonly ICloudDocsAuthService _authService;
    private readonly ICloudDocsService _docsService;

    public CloudDocsUploadTool(ICloudDocsAuthService authService, ICloudDocsService docsService)
    {
        _authService = authService;
        _docsService = docsService;
    }

    [Description("360Teams云文档-打开浏览器登录并捕获 sid 会话。半自动:浏览器弹出后由用户完成 SSO 登录,脚本自动读出 sid 并验证保存。sid 存本地缓存,后续上传自动复用,过期才需重登。")]
    public async Task<Output> LoginCloudDocs(
        [Description("等待用户完成登录的超时秒数,默认 300")] int timeoutSeconds = 300,
        [Description("浏览器通道,如 'chrome' 复用本地 Chrome;留空用 Playwright 自带 Chromium")] string? channel = null,
        CancellationToken cancellationToken = default)
    {
        var login = await _authService.LoginAsync(timeoutSeconds, channel, cancellationToken);
        if (!login.Success)
            return new Output { Success = false, Error = login.Error, Result = $"登录失败: {login.Error}" };

        return new Output
        {
            Success = true,
            Sid = login.Sid,
            Result = $"云文档登录成功,sid 已保存({_authService.WebBaseUrl})。后续上传可直接调用 UploadCloudDocsFolder / UploadCloudDocsFile。"
        };
    }

    [Description("360Teams云文档-检查本地缓存的 sid 是否仍然有效(GET /api/v1/files/quota 验证)。")]
    public async Task<Output> CheckCloudDocsLogin(CancellationToken cancellationToken = default)
    {
        var ok = await _authService.IsAuthenticatedAsync(cancellationToken);
        return new Output
        {
            Success = ok,
            Result = ok
                ? $"云文档会话有效({_authService.WebBaseUrl}),可直接上传。"
                : "云文档会话无效或未登录,请先调用 LoginCloudDocs。"
        };
    }

    [Description("360Teams云文档-递归上传本地文件夹到云文档空间。会自动创建同名根文件夹及子目录结构。需先 LoginCloudDocs。")]
    public async Task<Output> UploadCloudDocsFolder(
        [Description("本地要上传的文件夹路径")] string root,
        [Description("云文档目标父文件夹:可以是 guid、文件夹 URL、或根目录下的文件夹名(不存在则自动创建)。默认传到 https://doc.360teams.com/folder/gO3oxONDY2fVovqD")] string parent = "https://doc.360teams.com/folder/gO3oxONDY2fVovqD",
        [Description("云文档中根文件夹名,默认用本地文件夹名")] string? rootName = null,
        [Description("是否跳过同名同大小的已有文件,默认 true")] bool skipExisting = true,
        [Description("是否仅规划不实际上传(dry-run),默认 false")] bool dryRun = false,
        [Description("每个文件上传失败的重试次数,默认 2")] int uploadRetries = 2,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var summary = await _docsService.UploadFolderAsync(root, parent, rootName, dryRun, skipExisting, uploadRetries, cancellationToken);

            if (!string.IsNullOrEmpty(summary.Error))
                return new Output { Success = false, Error = summary.Error, Result = summary.Error };

            return new Output
            {
                Success = summary.Success,
                RootFolderGuid = summary.RootFolderGuid,
                TotalFiles = summary.TotalFiles,
                Uploaded = summary.Uploaded,
                Skipped = summary.Skipped,
                Failed = summary.Failed,
                FoldersCreated = summary.FoldersCreated,
                Result = dryRun
                    ? $"dry-run 规划完成,待上传文件 {summary.TotalFiles} 个。去掉 dryRun=true 执行实际上传。"
                    : $"上传完成: 共 {summary.TotalFiles} 个文件,成功 {summary.Uploaded},跳过 {summary.Skipped},失败 {summary.Failed};新建文件夹 {summary.FoldersCreated} 个{(string.IsNullOrEmpty(summary.RootFolderGuid) ? "" : $",根文件夹 guid={summary.RootFolderGuid}")}。{(summary.Failed > 0 ? "失败文件详见 Error。" : "")}"
            };
        }
        catch (Exception ex)
        {
            return new Output { Success = false, Error = ex.Message, Result = $"上传文件夹失败: {ex.Message}" };
        }
    }

    [Description("360Teams云文档-上传单个本地文件到指定文件夹。需先 LoginCloudDocs。")]
    public async Task<Output> UploadCloudDocsFile(
        [Description("本地文件路径")] string filePath,
        [Description("目标云文档文件夹 guid")] string folderGuid,
        [Description("云文档中的显示文件名,默认用本地文件名")] string? name = null,
        CancellationToken cancellationToken = default)
    {
        var result = await _docsService.UploadFileAsync(filePath, folderGuid, name, cancellationToken);
        return new Output
        {
            Success = result.Success,
            Guid = result.Guid,
            Error = result.Error,
            Result = result.Success
                ? $"上传成功: {result.Name} -> guid {result.Guid}"
                : $"上传失败: {result.Error}"
        };
    }

    [Description("360Teams云文档-列出指定文件夹下的子项(文件夹/文件),用于查找目标 folder guid。parentGuid 留空列根目录。")]
    public async Task<Output> ListCloudDocsFolders(
        [Description("父文件夹 guid,留空列根目录")] string? parentGuid = null,
        CancellationToken cancellationToken = default)
    {
        var list = await _docsService.ListChildrenAsync(parentGuid, cancellationToken);
        if (!list.IsSuccess)
            return new Output { Success = false, Error = list.Error, Result = $"列目录失败: {list.Error}" };

        var folders = list.Items.Where(i => i.IsFolder).ToList();
        var lines = folders.Select(f => $"- {f.Name}  guid={f.Guid}");
        return new Output
        {
            Success = true,
            Result = $"共 {folders.Count} 个文件夹:\n" + string.Join("\n", lines)
        };
    }
}
