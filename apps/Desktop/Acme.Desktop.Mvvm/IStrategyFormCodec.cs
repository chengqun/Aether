namespace Acme.Desktop.Mvvm;

/// <summary>
/// YAML 策略源文本 ↔ <see cref="StrategyFormModel"/> 转换端口。
/// 接口与模型同在 Mvvm（Trading 无关）；实现 <c>StrategyFormCodec</c> 在 Stockapp host，
/// 复用 <c>StrategyProgramYaml</c> + <c>ConditionDef</c>（Trading.Scripting/Domain），不重复解析逻辑。
/// 镜像 <c>IKnowledgeSourceParser</c> 经 DI 注入的模式。
/// 仅 Stockapp 注册实现；Qifu 不注册 → 解析为 null → 策略节点无表单（回退全宽代码编辑器）。
/// </summary>
public interface IStrategyFormCodec
{
    /// <summary>尝试把 YAML 策略源文本解析为可编辑模型。空/异常→false + null + error。</summary>
    bool TryParse(string? yaml, string name, out StrategyFormModel? model, out string? error);

    /// <summary>把可编辑模型序列化回 YAML 策略源文本（与 StrategyProgramYaml.TryDeserialize 兼容）。</summary>
    string Serialize(StrategyFormModel model);
}
