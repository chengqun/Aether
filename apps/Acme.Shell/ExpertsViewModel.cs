using System.Collections.ObjectModel;
using Acme.Agents.AI;
using Acme.Notes.AgentSource;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Acme.Shell;

/// <summary>专家页面 ViewModel：从自定义 Agent 目录加载，展示为专家卡片。UI 无关。</summary>
public partial class ExpertsViewModel : ObservableObject
{
    private readonly ICustomAgentCatalog _catalog;
    private readonly ICustomAgentRunner _runner;
    private readonly IUiDispatcher _dispatcher;

    public ExpertsViewModel(ICustomAgentCatalog catalog, ICustomAgentRunner runner, IUiDispatcher dispatcher)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    public ObservableCollection<ExpertItem> Experts { get; } = new();
    public ObservableCollection<ExpertItem> VisibleExperts { get; } = new();
    public ObservableCollection<string> Categories { get; } = new();

    [ObservableProperty] private string _selectedCategory = "全部";
    [ObservableProperty] private string _statusMessage = string.Empty;

    /// <summary>召唤变化事件（聊天页订阅）。</summary>
    public event Action? SummonChanged;

    /// <summary>从目录加载所有自定义 Agent。</summary>
    public async Task LoadAsync()
    {
        try
        {
            var all = await _catalog.ListAsync();
            var modelNames = _catalog.GetAvailableModels().ToDictionary(m => m.Id, m => m.Name);

            await _dispatcher.InvokeAsync(() =>
            {
                Experts.Clear();
                var catSet = new List<string>();

                foreach (var def in all)
                {
                    var item = new ExpertItem
                    {
                        Id = def.Id,
                        Name = def.Name,
                        Description = def.Description,
                        Category = string.IsNullOrWhiteSpace(def.Category) ? "普通对话" : def.Category,
                        SystemPrompt = def.SystemPrompt,
                        ModelId = def.ModelId,
                        ModelName = modelNames.TryGetValue(def.ModelId ?? string.Empty, out var mn) ? mn : string.Empty,
                        Tools = def.Tools.ToList(),
                        Memory = def.Memory,
                        MaxHistory = def.MaxHistory,
                    };
                    Experts.Add(item);

                    var cat = item.Category;
                    if (!catSet.Contains(cat)) catSet.Add(cat);
                }

                Categories.Clear();
                Categories.Add("全部");
                foreach (var c in catSet.OrderBy(c => c)) Categories.Add(c);
                if (!Categories.Contains(SelectedCategory)) SelectedCategory = "全部";

                RefreshVisibleExperts();
            });
        }
        catch (Exception ex)
        {
            StatusMessage = $"加载失败：{ex.Message}";
        }
    }

    private void RefreshVisibleExperts()
    {
        VisibleExperts.Clear();
        var filtered = SelectedCategory == "全部"
            ? Experts
            : Experts.Where(e => e.Category == SelectedCategory);
        foreach (var e in filtered) VisibleExperts.Add(e);
    }

    partial void OnSelectedCategoryChanged(string value) => RefreshVisibleExperts();

    /// <summary>创建新专家（仅在用户点保存后才落库）。</summary>
    public ExpertItem CreateNew()
    {
        return new ExpertItem
        {
            Id = Guid.NewGuid(),
            Name = string.Empty,
            Description = string.Empty,
            Category = "普通对话",
            SystemPrompt = string.Empty,
            Tools = new List<string>(),
            Memory = AgentMemoryMode.SlidingWindow,
            MaxHistory = 20,
        };
    }

    /// <summary>保存专家（新增或更新）。</summary>
    public async Task<(bool Success, string Error)> SaveAsync(ExpertItem item)
    {
        if (item == null || string.IsNullOrWhiteSpace(item.Name))
            return (false, "名称不能为空");

        try
        {
            var existing = Experts.FirstOrDefault(e => e.Id == item.Id);
            var def = new CustomAgentDefinition
            {
                Id = item.Id,
                Name = item.Name,
                Description = item.Description,
                Category = string.IsNullOrWhiteSpace(item.Category) ? "普通对话" : item.Category,
                SystemPrompt = item.SystemPrompt,
                ModelId = item.ModelId ?? string.Empty,
                Tools = item.Tools,
                Memory = item.Memory,
                MaxHistory = item.MaxHistory,
            };

            await _catalog.SaveAsync(def);
            // 配置变更后丢弃运行时缓存的旧 agent
            _runner.Invalidate(item.Id);
            await LoadAsync();
            StatusMessage = existing == null ? $"已创建：{item.Name}" : "已保存";
            SummonChanged?.Invoke();
            return (true, string.Empty);
        }
        catch (Exception ex)
        {
            return (false, $"保存失败：{ex.Message}");
        }
    }

    [RelayCommand]
    private async Task DeleteExpertAsync(ExpertItem item)
    {
        if (item == null) return;
        try
        {
            await _catalog.DeleteAsync(item.Id);
            _runner.Invalidate(item.Id);
            await LoadAsync();
            StatusMessage = $"已删除：{item.Name}";
            SummonChanged?.Invoke();
        }
        catch (Exception ex)
        {
            StatusMessage = $"删除失败：{ex.Message}";
        }
    }

    [RelayCommand]
    private void ToggleSummon(ExpertItem item)
    {
        if (item == null) return;
        item.IsSummoned = !item.IsSummoned;
        StatusMessage = item.IsSummoned ? $"已召唤：{item.Name}" : $"已取消召唤：{item.Name}";
        SummonChanged?.Invoke();
    }

    [RelayCommand]
    private void SelectCategory(string category)
    {
        SelectedCategory = category;
    }

    /// <summary>获取已召唤的专家列表。</summary>
    public List<ExpertItem> GetSummonedExperts() => Experts.Where(e => e.IsSummoned).ToList();

    /// <summary>创建编辑弹窗 ViewModel（从目录加载可用工具与模型列表）。</summary>
    public ExpertEditViewModel CreateEditViewModel(ExpertItem? existing)
    {
        var tools = _catalog.GetAvailableTools();
        var models = _catalog.GetAvailableModels();
        return new ExpertEditViewModel(existing, tools, models);
    }
}
