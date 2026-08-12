namespace Acme.Notes.Domain;

public enum NoteNodeType
{
    Note = 0,
    Agent = 1,
    Workflow = 2,
    Knowledge = 3,
    Rule = 4,
    CodeRule = 5,
    CodeStrategy = 6,
    Strategy = 7,
    /// <summary>Lean 算法策略：直接写 QCAlgorithm 子类（复用 Lean 全部能力），与 Lean 耦合。全宽代码编辑器。</summary>
    LeanAlgorithm = 8,
}
