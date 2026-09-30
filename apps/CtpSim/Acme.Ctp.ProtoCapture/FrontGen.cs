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

    public static void Run(string? capturePath, string? outPath)
    {
        try
        {
            Generate(capturePath, outPath);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Gen] ✗ 生成失败: {ex.Message}");
            Environment.ExitCode = 1;
        }
    }

    private static void Generate(string? capturePath, string? outPath)
    {
        capturePath ??= LatestCapture();
        outPath ??= FindRepoRoot() is { } root
            ? Path.Combine(root, "business", "Ctp", "Acme.Ctp.Front", "CtpFrontTemplates.cs")
            : throw Fail("未找到仓库根（向上未遇 Aether.AI.Platform.slnx）——显式传输出路径");

        Console.WriteLine($"[Gen] 抓包: {capturePath}");
        var frames = ParseCapture(capturePath);
        Console.WriteLine($"[Gen] 解析: {frames.Count} 帧");

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
        var dirName = Path.GetFileName(Path.GetDirectoryName(Path.GetFullPath(capturePath)));
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
            LocVal(p6, Hx("FE"), Asc(broker), "A(brokerId)", "⑥ broker"),
            LocVal(p6, Hx("E7"), Asc(user), "A(userId)", "⑥ user"),
            LocVal(p6, Hx("E5"), Asc(appId), "A(appId)", "⑥ AppID"),
        };
        CheckDisjoint("⑥", e6);

        var p8 = Payload(frames, 8);
        var e8 = new (int Off, int Len, string Expr)[]
        {
            LocVal(p8, Hx("FE"), Asc(broker), "A(brokerId)", "⑧ broker"),
            LocVal(p8, Hx("E7"), Asc(user), "A(userId)", "⑧ user"),
        };
        CheckDisjoint("⑧", e8);

        var e10 = new (int Off, int Len, string Expr)[]
        {
            LocFull(p10a, Hx("E503018DE3"), 1, "[loginReqId]", "⑩ loginReqId"),
            LocVal(p10a, Hx("E1"), Asc(broker), "A(brokerId)", "⑩ broker"),
            LocVal(p10a, Hx("E7"), Asc(user), "A(userId)", "⑩ user"),
            LocVal(p10a, Hx("1010030124"), Asc(day), "A(tradingDay)", "⑩ 交易日#1"),
            LocVal(p10a, Hx("E401E301"), Asc(day), "A(tradingDay)", "⑩ 交易日#2"),
        };
        CheckDisjoint("⑩", e10);

        var p12 = Payload(frames, 12);
        var e12 = new (int Off, int Len, string Expr)[]
        {
            LocFull(p12, Hx("E101E24014E3"), 1, "[instance]", "⑫ instance"),
            LocFull(p12, Hx("E19CE3"), 1, "[reqId]", "⑫ reqId"),
            LocVal(p12, Hx("E13F"), Asc(broker), "A(brokerId)", "⑫ broker"),
            LocVal(p12, Hx("E7"), Asc(user), "A(userId)", "⑫ user"),
            LocVal(p12, Hx("E7"), Asc(day), "A(tradingDay)", "⑫ 交易日"),
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
            LocVal(p16, Hx("E10C018A"), Asc(broker), "A(brokerId)", "⑯ broker"),
            // 账户#1（抓包值 = 用户名去尾 5 字符，语义未知；oracle 实证 AccountID=槽#2 非此处）：
            // 等宽守卫——恰好 5 字符进出，任意宽 accountId 不撑大 payload
            //（+1 增长曾把 E1 推到槽#2 值位 ⇒ SDK 读出空 AccountID）。
            LocValSur(p16, Hx("E1"), Asc(acctShort), Hx("E1"),
                $"A(accountId.Length >= {acctShort.Length} ? accountId[..{acctShort.Length}] : accountId.PadLeft({acctShort.Length}, '0'))", "⑯ 账户#1"),
            LocValSur(p16, Hx("E1"), Asc(user), Hx("E1"), "A(accountId)", "⑯ 账户#2"),
            LocVal(p16, Hx("E8"), Asc(day), "A(tradingDay)", "⑯ 交易日"),
        };
        e16.AddRange(balOffsets.Select(o => (o, balWireLen, "EscapedMoney(balance)")));
        CheckDisjoint("⑯", e16.ToArray());

        var p18 = Payload(frames, 18);
        if (p18.Length != 13) throw Fail($"⑱ 期望 13B，实际 {p18.Length}B");
        var e18 = new (int Off, int Len, string Expr)[] { (p18.Length - 1, 1, "[reqId]") };

        // ── 生成输出 ──
        var sb = new StringBuilder();
        EmitHeader(sb, capturePath, frames.Count);
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
            "BuildSysInfoRsp(string brokerId, string userId, byte loginReqId, string tradingDay)", "SysInfoRspTemplate", e10);

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
    }

    private static void EmitHelpers(StringBuilder sb)
    {
        sb.AppendLine("    internal static byte[] H(string hex) => Convert.FromHexString(hex);");
        sb.AppendLine();
        sb.AppendLine("    /// <summary>ASCII 字符串值 → 字节（协议字段值为 ASCII）。</summary>");
        sb.AppendLine("    internal static byte[] A(string s) => Encoding.ASCII.GetBytes(s);");
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
