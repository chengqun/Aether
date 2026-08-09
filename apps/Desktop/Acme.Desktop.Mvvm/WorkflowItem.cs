using Acme.Notes.Domain.Agents;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Acme.Desktop.Mvvm;

/// <summary>工作流卡片行模型。</summary>
public sealed partial class WorkflowItem : ObservableObject
{
    [ObservableProperty] private Guid _id;
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _description = string.Empty;
    [ObservableProperty] private string _category = "工作流";
    [ObservableProperty] private Guid _entryAgentId;
    [ObservableProperty] private List<Guid> _participants = new();
    [ObservableProperty] private List<WorkflowHandoffField> _handoffs = new();
    [ObservableProperty] private string? _handoffInstructions;
    [ObservableProperty] private bool _isSummoned;

    public string DisplayIcon => "🔗";
    public string ParticipantSummary => Participants.Count + " 个参与者";
}
