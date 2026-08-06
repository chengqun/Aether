using Acme.AppAutomation.Domain;

namespace Acme.AppAutomation.Application;

/// <summary>
/// 一个固定化的桌面操作流程（基类）。子类在 <see cref="ExecuteAsync"/> 里用 <see cref="IDesktopAutomation"/>
/// 原语编排出确定性序列（如「同花顺下单」= 切买入 tab→输代码→输价→输量→点下单→读回执）。
/// <para>args 是调用方传入的通用参数对象，子类自行取用。过程定义在 Infrastructure 注册进 <c>ProcedureCatalog</c>。</para>
/// </summary>
public abstract class Procedure
{
    /// <summary>过程名（如 "ths_place_order"），<see cref="IProcedureRunner"/> 按此分发。</summary>
    public abstract string Name { get; }

    public abstract Task<ProcedureResult> ExecuteAsync(IDesktopAutomation desktop, object? args, CancellationToken ct);
}
