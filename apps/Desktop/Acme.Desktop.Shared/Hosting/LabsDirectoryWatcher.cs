using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Acme.Desktop.Mvvm;
using Acme.Notes.Domain.Catalog;
using Acme.Notes.Infrastructure.Catalog;

namespace Acme.Desktop.Shared.Hosting;

/// <summary>
/// labs 目录 watcher：监听 labs 根目录的文件变更，驱动 <see cref="INoteCatalogSource.Changed"/>（即
/// <see cref="DirectoryNoteCatalogSource.NotifyChanged"/>），让代码规则/策略/Lean 算法的「文件变更 → 重编译/重加载」
/// 链路脱离对 <see cref="INoteService.NotesChanged"/>（SQLite）的依赖。
/// <para>职责边界：<see cref="DirectoryNoteCatalogSource"/> 自身不挂 watcher（见其注释「目录变化信号源在宿主层」）；
/// 本类是那个宿主层信号源。它独立于 labs 页 VM（<c>KnowledgeObjectsViewModel</c> 继承 <c>FileTreeVmBase</c> 的 watcher
/// 只在页面打开目录后才启动）——代码规则/策略的重编译不能依赖用户是否打开 labs 页，故必须有一个常驻的目录 watcher。</para>
/// <para>目录来源：<see cref="IKnowledgeDirectoryProvider"/>（CurrentDirectory ?? DefaultRoot）；目录切换时重挂 watcher。</para>
/// </summary>
public sealed class LabsDirectoryWatcher : IDisposable
{
    private readonly IKnowledgeDirectoryProvider _directory;
    private readonly INoteCatalogSource _catalog;
    private FileSystemWatcher? _watcher;
    private CancellationTokenSource? _reloadCts;
    private bool _disposed;

    public LabsDirectoryWatcher(IKnowledgeDirectoryProvider directory, INoteCatalogSource catalog)
    {
        _directory = directory ?? throw new ArgumentNullException(nameof(directory));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    }

    /// <summary>启动期（容器构建后）调用：挂当前目录 watcher + 订阅目录切换（换目录重挂）。</summary>
    public void Start()
    {
        AttachWatcher(ResolveRoot());
        _directory.DirectoryChanged += OnDirectoryChanged;
    }

    private string? ResolveRoot() => _directory.CurrentDirectory ?? _directory.DefaultRoot;

    private void OnDirectoryChanged(string? path) => AttachWatcher(path);

    private void AttachWatcher(string? root)
    {
        DetachWatcher();
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            return; // 尚未打开 labs 目录（或缺省根未创建）→ 不挂 watcher，等目录切换事件再挂。

        try
        {
            _watcher = new FileSystemWatcher(root)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName
                              | NotifyFilters.LastWrite | NotifyFilters.CreationTime,
                InternalBufferSize = 16 * 1024,
            };
            _watcher.Changed += (_, _) => ScheduleNotify();
            _watcher.Created += (_, _) => ScheduleNotify();
            _watcher.Deleted += (_, _) => ScheduleNotify();
            _watcher.Renamed += (_, _) => ScheduleNotify();
            _watcher.Error += (_, _) => ScheduleNotify(); // 缓冲溢出 → 尽力刷一次
            _watcher.EnableRaisingEvents = true;
        }
        catch { /* 无权限/路径无效 → 静默，无自动重载 */ }
    }

    private void DetachWatcher()
    {
        if (_watcher is not null)
        {
            try { _watcher.EnableRaisingEvents = false; _watcher.Dispose(); }
            catch { /* 忽略 */ }
            _watcher = null;
        }
        _reloadCts?.Cancel();
        _reloadCts = null;
    }

    /// <summary>去抖 400ms 后广播源变更（批量写盘只刷一次）。FSW 后台线程已脱离 UI，无需 marshal。</summary>
    private void ScheduleNotify()
    {
        _reloadCts?.Cancel();
        var cts = new CancellationTokenSource();
        _reloadCts = cts;
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(400, cts.Token); }
            catch (TaskCanceledException) { return; }
            if (cts.IsCancellationRequested || _disposed) return;
            if (_catalog is DirectoryNoteCatalogSource dir) dir.NotifyChanged();
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _directory.DirectoryChanged -= OnDirectoryChanged;
        DetachWatcher();
    }
}
