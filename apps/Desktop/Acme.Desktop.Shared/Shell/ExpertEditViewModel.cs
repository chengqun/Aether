using System.Collections.ObjectModel;
using Acme.Notes.AgentSource;
using Aether.AI.Harness.Tools;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Acme.Desktop.Shared.Shell;

/// <summary>工具分类组。</summary>
public sealed partial class ToolCategoryGroup : ObservableObject
{
    public string Category { get; }
    public ObservableCollection<ToolToggleItem> Tools { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExpandIndicator))]
    private bool _isExpanded = true;

    public string ExpandIndicator => IsExpanded ? "▼" : "▶";
    public int ToolCount => Tools.Count;

    public ToolCategoryGroup(string category)
    {
        Category = category;
    }

    [RelayCommand]
    private void ToggleExpanded() => IsExpanded = !IsExpanded;
}

/// <summary>可勾选的工具项。</summary>
public sealed partial class ToolToggleItem : ObservableObject
{
    [ObservableProperty]
    private bool _isEnabled;

    public string Name { get; }
    public string Description { get; }
    public string Category { get; }
    public string DisplayName => $"[{Category}] {Name}";

    public ToolToggleItem(string name, string description, string category, bool isEnabled = false)
    {
        Name = name;
        Description = description;
        Category = category;
        IsEnabled = isEnabled;
    }
}

public sealed partial class ExpertEditViewModel : ObservableObject
{
    public ExpertEditViewModel(ExpertItem? existing, IReadOnlyList<ToolMeta> allTools)
    {
        var selectedSet = new HashSet<string>(existing?.Tools ?? Enumerable.Empty<string>());

        var groups = new Dictionary<string, ToolCategoryGroup>();
        foreach (var meta in allTools)
        {
            if (!groups.TryGetValue(meta.Category, out var group))
            {
                group = new ToolCategoryGroup(meta.Category);
                groups[meta.Category] = group;
            }
            group.Tools.Add(new ToolToggleItem(meta.Name, meta.Description, meta.Category, selectedSet.Contains(meta.Name)));
        }

        ToolGroups = new ObservableCollection<ToolCategoryGroup>(groups.Values.OrderBy(g => g.Category));
        FlatTools = new ObservableCollection<ToolToggleItem>(
            groups.Values.SelectMany(g => g.Tools).OrderBy(t => t.Category).ThenBy(t => t.Name));

        if (existing != null)
        {
            Id = existing.Id;
            Name = existing.Name;
            Description = existing.Description;
            Category = existing.Category;
            SystemPrompt = existing.SystemPrompt;
            Memory = existing.Memory;
            MaxHistory = existing.MaxHistory;
            IsEditing = true;
        }
        else
        {
            Id = Guid.NewGuid();
            Name = string.Empty;
            Description = string.Empty;
            Category = "普通对话";
            SystemPrompt = string.Empty;
            Memory = AgentMemoryMode.SlidingWindow;
            MaxHistory = 20;
            IsEditing = false;
        }
    }

    public Guid Id { get; }
    public bool IsEditing { get; }

    public string WindowTitle => IsEditing ? "编辑专家" : "新建专家";

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _description = string.Empty;

    [ObservableProperty]
    private string _category = "普通对话";

    [ObservableProperty]
    private string _systemPrompt = string.Empty;

    [ObservableProperty]
    private AgentMemoryMode _memory = AgentMemoryMode.SlidingWindow;

    [ObservableProperty]
    private int _maxHistory = 20;

    [ObservableProperty]
    private string? _error;

    /// <summary>按分类分组的可勾选工具列表。</summary>
    public ObservableCollection<ToolCategoryGroup> ToolGroups { get; }

    /// <summary>扁平化的工具列表（简化版UI用）。</summary>
    public ObservableCollection<ToolToggleItem> FlatTools { get; }

    public bool HasError => !string.IsNullOrEmpty(Error);
    public bool CanSave => !string.IsNullOrWhiteSpace(Name);

    public AgentMemoryMode[] MemoryModes { get; } =
        { AgentMemoryMode.SlidingWindow, AgentMemoryMode.Full, AgentMemoryMode.Off };

    public event Action<ExpertItem>? SaveRequested;
    public event Action? CancelRequested;

    [RelayCommand]
    private void Save()
    {
        if (!CanSave)
        {
            Error = "名称不能为空";
            return;
        }

        var toolList = ToolGroups
            .SelectMany(g => g.Tools)
            .Where(t => t.IsEnabled)
            .Select(t => t.Name)
            .ToList();

        var item = new ExpertItem
        {
            Id = Id,
            Name = Name.Trim(),
            Description = Description.Trim(),
            Category = string.IsNullOrWhiteSpace(Category) ? "普通对话" : Category.Trim(),
            SystemPrompt = SystemPrompt,
            Tools = toolList,
            Memory = Memory,
            MaxHistory = MaxHistory,
        };

        SaveRequested?.Invoke(item);
    }

    [RelayCommand]
    private void Cancel()
    {
        CancelRequested?.Invoke();
    }
}
