using Acme.Desktop.Mvvm;
using Acme.Notes.Domain.Catalog;
using Aether.AI.Harness.Scripting;
using Microsoft.Extensions.DependencyInjection;

namespace Acme.Desktop.Shared.Hosting;

/// <summary>
/// 代码规则脚本启动器：容器构建后由各租户 App 调 <see cref="Start"/>。订阅 labs 目录源变更（<see cref="INoteCatalogSource.Changed"/>）
/// → 防抖重编译，并 fire-and-forget 首次 <c>LoadAsync</c>。对称于 Stockapp 内联的策略脚本启动逻辑，抽出共享以服务两租户。
/// </summary>
public static class CodeRuleHost
{
    public static void Start(IServiceProvider services)
    {
        try
        {
            var scripting = services.GetService<IRuleScriptingService>();
            if (scripting is null) return;
            var catalog = services.GetRequiredService<INoteCatalogSource>();
            catalog.Changed += _ => scripting.Reload(); // labs 目录变更 → 防抖重编译注册代码大脑
            scripting.LoadAsync().SafeFireAndForget();
        }
        catch
        {
            // 代码规则加载失败不阻断主流程（规则 tab 仍可用，召唤时报「未就绪」）。
        }
    }
}
