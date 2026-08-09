namespace Aether.Server.Domain.Shared.Enums
{
    /// <summary>
    /// 聊天对象类型
    /// </summary>
    public enum ChatTargetType
    {
        /// <summary>
        /// 好友
        /// </summary>
        Friend = 0,

        /// <summary>
        /// 设备
        /// </summary>
        Device = 1,

        /// <summary>
        /// 群组
        /// </summary>
        Group = 2,

        /// <summary>
        /// 股票
        /// </summary>
        Stock = 3,

        /// <summary>
        /// 服务号
        /// </summary>
        ServiceAccount = 4
    }

    /// <summary>
    /// 关系类型
    /// </summary>
    public enum RelationshipType
    {
        /// <summary>
        /// 好友关系
        /// </summary>
        Friendship = 0,

        /// <summary>
        /// 设备关系
        /// </summary>
        Device = 1,

        /// <summary>
        /// 群关系
        /// </summary>
        Group = 2,

        /// <summary>
        /// 股票关系
        /// </summary>
        Stock = 3,

        /// <summary>
        /// 服务号关系
        /// </summary>
        ServiceAccount = 4
    }

    /// <summary>
    /// 关系状态
    /// </summary>
    public enum RelationshipStatus
    {
        /// <summary>
        /// 待处理
        /// </summary>
        Pending = 0,

        /// <summary>
        /// 已接受
        /// </summary>
        Accepted = 1,

        /// <summary>
        /// 已拒绝
        /// </summary>
        Rejected = 2
    }

    /// <summary>
    /// 会话类型
    /// </summary>
    public enum ConversationType
    {
        /// <summary>
        /// 单聊
        /// </summary>
        Direct = 0,

        /// <summary>
        /// 群聊
        /// </summary>
        Group = 1
    }

    /// <summary>
    /// Agent 类型
    /// </summary>
    public enum AgentKind
    {
        /// <summary>
        /// LLM 驱动的 Agent
        /// </summary>
        Agent = 0,

        /// <summary>
        /// 多步骤工作流
        /// </summary>
        Workflow = 1,

        /// <summary>
        /// 规则驱动，无 LLM
        /// </summary>
        RuleBased = 2
    }

    /// <summary>
    /// 应用平台类型
    /// </summary>
    public enum AppPlatform
    {
        /// <summary>
        /// iOS
        /// </summary>
        iOS = 0,

        /// <summary>
        /// Android
        /// </summary>
        Android = 1,

        /// <summary>
        /// Windows
        /// </summary>
        Windows = 2,

        /// <summary>
        /// MacCatalyst
        /// </summary>
        MacCatalyst = 3,

        /// <summary>
        /// Linux
        /// </summary>
        Linux = 4
    }
}