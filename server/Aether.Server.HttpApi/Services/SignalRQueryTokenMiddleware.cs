using Microsoft.AspNetCore.Http;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace Aether.Server.HttpApi.Services;

/// <summary>
/// 从 WebSocket 升级请求的 access_token 查询参数提取 token，
/// 注入到 Authorization 头，使 OpenIddict 验证中间件能认证 WebSocket 连接。
/// </summary>
public class SignalRQueryTokenMiddleware
{
    private readonly RequestDelegate _next;

    public SignalRQueryTokenMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Headers.TryGetValue("Upgrade", out var upgradeHeader)
            && upgradeHeader.ToString().Equals("websocket", StringComparison.OrdinalIgnoreCase))
        {
            var token = context.Request.Query["access_token"].FirstOrDefault();
            if (!string.IsNullOrEmpty(token))
            {
                context.Request.Headers.Append("Authorization", $"Bearer {token}");
            }
        }

        await _next(context);
    }
}
