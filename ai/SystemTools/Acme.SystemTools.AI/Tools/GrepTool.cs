using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;

namespace Acme.SystemTools.AI.Tools;

public class GrepTool
{
    public enum OutputMode
    {
        [Description("输出匹配的内容和行号")]
        Content,
        [Description("输出匹配的文件名")]
        FilesWithMatches,
        [Description("输出匹配的行数")]
        Count
    }

    public class Output
    {
        public OutputMode Mode { get; set; }
        public List<string> Files { get; set; } = new List<string>();
        public string Content { get; set; } = string.Empty;
        public int? NumMatches { get; set; }
        public string? Error { get; set; }
    }

    private class SearchResult
    {
        public string FilePath { get; set; } = string.Empty;
        public int MatchCount { get; set; }
        public List<LineMatch> LineMatches { get; set; } = new List<LineMatch>();
    }

    private class LineMatch
    {
        public int LineNumber { get; set; }
        public string Line { get; set; } = string.Empty;
    }

    [Description("在文件中搜索匹配的内容，支持正则表达式")]
    public async Task<Output> Grep([Description("要搜索的模式（正则表达式）")] string pattern, [Description("搜索路径（文件或目录）")] string path, [Description("文件扩展名过滤（如 *.cs, *.py）")] string? glob = null, [Description("输出模式")] OutputMode? outputMode = null, [Description("是否忽略大小写")] bool? caseInsensitive = null, [Description("最大结果数量")] int? limit = null, CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrEmpty(pattern))
            {
                return new Output { Error = "搜索模式不能为空" };
            }

            var searchPath = string.IsNullOrEmpty(path) ? Directory.GetCurrentDirectory() : Path.GetFullPath(path);

            if (!Directory.Exists(searchPath) && !File.Exists(searchPath))
            {
                return new Output { Error = $"路径不存在: {searchPath}" };
            }

            var mode = outputMode ?? OutputMode.FilesWithMatches;
            var results = new List<SearchResult>();

            var regexOptions = RegexOptions.Compiled;
            if (caseInsensitive ?? false)
            {
                regexOptions |= RegexOptions.IgnoreCase;
            }

            var regex = new Regex(pattern, regexOptions);

            Func<string, bool> fileFilter = f => true;
            if (!string.IsNullOrEmpty(glob))
            {
                var globPattern = "^" + Regex.Escape(glob).Replace("\\*", ".*").Replace("\\?", ".") + "$";
                var globRegex = new Regex(globPattern, RegexOptions.IgnoreCase);
                fileFilter = f => globRegex.IsMatch(Path.GetFileName(f));
            }

            if (File.Exists(searchPath))
            {
                await SearchFile(searchPath, regex, results, cancellationToken);
            }
            else
            {
                await SearchDirectory(searchPath, regex, results, fileFilter, cancellationToken);
            }

            if (limit.HasValue && limit.Value > 0 && mode == OutputMode.FilesWithMatches)
            {
                results = results.Take(limit.Value).ToList();
            }

            var output = new Output
            {
                Mode = mode,
                Files = results.Select(r => r.FilePath).ToList()
            };

            switch (mode)
            {
                case OutputMode.Content:
                    var contentBuilder = new System.Text.StringBuilder();
                    foreach (var result in results)
                    {
                        foreach (var match in result.LineMatches)
                        {
                            contentBuilder.AppendLine($"{result.FilePath}:{match.LineNumber}:{match.Line}");
                        }
                    }
                    output.Content = contentBuilder.ToString();
                    output.NumMatches = results.Sum(r => r.MatchCount);
                    break;

                case OutputMode.Count:
                    var countBuilder = new System.Text.StringBuilder();
                    var totalMatches = 0;
                    foreach (var result in results)
                    {
                        countBuilder.AppendLine($"{result.FilePath}:{result.MatchCount}");
                        totalMatches += result.MatchCount;
                    }
                    output.Content = countBuilder.ToString();
                    output.NumMatches = totalMatches;
                    break;
            }

            return output;
        }
        catch (Exception ex)
        {
            return new Output { Error = $"搜索失败: {ex.Message}" };
        }
    }

    private async Task SearchDirectory(string directory, Regex regex, List<SearchResult> results, Func<string, bool> fileFilter, CancellationToken cancellationToken)
    {
        try
        {
            var files = Directory.GetFiles(directory);
            foreach (var file in files)
            {
                if (cancellationToken.IsCancellationRequested)
                    return;

                if (!fileFilter(file))
                    continue;

                await SearchFile(file, regex, results, cancellationToken);
            }

            var subdirectories = Directory.GetDirectories(directory);
            foreach (var subdir in subdirectories)
            {
                if (cancellationToken.IsCancellationRequested)
                    return;

                var dirName = Path.GetFileName(subdir);
                if (FileSearchHelpers.IsIgnoredDirectory(dirName))
                    continue;

                await SearchDirectory(subdir, regex, results, fileFilter, cancellationToken);
            }
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private async Task SearchFile(string filePath, Regex regex, List<SearchResult> results, CancellationToken cancellationToken)
    {
        try
        {
            // 跳二进制文件（DLL/图片/压缩包等），避免当文本逐行匹配的噪音与开销（对标 grep -I / ripgrep）
            if (FileSearchHelpers.IsBinary(filePath)) return;

            var result = new SearchResult { FilePath = filePath };
            var matchCount = 0;
            int lineNo = 0;

            // 流式逐行读，不整文件入内存（大文件不爆内存；原 ReadAllTextAsync 会把整文件读进来）
            using var sr = new StreamReader(filePath);
            string? line;
            while ((line = await sr.ReadLineAsync(cancellationToken)) is not null)
            {
                lineNo++;
                if (cancellationToken.IsCancellationRequested)
                    return;

                var matches = regex.Matches(line);
                if (matches.Count > 0)
                {
                    matchCount += matches.Count;
                    result.LineMatches.Add(new LineMatch { LineNumber = lineNo, Line = line });
                }
            }

            if (matchCount > 0)
            {
                result.MatchCount = matchCount;
                results.Add(result);
            }
        }
        catch (Exception)
        {
        }
    }
}
