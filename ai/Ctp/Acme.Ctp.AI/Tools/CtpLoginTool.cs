using System;
using System.Threading.Tasks;
using Acme.Ctp.Infrastructure;
using Ctp.Net.Core.Trader;

namespace Acme.Ctp.AI.Tools;

public class CtpLoginTool
{
    private readonly CtpSessionService _session;

    public CtpLoginTool(CtpSessionService session)
    {
        _session = session;
    }

    public async Task<string> LoginAsync(
        string frontAddress, string brokerId, string userId,
        string password, string appId, string authCode)
    {
        if (_session.IsConnected)
        {
            return "已有活跃连接，请先登出";
        }

        var request = new CtpConnectRequest
        {
            FrontAddress = frontAddress,
            BrokerId = brokerId,
            UserId = userId,
            Password = password,
            AppId = appId,
            AuthCode = authCode
        };

        try
        {
            var result = await _session.LoginAsync(request);
            return $"登录成功！交易日={result.TradingDay} FrontID={result.FrontID} SessionID={result.SessionID}";
        }
        catch (CtpBusinessException ex)
        {
            return $"登录失败 [{ex.ErrorId}]: {ex.ErrorMsg}";
        }
        catch (Exception ex)
        {
            return $"登录失败: {ex.Message}";
        }
    }

    public string Logout()
    {
        _session.Logout();
        return "已登出";
    }

    public string GetStatus()
    {
        if (_session.IsConnected)
        {
            var result = _session.LoginResult;
            return $"已连接 - 交易日={result?.TradingDay} FrontID={result?.FrontID}";
        }
        return "未连接";
    }
}
