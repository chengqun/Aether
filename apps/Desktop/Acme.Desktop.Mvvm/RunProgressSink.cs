using Aether.AI.Core.Agents;

namespace Acme.Desktop.Mvvm;

/// <summary>
/// 运行关键节点信号的下沉出口。聊天流（<see cref="StreamHandlers"/> 的工具/审批/计划/完成/错误事件）构造出
/// <see cref="RunProgress"/>（Core 层），经此接口广播给所有「关键信息消费者」——桌宠气泡、手机端 SignalR 进度、
/// 未来任何悬浮形态。UI 无关（零 Avalonia），故放 Mvvm 供 host 层实现，与 <see cref="IAgentExecutor"/>
/// 「业务端口 → 宿主实现」同一范式。
/// <para>
/// 粒度约定：只传关键节点（<see cref="RunProgressKind"/>），**不传逐字 token**——需要打字机效果的 UI
/// （桌宠）另走细粒度通道，避免只想看进度的消费者（手机端）被逐字刷屏。
/// </para>
/// </summary>
public interface IRunProgressSink
{
    /// <summary>下沉一条关键节点信号。消费方负责 UI 线程 marshal + 各自的呈现翻译。</summary>
    void Post(RunProgress signal);
}
