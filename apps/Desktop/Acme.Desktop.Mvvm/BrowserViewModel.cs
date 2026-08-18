using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.Desktop.Mvvm;

/// <summary>「浏览器网络」面板 ViewModel：标签条（会话）+ 请求列表（当前活跃会话的 HTTP 请求流）。
/// 照 <see cref="TerminalViewModel"/> 范式：元数据投影 + 订阅 <see cref="IBrowserService"/> 事件 diff 重建列表 + 命令转发 service（不持控件）。
/// 浏览器画面是 Playwright 独立 OS 窗口；本 VM/View 只展示请求列表。UI-agnostic（零 Avalonia）。</summary>
public partial class BrowserViewModel : ObservableObject
{
    private readonly IBrowserService _service;
    private readonly IUiDispatcher _dispatcher;

    /// <summary>会话标签条数据源。</summary>
    public ObservableCollection<BrowserSessionItem> Sessions { get; } = new();
    /// <summary>当前活跃会话的请求列表数据源。</summary>
    public ObservableCollection<NetworkRequestItem> Requests { get; } = new();

    [ObservableProperty] private BrowserSessionItem? _activeSession;
    [ObservableProperty] private NetworkRequestItem? _selectedRequest;
    /// <summary>地址栏输入（回车 Navigate；空时禁用）。</summary>
    [ObservableProperty] private string? _addressBar;

    public BrowserViewModel(IServiceProvider serviceProvider)
    {
        _service = serviceProvider.GetRequiredService<IBrowserService>();
        _dispatcher = serviceProvider.GetRequiredService<IUiDispatcher>();
        _service.SessionsChanged += OnSessionsChanged;
        _service.ActiveSessionChanged += OnActiveSessionChanged;
        _service.RequestsChanged += OnRequestsChanged;
        _service.SessionClosed += OnSessionClosed;
        RebuildSessions();
    }

    // —— service 事件 → marshal UI 线程 → diff 重建 ——

    private void OnSessionsChanged() => _dispatcher.Post(RebuildSessions);
    private void OnActiveSessionChanged(Guid id) => _dispatcher.Post(() => { RebuildSessions(); RebuildRequests(); });
    private void OnRequestsChanged(Guid id)
    {
        // 仅当变更的是当前活跃会话时刷新请求列表（非活跃会话的请求累积在 service，切回时再显示）。
        if (_service.ActiveSessionId == id) _dispatcher.Post(RebuildRequests);
    }
    private void OnSessionClosed(Guid id) => _dispatcher.Post(() =>
    {
        // 标签 IsAlive=false（标签保留供用户关闭，照终端退出范式）。
        var item = Sessions.FirstOrDefault(s => s.Id == id);
        if (item is not null) item.IsAlive = false;
    });

    /// <summary>从 service 快照 diff 重建标签条（删消失/加新/同步 Title/Url/IsAlive/IsActive/RequestCount）。</summary>
    private void RebuildSessions()
    {
        var snapshot = _service.GetSessions();
        // 删消失项。
        for (int i = Sessions.Count - 1; i >= 0; i--)
            if (snapshot.All(s => s.Id != Sessions[i].Id)) Sessions.RemoveAt(i);
        // 加新项 + 同步现有项。
        var activeId = _service.ActiveSessionId;
        foreach (var s in snapshot)
        {
            var item = Sessions.FirstOrDefault(x => x.Id == s.Id);
            if (item is null)
            {
                item = new BrowserSessionItem(s.Id, s.Title, s.Url, s.IsAlive);
                Sessions.Add(item);
            }
            item.Title = s.Title;
            item.Url = s.Url;
            item.IsAlive = s.IsAlive;
            item.IsActive = s.Id == activeId;
            item.RequestCount = _service.GetRequests(s.Id).Count;
        }
        ActiveSession = Sessions.FirstOrDefault(s => s.Id == activeId);
        NavigateCommand.NotifyCanExecuteChanged();
    }

    /// <summary>从 service 快照 diff 重建当前活跃会话的请求列表（加新项；请求不删故无需移除）。
    /// NetworkRequestItem 是 service 持有的同一对象——其 Status/Duration 等 ObservableProperty 由 service 在 Response 事件改，UI 自动刷新；
    /// VM 只负责把新请求加进集合。</summary>
    private void RebuildRequests()
    {
        var activeId = _service.ActiveSessionId;
        if (activeId is null) { Requests.Clear(); return; }
        var snapshot = _service.GetRequests(activeId.Value);
        // diff：集合里没有的（新请求）追加；已有的不动（service 已改其字段）。
        var existingIds = new HashSet<Guid>(Requests.Select(r => r.Id));
        foreach (var r in snapshot)
            if (existingIds.Add(r.Id)) Requests.Add(r);
        // 清除集合里有但快照没有的（ClearRequests 后）。
        var snapshotIds = snapshot.Select(r => r.Id).ToHashSet();
        for (int i = Requests.Count - 1; i >= 0; i--)
            if (!snapshotIds.Contains(Requests[i].Id)) Requests.RemoveAt(i);
        ClearRequestsCommand.NotifyCanExecuteChanged();
    }

    // —— 命令（纯转发 service，不持控件）——

    /// <summary>地址栏回车 / 「打开」按钮：有活跃会话则导航，无则建新会话。</summary>
    [RelayCommand(CanExecute = nameof(CanNavigate))]
    private void Navigate()
    {
        var url = AddressBar;
        if (string.IsNullOrWhiteSpace(url)) return;
        if (!Uri.TryCreate(url, UriKind.Absolute, out _) && !url.StartsWith("about:", StringComparison.Ordinal))
        {
            // 非完整 URL 自动补 https://。
            url = "https://" + url;
        }
        if (_service.ActiveSessionId is { } id) _service.NavigateAsync(id, url).SafeFireAndForget();
        else _service.OpenSession(url);
    }
    private bool CanNavigate() => !string.IsNullOrWhiteSpace(AddressBar);

    /// <summary>新建空白会话（用户随后在地址栏输入 URL 导航）。</summary>
    [RelayCommand]
    private void NewSession() => _service.OpenSession("about:blank");

    /// <summary>关闭会话（关浏览器 + 移除标签）。</summary>
    [RelayCommand]
    private void CloseSession(BrowserSessionItem? item)
    {
        if (item is not null) _service.CloseSession(item.Id);
    }

    /// <summary>切换活跃会话标签。</summary>
    [RelayCommand]
    private void SwitchSession(BrowserSessionItem? item)
    {
        if (item is not null) _service.SetActive(item.Id);
    }

    /// <summary>清当前会话请求记录。</summary>
    [RelayCommand(CanExecute = nameof(CanClearRequests))]
    private void ClearRequests()
    {
        if (_service.ActiveSessionId is { } id) _service.ClearRequests(id);
    }
    private bool CanClearRequests() => Requests.Count > 0;

    partial void OnAddressBarChanged(string? value) => NavigateCommand.NotifyCanExecuteChanged();
}
