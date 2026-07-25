using System.Collections.ObjectModel;
using Acme.Notes.Application;
using Acme.Notes.AgentSource;
using Acme.Notes.Domain;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.Desktop.Shared.Shell;

/// <summary>提供 AgentMemoryMode 枚举值列表供 UI 绑定。</summary>
public static class AgentMemoryModeValues
{
    public static AgentMemoryMode[] All { get; } =
        { AgentMemoryMode.SlidingWindow, AgentMemoryMode.Full, AgentMemoryMode.Off };
}

/// <summary>专家（Agent）卡片行模型。</summary>
public sealed partial class ExpertItem : ObservableObject
{
    [ObservableProperty] private Guid _id;
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _description = string.Empty;
    [ObservableProperty] private string _category = "普通对话";
    [ObservableProperty] private string _systemPrompt = string.Empty;
    [ObservableProperty] private List<string> _tools = new();
    [ObservableProperty] private bool _isSummoned;

    /// <summary>记忆模式：SlidingWindow=滑动窗口, Full=全量, Off=关闭。</summary>
    [ObservableProperty] private AgentMemoryMode _memory = AgentMemoryMode.SlidingWindow;

    /// <summary>滑动窗口最大历史条数。</summary>
    [ObservableProperty] private int _maxHistory = 20;

    /// <summary>工具列表的文本表示（逗号分隔，供 UI 编辑）。</summary>
    public string ToolsText
    {
        get => Tools.Count == 0 ? string.Empty : string.Join(", ", Tools);
        set => Tools = value.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim()).Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
    }

    public string DisplayIcon => "🤖";
    public string SourceBadge => "本地";

    /// <summary>记忆模式显示文本。</summary>
    public string MemoryLabel => Memory switch
    {
        AgentMemoryMode.SlidingWindow => "滑动窗口",
        AgentMemoryMode.Full => "全量记忆",
        AgentMemoryMode.Off => "关闭",
        _ => Memory.ToString(),
    };
}

/// <summary>专家页面 ViewModel：从 Notes 中加载 Agent 类型笔记，展示为专家卡片。</summary>
public partial class ExpertsViewModel : ObservableObject
{
    private readonly IServiceProvider _serviceProvider;

    private INoteService NoteService => _noteService ??= _serviceProvider.GetRequiredService<INoteService>();
    private INoteService? _noteService;

    public ObservableCollection<ExpertItem> Experts { get; } = new();
    public ObservableCollection<ExpertItem> VisibleExperts { get; } = new();
    public ObservableCollection<string> Categories { get; } = new();

    [ObservableProperty] private string _selectedCategory = "全部";
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private bool _isEditing;
    [ObservableProperty] private ExpertItem? _editingItem;

    /// <summary>召唤变化事件（聊天页订阅）。</summary>
    public event Action? SummonChanged;

    public ExpertsViewModel(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    /// <summary>从 Notes 中加载所有 Agent 类型的笔记。</summary>
    public async Task LoadAsync()
    {
        try
        {
            var all = await NoteService.GetAllAsync();
            var agentNotes = all.Where(n => n.NodeType == NoteNodeType.Agent).ToList();

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                Experts.Clear();
                var catSet = new List<string>();

                foreach (var note in agentNotes)
                {
                    if (AgentSourceParser.TryParse(note.Content, out var fields, out _) && fields != null)
                    {
                        var item = new ExpertItem
                        {
                            Id = Guid.TryParse(note.Id, out var gid) ? gid : Guid.NewGuid(),
                            Name = fields.Name,
                            Description = fields.Description,
                            Category = string.IsNullOrWhiteSpace(fields.Category) ? "普通对话" : fields.Category,
                            SystemPrompt = fields.Prompt,
                            Tools = fields.Tools,
                            Memory = fields.Memory,
                            MaxHistory = fields.MaxHistory,
                        };
                        Experts.Add(item);

                        var cat = item.Category;
                        if (!catSet.Contains(cat)) catSet.Add(cat);
                    }
                    else
                    {
                        // 解析失败也展示，用标题当名称
                        var item = new ExpertItem
                        {
                            Id = Guid.TryParse(note.Id, out var gid) ? gid : Guid.NewGuid(),
                            Name = note.Title,
                            Description = "（源格式无效）",
                            Category = "普通对话",
                            SystemPrompt = note.Content,
                        };
                        Experts.Add(item);
                        if (!catSet.Contains("普通对话")) catSet.Add("普通对话");
                    }
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

    /// <summary>新建 Agent（保存为 NoteNodeType=Agent 的笔记）。</summary>
    [RelayCommand]
    private async Task AddExpertAsync()
    {
        try
        {
            var name = "新专家";
            var source = AgentSourceParser.Template(name);
            var dto = await NoteService.CreateAsync(new CreateNoteRequest
            {
                Title = name,
                Content = source,
                NodeType = NoteNodeType.Agent,
            });
            await LoadAsync();
            StatusMessage = $"已创建：{name}";
            SummonChanged?.Invoke();

            // 打开编辑
            if (Guid.TryParse(dto.Id, out var newId))
            {
                var created = Experts.FirstOrDefault(e => e.Id == newId);
                if (created != null) BeginEdit(created);
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"创建失败：{ex.Message}";
        }
    }

    private void BeginEdit(ExpertItem item)
    {
        EditingItem = new ExpertItem
        {
            Id = item.Id,
            Name = item.Name,
            Description = item.Description,
            Category = item.Category,
            SystemPrompt = item.SystemPrompt,
            Tools = new List<string>(item.Tools),
            Memory = item.Memory,
            MaxHistory = item.MaxHistory,
            IsSummoned = item.IsSummoned,
        };
        IsEditing = true;
    }

    [RelayCommand]
    private void EditExpert(ExpertItem item)
    {
        if (item == null) return;
        BeginEdit(item);
    }

    [RelayCommand]
    private async Task SaveExpertAsync()
    {
        if (EditingItem == null || string.IsNullOrWhiteSpace(EditingItem.Name)) return;

        try
        {
            var source = AgentSourceParser.Serialize(
                EditingItem.Name,
                EditingItem.Description,
                EditingItem.Category,
                EditingItem.Tools,
                EditingItem.Memory,
                EditingItem.MaxHistory,
                EditingItem.SystemPrompt);

            await NoteService.UpdateAsync(new UpdateNoteRequest
            {
                Id = EditingItem.Id.ToString(),
                Title = EditingItem.Name,
                Content = source,
                NodeType = NoteNodeType.Agent,
            });

            await LoadAsync();
            IsEditing = false;
            EditingItem = null;
            StatusMessage = "已保存";
            SummonChanged?.Invoke();
        }
        catch (Exception ex)
        {
            StatusMessage = $"保存失败：{ex.Message}";
        }
    }

    [RelayCommand]
    private void CancelEdit()
    {
        IsEditing = false;
        EditingItem = null;
    }

    [RelayCommand]
    private async Task DeleteExpertAsync(ExpertItem item)
    {
        if (item == null) return;
        try
        {
            await NoteService.DeleteAsync(item.Id.ToString());
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
}
