using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Aether.Server.Application.Contracts.DTOs.Moment;
using Aether.Server.Application.Contracts.Services.Moment;
using Aether.Server.Domain.Entities;
using Aether.Server.Domain.Shared.Enums;
using Aether.Server.Domain.Services.Moment;
using Aether.Server.Domain.Services.Securities;
using Volo.Abp.Application.Services;
using Volo.Abp.Domain.Repositories;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Volo.Abp.Features;
using Volo.Abp.Identity;
using Volo.Abp;

namespace Aether.Server.Application.Services.Moment
{
    [Authorize]
    [RequiresFeature("Chat.Moment")]
    [Route("api/app/moment")]
    public class MomentAppService : ApplicationService, IMomentAppService
    {
        private readonly IMomentManager _momentManager;
        private readonly IRepository<AppMoment, Guid> _momentRepository;
        private readonly IRepository<AppRelationship, Guid> _relationshipRepository;
        private readonly IRepository<IdentityUser, Guid> _userRepository;
        private readonly ISecurityManager _securityManager;

        public MomentAppService(
            IMomentManager momentManager,
            IRepository<AppMoment, Guid> momentRepository,
            IRepository<AppRelationship, Guid> relationshipRepository,
            IRepository<IdentityUser, Guid> userRepository,
            ISecurityManager securityManager)
        {
            _momentManager = momentManager;
            _momentRepository = momentRepository;
            _relationshipRepository = relationshipRepository;
            _userRepository = userRepository;
            _securityManager = securityManager;
        }

        [HttpPost("moment")]
        public async Task<MomentDto> CreateMomentAsync(CreateMomentDto input)
        {
            var currentUserId = CurrentUser.Id ?? throw new BusinessException("Aether:UserNotLoggedIn");

            if (input.StockId.HasValue)
            {
                var moment = await _momentManager.CreateStockMomentAsync(input.StockId.Value, input.Content, input.ImageUrl);
                var stock = await _securityManager.GetByIdAsync(input.StockId.Value);
                return MapDto(moment, stock?.Name ?? "");
            }
            else
            {
                var moment = await _momentManager.CreateMomentAsync(currentUserId, input.Content, input.ImageUrl, input.Type, input.Visibility);
                var user = await _userRepository.GetAsync(currentUserId);
                return MapDto(moment, user.UserName);
            }
        }

        /// <summary>
        /// 我的朋友圈：好友+关注股票+自己的动态
        /// </summary>
        [HttpGet("friends-moments")]
        public async Task<List<MomentDto>> GetFriendsMomentsAsync(int limit = 50, int offset = 0, DateTime? since = null)
        {
            var currentUserId = CurrentUser.Id ?? throw new BusinessException("Aether:UserNotLoggedIn");
            var moments = await QueryFriendsMomentsAsync(currentUserId, null, null, limit, offset, since);
            return await MapDtosAsync(moments);
        }

        /// <summary>
        /// 我的朋友圈中某对象的动态（2是1的子集）
        /// </summary>
        [HttpGet("moments/{targetId}")]
        public async Task<List<MomentDto>> GetMomentsAsync([FromRoute] string targetId, string type, int limit = 50, int offset = 0, DateTime? since = null)
        {
            var currentUserId = CurrentUser.Id ?? throw new BusinessException("Aether:UserNotLoggedIn");
            var (filterUserId, filterType) = await ResolveTargetAsync(targetId, type);
            if (filterUserId == null) return new List<MomentDto>();

            var moments = await QueryFriendsMomentsAsync(currentUserId, filterUserId, filterType, limit, offset, since);
            return await MapDtosAsync(moments);
        }

        [HttpDelete("moment/{id}")]
        public async Task DeleteMomentAsync(Guid id)
        {
            var currentUserId = CurrentUser.Id ?? throw new BusinessException("Aether:UserNotLoggedIn");
            await _momentManager.DeleteMomentAsync(id, currentUserId);
        }

        // --- 核心：查我的朋友圈，可选按 targetId+type 过滤 ---

        private async Task<List<AppMoment>> QueryFriendsMomentsAsync(
            Guid currentUserId, Guid? filterUserId, string? filterType,
            int limit, int offset, DateTime? since)
        {
            // 好友ID + 关注的股票Guid
            var rels = await _relationshipRepository.GetListAsync(r =>
                r.UserId == currentUserId && r.Status == RelationshipStatus.Accepted);

            var friendIds = rels
                .Where(r => r.TargetType == RelationshipType.Friendship)
                .Select(r => Guid.Parse(r.TargetId))
                .Append(currentUserId)
                .ToList();

            var stockGuids = rels
                .Where(r => r.TargetType == RelationshipType.Stock)
                .Select(r => int.Parse(r.TargetId))
                .Select(id => _momentManager.CreateStockGuid(id))
                .ToList();

            // 所有可见的 UserId 集合 = 好友 + 关注的股票
            var visibleUserIds = friendIds.Concat(stockGuids).ToList();

            // 数据库级查询：好友动态(Visibility=Friends) + 关注股票动态(Type=Stock)
            var query = await _momentRepository.GetQueryableAsync();
            query = query.Where(m => visibleUserIds.Contains(m.UserId))
                .Where(m => m.Visibility == "Friends" || m.Type == "Stock");

            if (filterUserId != null)
                query = query.Where(m => m.UserId == filterUserId.Value);
            if (filterType != null)
                query = query.Where(m => m.Type == filterType);
            if (since != null)
                query = query.Where(m => m.CreatedAt > since.Value);

            return await AsyncExecuter.ToListAsync(
                query.OrderByDescending(m => m.CreatedAt).Skip(offset).Take(limit));
        }

        // --- targetId/type → Guid + Type ---

        private async Task<(Guid? userId, string? type)> ResolveTargetAsync(string targetId, string type)
        {
            return type?.ToLower() switch
            {
                "friendship" or "user" => (Guid.Parse(targetId), "User"),
                "stock" => (await ResolveStockGuidAsync(targetId), "Stock"),
                "device" => (_momentManager.CreateTargetGuid(targetId), "Device"),
                "group" => (_momentManager.CreateTargetGuid(targetId), "Group"),
                _ => await ResolveTargetDefaultAsync(targetId)
            };
        }

        private async Task<(Guid?, string?)> ResolveTargetDefaultAsync(string targetId)
        {
            if (int.TryParse(targetId, out var stockId))
                return (_momentManager.CreateStockGuid(stockId), "Stock");
            if (targetId.Contains('-'))
                return (Guid.Parse(targetId), "User");
            var stock = await _securityManager.FindAsync(targetId);
            return stock != null ? (_momentManager.CreateStockGuid(stock.Id), "Stock") : (null, null);
        }

        private async Task<Guid?> ResolveStockGuidAsync(string targetId)
        {
            if (int.TryParse(targetId, out var stockId))
                return _momentManager.CreateStockGuid(stockId);
            var stock = await _securityManager.FindAsync(targetId);
            return stock != null ? _momentManager.CreateStockGuid(stock.Id) : null;
        }

        // --- DTO 映射 ---

        private async Task<List<MomentDto>> MapDtosAsync(List<AppMoment> moments)
        {
            if (moments.Count == 0) return new List<MomentDto>();

            // 只查涉及的用户和股票
            var userIds = moments.Where(m => m.Type != "Stock").Select(m => m.UserId).Distinct().ToList();
            var users = userIds.Count > 0
                ? (await _userRepository.GetListAsync(u => userIds.Contains(u.Id))).ToDictionary(u => u.Id, u => u.UserName)
                : new Dictionary<Guid, string>();

            var stockGuids = moments.Where(m => m.Type == "Stock").Select(m => m.UserId).Distinct().ToList();
            var stocks = stockGuids.Count > 0
                ? (await _securityManager.GetAllAsync()).ToList()
                : new List<AppSecurity>();

            return moments.Select(m => new MomentDto
            {
                Id = m.Id,
                UserId = m.UserId,
                Username = m.Type == "Stock" ? GetStockName(m.UserId, stocks) : users.GetValueOrDefault(m.UserId, ""),
                Content = m.Content,
                ImageUrl = m.ImageUrl,
                CreatedAt = m.CreatedAt,
                Type = m.Type,
                Visibility = m.Visibility,
                TargetId = GetTargetId(m)
            }).ToList();
        }

        private static MomentDto MapDto(AppMoment m, string username) => new()
        {
            Id = m.Id, UserId = m.UserId, Username = username,
            Content = m.Content, ImageUrl = m.ImageUrl,
            CreatedAt = m.CreatedAt, Type = m.Type, Visibility = m.Visibility,
            TargetId = GetTargetId(m)
        };

        private static string? GetTargetId(AppMoment m)
        {
            try
            {
                return m.Type switch
                {
                    "Stock" => Convert.ToInt32(m.UserId.ToString()[..8], 16).ToString(),
                    "Device" or "Group" => m.UserId.ToString(),
                    _ => m.UserId.ToString()
                };
            }
            catch { return null; }
        }

        private static string GetStockName(Guid userId, List<AppSecurity> stocks)
        {
            try
            {
                var id = Convert.ToInt32(userId.ToString()[..8], 16);
                return stocks.FirstOrDefault(s => s.Id == id)?.Name ?? "";
            }
            catch { return ""; }
        }
    }
}
