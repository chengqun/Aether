using System;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.Desktop.Mvvm;

/// <summary>
/// 「终端」页 ViewModel：多标签会话元数据投影 + 新建/关闭/切换命令面。
/// 真实 PTY/控件生命周期在 <see cref="ITerminalService"/>（视图层 impl）——本 VM 仅持元数据投影，UI 无关。
/// 默认 cwd 跟随文档页打开的项目目录（镜像 <see cref="SourceControlViewModel"/>，订阅 <see cref="ICurrentProjectDirectoryProvider.DirectoryChanged"/>）。
/// </summary>
public partial class TerminalViewModel : ObservableObject
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ICurrentProjectDirectoryProvider _projectDirProvider;
    private readonly ITerminalService _service;
    private readonly IUiDispatcher _dispatcher;

    /// <summary>标签条数据源（绑 TerminalView 标签 ItemsControl）。</summary>
    public ObservableCollection<TerminalSessionItem> Sessions { get; } = new();

    [ObservableProperty] private TerminalSessionItem? _activeSession;

    public TerminalViewModel(IServiceProvider serviceProvider, ICurrentProjectDirectoryProvider projectDirProvider)
    {
        _serviceProvider = serviceProvider;
        _projectDirProvider = projectDirProvider;
        _service = serviceProvider.GetRequiredService<ITerminalService>();
        _dispatcher = serviceProvider.GetRequiredService<IUiDispatcher>();
        _service.SessionsChanged += OnSessionsChanged;
        _service.ActiveSessionChanged += OnActiveSessionChanged;
        _service.SessionExited += OnSessionExited;
        RebuildSessions();
    }

    /// <summary>新建会话的默认 cwd：文档页当前项目目录（空则用 shell 默认家目录）。</summary>
    private string? DefaultCwd => string.IsNullOrWhiteSpace(_projectDirProvider.CurrentDirectory)
        ? null
        : _projectDirProvider.CurrentDirectory;

    private void OnSessionsChanged() => _dispatcher.Post(RebuildSessions);

    private void OnActiveSessionChanged(Guid id) =>
        _dispatcher.Post(() =>
        {
            ActiveSession = Sessions.FirstOrDefault(s => s.Id == id);
            foreach (var s in Sessions)
                s.IsActive = s.Id == id;
        });

    private void OnSessionExited(Guid id) =>
        _dispatcher.Post(() =>
        {
            var s = Sessions.FirstOrDefault(x => x.Id == id);
            if (s is not null) s.IsRunning = false;
        });

    /// <summary>从 service 元数据快照重建标签列表（diff：增删 + 同步标题/运行态），保留选中。</summary>
    private void RebuildSessions()
    {
        var snapshot = _service.GetSessions();
        for (int i = Sessions.Count - 1; i >= 0; i--)
            if (!snapshot.Any(s => s.Id == Sessions[i].Id))
                Sessions.RemoveAt(i);

        foreach (var s in snapshot)
        {
            var existing = Sessions.FirstOrDefault(x => x.Id == s.Id);
            if (existing is null)
                Sessions.Add(new TerminalSessionItem(s.Id, s.Cwd, s.Title, s.IsRunning));
            else
            {
                existing.Title = s.Title;
                existing.IsRunning = s.IsRunning;
            }
        }

        if (ActiveSession is null || !Sessions.Contains(ActiveSession))
            ActiveSession = Sessions.FirstOrDefault(x => x.Id == _service.ActiveSessionId);
        foreach (var s in Sessions)
            s.IsActive = s.Id == _service.ActiveSessionId;
    }

    [RelayCommand]
    private void NewSession() => _service.CreateSession(DefaultCwd, null);

    [RelayCommand]
    private void CloseSession(TerminalSessionItem? item)
    {
        if (item is not null) _service.CloseSession(item.Id);
    }

    [RelayCommand]
    private void SwitchSession(TerminalSessionItem? item)
    {
        if (item is not null) _service.SetActive(item.Id);
    }
}
