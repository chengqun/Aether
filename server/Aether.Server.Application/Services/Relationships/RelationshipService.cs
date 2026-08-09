using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Aether.Server.Application.Contracts.DTOs.Relationships;
using Aether.Server.Application.Contracts.Services.Relationships;
using Aether.Server.Domain.Entities;
using Aether.Server.Domain.Shared.Enums;
using Aether.Server.Domain.Services.Relationships;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Volo.Abp.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Volo.Abp.Uow;
using Microsoft.AspNetCore.Mvc;
using Volo.Abp.Identity;
using Volo.Abp.Domain.Repositories;
using Volo.Abp.Features;
using Volo.Abp;

namespace Aether.Server.Application.Services.Relationships
{
    /// <summary>
    /// 关系服务
    /// 统一处理不同类型的关系管理
    /// </summary>
    [Authorize]
    [Route("api/app/relationship")]
    public class RelationshipService : ApplicationService, IRelationshipService
    {
        private readonly IRelationshipManager<AppRelationship, IdentityUser, Guid> _friendshipManager;
        private readonly IRelationshipManager<AppRelationship, AppDevice, Guid> _deviceRelationshipManager;
        private readonly IRelationshipManager<AppRelationship, AppGroup, Guid> _groupRelationshipManager;
        private readonly IRelationshipManager<AppRelationship, AppSecurity, Guid> _stockRelationshipManager;
        private readonly IRelationshipManager<AppRelationship, AppServiceAccount, Guid> _serviceAccountRelationshipManager;
        private readonly ILogger<RelationshipService> _logger;

        private readonly IFeatureChecker _featureChecker;

        public RelationshipService(
            IRelationshipManager<AppRelationship, IdentityUser, Guid> friendshipManager,
            IRelationshipManager<AppRelationship, AppDevice, Guid> deviceRelationshipManager,
            IRelationshipManager<AppRelationship, AppGroup, Guid> groupRelationshipManager,
            IRelationshipManager<AppRelationship, AppSecurity, Guid> stockRelationshipManager,
            IRelationshipManager<AppRelationship, AppServiceAccount, Guid> serviceAccountRelationshipManager,
            ILogger<RelationshipService> logger,
            IFeatureChecker featureChecker)
        {
            _friendshipManager = friendshipManager;
            _deviceRelationshipManager = deviceRelationshipManager;
            _groupRelationshipManager = groupRelationshipManager;
            _stockRelationshipManager = stockRelationshipManager;
            _serviceAccountRelationshipManager = serviceAccountRelationshipManager;
            _logger = logger;
            _featureChecker = featureChecker;
        }

        [UnitOfWork]
        [HttpPost("relationship/{targetId}")]
        [Authorize]
        public async Task<RelationshipDto> CreateRelationshipAsync(RelationshipType type, [FromRoute] string targetId)
        {
            var currentUserId = CurrentUser.Id.Value;
            _logger.LogInformation("User {UserId} creating {Type} relationship with {TargetId}",
                currentUserId, type, targetId);

            try
            {
                if (type == RelationshipType.Stock && !await _featureChecker.IsEnabledAsync("StockApp.Quote"))
                {
                    throw new BusinessException("Aether:FeatureNotEnabled", "当前租户未启用股票功能");
                }

                object relationship = type switch
                {
                    RelationshipType.Friendship => await _friendshipManager.CreateRelationshipAsync(currentUserId, targetId),
                    RelationshipType.Device => await _deviceRelationshipManager.CreateRelationshipAsync(currentUserId, targetId),
                    RelationshipType.Group => await _groupRelationshipManager.CreateRelationshipAsync(currentUserId, targetId),
                    RelationshipType.Stock => await _stockRelationshipManager.CreateRelationshipAsync(currentUserId, targetId),
                    RelationshipType.ServiceAccount => await _serviceAccountRelationshipManager.CreateRelationshipAsync(currentUserId, targetId),
                    _ => throw new ArgumentException($"Invalid relationship type: {type}")
                };

                _logger.LogInformation("{Type} relationship created successfully with {TargetId}", type, targetId);

                return await ConvertAppRelationshipToDto((AppRelationship)relationship);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating {Type} relationship with {TargetId}", type, targetId);
                throw;
            }
        }

        [UnitOfWork]
        [HttpPost("accept-relationship/{relationshipId}")]
        public async Task<RelationshipDto> AcceptRelationshipAsync([FromQuery] RelationshipType type, [FromRoute] string relationshipId)
        {
            var currentUserId = CurrentUser.Id.Value;
            _logger.LogInformation("User {UserId} accepting {Type} relationship {RelationshipId}",
                currentUserId, type, relationshipId);

            try
            {
                object relationship = type switch
                {
                    RelationshipType.Friendship => await _friendshipManager.AcceptRelationshipAsync(ConvertToId<Guid>(relationshipId), currentUserId),
                    RelationshipType.Device => await _deviceRelationshipManager.AcceptRelationshipAsync(ConvertToId<Guid>(relationshipId), currentUserId),
                    RelationshipType.Group => await _groupRelationshipManager.AcceptRelationshipAsync(ConvertToId<Guid>(relationshipId), currentUserId),
                    RelationshipType.Stock => await _stockRelationshipManager.AcceptRelationshipAsync(ConvertToId<Guid>(relationshipId), currentUserId),
                    RelationshipType.ServiceAccount => await _serviceAccountRelationshipManager.AcceptRelationshipAsync(ConvertToId<Guid>(relationshipId), currentUserId),
                    _ => throw new ArgumentException($"Invalid relationship type: {type}")
                };

                _logger.LogInformation("{Type} relationship {RelationshipId} accepted successfully", type, relationshipId);

                return await ConvertAppRelationshipToDto((AppRelationship)relationship);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error accepting {Type} relationship {RelationshipId}", type, relationshipId);
                throw;
            }
        }

        [UnitOfWork]
        [HttpPost("reject-relationship/{relationshipId}")]
        public async Task<RelationshipDto> RejectRelationshipAsync([FromQuery] RelationshipType type, [FromRoute] string relationshipId)
        {
            var currentUserId = CurrentUser.Id.Value;
            _logger.LogInformation("User {UserId} rejecting {Type} relationship {RelationshipId}",
                currentUserId, type, relationshipId);

            try
            {
                object relationship = type switch
                {
                    RelationshipType.Friendship => await _friendshipManager.RejectRelationshipAsync(ConvertToId<Guid>(relationshipId), currentUserId),
                    RelationshipType.Device => await _deviceRelationshipManager.RejectRelationshipAsync(ConvertToId<Guid>(relationshipId), currentUserId),
                    RelationshipType.Group => await _groupRelationshipManager.RejectRelationshipAsync(ConvertToId<Guid>(relationshipId), currentUserId),
                    RelationshipType.Stock => await _stockRelationshipManager.RejectRelationshipAsync(ConvertToId<Guid>(relationshipId), currentUserId),
                    RelationshipType.ServiceAccount => await _serviceAccountRelationshipManager.RejectRelationshipAsync(ConvertToId<Guid>(relationshipId), currentUserId),
                    _ => throw new ArgumentException($"Invalid relationship type: {type}")
                };

                _logger.LogInformation("{Type} relationship {RelationshipId} rejected successfully", type, relationshipId);

                return await ConvertAppRelationshipToDto((AppRelationship)relationship);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error rejecting {Type} relationship {RelationshipId}", type, relationshipId);
                throw;
            }
        }

        [UnitOfWork]
        [HttpDelete("relationship/{relationshipId}")]
        public async Task RemoveRelationshipAsync([FromQuery] RelationshipType type, [FromRoute] string relationshipId)
        {
            var currentUserId = CurrentUser.Id.Value;
            _logger.LogInformation("User {UserId} removing {Type} relationship {RelationshipId}",
                currentUserId, type, relationshipId);

            try
            {
                switch (type)
                {
                    case RelationshipType.Friendship:
                        await _friendshipManager.RemoveRelationshipAsync(ConvertToId<Guid>(relationshipId), currentUserId);
                        break;
                    case RelationshipType.Device:
                        await _deviceRelationshipManager.RemoveRelationshipAsync(ConvertToId<Guid>(relationshipId));
                        break;
                    case RelationshipType.Group:
                        await _groupRelationshipManager.RemoveRelationshipAsync(ConvertToId<Guid>(relationshipId), currentUserId);
                        break;
                    case RelationshipType.Stock:
                        await _stockRelationshipManager.RemoveRelationshipAsync(ConvertToId<Guid>(relationshipId));
                        break;
                    case RelationshipType.ServiceAccount:
                        await _serviceAccountRelationshipManager.RemoveRelationshipAsync(ConvertToId<Guid>(relationshipId));
                        break;
                    default:
                        throw new ArgumentException($"Invalid relationship type: {type}");
                }

                _logger.LogInformation("{Type} relationship {RelationshipId} removed successfully", type, relationshipId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error removing {Type} relationship {RelationshipId}", type, relationshipId);
                throw;
            }
        }

        [HttpGet("search-all-targets")]
        [Authorize]
        public async Task<List<TargetDto>> SearchAllTargetsAsync(string keyword)
        {
            var currentUserId = CurrentUser.Id.Value;
            _logger.LogInformation("User {UserId} searching all targets with keyword: {Keyword}",
                currentUserId, keyword);

            try
            {
                var allTargets = new List<TargetDto>();

                var friendTargets = (await _friendshipManager.SearchTargetsAsync(keyword)).Select(t => ConvertUserToTargetDto(t));
                allTargets.AddRange(friendTargets);

                var deviceTargets = (await _deviceRelationshipManager.SearchTargetsAsync(keyword)).Select(t => ConvertDeviceToTargetDto(t));
                allTargets.AddRange(deviceTargets);

                var groupTargets = (await _groupRelationshipManager.SearchTargetsAsync(keyword)).Select(t => ConvertGroupToTargetDto(t));
                allTargets.AddRange(groupTargets);

                if (await _featureChecker.IsEnabledAsync("StockApp.Quote"))
                {
                    var stockTargets = (await _stockRelationshipManager.SearchTargetsAsync(keyword)).Select(t => ConvertSecurityToTargetDto(t));
                    allTargets.AddRange(stockTargets);
                }

                var serviceAccountTargets = (await _serviceAccountRelationshipManager.SearchTargetsAsync(keyword)).Select(t => ConvertServiceAccountToTargetDto(t));
                allTargets.AddRange(serviceAccountTargets);

                _logger.LogInformation("Found {Count} total targets for keyword: {Keyword}",
                    allTargets.Count, keyword);

                return allTargets;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error searching all targets with keyword: {Keyword}", keyword);
                throw;
            }
        }

        [HttpGet("accepted-contacts")]
        [Authorize]
        public async Task<ContactBookDto> GetAcceptedContactsAsync()
        {
            var currentUserId = CurrentUser.Id.Value;
            _logger.LogInformation("User {UserId} retrieving accepted contacts", currentUserId);

            try
            {
                var contactBook = new ContactBookDto();

                var friends = await _friendshipManager.GetUserRelationshipsAsync(currentUserId);
                contactBook.Friends = new List<RelationshipDto>();
                foreach (var r in friends)
                    contactBook.Friends.Add(await ConvertAppRelationshipToDto(r));

                var devices = await _deviceRelationshipManager.GetUserRelationshipsAsync(currentUserId);
                contactBook.Devices = new List<RelationshipDto>();
                foreach (var r in devices)
                    contactBook.Devices.Add(await ConvertAppRelationshipToDto(r));

                var groups = await _groupRelationshipManager.GetUserRelationshipsAsync(currentUserId);
                contactBook.Groups = new List<RelationshipDto>();
                foreach (var r in groups)
                    contactBook.Groups.Add(await ConvertAppRelationshipToDto(r));

                if (await _featureChecker.IsEnabledAsync("StockApp.Quote"))
                {
                    var stocks = await _stockRelationshipManager.GetUserRelationshipsAsync(currentUserId);
                    contactBook.Stocks = new List<RelationshipDto>();
                    foreach (var r in stocks)
                        contactBook.Stocks.Add(await ConvertAppRelationshipToDto(r));
                }
                else
                {
                    contactBook.Stocks = new List<RelationshipDto>();
                }

                var serviceAccounts = await _serviceAccountRelationshipManager.GetUserRelationshipsAsync(currentUserId);
                contactBook.ServiceAccounts = new List<RelationshipDto>();
                foreach (var r in serviceAccounts)
                    contactBook.ServiceAccounts.Add(await ConvertAppRelationshipToDto(r));

                _logger.LogInformation("Retrieved accepted contacts with {DeviceCount} devices, {FriendCount} friends, {GroupCount} groups, {StockCount} stocks, {ServiceAccountCount} serviceAccounts",
                    contactBook.Devices.Count, contactBook.Friends.Count, contactBook.Groups.Count, contactBook.Stocks.Count, contactBook.ServiceAccounts.Count);

                return contactBook;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving accepted contacts");
                throw;
            }
        }

        [HttpGet("pending-requests")]
        [Authorize]
        public async Task<ContactBookDto> GetPendingRequestsAsync()
        {
            var currentUserId = CurrentUser.Id.Value;
            _logger.LogInformation("User {UserId} retrieving pending requests", currentUserId);

            try
            {
                var contactBook = new ContactBookDto();

                var friends = await _friendshipManager.GetPendingRequestsAsync(currentUserId);
                contactBook.Friends = new List<RelationshipDto>();
                foreach (var r in friends)
                    contactBook.Friends.Add(await ConvertAppRelationshipToDto(r));

                var devices = await _deviceRelationshipManager.GetPendingRequestsAsync(currentUserId);
                contactBook.Devices = new List<RelationshipDto>();
                foreach (var r in devices)
                    contactBook.Devices.Add(await ConvertAppRelationshipToDto(r));

                var groups = await _groupRelationshipManager.GetPendingRequestsAsync(currentUserId);
                contactBook.Groups = new List<RelationshipDto>();
                foreach (var r in groups)
                    contactBook.Groups.Add(await ConvertAppRelationshipToDto(r));

                contactBook.Stocks = new List<RelationshipDto>();

                _logger.LogInformation("Retrieved pending requests with {DeviceCount} devices, {FriendCount} friends, {GroupCount} groups",
                    contactBook.Devices.Count, contactBook.Friends.Count, contactBook.Groups.Count);

                return contactBook;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving pending requests");
                throw;
            }
        }

        private async Task<RelationshipDto> ConvertAppRelationshipToDto(AppRelationship relationship)
        {
            string targetName = "";
            string targetId = relationship.TargetId;
            string authorizationCode = "";
            string deviceType = "";

            switch (relationship.TargetType)
            {
                case RelationshipType.Friendship:
                    var userRepository = ServiceProvider.GetRequiredService<IRepository<IdentityUser, Guid>>();
                    // 待处理请求：UserId是发送者，TargetId是接收者
                    // 已接受关系：UserId是持有者，TargetId是对方
                    if (relationship.Status == RelationshipStatus.Pending)
                    {
                        var requester = await userRepository.GetAsync(relationship.UserId);
                        targetName = requester.UserName;
                    }
                    else
                    {
                        var targetUser = await userRepository.GetAsync(Guid.Parse(relationship.TargetId));
                        targetName = targetUser.UserName;
                    }
                    break;
                case RelationshipType.Device:
                    var deviceRepository = ServiceProvider.GetRequiredService<IRepository<AppDevice, Guid>>();
                    var device = await deviceRepository.FirstOrDefaultAsync(d => d.DeviceId == relationship.TargetId);
                    targetName = !string.IsNullOrEmpty(device?.DeviceName) ? device.DeviceName : relationship.TargetId;
                    authorizationCode = device?.AuthorizationCode ?? "";
                    deviceType = device?.DeviceType ?? "";
                    break;
                case RelationshipType.Stock:
                    var securityRepository = ServiceProvider.GetRequiredService<IRepository<AppSecurity, int>>();
                    var security = await securityRepository.FirstOrDefaultAsync(s => s.Id == int.Parse(relationship.TargetId));
                    targetName = security?.Name ?? "";
                    break;
                case RelationshipType.Group:
                    var groupRepository = ServiceProvider.GetRequiredService<IRepository<AppGroup, Guid>>();
                    var group = await groupRepository.FirstOrDefaultAsync(g => g.Id == Guid.Parse(relationship.TargetId));
                    targetName = group?.Name ?? "";
                    break;
                case RelationshipType.ServiceAccount:
                    var saRepository = ServiceProvider.GetRequiredService<IRepository<AppServiceAccount, Guid>>();
                    var serviceAccount = await saRepository.FirstOrDefaultAsync(sa => sa.Id == Guid.Parse(relationship.TargetId));
                    targetName = serviceAccount?.Name ?? "";
                    break;
            }

            return new RelationshipDto
            {
                Id = relationship.Id,
                Type = relationship.TargetType,
                TargetId = targetId,
                TargetName = targetName,
                Status = relationship.Status,
                CreationTime = relationship.CreationTime,
                LastModificationTime = relationship.LastModificationTime,
                AuthorizationCode = authorizationCode,
                DeviceType = deviceType
            };
        }

        private TargetDto ConvertUserToTargetDto(IdentityUser user)
        {
            return new TargetDto
            {
                Id = user.Id.ToString(),
                Name = user.UserName,
                Type = "User"
            };
        }

        private TargetDto ConvertDeviceToTargetDto(AppDevice device)
        {
            return new TargetDto
            {
                Id = device.DeviceId,
                Name = device.DeviceName,
                Type = "Device"
            };
        }

        private TargetDto ConvertGroupToTargetDto(AppGroup group)
        {
            return new TargetDto
            {
                Id = group.Id.ToString(),
                Name = group.Name,
                Type = "Group",
                Description = group.Description
            };
        }

        private TargetDto ConvertSecurityToTargetDto(AppSecurity security)
        {
            return new TargetDto
            {
                Id = security.Id.ToString(),
                Name = security.Name,
                Type = "Stock",
                Description = security.Code,
                SecurityInfo = new TargetSecurityInfo
                {
                    Code = security.Code,
                    SecurityType = security.Type,
                    Exchange = security.Exchange,
                    BaseCode = security.BaseCode
                }
            };
        }

        private TargetDto ConvertServiceAccountToTargetDto(AppServiceAccount serviceAccount)
        {
            return new TargetDto
            {
                Id = serviceAccount.Id.ToString(),
                Name = serviceAccount.Name,
                Type = "ServiceAccount",
                Description = serviceAccount.Description,
                ServiceAccountInfo = new TargetServiceAccountInfo
                {
                    Category = serviceAccount.Category,
                    IsVerified = serviceAccount.IsVerified
                }
            };
        }

        private T ConvertToId<T>(object id)
        {
            if (id is T typedId)
            {
                return typedId;
            }

            if (typeof(T) == typeof(Guid))
            {
                if (id is int intId)
                {
                    return (T)(object)intId;
                }
                if (Guid.TryParse(id.ToString(), out var guid))
                {
                    return (T)(object)guid;
                }
            }

            if (typeof(T) == typeof(int))
            {
                if (id is Guid guidId)
                {
                    return (T)(object)guidId;
                }
                if (int.TryParse(id.ToString(), out var parsedId))
                {
                    return (T)(object)parsedId;
                }
            }

            return (T)Convert.ChangeType(id, typeof(T));
        }
    }
}