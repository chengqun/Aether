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

    public ProcedureRunner(ProcedureCatalog catalog, IDesktopAutomation desktop)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _desktop = desktop ?? throw new ArgumentNullException(nameof(desktop));
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
