using Acme.AppAutomation.Domain;

namespace Acme.AppAutomation.Application;

/// <summary>过程目录：按名注册/查找 <see cref="Procedure"/>。过程在 DI 注册时自登记。</summary>
public sealed class ProcedureCatalog
{
    private readonly Dictionary<string, Procedure> _byName = new();

    public void Register(Procedure procedure)
    {
        if (procedure is null) throw new ArgumentNullException(nameof(procedure));
        _byName[procedure.Name] = procedure; // 后注册覆盖同名（便于测试/覆盖）
    }

    public bool TryGet(string name, out Procedure? procedure) => _byName.TryGetValue(name, out procedure);
}

/// <summary><see cref="IProcedureRunner"/> 默认实现：从目录查过程，注入 <see cref="IDesktopAutomation"/> 执行。</summary>
public sealed class ProcedureRunner : IProcedureRunner
{
    private readonly ProcedureCatalog _catalog;
    private readonly IDesktopAutomation _desktop;

    /// <param name="catalog">过程目录。</param>
    /// <param name="desktop">桌面控制原语。</param>
    /// <param name="procedures">DI 注册的所有过程——构造时自登记进 <paramref name="catalog"/>。
    /// 这是必须的：<c>AddAppAutomation</c> 把每个过程注册为 <c>Singleton&lt;Procedure&gt;</c>，
    /// 但只有此处注入 <c>IEnumerable&lt;Procedure&gt;</c> 才会真正解析它们、触发登记；
    /// 否则目录恒空、<see cref="RunAsync"/> 报「未知固定过程」。</param>
    public ProcedureRunner(ProcedureCatalog catalog, IDesktopAutomation desktop, IEnumerable<Procedure> procedures)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _desktop = desktop ?? throw new ArgumentNullException(nameof(desktop));
        foreach (var p in procedures ?? Array.Empty<Procedure>())
            _catalog.Register(p);
    }

    public async Task<ProcedureResult> RunAsync(string name, object? args = null, CancellationToken ct = default)
    {
        if (!_catalog.TryGet(name, out var procedure) || procedure is null)
            return ProcedureResult.Fail($"未知固定过程: {name}");
        if (!_desktop.IsConnected)
            return ProcedureResult.Fail("桌面控制后端未连接（MCP server 未就绪）");
        try
        {
            return await procedure.ExecuteAsync(_desktop, args, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return ProcedureResult.Fail($"过程 {name} 执行异常: {ex.Message}");
        }
    }
}
