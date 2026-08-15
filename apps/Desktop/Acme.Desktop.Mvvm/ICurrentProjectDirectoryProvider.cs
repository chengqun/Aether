namespace Acme.Desktop.Mvvm;

/// <summary>当前文档页打开的项目目录 accessor（DI 单例，可变）。
/// <see cref="DocumentsViewModel"/> 在 <c>RootPath</c> 变化时回写；
/// AI 工具（如 GetCurrentProjectDirectory）经此读取当前目录——让模型知道用户打开了哪个项目。</summary>
public interface ICurrentProjectDirectoryProvider
{
    /// <summary>当前打开的项目目录绝对路径（null = 未打开）。</summary>
    string? CurrentDirectory { get; set; }
}

/// <summary>默认实现：简单可变 POCO 单例。DocumentsViewModel 写、AI 工具读。</summary>
public sealed class CurrentProjectDirectoryProvider : ICurrentProjectDirectoryProvider
{
    public string? CurrentDirectory { get; set; }
}
