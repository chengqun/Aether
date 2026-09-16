using System.Threading.Channels;
using Acme.DeviceConnect.Domain;

namespace Acme.Desktop.Mvvm;

/// <summary>
/// 远程消息泵（手机→桌面 SignalR 路径的入站侧基础设施）：Progress 心跳过滤 + 已处理消息去重
/// （有界 LRU）+ 按会话有界队列 + 每会话独占 drainer 串行处理。
/// <para>从 <c>ChatWorkspaceViewModel</c> 抽出——这是纯粹的并发管线（谁被过滤、谁被去重、谁排队、
/// 谁串行跑），不掺聊天业务（跑 agent/摘要/回复留在 <c>ProcessRemoteMessageAsync</c>，经构造注入的
/// processor 委托回调）。抽出的动机之一是让这套并发机制可独立单测。</para>
/// <para>设计要点（#3 长任务不阻塞泵 + #1/#8/#9 加固，见 CLAUDE.md「传输层加固」）：SignalR On
/// 处理器只做过滤+去重+入队（微秒级返回），实际处理在每会话独占 drainer 里串行跑——既不阻塞
/// SignalR 消息泵（长 agent run 秒级，直接 await 会压垮泵），又保证同一会话消息 FIFO
/// （共享 convId 会话，并发写会竞态）。</para>
/// </summary>
public sealed class RemoteMessagePump
{
    private readonly Func<DeviceMessage, Task> _processor;

    // —— 已处理消息去重：有界 LRU（补拉回放的消息若已实时处理过则跳过——连接瞬断→重连竞态）——
    private readonly object _processedRemoteLock = new();
    private readonly HashSet<Guid> _processedRemoteIds = new();
    private readonly Queue<Guid> _processedRemoteQueue = new();
    private const int MaxProcessedRemoteIds = 200;

    // —— 每会话独占队列 + drainer ——
    private readonly Dictionary<Guid, Channel<DeviceMessage>> _remoteQueues = new();
    private readonly object _remoteQueuesLock = new();
    private const int MaxRemoteQueueDepth = 500;

    public RemoteMessagePump(Func<DeviceMessage, Task> processor)
    {
        _processor = processor ?? throw new ArgumentNullException(nameof(processor));
    }

    /// <summary>入站入口（SignalR 线程，须微秒级返回）：Progress 心跳过滤 → 去重 → 按会话入队。
    /// 实际处理经 drainer 串行回调 processor。</summary>
    public void Accept(DeviceMessage msg)
    {
        // 控制消息按 messageType 路由（不建可见 turn）：
        // Progress = 桌面是发送方，不消费自己发出的心跳。
        if (string.Equals(msg.MessageType, "Progress", StringComparison.OrdinalIgnoreCase)) return;

        // 去重：补拉回放的消息若已实时处理过则跳过（连接瞬断→重连竞态）。
        if (!TryClaimRemoteId(msg.Id)) return;

        EnqueueRemote(msg);
    }

    private bool TryClaimRemoteId(Guid id)
    {
        if (id == Guid.Empty) return true;
        lock (_processedRemoteLock)
        {
            if (!_processedRemoteIds.Add(id)) return false;
            _processedRemoteQueue.Enqueue(id);
            while (_processedRemoteQueue.Count > MaxProcessedRemoteIds)
                _processedRemoteIds.Remove(_processedRemoteQueue.Dequeue());
            return true;
        }
    }

    /// <summary>按会话入队并（首次时）启动独占 drainer。FIFO 保证同会话消息顺序处理。</summary>
    private void EnqueueRemote(DeviceMessage msg)
    {
        Channel<DeviceMessage>? ch;
        lock (_remoteQueuesLock)
        {
            if (!_remoteQueues.TryGetValue(msg.ConversationId, out ch))
            {
                ch = Channel.CreateBounded<DeviceMessage>(MaxRemoteQueueDepth);
                _remoteQueues[msg.ConversationId] = ch;
                DrainRemoteQueueAsync(msg.ConversationId, ch).SafeFireAndForget();
            }
        }
        // TryWrite：队列已满（500）说明严重积压，丢弃以保护内存——实际不应发生。
        ch.Writer.TryWrite(msg);
    }

    /// <summary>每会话独占 drainer：FIFO 依次处理消息，跑完一条接下一条。队列排空后自清理。</summary>
    private async Task DrainRemoteQueueAsync(Guid convId, Channel<DeviceMessage> ch)
    {
        await foreach (var msg in ch.Reader.ReadAllAsync())
        {
            try
            {
                await _processor(msg);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"[Chat] 远程消息处理异常 (conv {convId}): {ex.Message}");
            }
        }
        lock (_remoteQueuesLock)
        {
            if (_remoteQueues.TryGetValue(convId, out var current) && ReferenceEquals(current, ch))
                _remoteQueues.Remove(convId);
        }
    }
}
