using Acme.Llm.Domain;
using Acme.Mobile.Mvvm.ViewModels;

namespace Acme.Mobile.Shared.Views;

public partial class LlmConfigPage : ContentPage
{
    private readonly LlmConfigViewModel _vm;
    private LlmConfigDto? _editingDto;

    public LlmConfigPage(LlmConfigViewModel viewModel)
    {
        InitializeComponent();
        _vm = viewModel;
        BindingContext = _vm;
    }

    /// <summary>点「＋添加模型」：进入新增态（显示表单，回填默认值）。</summary>
    private void OnAddClicked(object? sender, EventArgs e)
    {
        _editingDto = null;
        EditTitleLabel.Text = "添加新 LLM";
        NameEntry.Text = string.Empty;
        ModelEntry.Text = string.Empty;
        ApiKeyEntry.Text = string.Empty;
        EndpointEntry.Text = string.Empty;
        TemperatureEntry.Text = "0.5";
        IsDefaultSwitch.IsToggled = false;
        CancelButton.Text = "取消";
        EditFormPanel.IsVisible = true;
    }

    private void OnLlmTapped(object? sender, TappedEventArgs e)
    {
        if (e.Parameter is LlmConfigDto dto)
            StartEdit(dto);
    }

    private void StartEdit(LlmConfigDto dto)
    {
        _editingDto = dto;
        EditTitleLabel.Text = "编辑 LLM";
        NameEntry.Text = dto.Name;
        ModelEntry.Text = dto.ModelName;
        ApiKeyEntry.Text = dto.ApiKey;
        EndpointEntry.Text = dto.Endpoint;
        TemperatureEntry.Text = dto.Temperature.ToString();
        IsDefaultSwitch.IsToggled = dto.IsDefault;
        CancelButton.Text = "取消";
        EditFormPanel.IsVisible = true;
    }

    private void OnCancelEditClicked(object? sender, EventArgs e)
    {
        ResetForm();
    }

    /// <summary>收起表单（清空 + 隐藏编辑面板）。</summary>
    private void ResetForm()
    {
        _editingDto = null;
        EditTitleLabel.Text = "添加新 LLM";
        NameEntry.Text = string.Empty;
        ModelEntry.Text = string.Empty;
        ApiKeyEntry.Text = string.Empty;
        EndpointEntry.Text = string.Empty;
        TemperatureEntry.Text = "0.5";
        IsDefaultSwitch.IsToggled = false;
        EditFormPanel.IsVisible = false;
    }

    private async void OnSaveClicked(object? sender, EventArgs e)
    {
        var dto = new LlmConfigDto
        {
            Id = _editingDto?.Id ?? Guid.NewGuid(),
            Name = NameEntry.Text ?? string.Empty,
            ModelName = ModelEntry.Text ?? string.Empty,
            ApiKey = ApiKeyEntry.Text ?? string.Empty,
            Endpoint = EndpointEntry.Text ?? string.Empty,
            Temperature = float.TryParse(TemperatureEntry.Text, out var t) ? t : 0.5f,
            IsDefault = IsDefaultSwitch.IsToggled,
            IsSystem = _editingDto?.IsSystem ?? false,
        };
        ResetForm();
        await _vm.SaveLlmCommand.ExecuteAsync(dto);
    }
}
