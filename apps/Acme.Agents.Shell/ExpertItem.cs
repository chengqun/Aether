using Acme.Notes.AgentSource;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Acme.Agents.Shell;

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
