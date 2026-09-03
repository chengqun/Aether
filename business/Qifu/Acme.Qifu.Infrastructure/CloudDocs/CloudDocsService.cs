using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Acme.Qifu.Domain.CloudDocs;

namespace Acme.Qifu.Infrastructure.CloudDocs;

/// <summary>
/// 360Teams 云文档上传服务：通过 web 通道（sid cookie）上传本地文件 / 文件夹。
/// 对应 Python 版 web_folder_upload.py 的 WebFolderClient + web_upload.py 的 WebUploader。
/// 上传走浏览器三步直传：post_policy → MinIO → selfCallback 注册。
/// </summary>
public class CloudDocsService : ICloudDocsService
{
    private const string CreateFilePath = "/api/v1/files";
    private const string ListFilesPath = "/api/v1/files";
    private const string PostPolicyPath = "/api/v1/files/upload/post_policy";
    private const int MaxUploadFilenameLength = 80;
    private static readonly HashSet<string> SkipNames = new() { ".DS_Store", "Thumbs.db" };
    private static readonly Regex UrlGUIDRe = new("^[A-Za-z0-9_-]{8,}$", RegexOptions.Compiled);
    private static readonly Regex BareGuidRe = new("^[A-Za-z0-9_-]{16,}$", RegexOptions.Compiled);

    private static readonly Dictionary<string, string> MimeOverrides = new(StringComparer.OrdinalIgnoreCase)
    {
        [".md"] = "text/markdown",
        [".markdown"] = "text/markdown",
        [".txt"] = "text/plain",
        [".csv"] = "text/csv",
        [".doc"] = "application/msword",
        [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        [".xls"] = "application/vnd.ms-excel",
        [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        [".ppt"] = "application/vnd.ms-powerpoint",
        [".pptx"] = "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        [".zip"] = "application/zip",
        [".xmind"] = "application/octet-stream",
    };

    private readonly HttpClient _httpClient;
    private readonly ICloudDocsAuthService _authService;
    private readonly int _timeoutSeconds;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public CloudDocsService(HttpClient httpClient, ICloudDocsAuthService authService, int timeoutSeconds = 60)
    {
        _httpClient = httpClient;
        _httpClient.Timeout = TimeSpan.FromSeconds(Math.Max(timeoutSeconds, 60));
        _authService = authService;
        _timeoutSeconds = timeoutSeconds;
    }

    private string BaseUrl => _authService.WebBaseUrl.TrimEnd('/');

    /// <inheritdoc />
    public async Task<CloudDocsListResult> ListChildrenAsync(string? folderGuid, CancellationToken ct = default)
    {
        var path = folderGuid != null ? $"{ListFilesPath}?folder={Uri.EscapeDataString(folderGuid)}" : ListFilesPath;
        var json = await RequestJsonAsync(path, HttpMethod.Get, null, ct);
        if (json == null) return new CloudDocsListResult { Error = "请求失败" };
        return new CloudDocsListResult { Success = true, Items = ParseItems(json.Value) };
    }

    /// <inheritdoc />
    public async Task<string?> CreateFolderAsync(string? parentGuid, string name, CancellationToken ct = default)
    {
        var body = new Dictionary<string, object> { ["name"] = name, ["type"] = "folder" };
        if (!string.IsNullOrEmpty(parentGuid)) body["folder"] = parentGuid!;
        var json = await RequestJsonAsync(CreateFilePath, HttpMethod.Post, body, ct);
        if (json == null) return null;
        return json.Value.TryGetProperty("guid", out var g) ? g.GetString() : null;
    }

    /// <inheritdoc />
    public async Task<string?> ResolveParentFolderGuidAsync(string reference, bool createIfMissing = true, CancellationToken ct = default)
    {
        var guid = ParseFolderGuid(reference);
        if (guid != null) return guid;

        var name = reference.Trim();
        if (string.IsNullOrEmpty(name)) return null;

        var existing = await FindChildFolderAsync(null, name, ct);
        if (existing != null) return existing;

        if (!createIfMissing) return null;
        return await CreateFolderAsync(null, name, ct);
    }

    /// <inheritdoc />
    public async Task<CloudDocsUploadFileResult> UploadFileAsync(string localPath, string folderGuid, string? displayName = null, CancellationToken ct = default)
    {
        var result = new CloudDocsUploadFileResult { Name = displayName ?? Path.GetFileName(localPath) };
        try
        {
            if (!File.Exists(localPath))
            {
                result.Error = $"本地文件不存在: {localPath}";
                return result;
            }

            var data = await File.ReadAllBytesAsync(localPath, ct);
            var filename = displayName ?? Path.GetFileName(localPath);
            var mime = GuessMime(filename);

            var cookie = await GetCookieOrThrowAsync(ct);
            var policy = await PostPolicyAsync(cookie, folderGuid, filename, data.Length, mime, ct);
            if (policy == null)
            {
                result.Error = "获取上传策略 post_policy 失败";
                return result;
            }

            var uploadUrl = policy.Value.GetProperty("uploadUrl");
            await UploadToStorageAsync(cookie, uploadUrl, filename, data, ct);
            var callbackSpec = policy.Value.GetProperty("selfCallback");
            await CallbackAsync(cookie, callbackSpec, filename, data.Length, ct);

            result.Success = true;
            result.Status = "ok";
            result.Size = data.Length;
            result.Guid = policy.Value.TryGetProperty("guid", out var g) ? g.GetString() : null;
            return result;
        }
        catch (Exception ex)
        {
            result.Error = ex.Message;
            return result;
        }
    }

    /// <inheritdoc />
    public async Task<CloudDocsUploadSummary> UploadFolderAsync(string root, string parentReference, string? rootName = null,
        bool dryRun = false, bool skipExisting = true, int uploadRetries = 2, CancellationToken ct = default)
    {
        var summary = new CloudDocsUploadSummary();

        var rootDir = new DirectoryInfo(root);
        if (!rootDir.Exists)
        {
            summary.Error = $"--root 必须是目录: {root}";
            return summary;
        }

        var cookie = dryRun ? null : await GetCookieOrThrowAsync(ct);

        // 解析目标父文件夹
        string? parentGuid;
        try
        {
            parentGuid = dryRun
                ? (ParseFolderGuid(parentReference) ?? parentReference)
                : await ResolveParentFolderGuidAsync(parentReference, true, ct);
        }
        catch (Exception ex)
        {
            summary.Error = $"解析云文档目标文件夹失败: {ex.Message}";
            return summary;
        }
        if (string.IsNullOrEmpty(parentGuid))
        {
            summary.Error = "需要 parent（目标文件夹 guid / URL / 根目录名）";
            return summary;
        }

        var rootFolderName = string.IsNullOrWhiteSpace(rootName) ? rootDir.Name : rootName!;
        var (dirs, files) = IterTree(rootDir);

        if (dryRun)
        {
            summary.Success = true;
            summary.TotalFiles = files.Count;
            summary.Files = files.Select(f => new CloudDocsUploadFileResult
            {
                Name = f.Name,
                RelativePath = GetRelativePath(rootDir, f),
                Status = "planned",
                Size = f.Length,
            }).ToList();
            return summary;
        }

        // 创建/复用根文件夹
        string rootGuid;
        var reusedRoot = await FindChildFolderAsync(parentGuid, rootFolderName, ct);
        if (reusedRoot != null)
        {
            rootGuid = reusedRoot;
        }
        else
        {
            rootGuid = await CreateFolderAsync(parentGuid, rootFolderName, ct) ?? throw new InvalidOperationException("创建根文件夹失败");
            summary.FoldersCreated++;
        }
        summary.RootFolderGuid = rootGuid;

        var folderMap = new Dictionary<string, string>(StringComparer.Ordinal) { [rootDir.FullName] = rootGuid };

        // 创建子文件夹
        foreach (var dir in dirs)
        {
            ct.ThrowIfCancellationRequested();
            var parentGuidForDir = folderMap[dir.Parent!.FullName];
            var existing = await FindChildFolderAsync(parentGuidForDir, dir.Name, ct);
            string guid;
            if (existing != null)
            {
                guid = existing;
            }
            else
            {
                guid = await CreateFolderAsync(parentGuidForDir, dir.Name, ct) ?? throw new InvalidOperationException($"创建文件夹失败: {dir.Name}");
                summary.FoldersCreated++;
            }
            folderMap[dir.FullName] = guid;
        }

        // 上传文件
        var usedNamesByParent = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        summary.TotalFiles = files.Count;

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            var parentGuidForFile = folderMap[file.DirectoryName!];
            var relativePath = GetRelativePath(rootDir, file);
            var uploadName = UniqueUploadName(file.Name, usedNamesByParent.GetOrAdd(file.DirectoryName!, _ => new HashSet<string>(StringComparer.Ordinal)));

            // 跳过同名同大小
            if (skipExisting)
            {
                var existingFile = await FindChildFileAsync(parentGuidForFile, uploadName, ct);
                if (existingFile != null && FileSizeMatches(existingFile.Value, file.Length))
                {
                    summary.Skipped++;
                    summary.Files.Add(new CloudDocsUploadFileResult
                    {
                        Name = file.Name,
                        RelativePath = relativePath,
                        Status = "skipped",
                        Size = file.Length,
                        Guid = existingFile.Value.TryGetProperty("guid", out var g) ? g.GetString() : null,
                    });
                    continue;
                }
            }

            // 重试上传
            Exception? lastError = null;
            for (int attempt = 0; attempt <= uploadRetries; attempt++)
            {
                try
                {
                    var outcome = await UploadFileInternalAsync(cookie!, parentGuidForFile, file, uploadName, ct);
                    summary.Uploaded++;
                    summary.Files.Add(new CloudDocsUploadFileResult
                    {
                        Success = true,
                        Name = file.Name,
                        RelativePath = relativePath,
                        Status = "ok",
                        Size = file.Length,
                        Guid = outcome,
                    });
                    lastError = null;
                    break;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    lastError = ex;
                    if (attempt < uploadRetries)
                        await Task.Delay(TimeSpan.FromSeconds(Math.Min(Math.Pow(2, attempt), 5)), ct);
                }
            }

            if (lastError != null)
            {
                summary.Failed++;
                summary.Files.Add(new CloudDocsUploadFileResult
                {
                    Name = file.Name,
                    RelativePath = relativePath,
                    Status = "error",
                    Size = file.Length,
                    Error = lastError.Message,
                });
            }
        }

        summary.Success = summary.Failed == 0;
        return summary;
    }

    // ══════════════════════════════════════════════════════════════
    // 内部：HTTP 请求
    // ══════════════════════════════════════════════════════════════

    private async Task<string> GetCookieOrThrowAsync(CancellationToken ct)
    {
        var sid = await _authService.GetSidAsync(ct);
        if (string.IsNullOrEmpty(sid))
            throw new InvalidOperationException("未找到云文档 sid，请先调用 LoginCloudDocs 登录。");
        return $"sid={sid}";
    }

    /// <summary>JSON GET/POST 请求（带 sid cookie + Origin/Referer/XHR 头）</summary>
    private async Task<JsonElement?> RequestJsonAsync(string path, HttpMethod method, object? body, CancellationToken ct)
    {
        var cookie = await GetCookieOrThrowAsync(ct);
        var url = BaseUrl + path;
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Add("Accept", "application/json, text/plain, */*");
        request.Headers.Add("Cookie", cookie);
        request.Headers.Add("Origin", BaseUrl);
        request.Headers.Add("Referer", BaseUrl + "/");
        request.Headers.Add("X-Requested-With", "XMLHttpRequest");

        if (body != null)
        {
            request.Content = new StringContent(JsonSerializer.Serialize(body, JsonOptions), Encoding.UTF8);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }

        using var response = await _httpClient.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"{method} {path} -> HTTP {(int)response.StatusCode}: {Truncate(text, 500)}");
        if (string.IsNullOrWhiteSpace(text)) return null;
        try { return JsonDocument.Parse(text).RootElement.Clone(); }
        catch { return null; }
    }

    private async Task<string?> FindChildFolderAsync(string? parentGuid, string name, CancellationToken ct)
    {
        var items = await ListChildrenInternalAsync(parentGuid, ct);
        foreach (var item in items)
        {
            if (item.TryGetProperty("name", out var n) && n.GetString() == name
                && item.TryGetProperty("isFolder", out var f) && f.GetBoolean()
                && item.TryGetProperty("guid", out var g))
                return g.GetString();
        }
        return null;
    }

    private async Task<JsonElement?> FindChildFileAsync(string? parentGuid, string name, CancellationToken ct)
    {
        var items = await ListChildrenInternalAsync(parentGuid, ct);
        foreach (var item in items)
        {
            if (item.TryGetProperty("name", out var n) && n.GetString() == name
                && (!item.TryGetProperty("isFolder", out var f) || !f.GetBoolean()))
                return item;
        }
        return null;
    }

    private async Task<List<JsonElement>> ListChildrenInternalAsync(string? parentGuid, CancellationToken ct)
    {
        var cookie = await GetCookieOrThrowAsync(ct);
        var path = parentGuid != null ? $"{ListFilesPath}?folder={Uri.EscapeDataString(parentGuid)}" : ListFilesPath;
        using var request = new HttpRequestMessage(HttpMethod.Get, BaseUrl + path);
        request.Headers.Add("Accept", "application/json, text/plain, */*");
        request.Headers.Add("Cookie", cookie);
        request.Headers.Add("Origin", BaseUrl);
        request.Headers.Add("Referer", BaseUrl + "/");
        request.Headers.Add("X-Requested-With", "XMLHttpRequest");

        using var response = await _httpClient.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) return new List<JsonElement>();
        var text = await response.Content.ReadAsStringAsync(ct);
        if (string.IsNullOrWhiteSpace(text)) return new List<JsonElement>();

        JsonElement root;
        try { root = JsonDocument.Parse(text).RootElement.Clone(); }
        catch { return new List<JsonElement>(); }

        if (root.ValueKind == JsonValueKind.Array)
            return root.EnumerateArray().ToList();

        if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (var key in new[] { "data", "files", "list", "items", "results" })
            {
                if (root.TryGetProperty(key, out var arr) && arr.ValueKind == JsonValueKind.Array)
                    return arr.EnumerateArray().ToList();
            }
        }
        return new List<JsonElement>();
    }

    /// <summary>第 1 步：获取上传策略（post_policy）</summary>
    private async Task<JsonElement?> PostPolicyAsync(string cookie, string parentGuid, string filename, int size, string mime, CancellationToken ct)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(filename), "encodedFileName");

        using var request = new HttpRequestMessage(HttpMethod.Post, BaseUrl + PostPolicyPath);
        request.Headers.Add("Accept", "application/json, text/plain, */*");
        request.Headers.Add("Cookie", cookie);
        request.Headers.Add("Origin", BaseUrl);
        request.Headers.Add("Referer", $"{BaseUrl}/folder/{parentGuid}");
        request.Headers.Add("X-File-Parent-Guid", parentGuid);
        request.Headers.Add("X-File-Size", size.ToString());
        request.Headers.Add("X-File-Type", mime);
        request.Headers.Add("X-Requested-With", "XMLHttpRequest");
        request.Content = form;

        using var response = await _httpClient.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"post_policy -> HTTP {(int)response.StatusCode}: {Truncate(text, 500)}");
        try { return JsonDocument.Parse(text).RootElement.Clone(); }
        catch { return null; }
    }

    /// <summary>第 2 步：直传文件到 MinIO（file 字段必须在最后）</summary>
    private async Task UploadToStorageAsync(string cookie, JsonElement uploadUrl, string filename, byte[] data, CancellationToken ct)
    {
        var url = uploadUrl.GetProperty("url").GetString()!;
        var formData = uploadUrl.GetProperty("formData");
        var contentType = formData.TryGetProperty("Content-Type", out var ctEl) ? ctEl.GetString() ?? "application/octet-stream" : "application/octet-stream";

        using var form = new MultipartFormDataContent();
        // 所有 formData 字段在前
        foreach (var prop in formData.EnumerateObject())
        {
            form.Add(new StringContent(prop.Value.ToString()), prop.Name);
        }
        // file 字段在最后
        var fileContent = new ByteArrayContent(data);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        form.Add(fileContent, "file", filename);

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Add("Accept", "application/json, text/plain, */*");
        request.Headers.Add("Cookie", cookie);
        request.Headers.Add("Origin", BaseUrl);
        request.Headers.Add("Referer", BaseUrl + "/");
        request.Content = form;

        using var response = await _httpClient.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"upload to storage -> HTTP {(int)response.StatusCode}: {Truncate(text, 500)}");
        }
    }

    /// <summary>第 3 步：回调注册文件</summary>
    private async Task CallbackAsync(string cookie, JsonElement callbackSpec, string filename, int size, CancellationToken ct)
    {
        var path = callbackSpec.GetProperty("path").GetString()!;
        var contentType = callbackSpec.TryGetProperty("contentType", out var ctEl) ? ctEl.GetString() ?? "application/json" : "application/json";

        var body = new Dictionary<string, object>();
        if (callbackSpec.TryGetProperty("body", out var bodyEl) && bodyEl.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in bodyEl.EnumerateObject())
                body[prop.Name] = prop.Value.ToString();
        }
        body.TryAdd("fname", filename);
        body.TryAdd("size", size);

        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        request.Headers.Add("Accept", "application/json, text/plain, */*");
        request.Headers.Add("Cookie", cookie);
        request.Headers.Add("Origin", BaseUrl);
        request.Headers.Add("Referer", BaseUrl + "/");
        request.Content = new StringContent(JsonSerializer.Serialize(body, JsonOptions), Encoding.UTF8);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);

        using var response = await _httpClient.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"callback -> HTTP {(int)response.StatusCode}: {Truncate(text, 500)}");
        }
    }

    /// <summary>单文件上传三步合一（返回 guid），不带 skip/retry，供 UploadFolderAsync 内部调用</summary>
    private async Task<string?> UploadFileInternalAsync(string cookie, string parentGuid, FileInfo file, string uploadName, CancellationToken ct)
    {
        var data = await File.ReadAllBytesAsync(file.FullName, ct);
        var mime = GuessMime(uploadName);

        var policy = await PostPolicyAsync(cookie, parentGuid, uploadName, data.Length, mime, ct)
            ?? throw new InvalidOperationException("获取上传策略 post_policy 失败");
        var uploadUrl = policy.GetProperty("uploadUrl");
        await UploadToStorageAsync(cookie, uploadUrl, uploadName, data, ct);
        var callbackSpec = policy.GetProperty("selfCallback");
        await CallbackAsync(cookie, callbackSpec, uploadName, data.Length, ct);

        return policy.TryGetProperty("guid", out var g) ? g.GetString() : null;
    }

    // ══════════════════════════════════════════════════════════════
    // 辅助
    // ══════════════════════════════════════════════════════════════

    private static List<CloudDocsItem> ParseItems(JsonElement root)
    {
        var items = new List<CloudDocsItem>();
        IEnumerable<JsonElement> source = root.ValueKind == JsonValueKind.Array
            ? root.EnumerateArray()
            : (root.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Array ? d.EnumerateArray()
                : Array.Empty<JsonElement>());
        foreach (var el in source)
        {
            items.Add(new CloudDocsItem
            {
                Guid = el.TryGetProperty("guid", out var g) ? g.GetString() : null,
                Name = el.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : string.Empty,
                IsFolder = el.TryGetProperty("isFolder", out var f) && f.GetBoolean(),
                FileSize = el.TryGetProperty("fileSize", out var fs) ? fs.TryGetInt64(out var v) ? v : 0 : 0,
            });
        }
        return items;
    }

    private static string GuessMime(string filename)
    {
        var ext = Path.GetExtension(filename).ToLowerInvariant();
        if (MimeOverrides.TryGetValue(ext, out var mime)) return mime;
        return "application/octet-stream";
    }

    private static (List<DirectoryInfo> Dirs, List<FileInfo> Files) IterTree(DirectoryInfo root)
    {
        var dirs = new List<DirectoryInfo>();
        var files = new List<FileInfo>();
        foreach (var entry in root.EnumerateFileSystemInfos("*", SearchOption.AllDirectories))
        {
            if (SkipNames.Contains(entry.Name)) continue;
            if (entry is DirectoryInfo d) dirs.Add(d);
            else if (entry is FileInfo f) files.Add(f);
        }
        dirs.Sort((a, b) => string.Compare(a.FullName, b.FullName, StringComparison.Ordinal));
        files.Sort((a, b) => string.Compare(a.FullName, b.FullName, StringComparison.Ordinal));
        return (dirs, files);
    }

    private static string GetRelativePath(DirectoryInfo root, FileSystemInfo file)
    {
        var fullRoot = root.FullName.EndsWith(Path.DirectorySeparatorChar) ? root.FullName : root.FullName + Path.DirectorySeparatorChar;
        return file.FullName.StartsWith(fullRoot, StringComparison.Ordinal)
            ? file.FullName[fullRoot.Length..]
            : file.Name;
    }

    private static bool FileSizeMatches(JsonElement item, long size)
    {
        foreach (var key in new[] { "fileSize", "size" })
        {
            if (!item.TryGetProperty(key, out var el)) continue;
            if (el.TryGetInt64(out var v)) return v == size;
        }
        return false;
    }

    private static string CompactFilename(string name, int limit = MaxUploadFilenameLength)
    {
        if (name.Length <= limit) return name;
        var stem = Path.GetFileNameWithoutExtension(name);
        var suffix = Path.GetExtension(name);
        if (suffix.Length > 0 && suffix.Length < 16)
        {
            var keep = Math.Max(1, limit - suffix.Length);
            return stem[..keep].TrimEnd(' ', '.') + suffix;
        }
        return name[..limit].TrimEnd(' ', '.');
    }

    private static string UniqueUploadName(string name, HashSet<string> used, int limit = MaxUploadFilenameLength)
    {
        var compacted = CompactFilename(name, limit);
        if (!used.Contains(compacted)) { used.Add(compacted); return compacted; }
        var stem = Path.GetFileNameWithoutExtension(compacted);
        var suffix = Path.GetExtension(compacted);
        int index = 1;
        while (true)
        {
            var marker = $" ({index})";
            var keep = Math.Max(1, limit - marker.Length - suffix.Length);
            var candidate = stem[..keep].TrimEnd(' ', '.') + marker + suffix;
            if (!used.Contains(candidate)) { used.Add(candidate); return candidate; }
            index++;
        }
    }

    /// <summary>解析文件夹引用：guid / 文件夹 URL / 根目录名（对应 parse_folder_guid）</summary>
    private static string? ParseFolderGuid(string reference)
    {
        var raw = reference?.Trim();
        if (string.IsNullOrEmpty(raw)) return null;

        if (Uri.TryCreate(raw, UriKind.Absolute, out var uri))
        {
            var query = ParseQueryString(uri.Query);
            foreach (var key in new[] { "folder", "folderGuid", "guid", "parent" })
            {
                if (query.TryGetValue(key, out var candidate) && !string.IsNullOrEmpty(candidate) && UrlGUIDRe.IsMatch(candidate))
                    return candidate;
            }
            var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            for (int i = parts.Length - 1; i >= 0; i--)
            {
                var part = Uri.UnescapeDataString(parts[i]);
                if (UrlGUIDRe.IsMatch(part) && part.ToLowerInvariant() is not ("folder" or "desktop" or "doc" or "files" or "recent"))
                    return part;
            }
            return null;
        }

        if (BareGuidRe.IsMatch(raw) && !raw.Any(char.IsWhiteSpace))
            return raw;
        return null;
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max];

    /// <summary>简易 query string 解析（避免引入 System.Web 依赖）</summary>
    private static Dictionary<string, string> ParseQueryString(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(query)) return result;
        var q = query.StartsWith('?') ? query[1..] : query;
        foreach (var pair in q.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            string key, value;
            if (eq < 0) { key = Uri.UnescapeDataString(pair); value = string.Empty; }
            else { key = Uri.UnescapeDataString(pair[..eq]); value = Uri.UnescapeDataString(pair[(eq + 1)..]); }
            result[key] = value;
        }
        return result;
    }
}

internal static class DictionaryExtensions
{
    public static TValue GetOrAdd<TKey, TValue>(this Dictionary<TKey, TValue> dict, TKey key, Func<TKey, TValue> factory) where TKey : notnull
    {
        if (!dict.TryGetValue(key, out var value))
        {
            value = factory(key);
            dict[key] = value;
        }
        return value;
    }
}
