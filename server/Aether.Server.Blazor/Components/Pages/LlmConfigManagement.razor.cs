using Aether.Server.Application.Contracts.DTOs.AgentConfig;
using Aether.Server.Application.Contracts.Services.AgentConfig;
using Microsoft.JSInterop;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Aether.Server.Blazor.Components.Pages;

public partial class LlmConfigManagement : AbpComponentBase
{
    // 系统级 LLM 配置
    protected List<LlmConfigDto> systemLlmConfigs = new();
    protected bool isLoadingSystemLlm = false;
    protected bool isSystemLlm = false;

    // 用户级 LLM 配置
    protected List<LlmConfigDto> llmConfigs = new();
    protected bool isLoadingLlm = false;
    protected bool showLlmModal = false;
    protected bool isEditingLlm = false;
    protected LlmConfigCreateDto editingLlm = new();

    protected bool isSaving = false;

    protected override async Task OnInitializedAsync()
    {
        await Task.WhenAll(LoadSystemLlmConfigsAsync(), LoadLlmConfigsAsync());
    }

    #region 系统级 LLM 配置

    protected async Task LoadSystemLlmConfigsAsync()
    {
        isLoadingSystemLlm = true;
        try
        {
            var result = await LlmConfigAppService.GetSystemLlmConfigsAsync();
            systemLlmConfigs = result.Items.ToList();
        }
        catch (Exception ex)
        {
            await Notify.Error($"加载系统LLM配置失败: {ex.Message}");
        }
        finally
        {
            isLoadingSystemLlm = false;
        }
    }

    protected void ShowCreateSystemLlmModal()
    {
        isSystemLlm = true;
        isEditingLlm = false;
        editingLlm = new LlmConfigCreateDto();
        showLlmModal = true;
    }

    protected void ShowEditSystemLlmModal(LlmConfigDto config)
    {
        isSystemLlm = true;
        isEditingLlm = true;
        editingLlm = new LlmConfigCreateDto
        {
            Id = config.Id,
            Name = config.Name,
            ModelName = config.ModelName,
            ApiKey = config.ApiKey,
            Endpoint = config.Endpoint,
            Temperature = config.Temperature,
            IsDefault = config.IsDefault
        };
        showLlmModal = true;
    }

    protected async Task SetSystemDefaultLlm(LlmConfigDto config)
    {
        try
        {
            await LlmConfigAppService.SetSystemDefaultLlmConfigAsync(config.Id);
            await Notify.Success("已设为系统默认");
            await LoadSystemLlmConfigsAsync();
        }
        catch (Exception ex)
        {
            await Notify.Error($"设置失败: {ex.Message}");
        }
    }

    protected async Task DeleteSystemLlmConfig(LlmConfigDto config)
    {
        if (!await JS.InvokeAsync<bool>("confirm", $"确定要删除系统LLM配置 \"{config.Name}\" 吗？"))
            return;

        try
        {
            await LlmConfigAppService.DeleteSystemLlmConfigAsync(config.Id);
            await Notify.Success("删除成功");
            await LoadSystemLlmConfigsAsync();
        }
        catch (Exception ex)
        {
            await Notify.Error($"删除失败: {ex.Message}");
        }
    }

    #endregion

    #region 用户级 LLM 配置

    protected async Task LoadLlmConfigsAsync()
    {
        isLoadingLlm = true;
        try
        {
            var result = await LlmConfigAppService.GetMyLlmConfigsAsync();
            // 仅展示用户级配置（排除系统级，系统级在上方单独展示）
            llmConfigs = result.Items.Where(c => !c.IsSystem).ToList();
        }
        catch (Exception ex)
        {
            await Notify.Error($"加载LLM配置失败: {ex.Message}");
        }
        finally
        {
            isLoadingLlm = false;
        }
    }

    protected void ShowCreateLlmModal()
    {
        isSystemLlm = false;
        isEditingLlm = false;
        editingLlm = new LlmConfigCreateDto();
        showLlmModal = true;
    }

    protected void ShowEditLlmModal(LlmConfigDto config)
    {
        isSystemLlm = false;
        isEditingLlm = true;
        editingLlm = new LlmConfigCreateDto
        {
            Id = config.Id,
            Name = config.Name,
            ModelName = config.ModelName,
            ApiKey = config.ApiKey,
            Endpoint = config.Endpoint,
            Temperature = config.Temperature,
            IsDefault = config.IsDefault
        };
        showLlmModal = true;
    }

    protected void CloseLlmModal()
    {
        showLlmModal = false;
    }

    protected async Task SaveLlmConfig()
    {
        isSaving = true;
        try
        {
            if (isSystemLlm)
            {
                await LlmConfigAppService.SetSystemLlmConfigAsync(editingLlm);
            }
            else
            {
                await LlmConfigAppService.SetMyLlmConfigAsync(editingLlm);
            }
            await Notify.Success(isEditingLlm ? "更新成功" : "创建成功");
            CloseLlmModal();
            if (isSystemLlm)
            {
                await LoadSystemLlmConfigsAsync();
            }
            else
            {
                await LoadLlmConfigsAsync();
            }
        }
        catch (Exception ex)
        {
            await Notify.Error($"保存失败: {ex.Message}");
        }
        finally
        {
            isSaving = false;
        }
    }

    protected async Task DeleteLlmConfig(LlmConfigDto config)
    {
        if (!await JS.InvokeAsync<bool>("confirm", $"确定要删除LLM配置 \"{config.Name}\" 吗？"))
            return;

        try
        {
            await LlmConfigAppService.DeleteLlmConfigAsync(config.Id);
            await Notify.Success("删除成功");
            await LoadLlmConfigsAsync();
        }
        catch (Exception ex)
        {
            await Notify.Error($"删除失败: {ex.Message}");
        }
    }

    #endregion

    protected static string MaskApiKey(string? apiKey)
    {
        if (string.IsNullOrEmpty(apiKey)) return "***";
        if (apiKey.Length <= 8) return "***";
        return apiKey[..4] + "****" + apiKey[^4..];
    }
}
