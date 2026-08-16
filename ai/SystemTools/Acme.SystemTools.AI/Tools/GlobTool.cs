using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Acme.SystemTools.AI.Tools;

public class GlobTool
{
    public class Output
    {
        public List<string> Files { get; set; } = new List<string>();
        public string? Error { get; set; }
    }

    [Description("使用通配符模式查找文件")]
    public async Task<Output> Glob([Description("要匹配的模式（如 *.cs, **/*.txt）")] string pattern, [Description("搜索路径")] string path = "", [Description("最大结果数量")] int? limit = null, CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrEmpty(pattern))
            {
                return new Output { Error = "匹配模式不能为空" };
            }

            var searchPath = string.IsNullOrEmpty(path) ? Directory.GetCurrentDirectory() : Path.GetFullPath(path);

            if (!Directory.Exists(searchPath))
            {
                return new Output { Error = $"目录不存在: {searchPath}" };
            }

            var files = new List<string>();
            var maxResults = limit ?? 100;

            await Task.Run(() =>
            {
                SearchFiles(searchPath, pattern, files, maxResults, cancellationToken);
            }, cancellationToken);

            return new Output
            {
                Files = files
            };
        }
        catch (Exception ex)
        {
            return new Output { Error = $"查找文件失败: {ex.Message}" };
        }
    }

    private void SearchFiles(string directory, string pattern, List<string> results, int maxResults, CancellationToken cancellationToken)
    {
        if (results.Count >= maxResults)
            return;

        try
        {
            var files = Directory.GetFiles(directory);
            var regexPattern = ConvertGlobToRegex(pattern);
            var regex = new Regex(regexPattern, RegexOptions.Compiled | RegexOptions.IgnoreCase);

            foreach (var file in files)
            {
                if (cancellationToken.IsCancellationRequested)
                    return;

                if (results.Count >= maxResults)
                    return;

                var fileName = Path.GetFileName(file);
                if (regex.IsMatch(fileName))
                {
                    results.Add(file);
                }
            }

            var subdirectories = Directory.GetDirectories(directory);
            foreach (var subdir in subdirectories)
            {
                if (cancellationToken.IsCancellationRequested)
                    return;

                var dirName = Path.GetFileName(subdir);
                if (FileSearchHelpers.IsIgnoredDirectory(dirName))
                    continue;

                SearchFiles(subdir, pattern, results, maxResults, cancellationToken);
            }
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private string ConvertGlobToRegex(string globPattern)
    {
        var regexPattern = Regex.Escape(globPattern);
        regexPattern = regexPattern
            .Replace("\\*\\*", ".*")
            .Replace("\\*", "[^\\\\/]*")
            .Replace("\\?", "[^\\\\/]");
        return "^" + regexPattern + "$";
    }
}
