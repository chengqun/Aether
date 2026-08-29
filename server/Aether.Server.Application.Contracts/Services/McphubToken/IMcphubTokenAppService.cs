using System.Threading.Tasks;
using Aether.Server.Application.Contracts.DTOs.McphubToken;
using Volo.Abp.Application.Services;

namespace Aether.Server.Application.Contracts.Services.McphubToken;

public interface IMcphubTokenAppService : IApplicationService
{
    /// <summary>
    /// 客户端上报/更新 mcphub 网关 JWT（需要 JWT 用户认证，桌面端浏览器 OAuth 登录后调用）
    /// </summary>
    Task SaveMyTokenAsync(SaveMcphubTokenDto input);

    /// <summary>
    /// 获取当前用户的 mcphub JWT（需要 JWT 用户认证，手机端拉取；不含敏感字段）
    /// </summary>
    Task<McphubTokenDto> GetMyTokenAsync();
}
