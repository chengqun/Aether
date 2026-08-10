using System;

namespace Acme.Desktop.Workbench.Abstractions;

/// <summary>
/// 视图注册贡献者：让租户 host 往 <see cref="IViewRegistry"/> 注册自己的 panel 视图（如 Stockapp 的期货/回测/股票页），
/// 而 Workbench 库不依赖租户程序集。WorkbenchViewRegistry 构造时解析所有贡献者并调 <see cref="Contribute"/>。
/// </summary>
public interface IViewRegistryContributor
{
    /// <summary>向注册表登记租户专属 panel 视图 + VM 工厂。</summary>
    void Contribute(IViewRegistry registry, IServiceProvider services);
}
