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
/// 链路脱离原 note 版 SQLite 变更通知的依赖。
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
            _watcher.Changed += (_, e) => ScheduleNotify(e.FullPath);
            _watcher.Created += (_, e) => ScheduleNotify(e.FullPath);
            _watcher.Deleted += (_, e) => ScheduleNotify(e.FullPath);
            _watcher.Renamed += (_, e) => ScheduleNotify(e.FullPath);
            // 缓冲溢出：FullPath 不可得 → 传 null（消费方按「未知」处理，退化为全量重载但不会误清聊天——见 MainViewModelBase 过滤）。
            _watcher.Error += (_, _) => ScheduleNotify(null);
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

    /// <summary>去抖 400ms 后广播源变更（批量写盘只刷一次）。期间累计变更路径的首段目录名，
    /// 让消费方能按「涉及的目录」判断是否需要重载（改 knowledge/ 不该重载聊天目标表）。FSW 后台线程已脱离 UI，无需 marshal。</summary>
    private readonly HashSet<string> _pendingDirs = new();

    private void ScheduleNotify(string? fullPath)
    {
        // 累计变更项的首段目录（相对 labs 根的顶层目录名）；null/扁平文件 → 记为 null 触发「未知」全量。
        string? top = null;
        if (fullPath is not null)
        {
            var root = _directory.CurrentDirectory ?? _directory.DefaultRoot;
            if (!string.IsNullOrWhiteSpace(root))
            {
                try
                {
                    var rel = Path.GetRelativePath(root, fullPath);
                    if (!rel.StartsWith("..", StringComparison.Ordinal))
                        top = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
                }
                catch { /* 路径不可靠 → null */ }
            }
        }

        lock (_pendingDirs)
        {
            if (top is null) _pendingDirs.Add("\0"); // 哨兵：表示「有未知路径」，广播时传 null
            else _pendingDirs.Add(top);
        }

        _reloadCts?.Cancel();
        var cts = new CancellationTokenSource();
        _reloadCts = cts;
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(400, cts.Token); }
            catch (TaskCanceledException) { return; }
            if (cts.IsCancellationRequested || _disposed) return;
            if (_catalog is not DirectoryNoteCatalogSource dir) return;

            HashSet<string> dirs;
            lock (_pendingDirs)
            {
                dirs = new HashSet<string>(_pendingDirs, StringComparer.OrdinalIgnoreCase);
                _pendingDirs.Clear();
            }
            // 含未知路径哨兵 → 传 null（消费方退化为「未知」全量，但不误清聊天）。否则传首段目录（消费方据此过滤）。
            string? path = dirs.Contains("\0") ? null : (dirs.Count == 1 ? dirs.First() : string.Join('|', dirs));
            dir.NotifyChanged(path);
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
