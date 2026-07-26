using System.Collections.ObjectModel;
using Acme.Agents.AI;
using Acme.Notes.AgentSource;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Acme.Agents.Shell;

/// <summary>工具选项（工具池项，供自定义 Agent 勾选）。</summary>
public partial class ToolOption : ObservableObject
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    [ObservableProperty] private bool _isSelected;
}

/// <summary>工具类别分组（UI 分组展示用，支持类别整体勾选）。</summary>
public partial class ToolCategoryGroup : ObservableObject
{
    public string Category { get; set; } = string.Empty;
    public ObservableCollection<ToolOption> Tools { get; } = new();

    /// <summary>类别是否全选（双向：设 true 全选该类工具，工具变化时反向更新）。</summary>
    public bool IsAllSelected
    {
        get => Tools.Count > 0 && Tools.All(t => t.IsSelected);
        set
        {
            foreach (var t in Tools) t.IsSelected = value;
            OnPropertyChanged();
        }
    }

    /// <summary>工具变化时刷新 IsAllSelected。</summary>
    public void RefreshIsAllSelected() => OnPropertyChanged(nameof(IsAllSelected));
}

/// <summary>记忆模式下拉选项（值 + 中文显示）。</summary>
public class MemoryModeOption
{
    public AgentMemoryMode Value { get; set; }
    public string Display { get; set; } = string.Empty;
}

public sealed partial class ExpertEditViewModel : ObservableObject
{
    public ExpertEditViewModel(ExpertItem? existing, IReadOnlyList<CustomToolMeta> allTools, IReadOnlyList<CustomModelOption> allModels)
    {
        AvailableModels = allModels ?? Array.Empty<CustomModelOption>();

        var selectedSet = new HashSet<string>(existing?.Tools ?? Enumerable.Empty<string>());

        // 按分类分组构建工具列表
        foreach (var meta in allTools)
        {
            var opt = new ToolOption
            {
                Name = meta.Name,
                Description = meta.Description,
                Category = meta.Category,
                IsSelected = selectedSet.Contains(meta.Name),
            };

            var group = ToolCategories.FirstOrDefault(g => g.Category == meta.Category);
            if (group == null)
            {
                group = new ToolCategoryGroup { Category = meta.Category };
                ToolCategories.Add(group);
            }

            // 工具选中变化时刷新所属类别的全选状态
            opt.PropertyChanged += (_, _) => group.RefreshIsAllSelected();
            Tools.Add(opt);
            group.Tools.Add(opt);
        }

        if (existing != null)
        {
            Id = existing.Id;
            Name = existing.Name;
            Description = existing.Description;
            Category = existing.Category;
            SystemPrompt = existing.SystemPrompt;
            ModelId = existing.ModelId;
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
            ModelId = string.Empty;
            Memory = AgentMemoryMode.SlidingWindow;
            MaxHistory = 20;
            IsEditing = false;
        }

        SelectedModelOption = AvailableModels.FirstOrDefault(m => m.Id == ModelId)
            ?? AvailableModels.FirstOrDefault();
    }

    public Guid Id { get; }
    public bool IsEditing { get; }

    public string WindowTitle => IsEditing ? "编辑专家" : "创建专家";

    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _description = string.Empty;
    [ObservableProperty] private string _category = "普通对话";
    [ObservableProperty] private string _systemPrompt = string.Empty;
    [ObservableProperty] private string _modelId = string.Empty;
    [ObservableProperty] private AgentMemoryMode _memory = AgentMemoryMode.SlidingWindow;
    [ObservableProperty] private int _maxHistory = 20;
    [ObservableProperty] private string? _error;

    /// <summary>可选模型列表（由目录 IModelSource 提供）。</summary>
    public IReadOnlyList<CustomModelOption> AvailableModels { get; }

    /// <summary>当前选中的模型选项（双向：设置时同步 ModelId）。</summary>
    public CustomModelOption? SelectedModelOption
    {
        get => AvailableModels.FirstOrDefault(m => m.Id == ModelId);
        set
        {
            if (value is not null && value.Id != ModelId)
            {
                ModelId = value.Id;
                OnPropertyChanged();
            }
        }
    }

    partial void OnModelIdChanged(string value) => OnPropertyChanged(nameof(SelectedModelOption));

    /// <summary>扁平工具列表（所有工具，含选中状态）。</summary>
    public ObservableCollection<ToolOption> Tools { get; } = new();

    /// <summary>按类别分组的工具列表（UI 分组展示用）。</summary>
    public ObservableCollection<ToolCategoryGroup> ToolCategories { get; } = new();

    /// <summary>选中工具数量。</summary>
    public int SelectedToolCount => Tools.Count(t => t.IsSelected);

    /// <summary>记忆模式可选列表。</summary>
    public static MemoryModeOption[] MemoryModeOptions { get; } =
    {
        new() { Value = AgentMemoryMode.Off, Display = "无记忆 · 每次独立运行" },
        new() { Value = AgentMemoryMode.SlidingWindow, Display = "滑动窗口 · 仅最近 N 条（推荐）" },
        new() { Value = AgentMemoryMode.Full, Display = "完整记忆 · 保留全部历史" },
    };

    /// <summary>当前选中的记忆模式选项。</summary>
    public MemoryModeOption? SelectedMemoryModeOption
    {
        get => MemoryModeOptions.FirstOrDefault(o => o.Value == Memory);
        set
        {
            if (value != null && value.Value != Memory)
            {
                Memory = value.Value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>窗口大小输入框是否可见（仅滑动窗口时需要配置 N）。</summary>
    public bool IsWindowVisible => Memory == AgentMemoryMode.SlidingWindow;

    partial void OnMemoryChanged(AgentMemoryMode value)
    {
        OnPropertyChanged(nameof(IsWindowVisible));
        OnPropertyChanged(nameof(SelectedMemoryModeOption));
    }

    /// <summary>Name 变化时刷新保存按钮可用性（CanSave 是计算属性，需手动通知）。</summary>
    partial void OnNameChanged(string value) => OnPropertyChanged(nameof(CanSave));

    public bool HasError => !string.IsNullOrEmpty(Error);
    public bool CanSave => !string.IsNullOrWhiteSpace(Name);

    public event Action<ExpertItem>? SaveRequested;
    public event Action? CancelRequested;

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void Save()
    {
        if (!CanSave)
        {
            Error = "名称不能为空";
            return;
        }

        var toolList = Tools.Where(t => t.IsSelected).Select(t => t.Name).ToList();

        var item = new ExpertItem
        {
            Id = Id,
            Name = Name.Trim(),
            Description = Description.Trim(),
            Category = string.IsNullOrWhiteSpace(Category) ? "普通对话" : Category.Trim(),
            SystemPrompt = SystemPrompt,
            ModelId = ModelId ?? string.Empty,
            Tools = toolList,
            Memory = Memory,
            MaxHistory = MaxHistory,
        };

        SaveRequested?.Invoke(item);
    }

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void Cancel()
    {
        CancelRequested?.Invoke();
    }
}
