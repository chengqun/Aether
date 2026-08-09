namespace Aether.Server.Permissions;

public static class AbpPermissions
{
    public const string GroupName = "Abp";

    // 设备管理权限
    public static class Devices
    {
        public const string Default = GroupName + ".Devices";
        public const string Manage = Default + ".Manage";
        public const string View = Default + ".View";
    }

    // 消息管理权限
    public static class Messages
    {
        public const string Default = GroupName + ".Messages";
        public const string Manage = Default + ".Manage";
        public const string Send = Default + ".Send";
        public const string Receive = Default + ".Receive";
    }

    // 好友管理权限
    public static class Friendships
    {
        public const string Default = GroupName + ".Friendships";
        public const string Manage = Default + ".Manage";
        public const string View = Default + ".View";
    }

    // 群组管理权限
    public static class Groups
    {
        public const string Default = GroupName + ".Groups";
        public const string Manage = Default + ".Manage";
        public const string View = Default + ".View";
    }

    // 朋友圈权限
    public static class Moments
    {
        public const string Default = GroupName + ".Moments";
        public const string Manage = Default + ".Manage";
        public const string View = Default + ".View";
    }

    // 股票管理权限
    public static class Stocks
    {
        public const string Default = GroupName + ".Stocks";
        public const string Manage = Default + ".Manage";
        public const string View = Default + ".View";
    }

    // 证券管理权限（股票基本信息）
    public static class Securities
    {
        public const string Default = GroupName + ".Securities";
        public const string Manage = Default + ".Manage";
        public const string Create = Default + ".Create";
        public const string Update = Default + ".Update";
        public const string Delete = Default + ".Delete";
    }

    // OpenAI配置权限
    public static class OpenAI
    {
        public const string Default = GroupName + ".OpenAI";
        public const string Manage = Default + ".Manage";
    }

    // Agent Prompt权限
    public static class AgentPrompts
    {
        public const string Default = GroupName + ".AgentPrompts";
        public const string Manage = Default + ".Manage";
    }

    // OpenIddict客户端管理权限
    public static class OpenIddict
    {
        public const string Default = GroupName + ".OpenIddict";
        public const string ClientManagement = Default + ".ClientManagement";
    }

    // 关系管理权限
    public static class Relationships
    {
        public const string Default = GroupName + ".Relationships";
        public const string Manage = Default + ".Manage";
        public const string View = Default + ".View";
        public const string Create = Default + ".Create";
        public const string Delete = Default + ".Delete";
    }

    // 聊天权限
    public static class Chat
    {
        public const string Default = GroupName + ".Chat";
        public const string Send = Default + ".Send";
        public const string View = Default + ".View";
    }

    // Agent配置管理权限
    public static class AgentConfigs
    {
        public const string Default = GroupName + ".AgentConfigs";
        public const string Manage = Default + ".Manage";
        public const string Create = Default + ".Create";
        public const string Update = Default + ".Update";
        public const string Delete = Default + ".Delete";
    }

    // LLM配置管理权限
    public static class LlmConfigs
    {
        public const string Default = GroupName + ".LlmConfigs";
        public const string Manage = Default + ".Manage";
        public const string Create = Default + ".Create";
        public const string Update = Default + ".Update";
        public const string Delete = Default + ".Delete";
    }

    // 系统级LLM配置管理权限
    public static class SystemLlmConfigs
    {
        public const string Default = GroupName + ".SystemLlmConfigs";
        public const string Manage = Default + ".Manage";
        public const string Create = Default + ".Create";
        public const string Update = Default + ".Update";
        public const string Delete = Default + ".Delete";
    }

    // 应用版本管理权限
    public static class VersionConfigs
    {
        public const string Default = GroupName + ".VersionConfigs";
        public const string Manage = Default + ".Manage";
        public const string Create = Default + ".Create";
        public const string Update = Default + ".Update";
        public const string Delete = Default + ".Delete";
    }

    // 服务号管理权限
    public static class ServiceAccounts
    {
        public const string Default = GroupName + ".ServiceAccounts";
        public const string Manage = Default + ".Manage";
        public const string Create = Default + ".Create";
        public const string Update = Default + ".Update";
        public const string Delete = Default + ".Delete";
        public const string View = Default + ".View";
        public const string Publish = Default + ".Publish";
    }

    // Agent 使用统计权限
    public static class AgentUsage
    {
        public const string Default = GroupName + ".AgentUsage";
        public const string View = Default + ".View";
        public const string Record = Default + ".Record";
    }
}
