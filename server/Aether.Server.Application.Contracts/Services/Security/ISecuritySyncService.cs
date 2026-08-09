using System.Threading.Tasks;
using Volo.Abp.Application.Services;

namespace Aether.Server.Application.Contracts.Services.Security;

public interface ISecuritySyncService : IApplicationService
{
    Task<SecuritySyncResult> SyncFromExternalAsync();
    Task<StockPickingMomentResult> SyncStockPickingMomentsAsync();
}
