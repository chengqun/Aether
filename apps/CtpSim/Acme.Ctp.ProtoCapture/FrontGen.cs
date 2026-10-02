// CtpFrontTemplates.cs 生成器（gen 子命令）——从抓包 verbatim 复刻 S→C 应答帧模板。
//
//   dotnet run --project apps/CtpSim/Acme.Ctp.ProtoCapture -- gen [trade.txt路径] [输出.cs路径]
//     两参均可省略：抓包默认取 $TMP/ctp-proto-capture 下帧数最多的 trade.txt
//     （同帧数取最新目录）；输出默认 <仓库根>/business/Ctp/Acme.Ctp.Front/CtpFrontTemplates.cs。
//
// 设计纪律（延续 doc/ctp-protocol/README.md 的抓包分析）：
//   - 零人工转录：所有替换偏移由 needle 长度/命中位置计算，本文件不含任何手数偏移；
//   - 生成期断言：每个 needle 恰好命中 1 次（余额 6 处）、编辑区互不重叠、
//     余额大端值「像钱」——任何失败抛异常非零退出，宁可响亮失败不静默错位；
//   - 真实凭据不入仓源码：broker/user/appId/交易日 从抓包内容自推导
//     （生成产物含抓包原样字节——入库与否由用户决策，见 README 凭据警示）。
using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;

namespace Acme.Ctp.ProtoCapture;

internal static class FrontGen
{
    // 抓包帧中 SimNow 账户余额的大端字节（≈1e6 量级——数值占位非凭据）；
    // ⑯ 资金应答中恰好出现 6 次（6 个资金字段同值）。若换了抓包/账户，命中数断言会响亮失败。
    private const string BalHex = "412E83E0EF6147AE";

    // 抓包行格式: "HH:mm:ss.fff #N C→S|S→C [N bytes] <大写HEX>"
    // 无 $ 锚（镜像 Python 原版）：RecordingProxy 对 >256B 的帧行截断为 256B + " …" 尾缀——
    // 前缀匹配照样捕获完整 hex 段（截断帧留在列表内供 1 基索引占位；一旦误作模板，
    // Payload() 的头/长度断言会响亮失败——⑨/⑳ 均非 M1 模板帧）。
    private static readonly Regex LineRe = new(
        @"^\d\d:\d\d:\d\d\.\d+ #\d+ (C→S|S→C) \[\d+ bytes\] ([0-9A-F]+)",
        RegexOptions.Compiled);

    public static void Run(string?[] args)
    {
        try
        {
            // 参数：`gen [抓包trade.txt ...] [输出.cs]`——.cs 结尾者（或最后一个）为输出，其余为抓包源。
            // M1 模板取第 1 个抓包（031656 固定源——挑战/token/交易日等 verbatim 基准）；
            // M2 模板（推送/撤单对/拒绝对）从**全部**抓包按 op 签名定位（v2 有推送、v1 有 ⑳ 拒绝对）。
            var outPath = args.LastOrDefault(a => a is not null && a.EndsWith(".cs", StringComparison.OrdinalIgnoreCase));
            if (outPath is null && args.Length > 1) outPath = args[^1];
            var captures = args.Where(a => a is not null && !a.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                .Select(a => a!).ToList();
            Generate(captures, outPath);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Gen] ✗ 生成失败: {ex.Message}");
            Environment.ExitCode = 1;
        }
    }

    private static void Generate(List<string> capturePaths, string? outPath)
    {
        if (capturePaths.Count == 0)
            capturePaths.Add(LatestCapture());
        outPath ??= FindRepoRoot() is { } root
            ? Path.Combine(root, "business", "Ctp", "Acme.Ctp.Front", "CtpFrontTemplates.cs")
            : throw Fail("未找到仓库根（向上未遇 Aether.AI.Platform.slnx）——显式传输出路径");

        Console.WriteLine($"[Gen] 抓包源: {string.Join(" + ", capturePaths)}");
        var frames = ParseCapture(capturePaths[0]);
        Console.WriteLine($"[Gen] M1 源解析: {frames.Count} 帧");
        // M2 帧池：全部抓包展平（op 签名定位，来源无关）
        var m2frames = capturePaths.SelectMany(ParseCapture).ToList();
        Console.WriteLine($"[Gen] M2 帧池: {m2frames.Count} 帧");

        // ── 派生常量：broker/user 取自 ⑤（认证请求 FE/E7 字段）、appId 取自 ⑦（E5 字段）、
        //    交易日取自 ⑩（1010030124 前缀后 8 位数字）——本源码不含任何账号字面量。
        var broker = ExtractRun(Payload(frames, 5), 0xFE) ?? throw Fail("⑤ 内无 FE 后可打印 BrokerID");
        var user = ExtractRun(Payload(frames, 5), 0xE7) ?? throw Fail("⑤ 内无 E7 后可打印 UserID");
        var appId = ExtractRun(Payload(frames, 7), 0xE5) ?? throw Fail("⑦ 内无 E5 后可打印 AppID");

        // ── 帧 ⑩：一次 256B 写 = 主帧（4+206）+ 伴随帧（4+42），两帧同写——模板各自截取 ──
        var raw10 = frames[9].Raw;
        if (raw10.Length != 256) throw Fail($"⑩ 期望原始写 256B，实际 {raw10.Length}B");
        if (raw10[0] != 0x02 || raw10[1] != 0x00 || BinaryPrimitives.ReadUInt16BigEndian(raw10.AsSpan(2, 2)) != 206)
            throw Fail("⑩ 主帧头不符（期望 02 00 00 CE）");
        var p10a = raw10[4..210];
        if (raw10[210] != 0x02 || raw10[211] != 0x00 || BinaryPrimitives.ReadUInt16BigEndian(raw10.AsSpan(212, 2)) != 42)
            throw Fail("⑩ 伴随帧头不符（期望 @210: 02 00 00 2A）");
        var companionFrame = raw10[210..];

        var day = ExtractDay(p10a);
        var dirName = Path.GetFileName(Path.GetDirectoryName(Path.GetFullPath(capturePaths[0])));
        if (dirName is { Length: >= 8 })
        {
            var dirDay = dirName[..8];
            if (dirDay.All(char.IsDigit) && dirDay != day)
                throw Fail($"抓包目录名日期 {dirDay} ≠ ⑩ 交易日 {day}");
        }

        var balVal = BinaryPrimitives.ReadDoubleBigEndian(Hx(BalHex));
        if (!(1000 < Math.Abs(balVal) && Math.Abs(balVal) < 1e9))
            throw Fail($"余额常量大端值不像钱（{balVal:R}）——BalHex 或字节序假设有误");

        Console.WriteLine($"[Gen] 派生: broker={broker} user={user} appId={appId} 交易日={day} 余额@抓包={balVal:R}");

        // ── 各帧模板 + 替换偏移（needle 定位，全部生成期断言唯一命中/无重叠）──
        var p4 = Payload(frames, 4);
        if (p4.Length != 19) throw Fail($"④ 期望 19B，实际 {p4.Length}B");

        var p6 = Payload(frames, 6);
        var e6 = new (int Off, int Len, string Expr)[]
        {
            LocFull(p6, Hx("E43011E502015BE3"), 1, "[reqId]", "⑥ reqId"),
            LocVal(p6, Hx("FE"), Asc(broker), $"F(brokerId, {broker.Length})", "⑥ broker"),
            LocVal(p6, Hx("E7"), Asc(user), $"F(userId, {user.Length})", "⑥ user"),
            LocVal(p6, Hx("E5"), Asc(appId), $"F(appId, {appId.Length})", "⑥ AppID"),
        };
        CheckDisjoint("⑥", e6);

        var p8 = Payload(frames, 8);
        var e8 = new (int Off, int Len, string Expr)[]
        {
            LocVal(p8, Hx("FE"), Asc(broker), $"F(brokerId, {broker.Length})", "⑧ broker"),
            LocVal(p8, Hx("E7"), Asc(user), $"F(userId, {user.Length})", "⑧ user"),
        };
        CheckDisjoint("⑧", e8);

        var e10 = new (int Off, int Len, string Expr)[]
        {
            LocFull(p10a, Hx("E503018DE3"), 1, "[loginReqId]", "⑩ loginReqId"),
            LocVal(p10a, Hx("E1"), Asc(broker), $"F(brokerId, {broker.Length})", "⑩ broker"),
            LocVal(p10a, Hx("E7"), Asc(user), $"F(userId, {user.Length})", "⑩ user"),
            LocVal(p10a, Hx("1010030124"), Asc(day), $"F(tradingDay, {day.Length})", "⑩ 交易日#1"),
            LocVal(p10a, Hx("E401E301"), Asc(day), $"F(tradingDay, {day.Length})", "⑩ 交易日#2"),
            // MaxOrderRef（M2）：M1 verbatim 回放抓包值 "1" 的断层修复——换成核心真实序列（客户端拿它起算 OrderRef）。
            // 变长（多位数变宽）——SDK 容忍度由 E2E T7 裁决；不支持则回退服务端 ref 重映射（计划 R1 降级路）。
            LocVal(p10a, Hx("EFEF012DAF7B02"), Asc("1"), "A(maxOrderRef)", "⑩ MaxOrderRef"),
        };
        CheckDisjoint("⑩", e10);

        var p12 = Payload(frames, 12);
        var e12 = new (int Off, int Len, string Expr)[]
        {
            LocFull(p12, Hx("E101E24014E3"), 1, "[instance]", "⑫ instance"),
            LocFull(p12, Hx("E19CE3"), 1, "[reqId]", "⑫ reqId"),
            LocVal(p12, Hx("E13F"), Asc(broker), $"F(brokerId, {broker.Length})", "⑫ broker"),
            LocVal(p12, Hx("E7"), Asc(user), $"F(userId, {user.Length})", "⑫ user"),
            LocVal(p12, Hx("E7"), Asc(day), $"F(tradingDay, {day.Length})", "⑫ 交易日"),
        };
        CheckDisjoint("⑫", e12);

        var p16 = Payload(frames, 16);
        // 余额字段：E0 转义的大端 double（SDK 解析前剥转义——999927.69 的逻辑大端 41 2E 83 EF 61 47 AE 14
        // 线上为 41 2E 83 E0 EF 61 47 AE 14，EF 前插 E0）。needle = 转义后前 8 线上字节（恰 6 命中）；
        // 槽宽由转义解码推导（值内 tag 字母表 {E0..EF, FE} 字节各占 2 线上字节 ⇒ 宽度可变，勿手数）。
        var balOffsets = FindN(p16, Hx(BalHex), 6, "⑯ 余额字段");
        var balWireLen = 0;
        var balDecoded = 0.0;
        foreach (var o in balOffsets)
        {
            var (wireLen, value) = DecodeEscapedMoney(p16, o, "⑯ 余额字段");
            if (balWireLen == 0) { balWireLen = wireLen; balDecoded = value; }
            else if (wireLen != balWireLen || value != balDecoded)
                throw Fail($"⑯ 余额字段 @{o}: 转义解码 (宽{wireLen}, {value:R}) ≠ 首槽 (宽{balWireLen}, {balDecoded:R})");
        }
        if (!(1000 < Math.Abs(balDecoded) && Math.Abs(balDecoded) < 1e9))
            throw Fail($"⑯ 余额转义解码值不像钱（{balDecoded:R}）——E0 转义模型有误");
        Console.WriteLine($"[Gen] ⑯ 余额: 转义解码 {balDecoded:R}（槽宽 {balWireLen}B 线上 = 8B 逻辑）");
        if (user.Length <= 1) throw Fail("用户名 ≤1 字符——⑯ 账户#1 短码派生不成立");
        var acctShort = user[..^1];   // 账户#1 = 用户名去尾字符（⑯ 内两处账户字段：短码 + 完整用户名）
        var e16 = new List<(int Off, int Len, string Expr)>
        {
            // reqId 回显标签 = E1 01 01 8E E3（响应侧标签 ≠ 请求侧 E1 2E——⑫ 的 E1 9C 同理；
            // 实文核验: 帧16 op 后 `E301 E101 018E E3 05`，05 = qryAcct reqId 回显）。
            // 注: Python 原版 needle "E18EE3" 是从未执行过的转录笔误（字节不在帧内），此处已修正。
            LocFull(p16, Hx("E101018EE3"), 1, "[reqId]", "⑯ reqId"),
            LocVal(p16, Hx("E10C018A"), Asc(broker), $"F(brokerId, {broker.Length})", "⑯ broker"),
            // 账户#1（抓包值 = 用户名去尾 5 字符，语义未知；oracle 实证 AccountID=槽#2 非此处）：
            // 等宽守卫——恰好 5 字符进出，任意宽 accountId 不撑大 payload
            //（+1 增长曾把 E1 推到槽#2 值位 ⇒ SDK 读出空 AccountID）。
            LocValSur(p16, Hx("E1"), Asc(acctShort), Hx("E1"),
                $"A(accountId.Length >= {acctShort.Length} ? accountId[..{acctShort.Length}] : accountId.PadLeft({acctShort.Length}, '0'))", "⑯ 账户#1"),
            LocValSur(p16, Hx("E1"), Asc(user), Hx("E1"), $"F(accountId, {user.Length})", "⑯ 账户#2"),
            LocVal(p16, Hx("E8"), Asc(day), $"F(tradingDay, {day.Length})", "⑯ 交易日"),
        };
        e16.AddRange(balOffsets.Select(o => (o, balWireLen, "EscapedMoney(balance)")));
        CheckDisjoint("⑯", e16.ToArray());

        var p18 = Payload(frames, 18);
        if (p18.Length != 13) throw Fail($"⑱ 期望 13B，实际 {p18.Length}B");
        var e18 = new (int Off, int Len, string Expr)[] { (p18.Length - 1, 1, "[reqId]") };

        // ── 生成输出 ──
        var sb = new StringBuilder();
        EmitHeader(sb, capturePaths[0], frames.Count);
        EmitHelpers(sb);

        EmitTemplate(sb, "帧 ④ 连接确认（19B payload——verbatim 无替换）", "ConfirmPayload", p4);

        EmitTemplate(sb, $"帧 ⑥ 认证响应（{p6.Length}B payload：reqId/broker/user/AppId 替换）", "AuthRspTemplate", p6);
        EmitBuilder(sb, "⑥ 认证响应（Base64 token 抓包原样——固定挑战下 token 无会话效性，可回放）。",
            "BuildAuthRsp(string brokerId, string userId, string appId, byte reqId)", "AuthRspTemplate", e6);

        EmitTemplate(sb, $"帧 ⑧ 登录预响应（{p8.Length}B payload：broker/user 替换）", "LoginRspTemplate", p8);
        EmitBuilder(sb, "⑧ 登录预响应（SDK 收 ⑧ 后自动发 ⑨ 系统信息请求）。",
            "BuildLoginRsp(string brokerId, string userId)", "LoginRspTemplate", e8);

        EmitTemplate(sb, $"帧 ⑩ 登录结果主帧（{p10a.Length}B payload：loginReqId/broker/user/交易日×2 替换）", "SysInfoRspTemplate", p10a);
        EmitBuilder(sb, "⑩ 登录结果主帧（交易日/FrontID/SessionID/MaxOrderRef——⑩ 到达即完成 OnRspUserLogin）。",
            "BuildSysInfoRsp(string brokerId, string userId, byte loginReqId, string tradingDay, string maxOrderRef)", "SysInfoRspTemplate", e10);

        sb.AppendLine();
        sb.AppendLine("    /// <summary>⑩ 伴随帧（42B payload + 4B 头 = 46B 整帧——与主帧同一次 TCP 写出）。</summary>");
        sb.AppendLine("    internal static readonly byte[] SysInfoCompanionFrame = H(");
        EmitHexChunks(sb, companionFrame);
        sb.AppendLine("    );");

        EmitTemplate(sb, $"帧 ⑫ 结算确认应答（{p12.Length}B payload：instance/reqId/broker/user/交易日 替换；⑭ 复用同模板 instance=02）", "SettleRspTemplate", p12);
        EmitBuilder(sb, "⑫⑭ 结算确认应答（instance 自增：首次 01、二次 02）。",
            "BuildSettleRsp(byte instance, byte reqId, string brokerId, string userId, string tradingDay)", "SettleRspTemplate", e12);

        EmitTemplate(sb, $"帧 ⑯ 资金应答（{p16.Length}B payload：reqId/broker/账户×2/交易日 + 6 个 E0 转义大端余额字段）", "QryAccountRspTemplate", p16);
        EmitBuilder(sb, "⑯ 资金应答（6 个 E0 转义余额字段全部 ← Balance——整槽替换，线上宽度随值内 tag 字节数可变）。",
            "BuildQryAccountRsp(byte reqId, string brokerId, string accountId, string userId, string tradingDay, double balance)", "QryAccountRspTemplate", e16.ToArray());

        EmitTemplate(sb, $"帧 ⑱ 持仓应答·空表（{p18.Length}B payload：仅尾字节 reqId 替换）", "QryPositionEmptyRspTemplate", p18);
        EmitBuilder(sb, "⑱ 持仓应答（空表——M1 恒空；非空持仓需 M2 结构）。",
            "BuildQryPositionEmptyRsp(byte reqId)", "QryPositionEmptyRspTemplate", e18);

        // ═══════════════ M2 模板（2026-10-01 补抓包：推送 + ⑳/㉒ 应答对）═══════════════
        // 帧族（payload[4..] op 前缀）：F001=OnRtnOrder / F002=OnRtnTrade / F003=OnErrRtnOrderInsert /
        // F004=OnErrRtnOrderAction / 4001=OnRspOrderInsert / 4007=OnRspOrderAction。
        // 推送跨状态字节重排（'3' 的状态字节在 EA 段、'0' 的在 E9 段）⇒ 按状态分模板，各自 verbatim + Edit。
        var allPayloads = new List<byte[]>();
        foreach (var (raw, _) in m2frames)
        {
            try { allPayloads.AddRange(SplitWrite(raw)); }
            catch { /* 截断写（031656 的 ⑳）与心跳等非标准写跳过 */ }
        }
        Console.WriteLine($"[Gen] M2 帧池展平: {allPayloads.Count} payload");

        // 推送/回显帧携带的会话 FrontID+SessionID（补抓包会话 01+C8E0E69C，5B）——Edit 成 M1 ⑩ 的
        // verbatim 012DAF7B+02（同为 5B 零增长；D6：客户端按 ⑩ 值匹配回报/撤单定位，两边必须同源）。
        var capFrontSession = Hx("01C8E0E69C");

        byte[] RtnOrderByNeedle(string needleHex, string what)
        {
            var op = Hx("E102E2F001");
            var needle = Hx(needleHex);
            return allPayloads.FirstOrDefault(p => p.Length > 4 + op.Length
                && p.AsSpan(4, op.Length).SequenceEqual(op)
                && p.AsSpan().IndexOf(needle) >= 0)
                ?? throw Fail($"{what}: F001 帧内未找到 needle {needleHex}（抓包会话形状与假设不符）");
        }
        var pRtn3 = RtnOrderByNeedle("EA33E8", "OnRtnOrder '3'（未成交在队）");
        var pRtn0 = RtnOrderByNeedle("E9303030", "OnRtnOrder '0'（全部成交）");
        var pRtnT = FramesWithOp(allPayloads, "E102E2F002").FirstOrDefault()
            ?? throw Fail("OnRtnTrade（F002）未捕获——补抓包需含成交场景");
        // ⑳ 拒绝对 = v1 抓包的「无效的ExchangeID」失败对（ErrorID 0x94；成功报单不发 ⑳——D3 实证）；
        // ㉒ 对 = 推送源会话的撤单失败对（回显携带 capFrontSession——排除 v1 的另一会话）。
        var w20 = WriteContaining(m2frames, "E101E24001",
            p => p.AsSpan().IndexOf(Hx("E355E394")) >= 0, "⑳ 拒绝对（ExchangeID 错误）");
        var p20a = w20[0];   // 4001 OnRspOrderInsert（C# 层不可见但真实——复刻）
        var p20b = w20[1];   // F003 OnErrRtnOrderInsert（拒单唯一可见信号 → RspError）
        var w22 = WriteContaining(m2frames, "E101E24007",
            p => p.AsSpan().IndexOf(capFrontSession) >= 0, "㉒ 撤单对（推送源会话）");
        var p22a = w22[0];   // 4007 OnRspOrderAction（OrderActionAsync 阻塞等它）
        var p22b = w22[1];   // F004 OnErrRtnOrderAction
        Console.WriteLine($"[Gen] M2 帧长: Rtn3={pRtn3.Length} Rtn0={pRtn0.Length} RtnTrade={pRtnT.Length} ⑳={p20a.Length}+{p20b.Length} ㉒={p22a.Length}+{p22b.Length}");

        // —— Edit 收集器：报单族帧（推送/⑳）公共可变字段 ——
        // 变长替换仅限 SDK 自编码可变的字段（合约/价格/量/TradeId/ErrorMsg——客户端 ⑲ 自带可变宽度实证）；
        // user/broker 等宽归一化 F()（⑥ 前科：未知帧上的变长替换有挂死风险）。
        // 时间/OrderLocalID/OrderSysID 序列字段保留 verbatim（陈旧值无害——显示用，边界记录于 README）。
        List<(int Off, int Len, string Expr)> OrderFrameEdits(byte[] p, string name, string opHex)
        {
            var e = new List<(int, int, string)>();
            var op = Hx(opHex);
            e.Add(LocFull(p, Cat(op, [0xE3]), 1, "[instance]", $"{name} instance"));
            var or_ = ShapedRuns(p, r => r.Length == 12 && AllAsciiDigits(r));
            if (or_.Count != 1) throw Fail($"{name}: OrderRef run 命中 {or_.Count} 处（期望 1）");
            e.Add((or_[0].Off, 12, "A(orderRef)"));
            var en = TaggedRuns(p, 0xEA).Where(r => r.Len == 3 && p.AsSpan(r.Off, 3).ToArray().All(b => b is >= (byte)'0' and <= (byte)'4')).ToList();
            if (en.Count != 1) throw Fail($"{name}: 枚举 run 命中 {en.Count} 处（期望 1）");
            e.Add((en[0].Off, 3, "A($\"{priceType}{direction}{offsetFlag}\")"));
            var pr = TaggedRuns(p, 0xE4).Where(r => r.Len is >= 2 and <= 8 && p.AsSpan(r.Off, r.Len).ToArray().Any(b => b >= 0x80)).ToList();
            if (pr.Count != 1) throw Fail($"{name}: 价格 run 命中 {pr.Count} 处（期望 1）");
            e.Add((pr[0].Off, pr[0].Len, "TrimmedDouble(limitPrice)"));
            var vol = TaggedRuns(p, 0xE8).Where(r => r.Len is >= 2 and <= 5
                && p[r.Off + r.Len - 1] is (byte)'1' or (byte)'2' or (byte)'3'
                && p.AsSpan(r.Off, r.Len - 1).ToArray().Any(b => b < 0x20)).ToList();
            if (vol.Count != 1) throw Fail($"{name}: 量+TC run 命中 {vol.Count} 处（期望 1）");
            e.Add((vol[0].Off, vol[0].Len, "Cat(MinimalInt(volume), A(timeCondition.ToString()))"));
            // FrontID/SessionID 编辑整段移除（2026-10-01 实证）：替换破坏 E0 转义结构的逻辑长度 ⇒ SDK
            // 解析失步（InstrumentID 读空）。保留捕获 verbatim——客户端回报匹配按 OrderRef（wrapper 实证
            // 不看 FrontID/SessionID），⑩ 与推送的会话值不一致是已知边界（README 记录）。
            var uh = AllHits(p, Asc(user));
            if (uh.Count == 0) throw Fail($"{name}: user 未命中");
            foreach (var off in uh) e.Add((off, user.Length, $"F(userId, {user.Length})"));
            var bh = AllHits(p, Asc(broker));
            if (bh.Count == 0) throw Fail($"{name}: broker 未命中");
            foreach (var off in bh) e.Add((off, broker.Length, $"F(brokerId, {broker.Length})"));
            var ins = ShapedRuns(p, IsInstrumentRun);
            if (ins.Count == 0) throw Fail($"{name}: 合约 run 未命中");
            // 等宽 F（实证：推送帧变宽即 SDK 失步——ag2610@6B 进 m2701@5B 槽字段错位；超长截断为边界）
            foreach (var (off, len) in ins) e.Add((off, len, $"F(instrumentId, {len})"));
            foreach (var off in AllHits(p, Asc(day))) e.Add((off, day.Length, "A(tradingDay)"));
            CheckDisjoint(name, e.ToArray());
            return e;
        }

        // RspInfo 定位：E3 55 <id-tag E3> <1B id> <E0 转义 GBK run> → 替换区 = [E3 id gbk]（变长——
        // 真前置错误消息长度可变实证：v1 两种 ⑳ 长度不同 ⇒ SDK 容忍该帧族变长）。
        (int Off, int Len) LocateRspInfo(byte[] p, string name)
        {
            var hits = AllHits(p, Hx("E355E3"));
            if (hits.Count < 1) throw Fail($"{name}: RspInfo E355E3 未命中");
            var off = hits[0];
            var k = off + 4;
            while (k < p.Length)
            {
                if (p[k] == 0xE0 && k + 1 < p.Length) { k += 2; continue; }
                if (IsTagByte(p[k])) break;
                k++;
            }
            return (off + 2, k - (off + 2));
        }

        var eRtn3 = OrderFrameEdits(pRtn3, "RtnOrder3", "E102E2F001");
        var eRtn0 = OrderFrameEdits(pRtn0, "RtnOrder0", "E102E2F001");
        var eRtnT = new List<(int, int, string)>();
        {
            eRtnT.Add(LocFull(pRtnT, Cat(Hx("E102E2F002"), [0xE3]), 1, "[instance]", "RtnTrade instance"));
            var or_ = ShapedRuns(pRtnT, r => r.Length == 12 && AllAsciiDigits(r));
            if (or_.Count != 1) throw Fail($"RtnTrade: OrderRef 命中 {or_.Count}");
            eRtnT.Add((or_[0].Off, 12, "A(orderRef)"));
            var tid = TaggedRuns(pRtnT, 0xE6).Where(r => r.Len is >= 1 and <= 12 && AllAsciiDigits(pRtnT.AsSpan(r.Off, r.Len).ToArray())).ToList();
            if (tid.Count != 1) throw Fail($"RtnTrade: TradeID 命中 {tid.Count}");
            eRtnT.Add((tid[0].Off, tid[0].Len, $"F(tradeId, {tid[0].Len})"));   // 等宽（实证：变宽即失步；超长截断为边界）
            // 价格：EB + 二进制 run（"01"前缀 + 尾剥零价格）——前缀 verbatim（方向/开平回显保留捕获形状，边界见 README）
            var px = TaggedRuns(pRtnT, 0xEB).Where(r => r.Len is >= 3 and <= 10
                && pRtnT.AsSpan(r.Off, r.Len).ToArray().Any(b => b >= 0x80)).ToList();
            if (px.Count != 1) throw Fail($"RtnTrade: 价格 run 命中 {px.Count}");
            eRtnT.Add((px[0].Off + 2, px[0].Len - 2, "TrimmedDouble(price)"));
            // 量：E4 + 短二进制 run（≤0x09）
            var vol = TaggedRuns(pRtnT, 0xE4).Where(r => r.Len is 1 or 2 && pRtnT.AsSpan(r.Off, r.Len).ToArray().All(b => b <= 0x09)).ToList();
            if (vol.Count != 1) throw Fail($"RtnTrade: 量 run 命中 {vol.Count}");
            eRtnT.Add((vol[0].Off, vol[0].Len, "MinimalInt(volume)"));
            // FrontID/SessionID 不编辑（同上——E0 转义结构保护）
            foreach (var off in AllHits(pRtnT, Asc(user))) eRtnT.Add((off, user.Length, $"F(userId, {user.Length})"));
            foreach (var off in AllHits(pRtnT, Asc(broker))) eRtnT.Add((off, broker.Length, $"F(brokerId, {broker.Length})"));
            foreach (var (off, len) in ShapedRuns(pRtnT, IsInstrumentRun)) eRtnT.Add((off, len, $"F(instrumentId, {len})"));
            foreach (var off in AllHits(pRtnT, Asc(day))) eRtnT.Add((off, day.Length, "A(tradingDay)"));
            CheckDisjoint("RtnTrade", eRtnT.ToArray());
        }

        // '5'（已撤销）合成：'3' 模板 + 状态字节 EA33→EA35 + 状态消息（未成交→已撤销，同宽 6B GBK）
        var statusOff = AllHits(pRtn3, Hx("EA33"));
        if (statusOff.Count != 1) throw Fail($"RtnOrder3: 状态字节 EA33 命中 {statusOff.Count} 处");
        var msgOff = AllHits(pRtn3, Hx("CEB4B3C9BDBB"));
        if (msgOff.Count != 1) throw Fail($"RtnOrder3: 状态消息（未成交）命中 {msgOff.Count} 处");
        var eRtn5 = new List<(int, int, string)>(eRtn3)
        {
            (statusOff[0] + 1, 1, "[(byte)'5']"),
            (msgOff[0], 6, "EscapedGbk(\"已撤销\")"),
        };

        // ⑳ 拒绝对（4001：instance/reqId/RspInfo/回显族；F003：instance/RspInfo/回显族——reqId 回显保留 verbatim）
        var e20a = OrderFrameEdits(p20a, "⑳-4001", "E101E24001");
        e20a.Add(LocFull(p20a, Hx("E10201E0E5E3"), 1, "[reqId]", "⑳-4001 reqId"));
        var rsp20a = LocateRspInfo(p20a, "⑳-4001");
        e20a.Add((rsp20a.Off, rsp20a.Len, "Cat(H(\"E3\"), MinimalInt(errorId), EscapedGbk(errorMsg))"));
        CheckDisjoint("⑳-4001", e20a.ToArray());
        var e20b = OrderFrameEdits(p20b, "⑳-F003", "E102E2F003");
        var rsp20b = LocateRspInfo(p20b, "⑳-F003");
        e20b.Add((rsp20b.Off, rsp20b.Len, "Cat(H(\"E3\"), MinimalInt(errorId), EscapedGbk(errorMsg))"));
        CheckDisjoint("⑳-F003", e20b.ToArray());

        // ㉒ 对（4007：instance/reqId/RspInfo + OrderRef/FrontSession/user/broker 回显；F004：instance/RspInfo/回显）
        List<(int, int, string)> ActionFrameEdits(byte[] p, string name, string opHex)
        {
            var e = new List<(int, int, string)>();
            var op = Hx(opHex);
            e.Add(LocFull(p, Cat(op, [0xE3]), 1, "[instance]", $"{name} instance"));
            var or_ = ShapedRuns(p, r => r.Length == 12 && AllAsciiDigits(r));
            if (or_.Count != 1) throw Fail($"{name}: OrderRef 命中 {or_.Count}");
            e.Add((or_[0].Off, 12, "A(orderRef)"));
            foreach (var off in AllHits(p, Asc(user))) e.Add((off, user.Length, $"F(userId, {user.Length})"));
            foreach (var off in AllHits(p, Asc(broker))) e.Add((off, broker.Length, $"F(brokerId, {broker.Length})"));
            return e;
        }
        var e22a = ActionFrameEdits(p22a, "㉒-4007", "E101E24007");
        // 4007 的 reqId：E1 02 01 A5 E3 后 1B（OrderActionAsync 按它完成 pending——必须回显；抓包实证形状）
        e22a.Add(LocFull(p22a, Hx("E10201A5E3"), 1, "[reqId]", "㉒-4007 reqId"));
        var rsp22 = LocateRspInfo(p22a, "㉒-4007");
        var e22aErr = new List<(int, int, string)>(e22a)
        {
            (rsp22.Off, rsp22.Len, "Cat(H(\"E3\"), MinimalInt(errorId), EscapedGbk(errorMsg))"),
        };
        CheckDisjoint("㉒-4007", e22aErr.ToArray());
        var e22aOk = new List<(int, int, string)>(e22a)
        {
            // 成功 RspInfo = E4 + GBK「正确」的 ASCII hex 串（⑫⑭ 实证）——变长（错误区→9B）
            (rsp22.Off, rsp22.Len, "Cat(H(\"E4\"), A(\"D5FDC8B7\"))"),
        };
        var e22b = ActionFrameEdits(p22b, "㉒-F004", "E102E2F004");
        var rsp22b = LocateRspInfo(p22b, "㉒-F004");
        e22b.Add((rsp22b.Off, rsp22b.Len, "Cat(H(\"E3\"), MinimalInt(errorId), EscapedGbk(errorMsg))"));
        CheckDisjoint("㉒-F004", e22b.ToArray());

        // —— 发射 M2 模板 + 构造器 + 常量 ——
        sb.AppendLine();
        sb.AppendLine("    /// <summary>M1 ⑩ verbatim 会话身份（FrontID+SessionID，5B）——推送/回显帧 Edit 用（D6：与 ⑩ 同源）。</summary>");
        sb.AppendLine("    internal static readonly byte[] SessionFrontIdSessionId = H(\"012DAF7B02\");");
        EmitTemplate(sb, $"M2 帧 OnRtnOrder '3'（{pRtn3.Length}B payload：未成交在队推送——F001）", "RtnOrder3Template", pRtn3);
        EmitBuilder(sb, "OnRtnOrder '3'（未成交在队——受理/挂单状态推送）。",
            "BuildRtnOrder3(byte instance, string userId, string brokerId, string instrumentId, string orderRef, char priceType, char direction, char offsetFlag, double limitPrice, int volume, char timeCondition, string tradingDay)", "RtnOrder3Template", eRtn3);
        EmitTemplate(sb, $"M2 帧 OnRtnOrder '0'（{pRtn0.Length}B payload：全部成交推送——F001；VolumeTraded 保留捕获值 1，>1 手的全成回报成交量失真（边界））", "RtnOrder0Template", pRtn0);
        EmitBuilder(sb, "OnRtnOrder '0'（全部成交推送）。",
            "BuildRtnOrder0(byte instance, string userId, string brokerId, string instrumentId, string orderRef, char priceType, char direction, char offsetFlag, double limitPrice, int volume, char timeCondition, string tradingDay)", "RtnOrder0Template", eRtn0);
        EmitBuilder(sb, "OnRtnOrder '5'（已撤销——'3' 模板合成：状态字节/状态消息替换）。",
            "BuildRtnOrder5(byte instance, string userId, string brokerId, string instrumentId, string orderRef, char priceType, char direction, char offsetFlag, double limitPrice, int volume, char timeCondition, string tradingDay)", "RtnOrder3Template", eRtn5);
        EmitTemplate(sb, $"M2 帧 OnRtnTrade（{pRtnT.Length}B payload：成交回报推送——F002）", "RtnTradeTemplate", pRtnT);
        EmitBuilder(sb, "OnRtnTrade（成交回报推送——方向/开平前缀保留捕获形状（边界：非买开成交的方向回显失真））。",
            "BuildRtnTrade(byte instance, string userId, string brokerId, string instrumentId, string orderRef, string tradeId, double price, int volume, string tradingDay)", "RtnTradeTemplate", eRtnT);
        EmitTemplate(sb, $"M2 帧 ⑳-4001（{p20a.Length}B payload：OnRspOrderInsert 拒绝——C# 层不可见但真实，复刻）", "OrderInsertRspErrTemplate", p20a);
        EmitBuilder(sb, "⑳-4001 拒绝（payload；会话层与 F003 各自 BuildFrame 后拼接一次写）。",
            "BuildOrderInsertRspErr(byte instance, byte reqId, int errorId, string errorMsg, string userId, string brokerId, string instrumentId, string orderRef, char priceType, char direction, char offsetFlag, double limitPrice, int volume, char timeCondition)", "OrderInsertRspErrTemplate", e20a);
        EmitTemplate(sb, $"M2 帧 ⑳-F003（{p20b.Length}B payload：OnErrRtnOrderInsert——拒单在 C# 层唯一可见信号 → RspError）", "ErrRtnOrderInsertTemplate", p20b);
        EmitBuilder(sb, "⑳-F003 报单错误回报（拒单 → RspError 事件）。",
            "BuildErrRtnOrderInsert(byte instance, int errorId, string errorMsg, string userId, string brokerId, string instrumentId, string orderRef, char priceType, char direction, char offsetFlag, double limitPrice, int volume, char timeCondition)", "ErrRtnOrderInsertTemplate", e20b);
        EmitTemplate(sb, $"M2 帧 ㉒-4007（{p22a.Length}B payload：OnRspOrderAction——OrderActionAsync 阻塞等它）", "OrderActionRspTemplate", p22a);
        EmitBuilder(sb, "㉒-4007 撤单应答·失败（RspInfo ← 核心错误码/消息）。",
            "BuildOrderActionRspErr(byte instance, byte reqId, int errorId, string errorMsg, string userId, string brokerId, string orderRef)", "OrderActionRspTemplate", e22aErr);
        EmitBuilder(sb, "㉒-4007 撤单应答·成功（RspInfo → E4 +「正确」——成功撤单不发 F004）。",
            "BuildOrderActionRspOk(byte instance, byte reqId, string userId, string brokerId, string orderRef)", "OrderActionRspTemplate", e22aOk);
        EmitTemplate(sb, $"M2 帧 ㉒-F004（{p22b.Length}B payload：OnErrRtnOrderAction 撤单错误回报）", "ErrRtnOrderActionTemplate", p22b);
        EmitBuilder(sb, "㉒-F004 撤单错误回报（payload；会话层与 4007 拼接一次写）。",
            "BuildErrRtnOrderAction(byte instance, int errorId, string errorMsg, string userId, string brokerId, string orderRef)", "ErrRtnOrderActionTemplate", e22b);

        // ═══════════════ M3 模板（行情腿——2026-10-01 v2 抓包 md.txt）═══════════════
        // MD 腿信封与交易腿同款（握手/挑战/确认复用）；⑤ MD 登录 op E4 30 E6 03；⑦ 订阅 op E4 44 01；
        // tick 推送 op E4 F1 01——多合约分节（每合约 A 块 + E1 回显 + B 块），取首合约完整节做单合约模板。
        var mdPaths = capturePaths.Select(p => Path.Combine(Path.GetDirectoryName(p)!, "md.txt")).Where(File.Exists).ToList();
        var mdPayloads = new List<byte[]>();
        var mdByFile = new List<List<byte[]>>();
        foreach (var path in mdPaths)
        {
            var filePayloads = new List<byte[]>();
            foreach (var (raw, _) in ParseCaptureRaw(path))
            {
                try { filePayloads.AddRange(SplitWrite(raw)); }
                catch { /* 截断/心跳写跳过 */ }
            }
            mdByFile.Add(filePayloads);
            mdPayloads.AddRange(filePayloads);
        }
        var pMdLogin = mdPayloads.FirstOrDefault(p => p.Length is > 9 and < 100
                && p.AsSpan(4, 5).SequenceEqual(Hx("E43001E502"))
                && p.AsSpan().IndexOf(Hx("4E6F204572726F72")) >= 0)   // "No Error"（区别于交易腿 ⑩ 同 op 前缀）
            ?? throw Fail("MdLoginRsp 未捕获（op E43001 + No Error + <100B）——补抓包需含 MD 登录会话");
        var eMdLogin = new List<(int, int, string)>();
        foreach (var off in AllHits(pMdLogin, Asc(day))) eMdLogin.Add((off, day.Length, "A(tradingDay)"));
        CheckDisjoint("MdLoginRsp", eMdLogin.ToArray());

        // tick 推送模板 = **E4F103 流式增量帧**（2026-10-02 长会话抓包定案：481 帧/次会话、SDK 正常逐帧
        // 回调零洪水）。E4F101 是订阅后**一次性快照** op（真前置只发一次）——旧模板取自它，重复推送快照
        // 帧触发 native 无限重投（~25 万次/秒，8 种帧形状实验均复现）。E4F103 单合约结构（105B m2701 例）：
        //   E4F101→E4F103 op | E5 04 | E1 d2 | E4 "2439" 链路静态 | E1 [86]+合约 | EB "HH:mm:ss" |
        //   E5 交易日 | E1 合约（净） | E1 "2433" | E1 [1c]+last(3B) | E6 量串 | E2 量串 | E4 "2434" |
        //   E1 [18]+bid(3B) | E7 [量2B]+ask(3B) | E7 [量3B] | E1 08 E0转义额——价格三槽/时间/日/合约编辑，量串 verbatim。
        List<(int, int, string)>? eTick = null;
        var pTick = Array.Empty<byte>();
        List<byte[]> tickSource = mdPayloads;
        foreach (var filePayloads in mdByFile)
        {
        foreach (var cand in filePayloads.Where(p => p.Length is > 60 and < 130 && p.AsSpan(4, 3).SequenceEqual(Hx("E4F103")))
                     .OrderByDescending(p => p.Length))
        {
            var t = cand;
            // 结构定位：合约×2（E1 [86]前缀 6B + E1 净 5B）、时间（EB 8 字形）、日（8 数字 run）、
            // last（E1 run [1c]+3B）、bid（E1 run [18]+3B）、ask（E7 run 5B 且 run[2]≥0x40——量 2B 后缀价格）
            var e1s = TaggedRuns(t, 0xE1).ToList();
            var instPref = e1s.Where(r => r.Len == 6 && t[r.Off] == 0x86 && IsInstrumentRun(t[(r.Off + 1)..(r.Off + r.Len)])).ToList();
            var instClean = e1s.Where(r => r.Len is >= 4 and <= 8 && IsInstrumentRun(t[r.Off..(r.Off + r.Len)])).ToList();
            var lastSlot = e1s.Where(r => r.Len == 4 && t[r.Off] == 0x1c).ToList();
            var bidSlot = e1s.Where(r => r.Len == 4 && t[r.Off] == 0x18).ToList();
            var e7s = TaggedRuns(t, 0xE7).ToList();
            var askSlot = e7s.Where(r => r.Len == 5 && t[r.Off + 2] >= 0x40).ToList();
            var times = ShapedRuns(t, IsTimeRun);
            var days = ShapedRuns(t, IsDayRun);
            if (instPref.Count != 1 || instClean.Count != 1 || lastSlot.Count != 1 || bidSlot.Count != 1
                || askSlot.Count != 1 || times.Count != 1 || days.Count != 1) continue;
            // 钉死 m2701 帧（E2E 验证过的形状——跨帧的 ask 槽结构有变体，换帧曾致 ask 解析漂移）
            if (Encoding.ASCII.GetString(t[instClean[0].Off..(instClean[0].Off + instClean[0].Len)]) != "m2701") continue;
            var dayVal = Encoding.ASCII.GetString(t[days[0].Off..(days[0].Off + 8)]);
            var e = new List<(int, int, string)>();
            e.Add((days[0].Off, 8, "A(tradingDay)"));
            e.Add((instPref[0].Off + 1, 5, "F(instrumentId, 5)"));   // 0x86 前缀后的合约体
            e.Add((instClean[0].Off, instClean[0].Len, $"F(instrumentId, {instClean[0].Len})"));
            e.Add((times[0].Off, 8, "A(updateTime)"));
            e.Add((lastSlot[0].Off + 1, 3, "PadTrimmedDouble(lastPrice, 3)"));
            e.Add((bidSlot[0].Off + 1, 3, "PadTrimmedDouble(bidPrice, 3)"));
            // ask 槽 wire 偏移 = 跳过前 2 个逻辑字节（bidvol）后的 wire 位置——bidvol 字节若落在
            // tag 字母表（E0..EF/FE）会被 E0 转义、wire 额外 +1（107B 帧实证：bidvol [02,E9] → wire [02,E0,E9]，
            // 朴素 +2 编辑撞进转义对 → SDK 读出捕获值 3372 而非注入价）。转义感知跳字节。
            var askOff = askSlot[0].Off;
            var askSkip = 0;
            while (askSkip < 2)
            {
                if (t[askOff] == 0xE0) askOff += 2;   // 转义对 = 1 逻辑字节
                else askOff += 1;
                askSkip++;
            }
            e.Add((askOff, 3, "PadTrimmedDouble(askPrice, 3)"));
            CheckDisjoint("MdTick", e.ToArray());
            eTick = e;
            pTick = t;
            tickSource = filePayloads;
            Console.WriteLine($"[Gen] MdTick={t.Length}B E4F103（day={dayVal} inst={Encoding.ASCII.GetString(t[(instClean[0].Off)..(instClean[0].Off + instClean[0].Len)])}）");
            break;
        }
        if (eTick is not null) break;   // 内层 break 只退本文件循环——不退外层会被后续文件的匹配覆盖（曾致 109B→107B 静默换帧）
        }
        if (eTick is null) throw Fail("E4F103 流式 tick 帧未捕获或结构槽未命中——需长会话抓包（订阅后保持 MD 存活收 tick 流）");

        // 订阅应答：捕获帧 verbatim（E44402 + 回显合约块——SDK 无应答会断连，2026-10-01 E2E 实证）。
        // 边界：多合约订阅的回显是捕获的 4 合约块（首块 m2701/DCE 与测试匹配；幻影 ack 无害——wrapper 不校验）。
        // 与 tick 模板同文件（同一真实会话——异会话的计数器致 SDK 失步挂死）且回显块含 tick 首节合约
        var tickInstNeedle = pTick.Skip(TaggedRuns(pTick, 0xE1).Where(r => IsInstrumentRun(pTick.AsSpan(r.Off, r.Len).ToArray())).First().Off).Take(5).ToArray();
        var pMdSubRsp = tickSource.Where(p => p.Length > 9 && p.AsSpan(4, 3).SequenceEqual(Hx("E44402"))
                && p.AsSpan().IndexOf(tickInstNeedle) >= 0).OrderByDescending(p => p.Length).FirstOrDefault()
            ?? throw Fail("MdSubscribeRsp（op E44402）未捕获——补抓包需含订阅会话");
        // ═══ 查合约 tag 应答模板（2026-10-02 过滤查询抓包定案）═══
        // 真前置按应答大小选格式：大清单（1.15MB/15990 行）走 LZ4 批量二进制（通道路由 0104FA0F，SDK 二进制
        // strings 证实 Lz4Protocol.cpp——不逆向）；过滤单合约（421B）走 tag 协议（路由 0103344C，op E104E2802F）。
        // 本地前置只回上市表（小）⇒ tag 模板即可。请求侧 op E104E2802E、reqId 标签族 E1 01 01 5D E3；
        // 响应 instance/reqId 回显请求值。多行 = 每合约一帧同写（首帧 verbatim isLast——isLast 编码未逆向，
        // 多合约时只有首行到达 SDK，v1 边界：默认上市表 = 单合约 zx001）。
        var pQryInst = allPayloads.Where(p => p.Length is > 100 and < 1000
                && p.AsSpan(4, 5).SequenceEqual(Hx("E104E2802F")))
            .OrderBy(p => p.Length).FirstOrDefault()
            ?? throw Fail("查合约 tag 应答帧（op E104E2802F，<1000B）未捕获——需过滤单合约查询抓包（大清单走 LZ4 批量格式不适用本地小上市表）");
        var eQryInst = new List<(int, int, string)>();
        eQryInst.Add(LocFull(pQryInst, Cat(Hx("E104E2802F"), [0xE3]), 1, "[instance]", "QryInstrumentRsp instance"));
        eQryInst.Add(LocFull(pQryInst, Hx("E101025EE3"), 1, "[reqId]", "QryInstrumentRsp reqId"));
        var instIdHits = AllHits(pQryInst, Asc("sc2706"));
        if (instIdHits.Count < 1) throw Fail($"QryInstrumentRsp: 合约号槽命中 {instIdHits.Count} 处");
        foreach (var off in instIdHits) eQryInst.Add((off, 6, "F(instrumentId, 6)"));
        foreach (var off in AllHits(pQryInst, Asc("INE"))) eQryInst.Add((off, 3, "F(exchangeId, 3)"));
        CheckDisjoint("QryInstrumentRsp", eQryInst.ToArray());
        Console.WriteLine($"[Gen] M3 帧长: MdLoginRsp={pMdLogin.Length} MdTick={pTick.Length} MdSubscribeRsp={pMdSubRsp.Length} QryInstrumentRsp={pQryInst.Length}B（合约槽×{instIdHits.Count}）");
        EmitTemplate(sb, $"M3 帧 MdSubscribeRsp（{pMdSubRsp.Length}B payload：订阅应答——捕获 verbatim，回显块边界见注释）", "MdSubscribeRspTemplate", pMdSubRsp);
        EmitTemplate(sb, $"M3 帧 QryInstrumentRsp（{pQryInst.Length}B payload：查合约 tag 应答——单行模板（InstrumentId/ExchangeId 等宽编辑；VolumeMultiple/PriceTick 等保留捕获 verbatim——板子只读 id/交易所）", "QryInstrumentRspTemplate", pQryInst);
        EmitBuilder(sb, "查合约应答（板子数据源——instance/reqId 回显请求值；多合约=每合约一帧同写，首帧 verbatim isLast）。",
            "BuildQryInstrumentRsp(byte instance, byte reqId, string instrumentId, string exchangeId)", "QryInstrumentRspTemplate", eQryInst);

        EmitTemplate(sb, $"M3 帧 MdLoginRsp（{pMdLogin.Length}B payload：MD 登录应答——交易日编辑）", "MdLoginRspTemplate", pMdLogin);
        EmitBuilder(sb, "MD 登录应答（凭据不校验——行情订阅无账户语义）。",
            "BuildMdLoginRsp(string tradingDay)", "MdLoginRspTemplate", eMdLogin);
        EmitTemplate(sb, $"M3 帧 MdTick（{pTick.Length}B payload：单合约 E4F103 流式 tick——价格三槽等宽填充，量串 verbatim）", "MdTickTemplate", pTick);
        EmitBuilder(sb, "tick 推送（E4F103 流式 op——E4F101 快照 op 重复推送触发 native 重投，勿改回）；last/bid/ask 等宽填充，量串 verbatim。",
            "BuildMdTick(string instrumentId, string tradingDay, string updateTime, double lastPrice, double bidPrice, double askPrice)", "MdTickTemplate", eTick);

        sb.AppendLine("}");

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        File.WriteAllText(outPath, sb.ToString());
        Console.WriteLine($"[Gen] 帧长: ④=19 ⑥={p6.Length} ⑧={p8.Length} ⑩主={p10a.Length} ⑩伴随={companionFrame.Length} ⑫={p12.Length} ⑯={p16.Length} ⑱=13");
        Console.WriteLine($"[Gen] ✓ 写出: {outPath}（{sb.Length} 字符）");
    }

    // ── 定位原语：needle = 前缀+值[+后缀]；偏移 = 命中 + 前缀长，旧长 = 值长 ──

    /// <summary>纯前缀 needle：值紧随 needle 之后（reqId/instance 等单字节回显字段）。</summary>
    private static (int Off, int Len, string Expr) LocFull(byte[] p, byte[] prefixNeedle, int valueLen, string expr, string what)
    {
        var hit = Find1(p, prefixNeedle, what);
        return (hit + prefixNeedle.Length, valueLen, expr);
    }

    /// <summary>前缀+值：值本身要被替换（broker/user/交易日等标记界定字段）。</summary>
    private static (int Off, int Len, string Expr) LocVal(byte[] p, byte[] prefix, byte[] value, string expr, string what)
    {
        var hit = Find1(p, Cat(prefix, value), what);
        return (hit + prefix.Length, value.Length, expr);
    }

    /// <summary>前缀+值+后缀：后缀参与唯一性（⑯ 账户#1 短码是账户#2 完整用户名的前缀——
    /// 无后缀会双命中）。后缀字节本身不被替换。</summary>
    private static (int Off, int Len, string Expr) LocValSur(byte[] p, byte[] prefix, byte[] value, byte[] suffix, string expr, string what)
    {
        var hit = Find1(p, Cat(prefix, value, suffix), what);
        return (hit + prefix.Length, value.Length, expr);
    }

    private static void CheckDisjoint(string name, (int Off, int Len, string Expr)[] edits)
    {
        var ordered = edits.OrderBy(e => e.Off).ToArray();
        for (var i = 0; i + 1 < ordered.Length; i++)
            if (ordered[i].Off + ordered[i].Len > ordered[i + 1].Off)
                throw Fail($"{name}: 编辑区重叠 @ {ordered[i].Off}+{ordered[i].Len} > {ordered[i + 1].Off}（needle 定位有误）");
    }

    // ── 查找（全部响亮断言）──

    private static int Find1(byte[] p, byte[] needle, string what)
    {
        var hits = AllHits(p, needle);
        if (hits.Count != 1)
            throw Fail($"{what}: needle {ToHex(needle)} 命中 {hits.Count} 处 @ [{string.Join(",", hits)}]（期望恰好 1 处）——抓包会话形状与假设不符");
        return hits[0];
    }

    private static int[] FindN(byte[] p, byte[] needle, int expected, string what)
    {
        var hits = AllHits(p, needle);
        if (hits.Count != expected)
            throw Fail($"{what}: needle {ToHex(needle)} 命中 {hits.Count} 处 @ [{string.Join(",", hits)}]（期望 {expected} 处）");
        return hits.ToArray();
    }

    private static List<int> AllHits(ReadOnlySpan<byte> p, ReadOnlySpan<byte> needle)
    {
        var hits = new List<int>();
        var i = 0;
        while (true)
        {
            var idx = p[i..].IndexOf(needle);
            if (idx < 0) break;
            hits.Add(i + idx);
            i += idx + 1;
        }
        return hits;
    }

    // ── M2 结构化 run 查找（2026-10-01 补抓包：推送/撤单/拒绝对帧的字段定位）──
    // run = tag 后到下一 tag 字母表字节（E1..EF+FE；E0+下一字节为转义对属数据）的值段。

    private static bool IsTagByte(byte b) => b is >= 0xE1 and <= 0xEF or 0xFE;

    /// <summary>枚举 payload 内指定 tag 的全部 (值偏移, 值长)——E0 转义感知。</summary>
    private static IEnumerable<(int Off, int Len)> TaggedRuns(byte[] p, byte tag)
    {
        for (var i = 4; i < p.Length; i++)
        {
            if (p[i] != tag) continue;
            var j = i + 1;
            while (j < p.Length)
            {
                if (p[j] == 0xE0 && j + 1 < p.Length) { j += 2; continue; }
                if (IsTagByte(p[j])) break;
                j++;
            }
            if (j > i + 1) yield return (i + 1, j - i - 1);
            i = j - 1;
        }
    }

    /// <summary>任意 tag 下满足形状谓词的全部 run（工具：OrderRef/枚举/价格等结构定位）。</summary>
    private static List<(int Off, int Len)> ShapedRuns(byte[] p, Func<byte[], bool> match)
    {
        var outList = new List<(int, int)>();
        for (var i = 4; i < p.Length; i++)
        {
            if (!IsTagByte(p[i]) || p[i] == 0xE0) continue;
            var j = i + 1;
            while (j < p.Length)
            {
                if (p[j] == 0xE0 && j + 1 < p.Length) { j += 2; continue; }
                if (IsTagByte(p[j])) break;
                j++;
            }
            if (j > i + 1 && match(p[(i + 1)..j]))
                outList.Add((i + 1, j - i - 1));
            i = j - 1;
        }
        return outList;
    }

    private static bool AllAsciiDigits(byte[] r) => r.Length > 0 && r.All(b => b is >= (byte)'0' and <= (byte)'9');
    private static bool IsInstrumentRun(byte[] r)
    {
        var letters = 0;
        while (letters < r.Length && char.IsAsciiLetter((char)r[letters])) letters++;
        return letters is >= 1 and <= 2 && r.Length - letters is >= 3 and <= 4
            && r.Skip(letters).All(b => b is >= (byte)'0' and <= (byte)'9');
    }
    private static bool IsTimeRun(byte[] r)
        => r.Length == 8 && r[2] == (byte)':' && r[5] == (byte)':' && AllAsciiDigits(r.Where((_, k) => k is 0 or 1 or 3 or 4 or 6 or 7).ToArray());
    private static bool IsDayRun(byte[] r) => r.Length == 8 && AllAsciiDigits(r) && r[0] == (byte)'2';

    /// <summary>op 定位帧池中的帧（payload[4..] 前缀匹配）；可选内容过滤器。</summary>
    private static List<byte[]> FramesWithOp(List<byte[]> payloads, string opHex, Func<byte[], bool>? filter = null)
    {
        var op = Hx(opHex);
        return payloads.Where(p => p.Length > 4 + op.Length && p.AsSpan(4, op.Length).SequenceEqual(op)
            && (filter is null || filter(p))).ToList();
    }

    /// <summary>一次 TCP 写 → payload 列表（多帧同写切分：⑩=2帧、⑳/㉒=2帧、推送批写=N帧）。
    /// 声明长度超出剩余 ⇒ 响亮失败（抓包截断——RecordingProxy 上限须 ≥8192）。</summary>
    private static List<byte[]> SplitWrite(byte[] raw)
    {
        var outList = new List<byte[]>();
        var i = 0;
        while (i + 4 <= raw.Length && raw[i] == 0x02 && raw[i + 1] == 0x00)
        {
            var ln = BinaryPrimitives.ReadUInt16BigEndian(raw.AsSpan(i + 2, 2));
            if (i + 4 + ln > raw.Length)
                throw Fail($"多帧写切分失败 @{i}：声明 {ln}B 超出剩余 {raw.Length - i - 4}B（抓包截断？——RecordingProxy 上限须 ≥8192）");
            outList.Add(raw[(i + 4)..(i + 4 + ln)]);
            i += 4 + ln;
        }
        if (i != raw.Length) throw Fail($"多帧写尾部残余 {raw.Length - i}B——帧头失步（截断或格式漂移）");
        return outList;
    }

    /// <summary>定位包含指定帧（op+过滤器）的那次写的全部子帧（⑳/㉒ 两帧同写的配对提取）。</summary>
    private static byte[][] WriteContaining(List<(byte[] Raw, string Dir)> frames, string opHex, Func<byte[], bool> filter, string what)
    {
        var op = Hx(opHex);
        foreach (var (raw, _) in frames)
        {
            List<byte[]> ps;
            try { ps = SplitWrite(raw); }
            catch { continue; }   // 心跳/握手等非标准帧跳过
            if (ps.Any(p => p.Length > 4 + op.Length && p.AsSpan(4, op.Length).SequenceEqual(op) && filter(p)))
                return ps.ToArray();
        }
        throw Fail($"{what}: 未在任一写中找到该帧");
    }


    // ── 转义解码 ──

    /// <summary>⑯ 余额槽转义解码：从 off 起累计 8 个逻辑字节（E0 = 转义前缀，下一字节为数据），
    /// 返回 (线上宽度, 逻辑大端 double)。用于推导槽宽（勿手数——宽度随值内 tag 字节数可变）。</summary>
    private static (int WireLen, double Value) DecodeEscapedMoney(byte[] p, int off, string what)
    {
        var logical = new byte[8];
        var li = 0;
        var i = off;
        while (li < 8)
        {
            if (i >= p.Length) throw Fail($"{what} @{off}: 未凑满 8 逻辑字节即到帧尾——E0 转义模型有误");
            var b = p[i++];
            if (b == 0xE0)
            {
                if (i >= p.Length) throw Fail($"{what} @{off}: 转义前缀 E0 后无数据字节——E0 转义模型有误");
                logical[li++] = p[i++];
            }
            else
            {
                logical[li++] = b;
            }
        }
        return (i - off, BinaryPrimitives.ReadDoubleBigEndian(logical));
    }

    // ── 抓包解析 ──

    /// <summary>宽松抓包解析（无 M1 会话形状断言——行情腿 md.txt 仅 8 帧且不查方向交替）。</summary>
    private static List<(byte[] Raw, string Dir)> ParseCaptureRaw(string path)
    {
        if (!File.Exists(path)) throw Fail($"抓包文件不存在: {path}");
        var frames = new List<(byte[] Raw, string Dir)>();
        foreach (var line in File.ReadLines(path))
        {
            if (line.Length == 0) continue;
            var m = LineRe.Match(line);
            if (!m.Success) continue;
            frames.Add((Convert.FromHexString(m.Groups[2].Value), m.Groups[1].Value));
        }
        return frames;
    }

    private static List<(byte[] Raw, string Dir)> ParseCapture(string path)
    {
        if (!File.Exists(path)) throw Fail($"抓包文件不存在: {path}");
        var frames = new List<(byte[] Raw, string Dir)>();
        var no = 0;
        foreach (var line in File.ReadLines(path))
        {
            no++;
            if (line.Length == 0) continue;
            var m = LineRe.Match(line);
            if (!m.Success)
                throw Fail(
                    $"抓包第 {no} 行不匹配记录格式（{line.Length} 字符）: " +
                    $"头50[{line[..Math.Min(50, line.Length)]}] 尾40[{line[^Math.Min(40, line.Length)..]}]");
            frames.Add((Convert.FromHexString(m.Groups[2].Value), m.Groups[1].Value));
        }
        if (frames.Count < 18)
            throw Fail($"抓包仅 {frames.Count} 帧（M1 模板需完整认证/登录/查询会话 ≥18 帧）: {path}");
        // 帧 1-18 应为严格的请求/应答交替（奇数 C→S、偶数 S→C）——形状漂移立即暴露
        for (var i = 1; i <= 18; i++)
        {
            var expected = i % 2 == 1 ? "C→S" : "S→C";
            if (frames[i - 1].Dir != expected)
                throw Fail($"帧 {i} 方向 {frames[i - 1].Dir} ≠ 期望 {expected}——会话形状与抓包分析不符");
        }
        return frames;
    }

    /// <summary>标准帧 payload（剥 4B 头；头/长度不符即抛——半帧或格式漂移不容忍）。</summary>
    private static byte[] Payload(List<(byte[] Raw, string Dir)> frames, int idx)
    {
        if (idx < 1 || idx > frames.Count)
            throw Fail($"帧 {idx} 不存在（抓包共 {frames.Count} 帧）");
        var raw = frames[idx - 1].Raw;
        if (raw.Length < 4 || raw[0] != 0x02 || raw[1] != 0x00)
            throw Fail($"帧 {idx} 非标准 0200 帧（头 = {ToHex(raw.AsSpan(0, Math.Min(4, raw.Length)))}）");
        var plen = BinaryPrimitives.ReadUInt16BigEndian(raw.AsSpan(2, 2));
        if (4 + plen != raw.Length)
            throw Fail($"帧 {idx} 长度不符：头声明 payload {plen}B + 4 ≠ 实际 {raw.Length}B");
        return raw[4..];
    }

    // ── 值提取（标记界定，同 CtpFieldCodec 语义：E1..EF+FE 终止 + 可打印 ASCII）──

    /// <summary>标记后第一个可打印 ASCII run（≥1 字符）。用于自推导 broker/user/appId。</summary>
    private static string? ExtractRun(byte[] p, byte marker)
    {
        for (var i = 0; i < p.Length; i++)
        {
            if (p[i] != marker) continue;
            var j = i + 1;
            while (j < p.Length && p[j] is >= 0x20 and <= 0x7E && !IsMarker(p[j])) j++;
            if (j > i + 1) return Encoding.ASCII.GetString(p[(i + 1)..j]);
        }
        return null;
    }

    private static bool IsMarker(byte b) => b is >= 0xE1 and <= 0xEF or 0xFE;

    /// <summary>⑩ 内 1010030124 前缀后的 8 位数字（yyyyMMdd）——交易日从帧内容自推导。</summary>
    private static string ExtractDay(byte[] p)
    {
        var prefix = Hx("1010030124");
        foreach (var hit in AllHits(p, prefix))
        {
            var end = hit + prefix.Length;
            if (end + 8 > p.Length) continue;
            var ok = true;
            for (var j = end; j < end + 8; j++)
                if (p[j] is < 0x30 or > 0x39) { ok = false; break; }
            if (ok) return Encoding.ASCII.GetString(p[end..(end + 8)]);
        }
        throw Fail("⑩ 内 1010030124 前缀后无 8 位数字交易日——会话形状与抓包分析不符");
    }

    // ── 路径解析 ──

    /// <summary>默认抓包 = $TMP/ctp-proto-capture 下 trade.txt 帧数最多的目录（同数取最新）。</summary>
    private static string LatestCapture()
    {
        var root = Path.Combine(Path.GetTempPath(), "ctp-proto-capture");
        if (!Directory.Exists(root))
            throw Fail($"抓包根目录不存在: {root}——先跑抓包模式，或显式传 trade.txt 路径");
        var best = Directory.GetDirectories(root)
            .Select(d => new { Dir = d, Trade = Path.Combine(d, "trade.txt") })
            .Where(x => File.Exists(x.Trade))
            .Select(x => new { x.Trade, x.Dir, Lines = File.ReadAllLines(x.Trade).Count(l => l.Length > 0) })
            .Where(x => x.Lines > 0)
            .OrderByDescending(x => x.Lines)
            .ThenByDescending(x => x.Dir)   // 目录名 = yyyyMMdd-HHmmss，字典序即时间序
            .FirstOrDefault();
        if (best is null)
            throw Fail($"{root} 下无含 trade.txt 的抓包目录——先跑抓包模式，或显式传路径");
        return best.Trade;
    }

    private static string? FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent!)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Aether.AI.Platform.slnx"))) return dir.FullName;
        }
        return null;
    }

    // ── 字节助手 ──

    private static byte[] Hx(string hex) => Convert.FromHexString(hex);
    private static byte[] Asc(string s) => Encoding.ASCII.GetBytes(s);

    private static byte[] Cat(params byte[][] parts)
    {
        var r = new byte[parts.Sum(p => p.Length)];
        var i = 0;
        foreach (var p in parts) { p.CopyTo(r, i); i += p.Length; }
        return r;
    }

    private static string ToHex(ReadOnlySpan<byte> b) => Convert.ToHexString(b);

    private static InvalidOperationException Fail(string msg) => new(msg);

    // ── 输出装配 ──

    private static void EmitHeader(StringBuilder sb, string capturePath, int frameCount)
    {
        sb.AppendLine("// ─────────────────────────────────────────────────────────────────────────────");
        sb.AppendLine("// CtpFrontTemplates.cs —— 由抓包自动生成，勿手改。");
        sb.AppendLine("// 生成器: dotnet run --project apps/CtpSim/Acme.Ctp.ProtoCapture -- gen <trade.txt> <本文件>");
        sb.AppendLine("//   （FrontGen.cs：抓包 S→C 应答帧 verbatim 复刻 + 标记界定字段替换；全部偏移由");
        sb.AppendLine("//     生成器计算，needle 唯一命中/编辑区无重叠/余额像钱均生成期断言——零人工转录。）");
        sb.AppendLine($"// 抓包源: {capturePath}（{frameCount} 帧）");
        sb.AppendLine("// 帧字节为抓包原样（含 SimNow 认证 token/密文/账号）——仅本地仿真用，勿入公网/勿分发。");
        sb.AppendLine("// ─────────────────────────────────────────────────────────────────────────────");
        sb.AppendLine();
        sb.AppendLine("using System.Buffers.Binary;");
        sb.AppendLine("using System.Linq;");
        sb.AppendLine("using System.Text;");
        sb.AppendLine();
        sb.AppendLine("namespace Acme.Ctp.Front;");
        sb.AppendLine();
        sb.AppendLine("/// <summary>CTP 前置应答帧模板（抓包 verbatim + 偏移替换）——由 ProtoCapture gen 生成。</summary>");
        sb.AppendLine("internal static class CtpFrontTemplates");
        sb.AppendLine("{");
        sb.AppendLine("    // GBK 错误消息（EscapedGbk）需要 CodePages provider——.NET 默认只带 UTF/ASCII，macOS/Linux 上必须注册");
        sb.AppendLine("    static CtpFrontTemplates() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);");
    }

    private static void EmitHelpers(StringBuilder sb)
    {
        sb.AppendLine("    internal static byte[] H(string hex) => Convert.FromHexString(hex);");
        sb.AppendLine();
        sb.AppendLine("    /// <summary>ASCII 字符串值 → 字节（协议字段值为 ASCII）。</summary>");
        sb.AppendLine("    internal static byte[] A(string s) => Encoding.ASCII.GetBytes(s);");
        sb.AppendLine();
        sb.AppendLine("    /// <summary>等宽归一化：值截断/右补空格到抓包槽宽再转 ASCII。verbatim 模板对字段宽度敏感——");
        sb.AppendLine("    /// 变长替换缩/撑 payload 曾致 SDK 记录遍历失步挂死（>6 字符用户/空 AppId 均实测）；");
        sb.AppendLine("    /// 任意宽度 broker/user/AppId/账户都归一到抓包形状。回显字段仅语义展示，账户身份以核心账本为准。</summary>");
        sb.AppendLine("    internal static byte[] F(string s, int width)");
        sb.AppendLine("    {");
        sb.AppendLine("        var v = s.Length > width ? s[..width] : s.PadRight(width, ' ');");
        sb.AppendLine("        return Encoding.ASCII.GetBytes(v);");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    /// <summary>资金字段值：大端 double（8B）。</summary>");
        sb.AppendLine("    internal static byte[] Money(double v)");
        sb.AppendLine("    {");
        sb.AppendLine("        var b = new byte[8];");
        sb.AppendLine("        BinaryPrimitives.WriteDoubleBigEndian(b, v);");
        sb.AppendLine("        return b;");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    /// <summary>资金字段值：大端 double + E0 转义（抓包实证 999927.69 → 逻辑 41 2E 83 EF 61 47 AE 14，");
        sb.AppendLine("    /// 线上 41 2E 83 E0 EF 61 47 AE 14——tag 字母表 {E0..EF, FE} 字节前插 E0；E0 自身亦须转义。");
        sb.AppendLine("    /// 转义范围 E0..EF+FE 为实证+推断（EF 由抓包证明，E0/FE 由「读取方按 tag 分界」必然性推出；");
        sb.AppendLine("    /// F0..FD/FF 未观察到、推定无需转义）。值的线上宽度可变（8B 起，每个转义 +1）。</summary>");
        sb.AppendLine("    internal static byte[] EscapedMoney(double v)");
        sb.AppendLine("    {");
        sb.AppendLine("        var raw = Money(v);");
        sb.AppendLine("        var n = 0;");
        sb.AppendLine("        foreach (var b in raw)");
        sb.AppendLine("            if (b is (>= 0xE0 and <= 0xEF) or 0xFE) n++;");
        sb.AppendLine("        var r = new byte[raw.Length + n];");
        sb.AppendLine("        var i = 0;");
        sb.AppendLine("        foreach (var b in raw)");
        sb.AppendLine("        {");
        sb.AppendLine("            if (b is (>= 0xE0 and <= 0xEF) or 0xFE) r[i++] = 0xE0;");
        sb.AppendLine("            r[i++] = b;");
        sb.AppendLine("        }");
        sb.AppendLine("        return r;");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    /// <summary>字节拼接（多段值组成一个 Edit 替换块）。</summary>");
        sb.AppendLine("    internal static byte[] Cat(params byte[][] parts)");
        sb.AppendLine("    {");
        sb.AppendLine("        var r = new byte[parts.Sum(p => p.Length)];");
        sb.AppendLine("        var i = 0;");
        sb.AppendLine("        foreach (var p in parts) { p.CopyTo(r, i); i += p.Length; }");
        sb.AppendLine("        return r;");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    /// <summary>价格槽等宽填充：TrimmedDouble 右补零到槽宽（解析侧右补零到 8B 还原同值——M3 实证变宽即失步）。</summary>");
        sb.AppendLine("    internal static byte[] PadTrimmedDouble(double v, int width)");
        sb.AppendLine("    {");
        sb.AppendLine("        var t = TrimmedDouble(v);");
        sb.AppendLine("        return t.Length >= width ? t[..width] : Cat(t, new byte[width - t.Length]);");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    /// <summary>大端 double 剥尾零（8000.0 → 40BF40 三字节——⑲/推送价格实证；整槽替换宽度随值）。</summary>");
        sb.AppendLine("    internal static byte[] TrimmedDouble(double v)");
        sb.AppendLine("    {");
        sb.AppendLine("        var b = Money(v);");
        sb.AppendLine("        var n = b.Length;");
        sb.AppendLine("        while (n > 1 && b[n - 1] == 0) n--;");
        sb.AppendLine("        return b[..n];");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    /// <summary>大端 int 剥前导零（2 → 02、148 → 94、量 1 → 01——⑲/㉑ 实证；宽度随值）。</summary>");
        sb.AppendLine("    internal static byte[] MinimalInt(long v)");
        sb.AppendLine("    {");
        sb.AppendLine("        if (v == 0) return [0x00];");
        sb.AppendLine("        var b = new byte[8];");
        sb.AppendLine("        BinaryPrimitives.WriteInt64BigEndian(b, v);");
        sb.AppendLine("        var n = 0;");
        sb.AppendLine("        while (n < 7 && b[n] == 0) n++;");
        sb.AppendLine("        return b[n..];");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    /// <summary>GBK 文本 + E0 转义（⑳ 实证：值内 {E0..EF,FE} 字节前插 E0，E0 自身亦转义——与 EscapedMoney 同一字母表）。</summary>");
        sb.AppendLine("    internal static byte[] EscapedGbk(string text)");
        sb.AppendLine("    {");
        sb.AppendLine("        var raw = Encoding.GetEncoding(\"GBK\").GetBytes(text);");
        sb.AppendLine("        var n = 0;");
        sb.AppendLine("        foreach (var b in raw)");
        sb.AppendLine("            if (b is (>= 0xE0 and <= 0xEF) or 0xFE) n++;");
        sb.AppendLine("        var r = new byte[raw.Length + n];");
        sb.AppendLine("        var i = 0;");
        sb.AppendLine("        foreach (var b in raw)");
        sb.AppendLine("        {");
        sb.AppendLine("            if (b is (>= 0xE0 and <= 0xEF) or 0xFE) r[i++] = 0xE0;");
        sb.AppendLine("            r[i++] = b;");
        sb.AppendLine("        }");
        sb.AppendLine("        return r;");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    internal readonly record struct Edit(int Offset, int OldLen, byte[] NewBytes);");
        sb.AppendLine();
        sb.AppendLine("    /// <summary>");
        sb.AppendLine("    /// 标记界定替换（值两侧有标记 ⇒ 变长替换安全）。偏移倒序应用（高偏移先改，");
        sb.AppendLine("    /// 低偏移不受影响）——生成期已断言编辑区互不重叠，运行期无需复查。");
        sb.AppendLine("    /// </summary>");
        sb.AppendLine("    internal static byte[] Apply(byte[] template, params Edit[] edits)");
        sb.AppendLine("    {");
        sb.AppendLine("        var result = template;");
        sb.AppendLine("        foreach (var e in edits.OrderByDescending(e => e.Offset).ToArray())");
        sb.AppendLine("        {");
        sb.AppendLine("            var next = new byte[result.Length - e.OldLen + e.NewBytes.Length];");
        sb.AppendLine("            result.AsSpan(0, e.Offset).CopyTo(next);");
        sb.AppendLine("            e.NewBytes.CopyTo(next, e.Offset);");
        sb.AppendLine("            result.AsSpan(e.Offset + e.OldLen).CopyTo(next.AsSpan(e.Offset + e.NewBytes.Length));");
        sb.AppendLine("            result = next;");
        sb.AppendLine("        }");
        sb.AppendLine("        return result;");
        sb.AppendLine("    }");
    }

    private static void EmitTemplate(StringBuilder sb, string comment, string name, byte[] payload)
    {
        sb.AppendLine();
        sb.AppendLine($"    // ===== {comment} =====");
        sb.AppendLine($"    internal static readonly byte[] {name} = H(");
        EmitHexChunks(sb, payload);
        sb.AppendLine("    );");
    }

    private static void EmitHexChunks(StringBuilder sb, byte[] payload)
    {
        // 单行字面量：C# 相邻字符串字面量【不】隐式拼接（那是 C/C++/Python 的特性）——
        // 相邻字面量在参数表里被解析为多个实参 → 每个非末段块恰好一个 CS1003「应输入 ,」。
        sb.AppendLine($"        \"{Convert.ToHexString(payload)}\"");
    }

    private static void EmitBuilder(StringBuilder sb, string doc, string signature, string templateField,
        IReadOnlyList<(int Off, int Len, string Expr)> edits)
    {
        sb.AppendLine();
        sb.AppendLine($"    /// <summary>{doc}</summary>");
        sb.AppendLine($"    internal static byte[] {signature} =>");
        sb.AppendLine($"        Apply({templateField},");
        // 逗号只在编辑之间——参数列表尾逗号是 CS1525（数组/集合初始化器才有尾逗号）
        for (var i = 0; i < edits.Count; i++)
        {
            var (off, len, expr) = edits[i];
            sb.Append($"            new Edit({off}, {len}, {expr})");
            if (i + 1 < edits.Count) sb.Append(',');
            sb.AppendLine();
        }
        sb.AppendLine("        );");
    }
}
