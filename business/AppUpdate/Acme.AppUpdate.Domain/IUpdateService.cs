using System.Threading.Tasks;

namespace Acme.AppUpdate.Domain;

public interface IUpdateService
{
    Task<CheckUpdateResult?> CheckUpdateAsync(AppPlatform platform, int currentVersionCode);
}