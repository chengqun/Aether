using System;
using Volo.Abp.Domain.Entities;

namespace Aether.Server.Domain.Entities
{
    public class AppSecurity : Entity<int>
    {
        public string Code { get; internal set; } = string.Empty;
        public string Name { get; internal set; } = string.Empty;
        public string Type { get; internal set; } = string.Empty;
        public string Exchange { get; internal set; } = string.Empty;
        public string BaseCode { get; internal set; } = string.Empty;
        public DateTime UpdatedAt { get; internal set; }

        public AppSecurity() { }

        public AppSecurity(string code, string name, string type, string exchange, string baseCode)
        {
            Code = code;
            Name = name;
            Type = type;
            Exchange = exchange;
            BaseCode = baseCode;
            UpdatedAt = DateTime.UtcNow;
        }

        /// <summary>
        /// 更新证券详情（名称、交易所、基础代码），同时刷新 UpdatedAt
        /// </summary>
        public void UpdateDetails(string name, string exchange, string baseCode)
        {
            Name = name;
            Exchange = exchange;
            BaseCode = baseCode;
            UpdatedAt = DateTime.UtcNow;
        }

        /// <summary>
        /// 更新证券全部信息（代码、名称、类型、交易所、基础代码），同时刷新 UpdatedAt
        /// </summary>
        public void UpdateAll(string code, string name, string type, string exchange, string baseCode)
        {
            Code = code;
            Name = name;
            Type = type;
            Exchange = exchange;
            BaseCode = baseCode;
            UpdatedAt = DateTime.UtcNow;
        }

        /// <summary>
        /// 标记为已更新（刷新 UpdatedAt 时间戳）
        /// </summary>
        public void MarkUpdated()
        {
            UpdatedAt = DateTime.UtcNow;
        }
    }
}
