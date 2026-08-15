namespace Acme.Desktop.Mvvm;

/// <summary>当前文档页打开的项目目录 accessor（DI 单例，可变）。
/// <see cref="DocumentsViewModel"/> 在 <c>RootPath</c> 变化时回写；
/// AI 工具（如 GetCurrentProjectDirectory）经此读取当前目录——让模型知道用户打开了哪个项目。
/// 源代码管理页（<see cref="SourceControlViewModel"/>）订阅 <see cref="DirectoryChanged"/> 响应式切换仓库。</summary>
public interface ICurrentProjectDirectoryProvider
{
    /// <summary>当前打开的项目目录绝对路径（null = 未打开）。setter 在值变化时 raise <see cref="DirectoryChanged"/>。</summary>
    string? CurrentDirectory { get; set; }

    /// <summary>当前目录变化时触发（打开/关闭/切换项目）。订阅者据此刷新依赖项目目录的状态。</summary>
    event Action<string?>? DirectoryChanged;
}

/// <summary>默认实现：简单可变 POCO 单例。DocumentsViewModel 写、AI 工具/SCM VM 读。</summary>
public sealed class CurrentProjectDirectoryProvider : ICurrentProjectDirectoryProvider
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

    public event Action<string?>? DirectoryChanged;
}
