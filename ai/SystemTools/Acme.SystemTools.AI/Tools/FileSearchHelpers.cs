using System;
using System.Collections.Generic;
using System.IO;

namespace Acme.SystemTools.AI.Tools;

/// <summary>文件搜索共享助手：忽略构建产物/VCS/依赖目录（对标 ripgrep 默认尊 .gitignore + 跳 bin/obj），
/// 以及二进制文件检测（对标 <c>grep -I</c> / ripgrep 自动跳二进制）。
/// 供 <see cref="GrepTool"/> / <see cref="GlobTool"/> 共用，避免各自重复实现。</summary>
internal static class FileSearchHelpers
{
    /// <summary>搜索时跳过的目录名（构建产物 / 包管理 / VCS / IDE / 运行时输出）。
    /// 大幅减少无效文件数（bin/obj 常是源码的数倍），是搜索提速的首要手段。</summary>
    private static readonly HashSet<string> _ignoredDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", "node_modules", ".git", ".vs", ".idea", ".vscode",
        "packages", "TestResults", ".sass-cache", "dist", "build", "target",
        "__pycache__", ".venv", "venv", ".next", ".nuxt",
    };

    /// <summary>目录是否应被搜索跳过：隐藏目录（<c>.</c> 开头）或在忽略名单内。</summary>
    public static bool IsIgnoredDirectory(string dirName)
        => dirName.StartsWith(".") || _ignoredDirs.Contains(dirName);

    /// <summary>二进制文件检测启发式（同 ripgrep/grep -I）：读前 8192 字节，含 NUL(\0) 视为二进制。
    /// 避免把 DLL/图片/压缩包当文本逐行匹配（慢 + 乱码噪音）。读取异常时保守返回 false（交由调用方尝试读）。</summary>
    public static bool IsBinary(string filePath)
    {
        const int sampleSize = 8192;
        try
        {
            using var fs = File.OpenRead(filePath);
            var buf = new byte[Math.Min(sampleSize, fs.Length)];
            int read = fs.Read(buf, 0, buf.Length);
            for (int i = 0; i < read; i++)
                if (buf[i] == 0) return true;
            return false;
        }
        catch
        {
            return false;
        }
    }
}
