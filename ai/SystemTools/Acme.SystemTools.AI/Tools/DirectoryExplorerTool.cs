using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Acme.SystemTools.AI;

public class DirectoryExplorerTool
{
    public class Output
    {
        public List<DirectoryItem> Items { get; set; } = new();
        public string? Error { get; set; }
    }

    public class DirectoryItem
    {
        public string Name { get; set; } = string.Empty;
        public string FullPath { get; set; } = string.Empty;
        public bool IsDirectory { get; set; }
        public bool IsHidden { get; set; }
        public long? Size { get; set; }
        public DateTime? LastModified { get; set; }
        public List<DirectoryItem>? Children { get; set; }
    }

    [Description("浏览目录结构")]
    public async Task<Output> ExploreDirectory([Description("要浏览的目录路径")] string path, [Description("递归深度（0 = 当前目录）")] int recursionDepth = 0, [Description("是否包含隐藏文件")] bool includeHidden = false, [Description("是否仅显示目录")] bool directoriesOnly = false, CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrEmpty(path))
            {
                return new Output { Error = "目录路径不能为空" };
            }

            var fullPath = Path.GetFullPath(path);

            if (!Directory.Exists(fullPath))
            {
                return new Output { Error = $"目录不存在: {path}" };
            }

            var items = await Task.Run(() =>
            {
                return GetDirectoryItems(fullPath, recursionDepth, includeHidden, directoriesOnly, cancellationToken);
            }, cancellationToken);

            return new Output
            {
                Items = items
            };
        }
        catch (Exception ex)
        {
            return new Output { Error = $"浏览目录失败: {ex.Message}" };
        }
    }

    private List<DirectoryItem> GetDirectoryItems(string directoryPath, int remainingDepth, bool includeHidden, bool directoriesOnly, CancellationToken cancellationToken)
    {
        var result = new List<DirectoryItem>();

        try
        {
            if (cancellationToken.IsCancellationRequested)
                return result;

            var dirInfo = new DirectoryInfo(directoryPath);

            // 获取子目录
            var directories = dirInfo.GetDirectories();
            foreach (var dir in directories)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                if (!includeHidden && (dir.Attributes & FileAttributes.Hidden) != 0)
                    continue;

                var item = new DirectoryItem
                {
                    Name = dir.Name,
                    FullPath = dir.FullName,
                    IsDirectory = true,
                    IsHidden = (dir.Attributes & FileAttributes.Hidden) != 0,
                    LastModified = dir.LastWriteTime
                };

                if (remainingDepth > 0)
                {
                    item.Children = GetDirectoryItems(dir.FullName, remainingDepth - 1, includeHidden, directoriesOnly, cancellationToken);
                }

                result.Add(item);
            }

            if (!directoriesOnly)
            {
                // 获取文件
                var files = dirInfo.GetFiles();
                foreach (var file in files)
                {
                    if (cancellationToken.IsCancellationRequested)
                        break;

                    if (!includeHidden && (file.Attributes & FileAttributes.Hidden) != 0)
                        continue;

                    result.Add(new DirectoryItem
                    {
                        Name = file.Name,
                        FullPath = file.FullName,
                        IsDirectory = false,
                        IsHidden = (file.Attributes & FileAttributes.Hidden) != 0,
                        Size = file.Length,
                        LastModified = file.LastWriteTime
                    });
                }
            }

            return result.OrderBy(x => !x.IsDirectory).ThenBy(x => x.Name).ToList();
        }
        catch (Exception)
        {
            return result;
        }
    }
}
