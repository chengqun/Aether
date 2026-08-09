using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.OpenApi;
using Aether.Server.EntityFrameworkCore;
using Aether.Server.Localization;
using Aether.Server.MultiTenancy;
using OpenIddict.Validation.AspNetCore;
using Volo.Abp;
using Volo.Abp.Account.Web;
using Volo.Abp.AspNetCore.Mvc;
using Volo.Abp.AspNetCore.Mvc.Localization;
using Volo.Abp.AspNetCore.Serilog;
using Volo.Abp.Autofac;
using Volo.Abp.Modularity;
using Volo.Abp.Security.Claims;
using Volo.Abp.Swashbuckle;
using Volo.Abp.OpenIddict;
using Volo.Abp.UI.Navigation.Urls;
using Volo.Abp.VirtualFileSystem;

namespace Aether.Server.HttpApi.Host;

[DependsOn(
    typeof(AbpApplicationModule),
    typeof(AbpEntityFrameworkCoreModule),
    typeof(AbpHttpApiModule),
    typeof(AbpAutofacModule),
    typeof(AbpSwashbuckleModule),
    typeof(AbpAspNetCoreSerilogModule),
    typeof(AbpAccountWebOpenIddictModule)
)]
public class AbpHttpApiHostModule : AbpModule
{
    public override void PreConfigureServices(ServiceConfigurationContext context)
    {
        var hostingEnvironment = context.Services.GetHostingEnvironment();
        var configuration = context.Services.GetConfiguration();

        context.Services.PreConfigure<AbpMvcDataAnnotationsLocalizationOptions>(options =>
        {
            options.AddAssemblyResource(
                typeof(AbpResource),
                typeof(AbpDomainModule).Assembly,
                typeof(AbpDomainSharedModule).Assembly,
                typeof(AbpApplicationModule).Assembly,
                typeof(AbpApplicationContractsModule).Assembly,
                typeof(AbpHttpApiHostModule).Assembly
            );
        });

        PreConfigure<OpenIddictBuilder>(builder =>
        {
            builder.AddValidation(options =>
            {
                options.AddAudiences("Abp");
                options.UseLocalServer();
                options.UseAspNetCore();
            });
        });

        PreConfigure<OpenIddictServerBuilder>(builder =>
        {
            builder.AllowPasswordFlow();
            builder.AllowRefreshTokenFlow();
            builder.SetTokenEndpointUris("connect/token");
            builder.SetRevocationEndpointUris("connect/revocation");
            builder.SetIntrospectionEndpointUris("connect/introspect");
            
            builder.Configure(options => options.AccessTokenLifetime = TimeSpan.FromDays(30));
            builder.Configure(options => options.RefreshTokenLifetime = TimeSpan.FromDays(365));
        });

        if (!hostingEnvironment.IsDevelopment())
        {
            PreConfigure<AbpOpenIddictAspNetCoreOptions>(options =>
            {
                options.AddDevelopmentEncryptionAndSigningCertificate = false;
            });

            PreConfigure<OpenIddictServerBuilder>(serverBuilder =>
            {
                var configuration = context.Services.GetConfiguration();
                var certPassword = configuration["OpenIddict:CertificatePassword"] ?? throw new InvalidOperationException("OpenIddict:CertificatePassword is not configured.");
                serverBuilder.AddProductionEncryptionAndSigningCertificate("openiddict.pfx", certPassword);
            });
        }
    }

    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        var hostingEnvironment = context.Services.GetHostingEnvironment();
        var configuration = context.Services.GetConfiguration();

        context.Services.AddHttpContextAccessor();
        context.Services.AddHttpClient();

        ConfigureAuthentication(context);
        ConfigureUrls(configuration);
        ConfigureVirtualFileSystem(hostingEnvironment);
        ConfigureSwaggerServices(context.Services);
        ConfigureAutoApiControllers();

        context.Services.AddMapperlyObjectMapper<AbpHttpApiHostModule>();

        context.Services.AddSignalR();
        // 注册自定义 IUserIdProvider，使用 "sub" claim 作为 SignalR 用户 ID
        // 与 ChatHub.ResolveIdentityIdAsync 的逻辑一致，确保 Clients.User(userId) 能正确路由
        context.Services.AddSingleton<Microsoft.AspNetCore.SignalR.IUserIdProvider, Hubs.SubClaimUserIdProvider>();

        // 注册每日定时同步任务（上海时间 20:01）
        context.Services.AddHostedService<DailySyncBackgroundService>();
    }

    private void ConfigureAuthentication(ServiceConfigurationContext context)
    {
        context.Services.AddAuthentication(options =>
        {
            options.DefaultAuthenticateScheme = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme;
            options.DefaultChallengeScheme = OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme;
        });
        context.Services.Configure<AbpClaimsPrincipalFactoryOptions>(options =>
        {
            options.IsDynamicClaimsEnabled = true;
        });
    }

    private void ConfigureUrls(IConfiguration configuration)
    {
        Configure<AppUrlOptions>(options =>
        {
            options.Applications["MVC"].RootUrl = configuration["App:SelfUrl"];
            options.RedirectAllowedUrls.AddRange(configuration["App:RedirectAllowedUrls"]?.Split(',') ?? Array.Empty<string>());
        });
    }

    private void ConfigureVirtualFileSystem(IWebHostEnvironment hostingEnvironment)
    {
        if (hostingEnvironment.IsDevelopment())
        {
            Configure<AbpVirtualFileSystemOptions>(options =>
            {
                options.FileSets.ReplaceEmbeddedByPhysical<AbpDomainSharedModule>(Path.Combine(hostingEnvironment.ContentRootPath, $"..{Path.DirectorySeparatorChar}Aether.Server.Domain.Shared"));
                options.FileSets.ReplaceEmbeddedByPhysical<AbpDomainModule>(Path.Combine(hostingEnvironment.ContentRootPath, $"..{Path.DirectorySeparatorChar}Aether.Server.Domain"));
                options.FileSets.ReplaceEmbeddedByPhysical<AbpApplicationContractsModule>(Path.Combine(hostingEnvironment.ContentRootPath, $"..{Path.DirectorySeparatorChar}Aether.Server.Application.Contracts"));
                options.FileSets.ReplaceEmbeddedByPhysical<AbpApplicationModule>(Path.Combine(hostingEnvironment.ContentRootPath, $"..{Path.DirectorySeparatorChar}Aether.Server.Application"));
                options.FileSets.ReplaceEmbeddedByPhysical<AbpHttpApiHostModule>(hostingEnvironment.ContentRootPath);
            });
        }
    }

    private void ConfigureSwaggerServices(IServiceCollection services)
    {
        services.AddAbpSwaggerGen(
            options =>
            {
                options.SwaggerDoc("v1", new OpenApiInfo { Title = "Aether API", Version = "v1" });
                options.DocInclusionPredicate((docName, description) => true);
                options.CustomSchemaIds(type => type.FullName);
            }
        );
    }

    private void ConfigureAutoApiControllers()
    {
        Configure<AbpAspNetCoreMvcOptions>(options =>
        {
            options.ConventionalControllers.Create(typeof(AbpApplicationModule).Assembly);
        });
    }

    public override async Task OnApplicationInitializationAsync(ApplicationInitializationContext context)
    {
        var env = context.GetEnvironment();
        var app = context.GetApplicationBuilder();
        var configuration = context.ServiceProvider.GetRequiredService<IConfiguration>();
        var logger = context.ServiceProvider.GetRequiredService<ILogger<AbpHttpApiHostModule>>();

        try
        {
            logger.LogInformation("[ABP] Initializing ABP database migrations...");
            var dbMigrationService = context.ServiceProvider.GetRequiredService<Aether.Server.Data.AbpDbMigrationService>();
            await dbMigrationService.MigrateAsync();
            logger.LogInformation("[ABP] ABP database migrations completed successfully.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[ABP] Failed to initialize ABP database migrations: {Message}", ex.Message);
        }

        app.UseAbpRequestLocalization();

        if (env.IsDevelopment())
        {
            app.UseDeveloperExceptionPage();
        }
        else
        {
            app.UseExceptionHandler("/Error");
            app.UseHsts();
        }

        app.UseHttpsRedirection();
        app.UseCorrelationId();
        app.MapAbpStaticAssets();
        app.UseRouting();

        // SignalR WebSocket 认证：从查询参数提取 token 注入到请求头
        app.UseMiddleware<Aether.Server.HttpApi.Services.SignalRQueryTokenMiddleware>();

        app.UseAuthentication();
        app.UseAbpOpenIddictValidation();

        if (MultiTenancyConsts.IsEnabled)
        {
            app.UseMultiTenancy();
        }
        app.UseUnitOfWork();
        app.UseDynamicClaims();
        app.UseAuthorization();

        if (env.IsDevelopment())
        {
            app.UseSwagger();
            app.UseAbpSwaggerUI(options =>
            {
                options.SwaggerEndpoint("/swagger/v1/swagger.json", "Aether API");
            });
        }

        app.UseConfiguredEndpoints(builder =>
        {
            builder.MapControllers();
            builder.MapHub<Aether.Server.HttpApi.Hubs.ChatHub>("/hubs/chat");
        });
    }
}