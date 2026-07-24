using System;
using System.Threading.Tasks;

namespace Acme.Securities.Application;

public interface ISecurityClientSyncService
{
    Task<SecuritySyncResult> SyncFromServerAsync();
}

public class SecuritySyncResult
{
    public int SyncedCount { get; set; }
    public bool IsFullSync { get; set; }
    public DateTime? LastSyncTime { get; set; }
}
