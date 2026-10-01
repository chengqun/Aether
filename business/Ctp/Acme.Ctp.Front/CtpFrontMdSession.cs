using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using Acme.Ctp.Domain;
using Acme.Ctp.Simulator;

namespace Acme.Ctp.Front;

/// <summary>
/// CTP 行情腿会话（M3）：握手/挑战/确认与交易腿同款线协议信封 → MD 登录（op <c>E4 30 E6 03</c>，
/// 凭据不校验——行情订阅无账户语义）→ 订阅（op <c>E4 44 01</c>，解析合约集，不发订阅应答——
/// wrapper 的 <c>SubscribeMarketDataAsync</c> 不阻塞等待，tick 流即开始）→ tick 推送
/// （<see cref="SimTradingCore.TickProduced"/> → E4F101 单合约帧，按订阅集过滤）。
/// 出站管道与交易腿同款（Channel 单写者 + RoutePush）；tick 是纯推送，无应答排序需求（无 capture 缓冲）。
/// </summary>
internal sealed class CtpFrontMdSession : IDisposable
{
    private enum State { WaitingHandshake, WaitingChallengeResponse, WaitingMdLogin, Ready, Closed }

    private readonly TcpClient _client;
    private readonly SimTradingCore _core;
    private readonly Action<string> _log;
    private State _state = State.WaitingHandshake;
    private readonly CtpFrameParser _parser = new();
    private readonly HashSet<string> _subscribed = new(StringComparer.OrdinalIgnoreCase);

    private readonly Channel<byte[]> _out = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    private readonly object _writeGate = new();

    public CtpFrontMdSession(TcpClient client, SimTradingCore core, Action<string> log)
    {
        _client = client;
        _core = core;
        _log = log;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        var remote = _client.Client.RemoteEndPoint?.ToString() ?? "?";
        _log($"MD 会话启动: {remote}");
        var writer = Task.CompletedTask;
        try
        {
            using var stream = _client.GetStream();
            writer = WriterLoopAsync(stream, ct);
            var buffer = new byte[64 * 1024];
            while (_state != State.Closed && !ct.IsCancellationRequested)
            {
                var n = await stream.ReadAsync(buffer, ct);
                if (n == 0) break;
                foreach (var frame in _parser.Feed(buffer.AsSpan(0, n)))
                {
                    var response = ProcessFrame(frame);
                    if (response is { Length: > 0 })
                        _out.Writer.TryWrite(response);
                }
            }
        }
        catch (Exception ex)
        {
            _log($"MD 会话异常: {ex.Message}");
        }
        finally
        {
            _state = State.Closed;
            _core.TickProduced -= OnTick;
            _out.Writer.TryComplete();
            try { await writer; } catch { }
            _log($"MD 会话结束: {remote}（订阅 {string.Join(",", _subscribed)}）");
        }
    }

    private async Task WriterLoopAsync(NetworkStream stream, CancellationToken ct)
    {
        try
        {
            await foreach (var frame in _out.Reader.ReadAllAsync(ct))
            {
                _log($"→ [{frame.Length}B] {Convert.ToHexString(frame)}");
                await stream.WriteAsync(frame, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _log($"MD 写循环异常: {ex.Message}"); }
    }

    private void RoutePush(byte[] frame)
    {
        lock (_writeGate) _out.Writer.TryWrite(frame);
    }

    private byte[]? ProcessFrame(CtpFrame frame)
    {
        _log($"← {frame}");
        if (frame.IsGarbage) return null;

        if (frame.IsHandshake)
        {
            if (_state != State.WaitingHandshake) return null;
            _state = State.WaitingChallengeResponse;
            return BuildFrame(CtpFrontSession.ChallengePayload);
        }

        return _state switch
        {
            State.WaitingChallengeResponse => HandleChallengeResponse(),
            State.WaitingMdLogin => HandleMdLogin(frame),
            State.Ready => HandleSubscribe(frame),
            _ => null,
        };
    }

    private byte[]? HandleChallengeResponse()
    {
        _state = State.WaitingMdLogin;
        return BuildFrame(CtpFrontTemplates.ConfirmPayload);
    }

    private byte[]? HandleMdLogin(CtpFrame frame)
    {
        var body = frame.Body;
        if (body.Length < 4 || body[0] != 0xE4 || body[1] != 0x30 || body[2] != 0xE6 || body[3] != 0x03)
        {
            _log($"非预期帧（等 MD 登录）: body前缀={HexPrefix(body)}");
            return null;
        }
        var user = ExtractInstrumentLike(body);
        _log($"MD 登录: user={user ?? "?"}（凭据不校验）");
        _state = State.Ready;
        _core.TickProduced += OnTick;
        return BuildFrame(CtpFrontTemplates.BuildMdLoginRsp(_core.CurrentTradingDay));
    }

    private byte[]? HandleSubscribe(CtpFrame frame)
    {
        var body = frame.Body;
        if (body.Length < 3 || body[0] != 0xE4 || body[1] != 0x44 || body[2] != 0x01)
        {
            _log($"非预期帧（等订阅）: body前缀={HexPrefix(body)}");
            return null;
        }
        // 解析合约集（严格合约形状 run——与交易腿 ⑲ 同款规则）并增量登记
        var added = new List<string>();
        for (var i = 0; i < body.Length; i++)
        {
            if (!IsTag(body[i])) continue;
            var j = i + 1;
            while (j < body.Length)
            {
                if (body[j] == 0xE0 && j + 1 < body.Length) { j += 2; continue; }
                if (IsTag(body[j])) break;
                j++;
            }
            if (j > i + 1 && IsInstrumentRun(body[(i + 1)..j]))
            {
                var inst = Encoding.ASCII.GetString(body[(i + 1)..j]);
                if (_subscribed.Add(inst)) added.Add(inst);
            }
            i = j - 1;
        }
        _log($"订阅: +[{string.Join(",", added)}] → 共 {_subscribed.Count} 个");
        // 订阅应答（捕获 verbatim——SDK 无应答会断连，E2E 实证；wrapper 不阻塞等待但 native 层需要）
        return BuildFrame(CtpFrontTemplates.MdSubscribeRspTemplate);
    }

    private void OnTick(CtpTick tick)
    {
        if (_state != State.Ready || !_subscribed.Contains(tick.InstrumentId)) return;
        var day = string.IsNullOrEmpty(tick.TradingDay) ? _core.CurrentTradingDay : tick.TradingDay;
        _log($"→ 推送 tick: {tick.InstrumentId} last={tick.LastPrice} bid={tick.BidPrice} ask={tick.AskPrice}");
        // 【已知边界·tick 推送暂缓】帧模板/字段编辑全部就绪（SDK 解析值验证正确：last/bid/ask/时间 ✓），
        // 但该 SDK 构建收到 E4F101 推送后 native 层无限重投同一记录（~25 万次/秒，7 种帧形状实验
        // 均复现：±E9 分隔/单元复制/迷你终止帧/单写拼接——根因不在帧形状；真实抓包的「无回调+断连」
        // 实为抓包驱动订阅后即 dispose，非 SDK 行为参照）。开启推送须先在真前置长会话补抓验证。
        _log($"⏸ tick 推送暂缓（边界）: {tick.InstrumentId} last={tick.LastPrice} bid={tick.BidPrice} ask={tick.AskPrice}");
    }

    // —— 工具（与交易腿同款规则）——

    private static bool IsTag(byte b) => b is >= 0xE1 and <= 0xEF or 0xFE;

    private static bool IsInstrumentRun(ReadOnlySpan<byte> r)
    {
        var letters = 0;
        while (letters < r.Length && char.IsAsciiLetter((char)r[letters])) letters++;
        return letters is >= 1 and <= 2 && r.Length - letters is >= 3 and <= 4   // 1-2 字母（官方命名；本地品种 zx 对齐）
            && r[letters..].ToArray().All(b => b is >= (byte)'0' and <= (byte)'9');
    }

    private static string? ExtractInstrumentLike(ReadOnlySpan<byte> body)
    {
        for (var i = 0; i < body.Length; i++)
        {
            if (body[i] != 0xE7) continue;
            var j = i + 1;
            while (j < body.Length && !IsTag(body[j])) j++;
            if (j > i + 1 && IsInstrumentRun(body[(i + 1)..j]))
                return Encoding.ASCII.GetString(body[(i + 1)..j]);
        }
        return null;
    }

    private static string HexPrefix(ReadOnlySpan<byte> body) => Convert.ToHexString(body[..Math.Min(body.Length, 16)]);

    private static byte[] BuildFrame(byte[] payload)
    {
        var frame = new byte[4 + payload.Length];
        frame[0] = 0x02;
        frame[1] = 0x00;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(2, 2), (ushort)payload.Length);
        payload.CopyTo(frame, 4);
        return frame;
    }

    public void Dispose()
    {
        _state = State.Closed;
        try { _client.Close(); } catch { }
    }
}
