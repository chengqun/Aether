namespace Acme.AppAutomation.Domain;

/// <summary>
/// 固定过程运行器端口。上层（如 <c>IStockBrokerage</c> 的 MCP 适配器）按名调一个固化的 UI 操作流程。
/// <para>过程名 + 通用参数 → <see cref="ProcedureResult"/>。过程定义在 Infrastructure（同花顺等），
/// 由 <c>ProcedureCatalog</c> 注册。这层不耦合任何业务上下文（不引 StockTrading）。</para>
/// </summary>
public interface IProcedureRunner
{
    Task<ProcedureResult> RunAsync(string name, object? args = null, CancellationToken ct = default);
}

/// <summary>固定过程执行结果。Data 为可选 JSON（如持仓表），由调用方按需解析。</summary>
public sealed class ProcedureResult
{
    public bool Success { get; init; }
    public string? Error { get; init; }
    public string? Data { get; init; }

    public static ProcedureResult Ok(string? data = null) => new() { Success = true, Data = data };
    public static ProcedureResult Fail(string error) => new() { Success = false, Error = error };
}
