using Aether.AI.Core.Agents;

namespace Acme.Desktop.Mvvm;

/// <summary>
/// 桌宠专属的「逐字答案流」（打字机效果）。关键节点走 <see cref="IRunProgressSink"/>（共享，
/// 手机端也吃），但逐字 token 是桌宠的细粒度呈现需求——手机端明确只要进度不要逐字，故逐字流不放共享信号层，
/// 由桌宠单独实现此接口消费。与 <see cref="IRunProgressSink"/> 分离，避免逐字噪音污染关键节点消费者。
/// </summary>
public interface IPetTokenStream
{
    /// <summary>追加一片答案 token（节流渲染由消费方负责）。</summary>
    void AppendToken(string token);

    /// <summary>本轮答案流结束（吃掉前先 flush）。</summary>
    void EndAnswer();
}
