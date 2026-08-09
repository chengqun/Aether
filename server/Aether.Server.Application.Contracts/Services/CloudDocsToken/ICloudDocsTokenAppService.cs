using System.Threading.Tasks;
using Aether.Server.Application.Contracts.DTOs.CloudDocsToken;
using Volo.Abp.Application.Services;

namespace Aether.Server.Application.Contracts.Services.CloudDocsToken;

public interface ICloudDocsTokenAppService : IApplicationService
{
    /// <summary>
    /// 客户端上报/更新云文档 sid（需要 JWT 用户认证）
    /// </summary>
    Task SaveMyTokenAsync(SaveCloudDocsTokenDto input);

    /// <summary>
    /// 获取当前用户的云文档 sid（需要 JWT 用户认证）
    /// </summary>
    Task<CloudDocsTokenDto> GetMyTokenAsync();
}
