using System.Threading.Tasks;
using Aether.Server.Application.Contracts.DTOs.YushuToken;
using Volo.Abp.Application.Services;

namespace Aether.Server.Application.Contracts.Services.YushuToken;

public interface IYushuTokenAppService : IApplicationService
{
    /// <summary>
    /// 客户端上报/更新毓数平台 Token（需要 JWT 用户认证）
    /// </summary>
    Task SaveMyTokenAsync(SaveYushuTokenDto input);

    /// <summary>
    /// 获取当前用户的毓数 Token（需要 JWT 用户认证，不含 RefreshToken 等敏感字段）
    /// </summary>
    Task<YushuTokenDto> GetMyTokenAsync();
}
