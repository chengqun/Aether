using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Acme.Mobile.Mvvm.Events;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Acme.Mobile.Mvvm.ViewModels;

/// <summary>
/// labs 文件只读查看 VM（列表项点开）。接收导航参数 <c>path</c>（相对 labs 根的路径），读文件全文显示。
/// 只读——编辑在各类型编辑页（后续阶段）做；本页先让用户能「看见」桌面同步下来的 labs 内容。
/// </summary>
public partial class LabsFileViewViewModel : NavigableViewModelBase
{
    private readonly Func<string?> _labsRoot;
    private string _relativePath = string.Empty;

    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private string _content = string.Empty;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    public event EventHandler<NavigationRequestedEvent>? NavigationRequested;

    public LabsFileViewViewModel(Func<string?> labsRoot)
    {
        _labsRoot = labsRoot ?? throw new ArgumentNullException(nameof(labsRoot));
    }

    /// <summary>接收导航参数（path = 相对 labs 根的路径）。</summary>
    public void SetParameters(IDictionary<string, object> query)
    {
        if (query == null) return;
        if (query.TryGetValue("path", out var p) && p is string path)
        {
            _relativePath = path;
            Title = Path.GetFileName(path);
            _ = LoadAsync();
        }
    }

    private async Task LoadAsync()
    {
        var root = _labsRoot();
        if (string.IsNullOrWhiteSpace(root)) return;

        var full = Path.Combine(root, _relativePath.Replace('/', Path.DirectorySeparatorChar));
        try
        {
            if (!File.Exists(full))
            {
                StatusMessage = "文件不存在";
                return;
            }
            var text = await File.ReadAllTextAsync(full);
            // 二进制/超长文件守卫：5MB 上限，超了截断提示（避免大文件卡死手机 UI）。
            if (text.Length > 5 * 1024 * 1024)
            {
                Content = text[..(5 * 1024 * 1024)];
                StatusMessage = "文件过大，已截断显示前 5MB";
            }
            else
            {
                Content = text;
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"读取失败: {ex.Message}";
        }
    }

    [RelayCommand]
    private void GoBack() => NavigationRequested?.Invoke(this, NavigationRequestedEvent.Back());
}
