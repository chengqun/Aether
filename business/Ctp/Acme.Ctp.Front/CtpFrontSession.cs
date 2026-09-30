using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using Acme.Ctp.Domain;
using Acme.Ctp.Simulator;

namespace Acme.Ctp.Front;

/// <summary>
/// CTP 前置会话：一条 TCP 连接 = 一个会话状态机。
/// <para>
/// 状态流转（从抓包还原的序列）：
/// <code>
/// Handshake(①) → Challenge(②) → ChallengeResponse(③→④) →
///   Auth(⑤→⑥) → Login(⑦→⑧) → Authenticated(⑨→⑩ …⑪⑫⑬⑭⑮⑯⑰⑱)
/// </code>
/// </para>
/// <para>
/// <b>应答策略 = 抓包 verbatim 复刻</b>（<see cref="CtpFrontTemplates"/>，由 ProtoCapture gen 生成器算偏移：
/// <c>dotnet run --project apps/CtpSim/Acme.Ctp.ProtoCapture -- gen</c>）：
/// 官方 C++ SDK 自己解析帧——我们只把捕获的 S→C 帧原样回放，替换 broker/user/reqId/交易日/资金等
/// 少数标记界定字段。密码策略：发<b>固定挑战</b> ⇒ 同密码+同 nonce ⇒ 同密文 ⇒ 存密文当哈希比对。
/// </para>
/// </summary>
internal sealed class CtpFrontSession : IDisposable
{
    private enum State
    {
        WaitingHandshake,
        WaitingChallengeResponse,
        WaitingAuth,
        WaitingLogin,
        Authenticated,
        Closed,
    }

    private readonly TcpClient _client;
    private readonly SimTradingCore _core;
    private readonly Action<string> _log;
    private State _state = State.WaitingHandshake;
    private readonly CtpFrameParser _parser = new();

    // 会话身份（⑤/⑦ 请求中提取）
    private string _userId = "";
    private string _brokerId = "";

    // 登录后上下文
    private CtpLoginResult? _login;
    private byte _loginReqId;         // ⑦ 的 reqId——⑩ 回显它（SDK 以此归并登录结果）
    private byte _settleInstance;     // 结算确认应答序号（抓包 ⑫=01 ⑭=02——会话内自增）

    public CtpFrontSession(TcpClient client, SimTradingCore core, Action<string> log)
    {
        _client = client;
        _core = core;
        _log = log;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        var remote = _client.Client.RemoteEndPoint?.ToString() ?? "?";
        _log($"[CTP-Front] 会话启动: {remote}");
        try
        {
            using var stream = _client.GetStream();
            var buffer = new byte[64 * 1024];
            while (_state != State.Closed && !ct.IsCancellationRequested)
            {
                var n = await stream.ReadAsync(buffer, ct);
                if (n == 0) break;

                var frames = _parser.Feed(buffer.AsSpan(0, n));
                foreach (var frame in frames)
                {
                    var response = ProcessFrame(frame);
                    if (response is { Length: > 0 })
                    {
                        // 出站帧也记日志——诊断「SDK 收到坏响应后挂死」时需要看见我们到底发了什么字节
                        _log($"[CTP-Front] → [{response.Length}B] {Convert.ToHexString(response)}");
                        await stream.WriteAsync(response, ct);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _log($"[CTP-Front] 会话异常: {ex.Message}");
        }
        finally
        {
            _state = State.Closed;
            _log($"[CTP-Front] 会话结束: {remote}");
        }
    }

    /// <summary>处理一个入站帧，返回应答帧（null = 无应答）。</summary>
    private byte[]? ProcessFrame(CtpFrame frame)
    {
        _log($"[CTP-Front] ← {frame}");

        if (frame.IsGarbage)
        {
            _log($"[CTP-Front] ✗ 垃圾帧丢弃(64B): {Convert.ToHexString(frame.Raw)}");
            return null;
        }

        return frame.IsHandshake
            ? HandleHandshake(frame)
            : _state switch
            {
                State.WaitingChallengeResponse => HandleChallengeResponse(frame),
                State.WaitingAuth => HandleAuth(frame),
                State.WaitingLogin => HandleLogin(frame),
                State.Authenticated => HandleBusiness(frame),
                _ => null,
            };
    }

    // —— ① 初始握手：客户端发版本串，我们回固定挑战 ② ——

    private byte[]? HandleHandshake(CtpFrame frame)
    {
        // 状态守卫：只接受初始握手。会话中途再收到 0006 开头的数据（对端重连半帧/垃圾被误分帧）
        // 若照旧应答 ⇒ 重发挑战 ② + 状态机重置回 WaitingChallengeResponse——已认证会话被腰斩。
        if (_state != State.WaitingHandshake)
        {
            _log($"[CTP-Front] 会话中途收到握手帧——忽略（当前状态={_state}）: {HexPrefix(frame.Payload)}");
            return null;
        }
        _state = State.WaitingChallengeResponse;
        return BuildFrame(ChallengePayload);
    }

    /// <summary>
    /// 固定挑战 payload——从 SimNow 真实抓包帧②逐字节提取（175B，含完整 ~128B 加密 nonce）。
    /// 服务端发固定挑战 ⇒ 同密码+同挑战 ⇒ 同密文 ⇒ 存密文当哈希比对（无需逆向加密算法）。
    /// </summary>
    private static readonly byte[] ChallengePayload = Convert.FromHexString(
        "0103344ce43026e5020192e755efefefefefea40330135e3802094e0e753222cf1d8ce9a2a3782e0e51bcba2e0ea43a6b6f3759f278ca11ebdf1a3d062cd86d13f38a655c65492307ee0eb99c7b76424bda606bf81207fe0e40d280796f81730fb3731051d0980f1d2ddf3797f356c91e0ec3eae3de0e7e0e022a99ec108f57ab42d3ab0fb7ce0e869c93d8e32fdfe4f64e0e06a5084b72d7ac89141b868ae34f1b40cefefefefefefefefefefefec");

    // —— ③ 挑战响应 → ④ 连接确认（verbatim 复刻）——

    private byte[]? HandleChallengeResponse(CtpFrame frame)
    {
        // 客户端回加密的挑战响应——直接接受，发连接确认（抓包帧④ 19B 原样）
        _state = State.WaitingAuth;
        return BuildFrame(CtpFrontTemplates.ConfirmPayload);
    }

    // —— ⑤ 认证请求 → ⑥ 认证响应（模板回放）——
    // ⑤ 与 ⑦ 共享 op 签名 E1 01 E2 30 10（body[0..5]）+ E3 reqId（body[6]）——按状态机区分。

    private byte[]? HandleAuth(CtpFrame frame)
    {
        var body = frame.Body;
        if (!MatchesSignature(body, "E101E23010"))
        {
            _log($"[CTP-Front] 非预期帧（等认证）: body前缀={HexPrefix(body)}");
            return null;
        }

        var reqId = ReqIdAfterOp(body);
        _brokerId = CtpFieldCodec.ExtractField(body, 0xFE) ?? "";
        _userId = CtpFieldCodec.ExtractField(body, 0xE7) ?? "";
        var appId = CtpFieldCodec.ExtractField(body, 0xE4) ?? "";

        _log($"[CTP-Front] 认证: broker={_brokerId} user={_userId} app={appId} reqId={reqId}");

        // 认证响应——接受（我们的交易所不做 AppId 校验）
        _state = State.WaitingLogin;
        return reqId is { } id
            ? BuildFrame(CtpFrontTemplates.BuildAuthRsp(_brokerId, _userId, appId, id))
            : null;
    }

    // —— ⑦ 登录请求 → ⑧ 登录预响应（模板回放；完整登录结果由 ⑩ 承载）——

    private byte[]? HandleLogin(CtpFrame frame)
    {
        var body = frame.Body;
        if (!MatchesSignature(body, "E101E23010"))
        {
            _log($"[CTP-Front] 非预期帧（等登录）: body前缀={HexPrefix(body)}");
            return null;
        }

        _loginReqId = ReqIdAfterOp(body) ?? 0;
        _brokerId = CtpFieldCodec.ExtractField(body, 0xFE) ?? _brokerId;
        _userId = CtpFieldCodec.ExtractField(body, 0xE7) ?? _userId;
        var appId = CtpFieldCodec.ExtractField(body, 0xE5) ?? "";

        // 加密密码（E6 标记）——存密文当哈希（同密码+同挑战 ⇒ 同密文）
        var encryptedPwd = CtpFieldCodec.ExtractEncrypted(body);
        var pwdHex = encryptedPwd is { Length: > 0 }
            ? Convert.ToHexString(encryptedPwd)[..Math.Min(16, encryptedPwd.Length * 2)]
            : "";

        _log($"[CTP-Front] 登录: broker={_brokerId} user={_userId} app={appId} reqId={_loginReqId} pwd=({encryptedPwd?.Length ?? 0}B 密文)");

        try
        {
            _login = _core.Login(_brokerId, _userId, pwdHex);
            _log($"[CTP-Front] SimTradingCore 登录成功: 交易日={_login.TradingDay} MaxOrderRef={_login.MaxOrderRef}");

            // ⑧ 只是预响应——交易日/FrontID/SessionID/MaxOrderRef 由 ⑩（随 ⑨ 系统信息请求下发）承载
            _state = State.Authenticated;
            return BuildFrame(CtpFrontTemplates.BuildLoginRsp(_brokerId, _userId));
        }
        catch (CtpBusinessException ex)
        {
            // 登录失败帧的形状未抓包——诚实失败：记日志并断开（客户端 SDK 会报连接断开）
            _log($"[CTP-Front] 登录失败: [{ex.ErrorId}] {ex.ErrorMsg}——关闭连接");
            _state = State.Closed;
            return null;
        }
    }

    // —— ⑨ 之后：业务帧按 op 签名分派（顺序敏感：先长签名后短前缀）——

    private byte[]? HandleBusiness(CtpFrame frame)
    {
        var body = frame.Body;
        var day = _login?.TradingDay ?? "";

        // ⑨ 系统信息请求: body[0..2] = E4 30 24（SDK 收到 ⑧ 后自动发）
        // → ⑩ 主帧（登录结果：交易日/FrontID/SessionID/MaxOrderRef）+ 伴随帧，两帧一次写回
        if (body.Length >= 3 && body[0] == 0xE4 && body[1] == 0x30 && body[2] == 0x24)
        {
            _log($"[CTP-Front] 系统信息请求 → 回登录结果(⑩)");
            var main = BuildFrame(CtpFrontTemplates.BuildSysInfoRsp(_brokerId, _userId, _loginReqId, day));
            var both = new byte[main.Length + CtpFrontTemplates.SysInfoCompanionFrame.Length];
            main.CopyTo(both, 0);
            CtpFrontTemplates.SysInfoCompanionFrame.CopyTo(both, main.Length);
            return both;
        }

        // ⑪⑬ 结算确认请求: E1 01 E2 40 13；reqId 在 E1 01 E1 43 E3 之后（⑪=03 ⑬=04）。
        // 注意 E19CE3 是「响应」帧⑫的 reqId 回显字段——请求里没有；请求侧的 reqId 标签是 E143
        // （与 ⑮ 的 E12EE3 / ⑰ 的 E1A6E3 同构：E1 01 E1 <tag> E3 <reqId>，body[6] 是链路计数≠reqId）。
        if (MatchesSignature(body, "E101E24013"))
        {
            var reqId = ExtractReqIdAfter(body, "E101E143E3");
            _settleInstance = (byte)Math.Min(255, _settleInstance + 1);
            _log($"[CTP-Front] 结算确认 #{_settleInstance} reqId={reqId}");
            return reqId is { } id
                ? BuildFrame(CtpFrontTemplates.BuildSettleRsp(_settleInstance, id, _brokerId, _userId, day))
                : null;
        }

        // ⑮ 资金查询请求: E1 04 E2 80 06；reqId 在 E1 2E E3 之后（=05）
        if (MatchesSignature(body, "E104E28006"))
        {
            var reqId = ExtractReqIdAfter(body, "E12EE3");
            var accountId = SimTradingCore.AccountIdOf(_userId);
            var account = _core.QueryAccount(accountId);
            _log($"[CTP-Front] 资金查询 reqId={reqId} balance={account.Balance}");
            return reqId is { } id
                ? BuildFrame(CtpFrontTemplates.BuildQryAccountRsp(id, _brokerId, account.AccountId, _userId, day, account.Balance))
                : null;
        }

        // ⑰ 持仓查询请求: E1 04 E2 80 04；reqId 在 E1 A6 E3 之后（=06）
        if (MatchesSignature(body, "E104E28004"))
        {
            var reqId = ExtractReqIdAfter(body, "E1A6E3");
            var positions = _core.QueryPositions(SimTradingCore.AccountIdOf(_userId));
            if (positions.Count > 0)
            {
                // 里程碑 1 只有「空持仓」响应模板——非空持仓帧形状待补抓包
                _log($"[CTP-Front] ⚠️ 持仓 {positions.Count} 条非空——里程碑 1 回空表");
            }
            _log($"[CTP-Front] 持仓查询 reqId={reqId} → 空表");
            return reqId is { } id
                ? BuildFrame(CtpFrontTemplates.BuildQryPositionEmptyRsp(id))
                : null;
        }

        // ⑲ 报单: E1 01 E2 40 E4 05 —— 里程碑 2
        if (MatchesSignature(body, "E101E240E405"))
        {
            _log($"[CTP-Front] 🚧 报单请求（里程碑 2 暂不应答）: body前缀={HexPrefix(body)}");
            return null;
        }

        // ㉑ 撤单: E1 01 E2 40 06 —— 里程碑 2
        if (MatchesSignature(body, "E101E240") && body.Length > 4 && body[4] == 0x06)
        {
            _log($"[CTP-Front] 🚧 撤单请求（里程碑 2 暂不应答）: body前缀={HexPrefix(body)}");
            return null;
        }

        _log($"[CTP-Front] 未知业务帧: body前缀={HexPrefix(body)} len={body.Length}");
        return null;
    }

    // —— 提取工具 ——

    /// <summary>op 签名前缀匹配（hex → bytes → SequenceEqual）。</summary>
    private static bool MatchesSignature(ReadOnlySpan<byte> body, string sigHex)
    {
        var sig = Convert.FromHexString(sigHex);
        return body.Length >= sig.Length && body[..sig.Length].SequenceEqual(sig);
    }

    /// <summary>⑤/⑦ 共享 op 签名的 reqId：body[5]==E3 时 body[6] 即 reqId。</summary>
    private static byte? ReqIdAfterOp(ReadOnlySpan<byte> body)
        => body.Length > 6 && body[5] == 0xE3 ? body[6] : null;

    /// <summary>定位 needle 后紧跟的 1B reqId（⑪⑬ E19CE3 / ⑮ E12EE3 / ⑰ E1A6E3）。</summary>
    private byte? ExtractReqIdAfter(ReadOnlySpan<byte> body, string needleHex)
    {
        var needle = Convert.FromHexString(needleHex);
        var hit = body.IndexOf(needle);
        if (hit < 0 || hit + needle.Length >= body.Length)
        {
            _log($"[CTP-Front] reqId needle {needleHex} 未命中");
            return null;
        }
        return body[hit + needle.Length];
    }

    private static string HexPrefix(ReadOnlySpan<byte> body)
        => Convert.ToHexString(body[..Math.Min(body.Length, 16)]);

    // —— 帧构造 ——

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
