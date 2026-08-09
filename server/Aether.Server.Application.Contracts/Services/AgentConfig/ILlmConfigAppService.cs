using System;
using System.Threading.Tasks;
using Aether.Server.Application.Contracts.DTOs.AgentConfig;
using Volo.Abp.Application.Dtos;
using Volo.Abp.Application.Services;

namespace Aether.Server.Application.Contracts.Services.AgentConfig;

public interface ILlmConfigAppService : IApplicationService
{
    /// <summary>获取当前用户可见的所有 LLM 配置（系统级只读 + 用户私有）</summary>
    Task<ListResultDto<LlmConfigDto>> GetMyLlmConfigsAsync();

    /// <summary>新增或更新用户级 LLM 配置（Id 有值则更新，否则新增）</summary>
    Task<LlmConfigDto> SetMyLlmConfigAsync(LlmConfigCreateDto input);

    /// <summary>设置用户级默认 LLM 配置（其他用户配置取消默认）</summary>
    Task SetDefaultLlmConfigAsync(Guid id);

    /// <summary>删除用户级 LLM 配置（检查是否有 Agent 在用）</summary>
    Task DeleteLlmConfigAsync(Guid id);

    /// <summary>获取系统级 LLM 配置（仅管理员）</summary>
    Task<ListResultDto<LlmConfigDto>> GetSystemLlmConfigsAsync();

    /// <summary>新增或更新系统级 LLM 配置（仅管理员，Id 有值则更新，否则新增）</summary>
    Task<LlmConfigDto> SetSystemLlmConfigAsync(LlmConfigCreateDto input);

    /// <summary>设置系统默认 LLM 配置（仅管理员）</summary>
    Task SetSystemDefaultLlmConfigAsync(Guid id);

    /// <summary>删除系统级 LLM 配置（仅管理员，检查是否有 Agent 在用）</summary>
    Task DeleteSystemLlmConfigAsync(Guid id);
}