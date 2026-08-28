using Aether.Server.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Volo.Abp.AuditLogging.EntityFrameworkCore;
using Volo.Abp.BackgroundJobs.EntityFrameworkCore;
using Volo.Abp.Data;
using Volo.Abp.DependencyInjection;
using Volo.Abp.EntityFrameworkCore;
using Volo.Abp.FeatureManagement.EntityFrameworkCore;
using Volo.Abp.Identity;
using Volo.Abp.Identity.EntityFrameworkCore;
using Volo.Abp.OpenIddict.EntityFrameworkCore;
using Volo.Abp.PermissionManagement.EntityFrameworkCore;
using Volo.Abp.SettingManagement.EntityFrameworkCore;
using Volo.Abp.TenantManagement;
using Volo.Abp.TenantManagement.EntityFrameworkCore;

namespace Aether.Server.EntityFrameworkCore;

[ReplaceDbContext(typeof(IIdentityDbContext))]
[ReplaceDbContext(typeof(ITenantManagementDbContext))]
[ConnectionStringName("Default")]
public class AbpDbContext :
    AbpDbContext<AbpDbContext>,
    IIdentityDbContext,
    ITenantManagementDbContext
{
    /* Add DbSet properties for your Aggregate Roots / Entities here. */

    #region Entities from the modules

    /* Notice: We only implemented IIdentityDbContext and ITenantManagementDbContext
     * and replaced them for this DbContext. This allows you to perform JOIN
     * queries for the entities of these modules over the repositories easily. You
     * typically don't need that for other modules. But, if you need, you can
     * implement the DbContext interface of the needed module and use ReplaceDbContext
     * attribute just like IIdentityDbContext and ITenantManagementDbContext.
     *
     * More info: Replacing a DbContext of a module ensures that the related module
     * uses this DbContext on runtime. Otherwise, it will use its own DbContext class.
     */

    //Identity
    public DbSet<IdentityUser> Users { get; set; }
    public DbSet<IdentityRole> Roles { get; set; }
    public DbSet<IdentityClaimType> ClaimTypes { get; set; }
    public DbSet<OrganizationUnit> OrganizationUnits { get; set; }
    public DbSet<IdentitySecurityLog> SecurityLogs { get; set; }
    public DbSet<IdentityLinkUser> LinkUsers { get; set; }
    public DbSet<IdentityUserDelegation> UserDelegations { get; set; }
    public DbSet<IdentitySession> Sessions { get; set; }
    // Tenant Management
    public DbSet<Tenant> Tenants { get; set; }
    public DbSet<TenantConnectionString> TenantConnectionStrings { get; set; }

    #endregion

    #region SDK Entities

    public virtual DbSet<AppSecurity> Securities { get; set; } = null!;
    
    // 设备管理实体
    public virtual DbSet<AppDevice> Devices { get; set; } = null!;
    
    // 关系实体
    public virtual DbSet<AppRelationship> Relationships { get; set; } = null!;
    
    // 群聊实体
    public virtual DbSet<AppGroup> Groups { get; set; } = null!;

    // 朋友圈实体
    public virtual DbSet<AppMoment> Moments { get; set; } = null!;

    // 聊天实体
    public virtual DbSet<AppConversation> Conversations { get; set; } = null!;
    public virtual DbSet<AppConversationParticipant> ConversationParticipants { get; set; } = null!;
    public virtual DbSet<AppChatMessage> ChatMessages { get; set; } = null!;

    // 应用版本配置
    public virtual DbSet<AppVersionConfig> VersionConfigs { get; set; } = null!;

    // LLM 配置（每个用户可有多条，如 DeepSeek、GPT-4o、本地模型）
    public virtual DbSet<AppLlmConfig> LlmConfigs { get; set; } = null!;

    // 服务号实体
    public virtual DbSet<AppServiceAccount> ServiceAccounts { get; set; } = null!;

    // 服务号发布的内容
    public virtual DbSet<AppServiceAccountPost> ServiceAccountPosts { get; set; } = null!;

    // Agent 使用记录
    public virtual DbSet<AppAgentUsageRecord> AgentUsageRecords { get; set; } = null!;

    // 毓数平台 Token（跨端同步）
    public virtual DbSet<AppYushuToken> YushuTokens { get; set; } = null!;

    // 360Teams 云文档 sid（跨端同步）
    public virtual DbSet<AppCloudDocsToken> CloudDocsTokens { get; set; } = null!;

    #endregion

    public AbpDbContext(DbContextOptions<AbpDbContext> options)
        : base(options)
    {

    }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        /* Include modules to your migration db context */

        builder.ConfigurePermissionManagement();
        builder.ConfigureSettingManagement();
        builder.ConfigureBackgroundJobs();
        builder.ConfigureAuditLogging();
        builder.ConfigureIdentity();
        builder.ConfigureOpenIddict();
        builder.ConfigureFeatureManagement();
        builder.ConfigureTenantManagement();

        /* Configure your own tables/entities inside here */

        // 配置 SDK 表 - Securities
        builder.Entity<AppSecurity>(b =>
        {
            b.ToTable("AppSecurities");
            b.HasKey(s => s.Id);
            b.HasIndex(s => new { s.Code, s.Type }).IsUnique();
            b.HasIndex(s => s.Name);
            b.HasIndex(s => s.UpdatedAt);
        });

        // 配置设备管理表 - Devices
        builder.Entity<AppDevice>(b =>
        {
            b.ToTable("AppDevices");
            b.HasKey(d => d.Id);
            b.HasIndex(d => d.DeviceId);
            b.HasIndex(d => d.UserId);
        });

        // 配置关系表 - Relationships
        builder.Entity<AppRelationship>(b =>
        {
            b.ToTable("AppRelationships");
            b.HasKey(r => r.Id);
            b.HasIndex(r => r.UserId);
            b.HasIndex(r => r.TargetType);
            b.HasIndex(r => r.TargetId);
            b.HasIndex(r => r.Status);
        });

        // 配置群聊表 - Groups
        builder.Entity<AppGroup>(b =>
        {
            b.ToTable("AppGroups");
            b.HasKey(g => g.Id);
            b.HasIndex(g => g.OwnerId);
            b.Property(g => g.Name).HasMaxLength(128).IsRequired();
            b.Property(g => g.Description).HasMaxLength(512);
        });

        // 配置朋友圈表 - Moments
        builder.Entity<AppMoment>(b =>
        {
            b.ToTable("AppMoments");
            b.HasKey(m => m.Id);
            b.HasIndex(m => m.UserId);
            b.HasIndex(m => m.CreatedAt);
            b.HasIndex(m => new { m.UserId, m.Type });
            b.HasIndex(m => new { m.Type, m.CreatedAt });
            b.Property(m => m.Content).HasMaxLength(1000).IsRequired();
            b.Property(m => m.ImageUrl).HasMaxLength(500);
            b.Property(m => m.Type).HasMaxLength(50).IsRequired();
            b.Property(m => m.Visibility).HasMaxLength(50).IsRequired();
        });

        // 配置聊天会话表 - Conversations
        builder.Entity<AppConversation>(b =>
        {
            b.ToTable("AppConversations");
            b.HasKey(c => c.Id);
            b.HasIndex(c => c.ConversationKey).IsUnique();
            b.HasIndex(c => c.GroupId);
            b.Property(c => c.ConversationKey).HasMaxLength(256).IsRequired();
            b.Property(c => c.Name).HasMaxLength(128);
        });

        // 配置聊天参与者表 - ConversationParticipants
        builder.Entity<AppConversationParticipant>(b =>
        {
            b.ToTable("AppConversationParticipants");
            b.HasKey(p => p.Id);
            b.HasIndex(p => p.ConversationId);
            b.HasIndex(p => p.UserId);
            b.HasIndex(p => new { p.ConversationId, p.UserId }).IsUnique();
            b.Property(p => p.Role).HasMaxLength(32).IsRequired();
        });

        // 配置聊天消息表 - ChatMessages
        builder.Entity<AppChatMessage>(b =>
        {
            b.ToTable("AppChatMessages");
            b.HasKey(m => m.Id);
            b.HasIndex(m => m.ConversationId);
            b.HasIndex(m => m.SenderId);
            b.HasIndex(m => m.SentAt);
            b.Property(m => m.Content).IsRequired();
            b.Property(m => m.MessageType).HasMaxLength(32).IsRequired();
        });

        // 配置应用版本配置表 - VersionConfigs
        builder.Entity<AppVersionConfig>(b =>
        {
            b.ToTable("AppVersionConfigs");
            b.HasKey(v => v.Id);
            b.HasIndex(v => v.Platform);
            b.HasIndex(v => new { v.Platform, v.VersionCode }).IsUnique();
            b.Property(v => v.VersionName).HasMaxLength(50).IsRequired();
            b.Property(v => v.UpdateUrl).HasMaxLength(500).IsRequired();
            b.Property(v => v.UpdateNote).HasMaxLength(2000);
        });

        // 配置 LLM 配置表 - LlmConfigs
        builder.Entity<AppLlmConfig>(b =>
        {
            b.ToTable("AppLlmConfigs");
            b.HasKey(l => l.Id);
            b.HasIndex(l => l.CreatorId);
            b.HasIndex(l => new { l.IsSystem, l.IsDefault });
            b.HasIndex(l => new { l.CreatorId, l.IsSystem });
            b.Property(l => l.Name).HasMaxLength(64).IsRequired();
            b.Property(l => l.ModelName).HasMaxLength(128).IsRequired();
            b.Property(l => l.ApiKey).HasMaxLength(256).IsRequired();
            b.Property(l => l.Endpoint).HasMaxLength(256).IsRequired();
            b.Property(l => l.IsSystem).HasDefaultValue(false).IsRequired();
        });

        // 配置服务号表 - ServiceAccounts
        builder.Entity<AppServiceAccount>(b =>
        {
            b.ToTable("AppServiceAccounts");
            b.HasKey(sa => sa.Id);
            b.HasIndex(sa => sa.OwnerId);
            b.HasIndex(sa => sa.Category);
            b.HasIndex(sa => sa.Name);
            b.Property(sa => sa.Name).HasMaxLength(128).IsRequired();
            b.Property(sa => sa.AvatarUrl).HasMaxLength(500);
            b.Property(sa => sa.Description).HasMaxLength(512);
            b.Property(sa => sa.Category).HasMaxLength(64).IsRequired();
            b.Property(sa => sa.WelcomeMessage).HasMaxLength(500);
        });

        // 配置服务号内容表 - ServiceAccountPosts
        builder.Entity<AppServiceAccountPost>(b =>
        {
            b.ToTable("AppServiceAccountPosts");
            b.HasKey(p => p.Id);
            b.HasIndex(p => p.ServiceAccountId);
            b.HasIndex(p => new { p.ServiceAccountId, p.PublishedAt });
            b.HasIndex(p => p.ContentType);
            b.Property(p => p.Title).HasMaxLength(200).IsRequired();
            b.Property(p => p.Summary).HasMaxLength(500);
            b.Property(p => p.Content).IsRequired();
            b.Property(p => p.CoverImageUrl).HasMaxLength(500);
            b.Property(p => p.ContentType).HasMaxLength(32).IsRequired();
        });

        // 配置 Agent 使用记录表 - AgentUsageRecords
        builder.Entity<AppAgentUsageRecord>(b =>
        {
            b.ToTable("AppAgentUsageRecords");
            b.HasKey(r => r.Id);
            b.HasIndex(r => r.UserId);
            b.HasIndex(r => r.AgentName);
            b.HasIndex(r => r.CreationTime);
            b.Property(r => r.AgentName).HasMaxLength(64).IsRequired();
        });

        // 配置毓数平台 Token 表 - YushuTokens
        builder.Entity<AppYushuToken>(b =>
        {
            b.ToTable("AppYushuTokens");
            b.HasKey(t => t.Id);
            b.HasIndex(t => t.UserId).IsUnique();
            b.Property(t => t.AccessToken).HasMaxLength(2000).IsRequired();
            b.Property(t => t.RefreshToken).HasMaxLength(2000);
            b.Property(t => t.TokenType).HasMaxLength(32).IsRequired();
            b.Property(t => t.ClientConfigJson).HasMaxLength(2000);
        });

        // 配置 360Teams 云文档 sid 表 - CloudDocsTokens
        builder.Entity<AppCloudDocsToken>(b =>
        {
            b.ToTable("AppCloudDocsTokens");
            b.HasKey(t => t.Id);
            b.HasIndex(t => t.UserId).IsUnique();
            b.Property(t => t.Sid).HasMaxLength(2000).IsRequired();
            b.Property(t => t.Env).HasMaxLength(16).IsRequired();
            b.Property(t => t.CapturedAt).HasMaxLength(64);
        });



        //builder.Entity<YourEntity>(b =>
        //{
        //    b.ToTable(AbpConsts.DbTablePrefix + "YourEntities", AbpConsts.DbSchema);
        //    b.ConfigureByConvention(); //auto configure for the base class props
        //    //...
        //});
    }
}
