using System;

namespace Acme.Desktop.Mvvm;

/// <summary>
/// 知识库目录持有者（独立单例，与 <see cref="ICurrentProjectDirectoryProvider"/> 解耦）。
/// 文档版知识对象（<c>.md</c> 文件）的根目录=知识库目录，跨项目复用、可设在任意路径（含项目目录），
/// 不与文档页项目目录共用单例——避免知识库目录覆盖 <see cref="ICurrentProjectDirectoryProvider"/>
/// （被 <c>SourceControlViewModel</c>/<c>TerminalViewModel</c>/<c>ActivityViewModel</c> 订阅）导致 SCM/终端/活动页错乱。
/// 由 <see cref="KnowledgeObjectsViewModel"/> 写入（打开知识库目录时），由
/// <see cref="Acme.Desktop.Shared.Shell.DocumentKnowledgeTool"/> 读取（AI 工具操作知识库目录下 <c>.md</c>）。
/// </summary>
public interface IKnowledgeDirectoryProvider
{
    /// <summary>当前打开的知识库目录绝对路径（null = 未打开）。</summary>
    string? CurrentDirectory { get; set; }

    /// <summary>默认知识库目录（labs 根，宿主设为 <c>&lt;tenantDir&gt;/labs</c>）。
    /// 无已持久化目录时，页面/目录源回退到此默认根（创建 if missing）。</summary>
    string? DefaultRoot { get; set; }

    /// <summary>目录变化时引发（值变才发；同值不重复发）。</summary>
    event Action<string?>? DirectoryChanged;
}

/// <summary>
/// <see cref="IKnowledgeDirectoryProvider"/> 默认实现。镜像 <see cref="CurrentProjectDirectoryProvider"/>：
/// setter 值变（序号比较）才 raise <see cref="DirectoryChanged"/>。
/// </summary>
public sealed class KnowledgeDirectoryProvider : IKnowledgeDirectoryProvider
{
    private string? _currentDirectory;

    public string? CurrentDirectory
    {
        get => _currentDirectory;
        set
        {
            if (!string.Equals(_currentDirectory, value, StringComparison.Ordinal))
            {
                _currentDirectory = value;
                DirectoryChanged?.Invoke(value);
            }
        }
    }

    public string? DefaultRoot { get; set; }

    public event Action<string?>? DirectoryChanged;
}
