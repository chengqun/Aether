using Acme.DeviceConnect.Domain;
using Acme.Desktop.Mvvm;

namespace Acme.Desktop.Mvvm.Tests;

/// <summary>
/// <see cref="RemoteMessagePump"/> 并发管线回归——锁的是从 ChatWorkspaceViewModel 抽出的入站四道关卡
/// （#3 长任务不阻塞泵 + #1/#8 加固，见 CLAUDE.md「传输层加固」）：
/// Progress 心跳过滤 → 已处理去重（有界 LRU 200）→ 按会话有界队列（500 满丢弃）→ 每会话独占 drainer 串行。
/// 等待全部走精确计数门闩（处理器记录第 N 条时唤醒对应等待者），零 Sleep 轮询、零时序假设。
/// </summary>
public class RemoteMessagePumpTests
{
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(10);

    /// <summary>记录处理序的处理器假体：第 N 条被记录时精确唤醒等待者；
    /// <see cref="GateOf"/> 按消息返回阻塞 Task（模拟长 agent run），null=即时完成。</summary>
    private sealed class RecordingProcessor
    {
        private readonly object _sync = new();
        private readonly List<Guid> _order = new();
        private readonly Dictionary<int, TaskCompletionSource> _waiters = new();

        public Func<DeviceMessage, Task?>? GateOf { get; set; }

        public Task ProcessAsync(DeviceMessage msg)
        {
            TaskCompletionSource? hit = null;
            lock (_sync)
            {
                _order.Add(msg.Id);
                if (_waiters.Remove(_order.Count, out var tcs)) hit = tcs;
            }
            hit?.TrySetResult();
            return GateOf?.Invoke(msg) ?? Task.CompletedTask;
        }

        /// <summary>确定性等待：已处理条数到达 n（先到则同步完成）。</summary>
        public Task WhenProcessed(int n)
        {
            lock (_sync)
            {
                if (_order.Count >= n) return Task.CompletedTask;
                if (!_waiters.TryGetValue(n, out var tcs))
                {
                    tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    _waiters[n] = tcs;
                }
                return tcs.Task;
            }
        }

        /// <summary>处理序快照（含正在阻塞中的那条——记录发生在 gate 等待之前）。</summary>
        public Guid[] Snapshot()
        {
            lock (_sync) return _order.ToArray();
        }
    }

    private static DeviceMessage Msg(Guid id, Guid conversationId, string messageType = "Text") => new()
    {
        Id = id,
        ConversationId = conversationId,
        Content = "ping",
        MessageType = messageType,
    };

    // ── Progress 心跳过滤：桌面是发送方，永不消费自己的心跳 ──

    [Fact]
    public async Task Progress_heartbeat_is_never_processed()
    {
        var rec = new RecordingProcessor();
        var pump = new RemoteMessagePump(rec.ProcessAsync);
        var conv = Guid.NewGuid();

        pump.Accept(Msg(Guid.NewGuid(), conv, "Progress"));
        pump.Accept(Msg(Guid.NewGuid(), conv, "progress")); // 大小写不敏感
        pump.Accept(Msg(Guid.NewGuid(), conv, "Text"));

        await rec.WhenProcessed(1).WaitAsync(WaitLimit);
        Assert.Single(rec.Snapshot()); // 只有 Text 那条被处理
    }

    // ── 去重：同 id（补拉回放竞态）只处理一次 ──

    [Fact]
    public async Task Duplicate_id_is_processed_once()
    {
        var rec = new RecordingProcessor();
        var pump = new RemoteMessagePump(rec.ProcessAsync);
        var conv = Guid.NewGuid();
        var dup = Guid.NewGuid();

        pump.Accept(Msg(dup, conv));
        pump.Accept(Msg(dup, conv)); // 补拉回放的同一条
        pump.Accept(Msg(Guid.NewGuid(), conv)); // 哨兵：证明泵仍在处理后续消息

        await rec.WhenProcessed(2).WaitAsync(WaitLimit);
        var snap = rec.Snapshot();
        Assert.Equal(2, snap.Length);
        Assert.Equal(1, snap.Count(id => id == dup)); // 只处理过一次
    }

    // ── 空 Guid：不占用去重表槽位，永远放行 ──

    [Fact]
    public async Task Empty_guid_is_never_deduped()
    {
        var rec = new RecordingProcessor();
        var pump = new RemoteMessagePump(rec.ProcessAsync);
        var conv = Guid.NewGuid();

        pump.Accept(Msg(Guid.Empty, conv));
        pump.Accept(Msg(Guid.Empty, conv)); // 空 id 不进 LRU → 两条都处理

        await rec.WhenProcessed(2).WaitAsync(WaitLimit);
        Assert.Equal(2, rec.Snapshot().Length);
    }

    // ── 有界 LRU（200）：第 201 条淘汰最旧——被淘汰的 id 再来按新消息处理，仍在表内的继续去重 ──

    [Fact]
    public async Task Lru_evicts_oldest_id_beyond_200()
    {
        var rec = new RecordingProcessor();
        var pump = new RemoteMessagePump(rec.ProcessAsync);
        var conv = Guid.NewGuid();

        var ids = Enumerable.Range(0, 200).Select(_ => Guid.NewGuid()).ToArray();
        foreach (var id in ids) pump.Accept(Msg(id, conv));
        await rec.WhenProcessed(200).WaitAsync(WaitLimit);

        pump.Accept(Msg(Guid.NewGuid(), conv)); // 第 201 条 → 淘汰 ids[0]
        await rec.WhenProcessed(201).WaitAsync(WaitLimit);

        // 重投最早被淘汰的 id（补拉回放场景）→ 通过；仍在表内的最近 id → 去重
        pump.Accept(Msg(ids[0], conv));
        pump.Accept(Msg(ids[199], conv));
        pump.Accept(Msg(Guid.NewGuid(), conv)); // 哨兵：等它处理完再断言（前两条恰有一条不产生事件）
        await rec.WhenProcessed(203).WaitAsync(WaitLimit);

        var snap = rec.Snapshot();
        Assert.Equal(203, snap.Length);
        Assert.Equal(2, snap.Count(id => id == ids[0])); // 淘汰后放行 → 第二次也被处理
        Assert.Equal(1, snap.Count(id => id == ids[199])); // 仍在 LRU 内 → 只处理过一次
    }

    // ── 每会话独占 drainer：同会话 FIFO；跨会话互不阻塞（#3 长任务不阻塞泵） ──

    [Fact]
    public async Task Per_conversation_fifo_and_cross_conversation_independence()
    {
        var rec = new RecordingProcessor();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var a1 = Guid.NewGuid();
        // 只在 a1 上模拟长 agent run（conv A 的 drainer 卡在这条）；其余消息即时完成
        rec.GateOf = m => m.Id == a1 ? gate.Task : null;
        var pump = new RemoteMessagePump(rec.ProcessAsync);
        var convA = Guid.NewGuid();
        var convB = Guid.NewGuid();
        var a2 = Guid.NewGuid();
        var a3 = Guid.NewGuid();
        var b1 = Guid.NewGuid();
        var b2 = Guid.NewGuid();

        pump.Accept(Msg(a1, convA));
        await rec.WhenProcessed(1).WaitAsync(WaitLimit); // a1 已被 conv A drainer 取走并阻塞

        pump.Accept(Msg(a2, convA));
        pump.Accept(Msg(a3, convA));
        pump.Accept(Msg(b1, convB));
        pump.Accept(Msg(b2, convB));

        // conv A 阻塞时 conv B 照常处理完（独立 drainer）；A 的后续消息 FIFO 排在 a1 之后
        await rec.WhenProcessed(3).WaitAsync(WaitLimit);
        var mid = rec.Snapshot();
        Assert.Contains(b1, mid);
        Assert.Contains(b2, mid);
        Assert.DoesNotContain(a2, mid);
        Assert.DoesNotContain(a3, mid);

        gate.TrySetResult(); // 释放 a1 → conv A 队列续排
        await rec.WhenProcessed(5).WaitAsync(WaitLimit);
        var snap = rec.Snapshot();
        Assert.True(Array.IndexOf(snap, a1) < Array.IndexOf(snap, a2) && Array.IndexOf(snap, a2) < Array.IndexOf(snap, a3),
            "conv A 内 FIFO");
        Assert.True(Array.IndexOf(snap, b1) < Array.IndexOf(snap, b2), "conv B 内 FIFO");
    }

    // ── 有界队列（500）：drainer 卡住时持续入队，超容量的消息丢弃保护内存 ──

    [Fact]
    public async Task Bounded_queue_drops_overflow_when_drainer_is_stuck()
    {
        var rec = new RecordingProcessor();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rec.GateOf = _ => gate.Task; // 全部消息阻塞 → drainer 卡在第一条
        var pump = new RemoteMessagePump(rec.ProcessAsync);
        var conv = Guid.NewGuid();

        var first = Guid.NewGuid();
        pump.Accept(Msg(first, conv)); // drainer 取走并阻塞，队列尚未积压
        await rec.WhenProcessed(1).WaitAsync(WaitLimit);

        var queued = Enumerable.Range(0, 500).Select(_ => Guid.NewGuid()).ToArray();
        foreach (var id in queued) pump.Accept(Msg(id, conv)); // 恰好填满 500 容量
        var dropped = Guid.NewGuid();
        pump.Accept(Msg(dropped, conv)); // 容量已满 → TryWrite 失败 → 静默丢弃

        gate.TrySetResult();
        await rec.WhenProcessed(501).WaitAsync(WaitLimit); // first + 500 条排队者；dropped 永不被处理

        var snap = rec.Snapshot();
        Assert.Equal(501, snap.Length);
        Assert.DoesNotContain(dropped, snap);
        Assert.Equal(queued, snap.Skip(1)); // 排队者按入队顺序处理（FIFO）
    }
}
