using System;
using System.Linq;
using System.Security.Claims;
using Microsoft.AspNetCore.SignalR;

namespace Aether.Server.HttpApi.Hubs;

/// <summary>
/// 自定义 SignalR IUserIdProvider：使用 "sub" claim 作为用户 ID，
/// 与 ChatHub.ResolveIdentityIdAsync 的逻辑一致。
/// 确保 Clients.User(userId.ToString()) 能正确路由到目标用户的所有连接。
/// </summary>
public class SubClaimUserIdProvider : IUserIdProvider
{
    public string? GetUserId(HubConnectionContext connection)
    {
        // 优先读取 "sub" claim，与 ChatHub.ResolveIdentityIdAsync 一致
        var subClaim = connection.User?.FindFirst("sub");
        if (subClaim != null)
            return subClaim.Value;

        // 回退到 NameIdentifier claim
        var nameIdClaim = connection.User?.FindFirst(ClaimTypes.NameIdentifier);
        if (nameIdClaim != null)
            return nameIdClaim.Value;

        return null;
    }
}