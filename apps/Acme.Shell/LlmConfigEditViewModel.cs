using Acme.Llm.Domain;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Acme.Shell;

public sealed partial class LlmConfigEditViewModel : ObservableObject
{
    public LlmConfigEditViewModel(LlmConfigDto? existingConfig = null)
    {
        if (existingConfig != null)
        {
            Id = existingConfig.Id;
            Name = existingConfig.Name;
            ModelName = existingConfig.ModelName;
            ApiKey = existingConfig.ApiKey;
            Endpoint = existingConfig.Endpoint;
            Temperature = existingConfig.Temperature;
            IsDefault = existingConfig.IsDefault;
            IsSystem = existingConfig.IsSystem;
            IsEditing = true;
        }
        else
        {
            Id = Guid.Empty;
            IsDefault = false;
            IsSystem = false;
            IsEditing = false;
            Temperature = 0.5f;
        }
    }

    public Guid Id { get; }
    public bool IsEditing { get; }
    public bool IsSystem { get; }

    public string WindowTitle => IsEditing ? "编辑 LLM 配置" : "新增 LLM 配置";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    private string _name = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    private string _modelName = "";

    [ObservableProperty]
    private string _apiKey = "";

    [ObservableProperty]
    private string _endpoint = "";

    [ObservableProperty]
    private float _temperature = 0.5f;

    [ObservableProperty]
    private bool _isDefault;

    [ObservableProperty]
    private string? _error;

    public bool HasError => !string.IsNullOrEmpty(Error);

    public bool CanSave => !string.IsNullOrWhiteSpace(Name) && !string.IsNullOrWhiteSpace(ModelName);

    public event Action<LlmConfigDto>? SaveRequested;
    public event Action? CancelRequested;

    [RelayCommand]
    private void Save()
    {
        if (!CanSave)
        {
            Error = "配置名称和模型名称不能为空";
            return;
        }

        var config = new LlmConfigDto
        {
            Id = Id,
            Name = Name,
            ModelName = ModelName,
            ApiKey = ApiKey,
            Endpoint = string.IsNullOrWhiteSpace(Endpoint) ? "https://api.deepseek.com/v1" : Endpoint,
            Temperature = Temperature,
            IsDefault = IsDefault,
            IsSystem = IsSystem,
        };

        SaveRequested?.Invoke(config);
    }

    [RelayCommand]
    private void Cancel()
    {
        CancelRequested?.Invoke();
    }
}