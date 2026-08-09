using Acme.AppUpdate.Domain;
using Acme.AppAuth.Domain;
using System.Threading.Tasks;

namespace Acme.AppUpdate.Application;

public class UpdateService : IUpdateService
{
    private readonly IAppServerHttpClient _httpClient;

    public UpdateService(IAppServerHttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<CheckUpdateResult?> CheckUpdateAsync(AppPlatform platform, int currentVersionCode)
    {
        var input = new { Platform = (int)platform, CurrentVersionCode = currentVersionCode };
        // null = HTTP 失败（网络/服务端错误）；非 null = 成功（HasUpdate 区分有无更新）。
        // 之前 ?? new CheckUpdateResult() 把失败伪装成"无更新"，让 MeViewModel 的"检查更新失败"分支变死代码。
        return await _httpClient.PostAsync<CheckUpdateResult>("api/app/version-config/check-update", input);
    }
}