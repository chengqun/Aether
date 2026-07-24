using System.Threading.Tasks;
using Acme.Ctp.Application;
using Acme.Ctp.Domain;

namespace Acme.Ctp.AI.Tools;

public class CtpLoginTool
{
    private readonly ICtpTradingService _service;

    public CtpLoginTool(ICtpTradingService service)
    {
        _service = service;
    }

    public async Task<string> LoginAsync(
        string frontAddress, string mdFrontAddress, string brokerId, string userId,
        string password, string appId, string authCode)
    {
        if (_service.IsConnected)
        {
            return "已有活跃连接，请先登出";
        }

        var request = new CtpLoginRequest
        {
            FrontAddress = frontAddress,
            MdFrontAddress = mdFrontAddress,
            BrokerId = brokerId,
            UserId = userId,
            Password = password,
            AppId = appId,
            AuthCode = authCode
        };

        try
        {
            var result = await _service.LoginAsync(request);
            return $"登录成功！交易日={result.TradingDay} FrontID={result.FrontId} SessionID={result.SessionId}";
        }
        catch (Exception ex)
        {
            return $"登录失败: {ex.Message}";
        }
    }

    public async Task<string> LogoutAsync()
    {
        await _service.LogoutAsync();
        return "已登出";
    }

    public string GetStatus()
    {
        if (_service.IsConnected)
        {
            var result = _service.LoginResult;
            return $"已连接 - 交易日={result?.TradingDay} FrontID={result?.FrontId}";
        }
        return "未连接";
    }
}