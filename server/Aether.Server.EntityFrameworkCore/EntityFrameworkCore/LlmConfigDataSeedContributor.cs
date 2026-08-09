using System;
using System.Threading.Tasks;
using Aether.Server.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Volo.Abp.Data;
using Volo.Abp.DependencyInjection;
using Volo.Abp.Domain.Repositories;

namespace Aether.Server.EntityFrameworkCore;

/// <summary>
/// LLM 配置数据种子：确保系统级默认 LLM 配置存在。
/// 仅在数据库中没有任何 IsSystem=true 的配置时运行（幂等）。
/// 管理员可后续通过 Blazor 管理页增删改系统配置。
/// </summary>
public class LlmConfigDataSeedContributor : IDataSeedContributor, ITransientDependency
{
    private readonly IRepository<AppLlmConfig, Guid> _repository;

    public LlmConfigDataSeedContributor(IRepository<AppLlmConfig, Guid> repository)
    {
        _repository = repository;
    }

    public async Task SeedAsync(DataSeedContext context)
    {
        var dbContext = await _repository.GetDbContextAsync();
        if (await dbContext.Set<AppLlmConfig>().AnyAsync(x => x.IsSystem))
            return;  // 已有系统配置，不再种子

        // 创建系统默认 LLM 配置（DeepSeek V4 Flash）
        var systemDefault = new AppLlmConfig(
            name: "默认-DeepSeek",
            modelName: "deepseek-v4-flash",
            apiKey: "",  // 管理员后续通过 Blazor 页面填入实际 API Key
            endpoint: "https://api.deepseek.com",
            temperature: 0.5f,
            isDefault: true,
            isSystem: true
        );

        await _repository.InsertAsync(systemDefault, autoSave: true);
    }
}
