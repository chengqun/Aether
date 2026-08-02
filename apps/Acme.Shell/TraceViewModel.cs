using System.Collections.ObjectModel;
using Aether.AI.Harness.Telemetry;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Acme.Shell;

/// <summary>
/// 运行 Trace 查看 ViewModel：从 <see cref="TraceCollector"/> 实时收集最近 agent.run trace，
/// 供桌面端无控制台时查看每轮 run 的耗时/工具/错误。
/// </summary>
public partial class TraceViewModel : ObservableObject
{
    private readonly TraceCollector _collector;
    private readonly IUiDispatcher _dispatcher;

    public ObservableCollection<TraceEntry> Traces { get; } = new();

    public TraceViewModel(TraceCollector collector, IUiDispatcher dispatcher)
    {
        _collector = collector;
        _dispatcher = dispatcher;
        _collector.TraceCompleted += OnTraceCompleted;
        Refresh();
    }

    private void OnTraceCompleted(TraceEntry entry)
    {
        _dispatcher.Post(() =>
        {
            Traces.Insert(0, entry);
            while (Traces.Count > 50) Traces.RemoveAt(Traces.Count - 1);
        });
    }

    [RelayCommand] private void Refresh()
    {
        Traces.Clear();
        foreach (var t in _collector.GetTraces()) Traces.Add(t);
    }

    [RelayCommand] private void Clear()
    {
        _collector.Clear();
        Traces.Clear();
    }
}
