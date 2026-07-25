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

    public async Task<CheckUpdateResult> CheckUpdateAsync(AppPlatform platform, int currentVersionCode)
    {
        var input = new { Platform = (int)platform, CurrentVersionCode = currentVersionCode };
        var result = await _httpClient.PostAsync<CheckUpdateResult>("api/app/version-config/check-update", input);
        return result ?? new CheckUpdateResult();
    }
}