using System.Collections.ObjectModel;
using Acme.Notes.Application;
using Acme.Notes.AgentSource;
using Acme.Notes.Domain;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.Desktop.Shared.Shell;

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
            var source = AgentSourceParser.Serialize(
                item.Name,
                item.Description,
                item.Category,
                item.Tools,
                item.Memory,
                item.MaxHistory,
                item.SystemPrompt);

            var existing = Experts.FirstOrDefault(e => e.Id == item.Id);
            if (existing == null)
            {
                await NoteService.CreateAsync(new CreateNoteRequest
                {
                    Title = item.Name,
                    Content = source,
                    NodeType = NoteNodeType.Agent,
                });
            }
            else
            {
                await NoteService.UpdateAsync(new UpdateNoteRequest
                {
                    Id = item.Id.ToString(),
                    Title = item.Name,
                    Content = source,
                    NodeType = NoteNodeType.Agent,
                });
            }

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

    /// <summary>创建编辑弹窗 ViewModel（从 ToolRegistry 加载工具列表）。</summary>
    public ExpertEditViewModel CreateEditViewModel(ExpertItem? existing)
    {
        try
        {
            var registry = _serviceProvider.GetService<Aether.AI.Harness.Tools.ToolRegistry>();
            var tools = registry?.GetAllMetas() ?? Array.Empty<Aether.AI.Harness.Tools.ToolMeta>();
            return new ExpertEditViewModel(existing, tools);
        }
        catch
        {
            return new ExpertEditViewModel(existing, Array.Empty<Aether.AI.Harness.Tools.ToolMeta>());
        }
    }
}
