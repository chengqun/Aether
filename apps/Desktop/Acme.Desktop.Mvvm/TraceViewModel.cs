using System.Collections.ObjectModel;
using System.Text.Json;
using Aether.AI.Harness.Telemetry;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Acme.Desktop.Mvvm;

/// <summary>
/// 运行 Trace 查看 ViewModel：从 <see cref="TraceCollector"/> 实时收集最近 agent.run trace，
/// 供桌面端无控制台时查看每轮 run 的耗时/工具/错误。支持按 agent/status/事件关键词筛选 + 导出 JSON。
/// </summary>
public partial class TraceViewModel : ObservableObject
{
    private readonly TraceCollector _collector;
    private readonly IUiDispatcher _dispatcher;

    public ObservableCollection<TraceEntry> Traces { get; } = new();
    public ObservableCollection<TraceEntry> VisibleTraces { get; } = new();

    [ObservableProperty] private string _filterText = string.Empty;
    [ObservableProperty] private bool _showErrorsOnly;

    public TraceViewModel(TraceCollector collector, IUiDispatcher dispatcher)
    {
        _collector = collector;
        _dispatcher = dispatcher;
        _collector.TraceCompleted += OnTraceCompleted;
        Refresh();
    }

    partial void OnFilterTextChanged(string value) => ApplyFilter();
    partial void OnShowErrorsOnlyChanged(bool value) => ApplyFilter();

    private void OnTraceCompleted(TraceEntry entry)
    {
        _dispatcher.Post(() =>
        {
            Traces.Insert(0, entry);
            while (Traces.Count > 50) Traces.RemoveAt(Traces.Count - 1);
            ApplyFilter();
        });
    }

    private void ApplyFilter()
    {
        VisibleTraces.Clear();
        foreach (var t in Traces)
        {
            if (ShowErrorsOnly && !t.IsError) continue;
            if (string.IsNullOrEmpty(FilterText)
                || (t.AgentId?.Contains(FilterText, StringComparison.OrdinalIgnoreCase) ?? false)
                || t.Status.Contains(FilterText, StringComparison.OrdinalIgnoreCase)
                || t.Events.Any(e => (e.Detail?.Contains(FilterText, StringComparison.OrdinalIgnoreCase) ?? false)))
                VisibleTraces.Add(t);
        }
    }

    [RelayCommand] private void Refresh()
    {
        Traces.Clear();
        foreach (var t in _collector.GetTraces()) Traces.Add(t);
        ApplyFilter();
    }

    [RelayCommand] private void Clear()
    {
        _collector.Clear();
        Traces.Clear();
        VisibleTraces.Clear();
    }

    /// <summary>导出当前 Traces 为 JSON 字符串（供 View 写文件）。</summary>
    public string GetJson() => JsonSerializer.Serialize(Traces, new JsonSerializerOptions { WriteIndented = true });
}
