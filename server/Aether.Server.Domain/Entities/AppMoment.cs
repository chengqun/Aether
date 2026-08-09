using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using Volo.Abp.Domain.Entities;
using Volo.Abp.MultiTenancy;

namespace Aether.Server.Domain.Entities
{
    public class AppMoment : Entity<Guid>, IMultiTenant
    {
        public Guid? TenantId { get; set; }

        public Guid UserId { get; internal set; }

        [Required]
        [MaxLength(1000)]
        public string Content { get; internal set; } = string.Empty;

        public string? ImageUrl { get; internal set; }

        public DateTime CreatedAt { get; internal set; }

        public string Type { get; internal set; } = "Text";

        public string Visibility { get; internal set; } = "Friends";

        public AppMoment() { }

        public AppMoment(Guid id, Guid userId, string content)
            : base(id)
        {
            UserId = userId;
            Content = content;
            CreatedAt = DateTime.UtcNow;
        }

        /// <summary>
        /// 更新动态内容（最大 1000 字）
        /// </summary>
        public void UpdateContent(string content)
        {
            if (string.IsNullOrWhiteSpace(content))
                throw new ArgumentException("动态内容不能为空", nameof(content));
            if (content.Length > 1000)
                throw new ArgumentException("动态内容不能超过1000字", nameof(content));
            Content = content;
        }

        /// <summary>
        /// 设置图片 URL
        /// </summary>
        public void SetImage(string? imageUrl)
        {
            ImageUrl = imageUrl;
        }

        /// <summary>
        /// 设置可见性（Friends/Public/Private）
        /// </summary>
        public void SetVisibility(string visibility)
        {
            if (visibility != "Friends" && visibility != "Public" && visibility != "Private")
                throw new ArgumentException("可见性必须是 Friends、Public 或 Private", nameof(visibility));
            Visibility = visibility;
        }

        /// <summary>
        /// 设置动态类型（设置 Type + Visibility，用于创建时链式调用）
        /// </summary>
        public AppMoment WithType(string type)
        {
            Type = type;
            return this;
        }

        /// <summary>
        /// 设置图片 URL（链式调用）
        /// </summary>
        public AppMoment WithImage(string? imageUrl)
        {
            ImageUrl = imageUrl;
            return this;
        }

        /// <summary>
        /// 设置可见性（链式调用）
        /// </summary>
        public AppMoment WithVisibility(string visibility)
        {
            SetVisibility(visibility);
            return this;
        }

        /// <summary>
        /// 设置创建时间（用于导入历史数据时覆盖默认的 DateTime.UtcNow）
        /// </summary>
        public void SetCreatedAt(DateTime createdAt)
        {
            CreatedAt = createdAt;
        }
    }
}
