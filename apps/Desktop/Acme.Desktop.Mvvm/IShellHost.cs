namespace Acme.Desktop.Mvvm;

/// <summary>
/// Shell 宿主契约：暴露同级业务 VM（Experts/Workflows/Rules）供共享视图 reach-up 访问，
/// 解耦对 <see cref="MainViewModelBase"/> 的硬 cast。NotesView 表单工厂经 <see cref="EditVmContext.Main"/>
/// 消费此接口。<see cref="MainViewModelBase"/> 与 Codex 工作台的 WorkbenchShellViewModel 都实现。
/// </summary>
public interface IShellHost
{
    /// <summary>专家页 VM（agent 笔记表单工厂查同级 agent）。</summary>
    ExpertsViewModel Experts { get; }

    /// <summary>工作流页 VM（workflow 笔记表单工厂查同级 workflow）。</summary>
    WorkflowsViewModel Workflows { get; }

    /// <summary>规则页 VM（rule/codeRule 笔记表单工厂查同级规则）。</summary>
    RulesViewModel Rules { get; }
}
