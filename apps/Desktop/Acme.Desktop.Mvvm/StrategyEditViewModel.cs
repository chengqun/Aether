using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Acme.Desktop.Mvvm;

/// <summary>
/// YAML 声明式策略的结构化编辑 VM（笔记页右栏表单）。
/// 持 <see cref="StrategyFormModel"/> 行的可观测视图（params/indicators/rules 列表），编辑后 <see cref="Build"/>
/// 回模型并触发保存。照 note 版知识对象编辑器（已删）的内联构造 + Timer 防抖自动保存模式，
/// + <see cref="RuleEditViewModel"/> 的 AutoSaveStatus 反馈。条件编辑用扁平子句 + 组合器（None/AND/OR），
/// 不做递归树——exotic 条件（not/深层嵌套）走 <see cref="RuleRow.IsAdvanced"/> 只读回退。
/// 行类型本身即数据载体（可观测 POCO）：codec 直接读写行实例，VM 复用 model 行 + 订阅变更。
/// </summary>
public sealed partial class StrategyEditViewModel : ObservableObject
{
    private readonly SynchronizationContext? _syncContext = SynchronizationContext.Current;
    private Timer? _saveTimer;
    private bool _loading = true;

    public Guid Id { get; }

    public StrategyEditViewModel(Guid id, string name, StrategyFormModel model)
    {
        Id = id;
        _saveTimer = new Timer(_ => FlushSave(), null, Timeout.Infinite, Timeout.Infinite);
        Name = name;
        Default = string.IsNullOrWhiteSpace(model.Default) ? "Hold" : model.Default;

        foreach (var p in model.Params) Params.Add(p);
        Params.CollectionChanged += OnParamsChanged;
        foreach (var p in Params) SubscribeParam(p);

        foreach (var ind in model.Indicators)
        {
            Indicators.Add(ind);
            SubscribeIndicator(ind);
        }
        Indicators.CollectionChanged += OnIndicatorsChanged;

        foreach (var r in model.Rules)
        {
            Rules.Add(r);
            SubscribeRule(r);
        }
        Rules.CollectionChanged += OnRulesChanged;

        RebuildIndicatorNames();
        _loading = false;
    }

    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _default = "Hold";
    [ObservableProperty] private string? _error;
    [ObservableProperty] private string? _autoSaveStatus;

    public ObservableCollection<ParamRow> Params { get; } = new();
    public ObservableCollection<IndicatorRow> Indicators { get; } = new();
    public ObservableCollection<RuleRow> Rules { get; } = new();

    /// <summary>已声明指标名列表（操作数 ComboBox 选项）。指标增删/改名时重建。</summary>
    public ObservableCollection<string> AvailableIndicatorNames { get; } = new();

    public static string[] IndicatorTypes => StrategyFormModel.IndicatorTypes;
    public static string[] SignalOptions => StrategyFormModel.SignalOptions;
    public static string[] DefaultOptions => StrategyFormModel.DefaultOptions;
    public static string[] ComparisonOps => StrategyFormModel.ComparisonOps;
    public static string[] OutputNames => StrategyFormModel.OutputNames;
    public static CombinerOption[] CombinerOptions => StrategyFormModel.CombinerOptions;

    public bool HasError => !string.IsNullOrEmpty(Error);
    public bool CanSave => !string.IsNullOrWhiteSpace(Name);

    public event Action<StrategyFormModel>? SaveRequested;
    public event Action? CancelRequested;

    partial void OnNameChanged(string value) { OnPropertyChanged(nameof(CanSave)); ScheduleSave(); }
    partial void OnDefaultChanged(string value) => ScheduleSave();

    /// <summary>把可观测行投影回 <see cref="StrategyFormModel"/>（行实例共享，供 codec 序列化）。</summary>
    public StrategyFormModel Build()
    {
        var m = new StrategyFormModel
        {
            Name = Name?.Trim() ?? string.Empty,
            Default = string.IsNullOrWhiteSpace(Default) ? "Hold" : Default,
        };
        foreach (var p in Params) m.Params.Add(p);
        foreach (var i in Indicators) m.Indicators.Add(i);
        foreach (var r in Rules) m.Rules.Add(r);
        return m;
    }

    [RelayCommand] private void Save()
    {
        if (!CanSave) { Error = "名称不能为空"; return; }
        Error = null;
        SaveRequested?.Invoke(Build());
    }

    [RelayCommand] private void Cancel() => CancelRequested?.Invoke();

    // —— 列表 Add/Remove 命令 ——
    [RelayCommand] private void AddParam() => Params.Add(new ParamRow());
    [RelayCommand] private void RemoveParam(ParamRow row) => Params.Remove(row);

    [RelayCommand] private void AddIndicator() => Indicators.Add(new IndicatorRow());
    [RelayCommand] private void RemoveIndicator(IndicatorRow row) => Indicators.Remove(row);

    [RelayCommand] private void AddIndicatorArg(IndicatorRow row) => row.Args.Add(new IndicatorArgRow { Key = "period" });
    [RelayCommand] private void RemoveIndicatorArg(IndicatorArgRow arg)
    {
        foreach (var i in Indicators)
            if (i.Args.Remove(arg)) break;
    }

    [RelayCommand] private void AddRule()
    {
        var r = new RuleRow();
        r.Clauses.Add(new ClauseRow { Op = "cross_above" });
        Rules.Add(r);
    }
    [RelayCommand] private void RemoveRule(RuleRow row) => Rules.Remove(row);

    [RelayCommand] private void AddClause(RuleRow row) => row.Clauses.Add(new ClauseRow { Op = "gt" });
    [RelayCommand] private void RemoveClause(ClauseRow row)
    {
        foreach (var r in Rules)
            if (r.Clauses.Remove(row)) break;
    }

    // —— 订阅行变更 → 自动保存（新增项由 CollectionChanged 统一订阅，同 Knowledge Links 模式）——
    private void SubscribeParam(ParamRow r) => r._changed = (_, _) => ScheduleSave();

    private void SubscribeIndicator(IndicatorRow r)
    {
        r._changed = (_, _) => { ScheduleSave(); RebuildIndicatorNames(); };
        foreach (var a in r.Args) a._changed = (_, _) => ScheduleSave();
        r.Args.CollectionChanged += OnIndicatorArgsChanged;
    }

    private void OnIndicatorArgsChanged(object? s, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null) foreach (IndicatorArgRow a in e.NewItems) a._changed = (_, _) => ScheduleSave();
        if (e.OldItems is not null) foreach (IndicatorArgRow a in e.OldItems) a._changed = null;
        ScheduleSave();
    }

    private void SubscribeRule(RuleRow r)
    {
        r._changed = (_, _) => ScheduleSave();
        foreach (var c in r.Clauses) SubscribeClause(c);
        r.Clauses.CollectionChanged += OnClausesChanged;
    }

    private void OnClausesChanged(object? s, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null) foreach (ClauseRow c in e.NewItems) SubscribeClause(c);
        if (e.OldItems is not null) foreach (ClauseRow c in e.OldItems) UnsubscribeClause(c);
        ScheduleSave();
    }

    private void SubscribeClause(ClauseRow c)
    {
        c._changed = (_, _) => ScheduleSave();
        c.Left._changed = (_, _) => ScheduleSave();
        c.Right._changed = (_, _) => ScheduleSave();
    }
    private static void UnsubscribeClause(ClauseRow c)
    {
        c._changed = null;
        c.Left._changed = null;
        c.Right._changed = null;
    }

    private void OnParamsChanged(object? s, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null) foreach (ParamRow r in e.NewItems) SubscribeParam(r);
        if (e.OldItems is not null) foreach (ParamRow r in e.OldItems) r._changed = null;
        ScheduleSave();
    }
    private void OnIndicatorsChanged(object? s, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null) foreach (IndicatorRow r in e.NewItems) SubscribeIndicator(r);
        if (e.OldItems is not null) foreach (IndicatorRow r in e.OldItems) r._changed = null;
        RebuildIndicatorNames();
        ScheduleSave();
    }
    private void OnRulesChanged(object? s, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null) foreach (RuleRow r in e.NewItems) SubscribeRule(r);
        if (e.OldItems is not null) foreach (RuleRow r in e.OldItems) r._changed = null;
        ScheduleSave();
    }

    /// <summary>重建操作数 ComboBox 的指标名选项。</summary>
    private void RebuildIndicatorNames()
    {
        AvailableIndicatorNames.Clear();
        foreach (var i in Indicators)
            if (!string.IsNullOrWhiteSpace(i.Name))
                AvailableIndicatorNames.Add(i.Name);
    }

    /// <summary>字段/行变更后延迟自动保存（800ms debounce）。</summary>
    private void ScheduleSave()
    {
        if (_loading) return;
        _saveTimer?.Change(800, Timeout.Infinite);
    }

    private void FlushSave()
    {
        if (!CanSave) return;
        _syncContext?.Post(_ =>
        {
            SaveRequested?.Invoke(Build());
            AutoSaveStatus = "✓ 已自动保存";
            _ = Task.Delay(1500).ContinueWith(__ => AutoSaveStatus = null, TaskScheduler.FromCurrentSynchronizationContext());
        }, null);
    }
}
