using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Aether.Server.Application.Contracts.DTOs.Moment;
using Volo.Abp.Application.Services;

namespace Aether.Server.Application.Contracts.Services.Moment;

public interface IMomentAppService : IApplicationService
{
    // 动态创建和查询
    Task<MomentDto> CreateMomentAsync(CreateMomentDto input);
    Task<List<MomentDto>> GetFriendsMomentsAsync(int limit = 50, int offset = 0, DateTime? since = null);
    // 统一的动态查询接口（支持好友/股票/设备/群组）
    Task<List<MomentDto>> GetMomentsAsync(string targetId, string type, int limit = 50, int offset = 0, DateTime? since = null);
    Task DeleteMomentAsync(Guid id);
}