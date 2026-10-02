// ─────────────────────────────────────────────────────────────────────────────
// CtpFrontTemplates.cs —— 由抓包自动生成，勿手改。
// 生成器: dotnet run --project apps/CtpSim/Acme.Ctp.ProtoCapture -- gen <trade.txt> <本文件>
//   （FrontGen.cs：抓包 S→C 应答帧 verbatim 复刻 + 标记界定字段替换；全部偏移由
//     生成器计算，needle 唯一命中/编辑区无重叠/余额像钱均生成期断言——零人工转录。）
// 抓包源: /var/folders/wf/6fth7gs95xzfhk1n59kpzhvc0000gn/T/ctp-proto-capture/20260929-031656/trade.txt（22 帧）
// 帧字节为抓包原样（含 SimNow 认证 token/密文/账号）——仅本地仿真用，勿入公网/勿分发。
// ─────────────────────────────────────────────────────────────────────────────

using System.Buffers.Binary;
using System.Linq;
using System.Text;

namespace Acme.Ctp.Front;

/// <summary>CTP 前置应答帧模板（抓包 verbatim + 偏移替换）——由 ProtoCapture gen 生成。</summary>
internal static class CtpFrontTemplates
{
    // GBK 错误消息（EscapedGbk）需要 CodePages provider——.NET 默认只带 UTF/ASCII，macOS/Linux 上必须注册
    static CtpFrontTemplates() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    internal static byte[] H(string hex) => Convert.FromHexString(hex);

    /// <summary>ASCII 字符串值 → 字节（协议字段值为 ASCII）。</summary>
    internal static byte[] A(string s) => Encoding.ASCII.GetBytes(s);

    /// <summary>等宽归一化：值截断/右补空格到抓包槽宽再转 ASCII。verbatim 模板对字段宽度敏感——
    /// 变长替换缩/撑 payload 曾致 SDK 记录遍历失步挂死（>6 字符用户/空 AppId 均实测）；
    /// 任意宽度 broker/user/AppId/账户都归一到抓包形状。回显字段仅语义展示，账户身份以核心账本为准。</summary>
    internal static byte[] F(string s, int width)
    {
        var v = s.Length > width ? s[..width] : s.PadRight(width, ' ');
        return Encoding.ASCII.GetBytes(v);
    }

    /// <summary>资金字段值：大端 double（8B）。</summary>
    internal static byte[] Money(double v)
    {
        var b = new byte[8];
        BinaryPrimitives.WriteDoubleBigEndian(b, v);
        return b;
    }

    /// <summary>资金字段值：大端 double + E0 转义（抓包实证 999927.69 → 逻辑 41 2E 83 EF 61 47 AE 14，
    /// 线上 41 2E 83 E0 EF 61 47 AE 14——tag 字母表 {E0..EF, FE} 字节前插 E0；E0 自身亦须转义。
    /// 转义范围 E0..EF+FE 为实证+推断（EF 由抓包证明，E0/FE 由「读取方按 tag 分界」必然性推出；
    /// F0..FD/FF 未观察到、推定无需转义）。值的线上宽度可变（8B 起，每个转义 +1）。</summary>
    internal static byte[] EscapedMoney(double v)
    {
        var raw = Money(v);
        var n = 0;
        foreach (var b in raw)
            if (b is (>= 0xE0 and <= 0xEF) or 0xFE) n++;
        var r = new byte[raw.Length + n];
        var i = 0;
        foreach (var b in raw)
        {
            if (b is (>= 0xE0 and <= 0xEF) or 0xFE) r[i++] = 0xE0;
            r[i++] = b;
        }
        return r;
    }

    /// <summary>字节拼接（多段值组成一个 Edit 替换块）。</summary>
    internal static byte[] Cat(params byte[][] parts)
    {
        var r = new byte[parts.Sum(p => p.Length)];
        var i = 0;
        foreach (var p in parts) { p.CopyTo(r, i); i += p.Length; }
        return r;
    }

    /// <summary>价格槽等宽填充：TrimmedDouble 右补零到槽宽（解析侧右补零到 8B 还原同值——M3 实证变宽即失步）。</summary>
    internal static byte[] PadTrimmedDouble(double v, int width)
    {
        var t = TrimmedDouble(v);
        return t.Length >= width ? t[..width] : Cat(t, new byte[width - t.Length]);
    }

    /// <summary>大端 double 剥尾零（8000.0 → 40BF40 三字节——⑲/推送价格实证；整槽替换宽度随值）。</summary>
    internal static byte[] TrimmedDouble(double v)
    {
        var b = Money(v);
        var n = b.Length;
        while (n > 1 && b[n - 1] == 0) n--;
        return b[..n];
    }

    /// <summary>大端 int 剥前导零（2 → 02、148 → 94、量 1 → 01——⑲/㉑ 实证；宽度随值）。</summary>
    internal static byte[] MinimalInt(long v)
    {
        if (v == 0) return [0x00];
        var b = new byte[8];
        BinaryPrimitives.WriteInt64BigEndian(b, v);
        var n = 0;
        while (n < 7 && b[n] == 0) n++;
        return b[n..];
    }

    /// <summary>GBK 文本 + E0 转义（⑳ 实证：值内 {E0..EF,FE} 字节前插 E0，E0 自身亦转义——与 EscapedMoney 同一字母表）。</summary>
    internal static byte[] EscapedGbk(string text)
    {
        var raw = Encoding.GetEncoding("GBK").GetBytes(text);
        var n = 0;
        foreach (var b in raw)
            if (b is (>= 0xE0 and <= 0xEF) or 0xFE) n++;
        var r = new byte[raw.Length + n];
        var i = 0;
        foreach (var b in raw)
        {
            if (b is (>= 0xE0 and <= 0xEF) or 0xFE) r[i++] = 0xE0;
            r[i++] = b;
        }
        return r;
    }

    internal readonly record struct Edit(int Offset, int OldLen, byte[] NewBytes);

    /// <summary>
    /// 标记界定替换（值两侧有标记 ⇒ 变长替换安全）。偏移倒序应用（高偏移先改，
    /// 低偏移不受影响）——生成期已断言编辑区互不重叠，运行期无需复查。
    /// </summary>
    internal static byte[] Apply(byte[] template, params Edit[] edits)
    {
        var result = template;
        foreach (var e in edits.OrderByDescending(e => e.Offset).ToArray())
        {
            var next = new byte[result.Length - e.OldLen + e.NewBytes.Length];
            result.AsSpan(0, e.Offset).CopyTo(next);
            e.NewBytes.CopyTo(next, e.Offset);
            result.AsSpan(e.Offset + e.OldLen).CopyTo(next.AsSpan(e.Offset + e.NewBytes.Length));
            result = next;
        }
        return result;
    }

    // ===== 帧 ④ 连接确认（19B payload——verbatim 无替换） =====
    internal static readonly byte[] ConfirmPayload = H(
        "0103344CE43028E501E159E755EFEFEFEFEFEA"
    );

    // ===== 帧 ⑥ 认证响应（198B payload：reqId/broker/user/AppId 替换） =====
    internal static readonly byte[] AuthRspTemplate = H(
        "0103344CE43011E502015BE3011012E1FE39393939E7323437313633EFE67053327A777475667372787369526F426B6F43576D50517244436F666C56594B4D596867724C765261336873534632633143366F72584E357A62695334654451635336434341724D424A64746D64466D504D4B66487869654741364B4E487A5841447A616431554F493766326B566F3846366E6B42467966644530306B785753E573696D6E6F775F636C69656E745F74657374EF34EFEFEFE755E44354503AD5FDC8B7EFEFEFEFED"
    );

    /// <summary>⑥ 认证响应（Base64 token 抓包原样——固定挑战下 token 无会话效性，可回放）。</summary>
    internal static byte[] BuildAuthRsp(string brokerId, string userId, string appId, byte reqId) =>
        Apply(AuthRspTemplate,
            new Edit(12, 1, [reqId]),
            new Edit(17, 4, F(brokerId, 4)),
            new Edit(22, 6, F(userId, 6)),
            new Edit(159, 18, F(appId, 18))
        );

    // ===== 帧 ⑧ 登录预响应（77B payload：broker/user 替换） =====
    internal static readonly byte[] LoginRspTemplate = H(
        "0103344CE43011E502015BE301E355E4D5FDC8B7EFEFEFEFEFE21012E1FE39393939E7323437313633EFEFEFEFEFEFEFEFEFEFE30173696D6E6F775F636C69656E745F74657374EF31EFEFEFE4"
    );

    /// <summary>⑧ 登录预响应（SDK 收 ⑧ 后自动发 ⑨ 系统信息请求）。</summary>
    internal static byte[] BuildLoginRsp(string brokerId, string userId) =>
        Apply(LoginRspTemplate,
            new Edit(30, 4, F(brokerId, 4)),
            new Edit(35, 6, F(userId, 6))
        );

    // ===== 帧 ⑩ 登录结果主帧（206B payload：loginReqId/broker/user/交易日×2 替换） =====
    internal static readonly byte[] SysInfoRspTemplate = H(
        "0103344CE43001E503018DE302E355E4D5FDC8B7EFEFEFEFEFE24036E1087FFFFFFFE22710100301243230323630393239E131313A31363A3536E139393939E7323437313633EA54726164696E67486F7374696E67EFEF012DAF7B0231EC31313A31363A3535E131313A31363A3535E131313A31363A3535E131313A31323A3034E131313A31363A3535E176362E372E31335F58435F32303236303431332E31323336352E746B65726E656CE831313A31363A3535E401E301323032363039323931313A31333A3232EFEFEFEFE6"
    );

    /// <summary>⑩ 登录结果主帧（交易日/FrontID/SessionID/MaxOrderRef——⑩ 到达即完成 OnRspUserLogin）。</summary>
    internal static byte[] BuildSysInfoRsp(string brokerId, string userId, byte loginReqId, string tradingDay, string maxOrderRef) =>
        Apply(SysInfoRspTemplate,
            new Edit(12, 1, [loginReqId]),
            new Edit(59, 4, F(brokerId, 4)),
            new Edit(64, 6, F(userId, 6)),
            new Edit(41, 8, F(tradingDay, 8)),
            new Edit(185, 8, F(tradingDay, 8)),
            new Edit(92, 1, A(maxOrderRef))
        );

    /// <summary>⑩ 伴随帧（42B payload + 4B 头 = 46B 整帧——与主帧同一次 TCP 写出）。</summary>
    internal static readonly byte[] SysInfoCompanionFrame = H(
        "0200002A0103344CE4F102E504E128E41001E106E101E41001E106E102E41001E106E103E203A41001E106E104E4"
    );

    // ===== 帧 ⑫ 结算确认应答（65B payload：instance/reqId/broker/user/交易日 替换；⑭ 复用同模板 instance=02） =====
    internal static readonly byte[] SettleRspTemplate = H(
        "0103344CE101E24014E301E102E19CE303E355E4D5FDC8B7EFEFEFEFEFE2040FE13F39393939E7323437313633E73230323630393239E131313A31363A3536EFE7"
    );

    /// <summary>⑫⑭ 结算确认应答（instance 自增：首次 01、二次 02）。</summary>
    internal static byte[] BuildSettleRsp(byte instance, byte reqId, string brokerId, string userId, string tradingDay) =>
        Apply(SettleRspTemplate,
            new Edit(10, 1, [instance]),
            new Edit(16, 1, [reqId]),
            new Edit(34, 4, F(brokerId, 4)),
            new Edit(39, 6, F(userId, 6)),
            new Edit(46, 8, F(tradingDay, 8))
        );

    // ===== 帧 ⑯ 资金应答（134B payload：reqId/broker/账户×2/交易日 + 6 个 E0 转义大端余额字段） =====
    internal static readonly byte[] QryAccountRspTemplate = H(
        "0103344CE104E28007E301E101018EE305E10C018A39393939E13234373136E1323437313633E130E130E139EFE2412E83E0EF6147AE14412E83E0EF6147AE14EFEFEFEFEFEFEE412E83E0EF6147AE14412E83E0EF6147AE14412E83E0EF6147AE14E83230323630393239E401EFEFEFE3434E59EFEFEB412E83E0EF6147AE14EFEFEFEFEFEE"
    );

    /// <summary>⑯ 资金应答（6 个 E0 转义余额字段全部 ← Balance——整槽替换，线上宽度随值内 tag 字节数可变）。</summary>
    internal static byte[] BuildQryAccountRsp(byte reqId, string brokerId, string accountId, string userId, string tradingDay, double balance) =>
        Apply(QryAccountRspTemplate,
            new Edit(16, 1, [reqId]),
            new Edit(21, 4, F(brokerId, 4)),
            new Edit(26, 5, A(accountId.Length >= 5 ? accountId[..5] : accountId.PadLeft(5, '0'))),
            new Edit(32, 6, F(accountId, 6)),
            new Edit(99, 8, F(tradingDay, 8)),
            new Edit(46, 9, EscapedMoney(balance)),
            new Edit(55, 9, EscapedMoney(balance)),
            new Edit(71, 9, EscapedMoney(balance)),
            new Edit(80, 9, EscapedMoney(balance)),
            new Edit(89, 9, EscapedMoney(balance)),
            new Edit(119, 9, EscapedMoney(balance))
        );

    // ===== 帧 ⑱ 持仓应答·空表（13B payload：仅尾字节 reqId 替换） =====
    internal static readonly byte[] QryPositionEmptyRspTemplate = H(
        "0103344CE104E28005E302E706"
    );

    /// <summary>⑱ 持仓应答（空表——M1 恒空；非空持仓需 M2 结构）。</summary>
    internal static byte[] BuildQryPositionEmptyRsp(byte reqId) =>
        Apply(QryPositionEmptyRspTemplate,
            new Edit(12, 1, [reqId])
        );

    /// <summary>M1 ⑩ verbatim 会话身份（FrontID+SessionID，5B）——推送/回显帧 Edit 用（D6：与 ⑩ 同源）。</summary>
    internal static readonly byte[] SessionFrontIdSessionId = H("012DAF7B02");

    // ===== M2 帧 OnRtnOrder '3'（385B payload：未成交在队推送——F001） =====
    internal static readonly byte[] RtnOrder3Template = H(
        "0103344CE102E2F001E306E1010353E40401034F39393939E7323437313633E76D32373031EFEB303030303030303030303032E1323437313633EA323030E431E440A9C8E80133E931E30131E830EFEE202020202020202034363536E1444345E16D323730E139393939E131E3C9E139393939323437313431E16D32373031EFEB39393939636166E139393939E131E131E1393939E40133E3013230323630393239E401202020202020202039353037EA33E8013230323630393239E131303A35303A3437EFEFE739393939636166EFEC1341E301C8E0E69C40EBCEB4B3C9BDBBEFEFEFEFEFE510E710E86558E17FE3FDE66F70369F8C7FECFFFFFFE39CEFEFE53138302E3135382E3233322E313837EFE76D32373031EFEFEFEFEFE16D32373031E16DE131E132303237E131E131303030E131E131303030E131E13130E131E13230323630313139E13230323630313139E13230323730313135E13230323730313135E13230323730313230E13138302E3135382E3233322E313837EFEFE401"
    );

    /// <summary>OnRtnOrder '3'（未成交在队——受理/挂单状态推送）。</summary>
    internal static byte[] BuildRtnOrder3(byte instance, string userId, string brokerId, string instrumentId, string orderRef, char priceType, char direction, char offsetFlag, double limitPrice, int volume, char timeCondition, string tradingDay) =>
        Apply(RtnOrder3Template,
            new Edit(10, 1, [instance]),
            new Edit(39, 12, A(orderRef)),
            new Edit(59, 3, A($"{priceType}{direction}{offsetFlag}")),
            new Edit(65, 3, TrimmedDouble(limitPrice)),
            new Edit(69, 2, Cat(MinimalInt(volume), A(timeCondition.ToString()))),
            new Edit(25, 6, F(userId, 6)),
            new Edit(52, 6, F(userId, 6)),
            new Edit(20, 4, F(brokerId, 4)),
            new Edit(102, 4, F(brokerId, 4)),
            new Edit(111, 4, F(brokerId, 4)),
            new Edit(129, 4, F(brokerId, 4)),
            new Edit(137, 4, F(brokerId, 4)),
            new Edit(200, 4, F(brokerId, 4)),
            new Edit(32, 5, F(instrumentId, 5)),
            new Edit(97, 4, F(instrumentId, 4)),
            new Edit(122, 5, F(instrumentId, 5)),
            new Edit(274, 5, F(instrumentId, 5)),
            new Edit(285, 5, F(instrumentId, 5)),
            new Edit(154, 8, A(tradingDay)),
            new Edit(180, 8, A(tradingDay))
        );

    // ===== M2 帧 OnRtnOrder '0'（399B payload：全部成交推送——F001；VolumeTraded 保留捕获值 1，>1 手的全成回报成交量失真（边界）） =====
    internal static readonly byte[] RtnOrder0Template = H(
        "0103344CE102E2F001E309E1010353E40401034F39393939E7323437313633E76D32373031EFEB303030303030303030303033E1323437313633EA323030E431E440AA90E80133E931E30131E830EFEE202020202020202034363537E1444345E16D323730E139393939E131E3C9E139393939323437313431E16D32373031EFEB39393939636166E139393939E131E131E1393939E40130E43230323630393239E401202020202020202039353038E9303030E301E43230323630393239E131303A35303A3530EFEFE739393939636166EFEC1342E301C8E0E69C40EBC8ABB2BFB3C9BDBBB1A8B5A5D2D1CCE0E1BDBBEFEFEFEFE810E710E8655AE17FE3FDE66F70369F8C7FECFFFFFFE39CEFEFE53138302E3135382E3233322E313837EFE76D32373031EFEFEFEFEFE16D32373031E16DE131E132303237E131E131303030E131E131303030E131E13130E131E13230323630313139E13230323630313139E13230323730313135E13230323730313135E13230323730313230E13138302E3135382E3233322E313837EFEFE402"
    );

    /// <summary>OnRtnOrder '0'（全部成交推送）。</summary>
    internal static byte[] BuildRtnOrder0(byte instance, string userId, string brokerId, string instrumentId, string orderRef, char priceType, char direction, char offsetFlag, double limitPrice, int volume, char timeCondition, string tradingDay) =>
        Apply(RtnOrder0Template,
            new Edit(10, 1, [instance]),
            new Edit(39, 12, A(orderRef)),
            new Edit(59, 3, A($"{priceType}{direction}{offsetFlag}")),
            new Edit(65, 3, TrimmedDouble(limitPrice)),
            new Edit(69, 2, Cat(MinimalInt(volume), A(timeCondition.ToString()))),
            new Edit(25, 6, F(userId, 6)),
            new Edit(52, 6, F(userId, 6)),
            new Edit(20, 4, F(brokerId, 4)),
            new Edit(102, 4, F(brokerId, 4)),
            new Edit(111, 4, F(brokerId, 4)),
            new Edit(129, 4, F(brokerId, 4)),
            new Edit(137, 4, F(brokerId, 4)),
            new Edit(202, 4, F(brokerId, 4)),
            new Edit(32, 5, F(instrumentId, 5)),
            new Edit(97, 4, F(instrumentId, 4)),
            new Edit(122, 5, F(instrumentId, 5)),
            new Edit(288, 5, F(instrumentId, 5)),
            new Edit(299, 5, F(instrumentId, 5)),
            new Edit(153, 8, A(tradingDay)),
            new Edit(182, 8, A(tradingDay))
        );

    /// <summary>OnRtnOrder '5'（已撤销——'3' 模板合成：状态字节/状态消息替换）。</summary>
    internal static byte[] BuildRtnOrder5(byte instance, string userId, string brokerId, string instrumentId, string orderRef, char priceType, char direction, char offsetFlag, double limitPrice, int volume, char timeCondition, string tradingDay) =>
        Apply(RtnOrder3Template,
            new Edit(10, 1, [instance]),
            new Edit(39, 12, A(orderRef)),
            new Edit(59, 3, A($"{priceType}{direction}{offsetFlag}")),
            new Edit(65, 3, TrimmedDouble(limitPrice)),
            new Edit(69, 2, Cat(MinimalInt(volume), A(timeCondition.ToString()))),
            new Edit(25, 6, F(userId, 6)),
            new Edit(52, 6, F(userId, 6)),
            new Edit(20, 4, F(brokerId, 4)),
            new Edit(102, 4, F(brokerId, 4)),
            new Edit(111, 4, F(brokerId, 4)),
            new Edit(129, 4, F(brokerId, 4)),
            new Edit(137, 4, F(brokerId, 4)),
            new Edit(200, 4, F(brokerId, 4)),
            new Edit(32, 5, F(instrumentId, 5)),
            new Edit(97, 4, F(instrumentId, 4)),
            new Edit(122, 5, F(instrumentId, 5)),
            new Edit(274, 5, F(instrumentId, 5)),
            new Edit(285, 5, F(instrumentId, 5)),
            new Edit(154, 8, A(tradingDay)),
            new Edit(180, 8, A(tradingDay)),
            new Edit(177, 1, [(byte)'5']),
            new Edit(219, 6, EscapedGbk("已撤销"))
        );

    // ===== M2 帧 OnRtnTrade（283B payload：成交回报推送——F002） =====
    internal static readonly byte[] RtnTradeTemplate = H(
        "0103344CE102E2F002E30AE10101E0EFE4040901E0EB39393939E1444345E139E1323437313633E139393939E26D32373031EFEB303030303030303030303033E1323437313633EA444345E635333936EFE230202020202020202039353038E939393939E739393939323437313431E26D32373031EFEB303140AA3AE8013230323630393239E131303A35303A3530E130E139393939636166EE202020202020202034363537E139393939EFEF13433230323630393239E401E2655A30EFE26D32373031E1444345E16D32373031E16D32373031E16DE131E132303237E131E131303030E131E131303030E131E13130E131E13230323630313139E13230323630313139E13230323730313135E132E16D32373031EFEFEFEFEFE1"
    );

    /// <summary>OnRtnTrade（成交回报推送——方向/开平前缀保留捕获形状（边界：非买开成交的方向回显失真））。</summary>
    internal static byte[] BuildRtnTrade(byte instance, string userId, string brokerId, string instrumentId, string orderRef, string tradeId, double price, int volume, string tradingDay) =>
        Apply(RtnTradeTemplate,
            new Edit(10, 1, [instance]),
            new Edit(52, 12, A(orderRef)),
            new Edit(76, 4, F(tradeId, 4)),
            new Edit(121, 3, TrimmedDouble(price)),
            new Edit(184, 1, MinimalInt(volume)),
            new Edit(33, 6, F(userId, 6)),
            new Edit(65, 6, F(userId, 6)),
            new Edit(22, 4, F(brokerId, 4)),
            new Edit(40, 4, F(brokerId, 4)),
            new Edit(96, 4, F(brokerId, 4)),
            new Edit(101, 4, F(brokerId, 4)),
            new Edit(146, 4, F(brokerId, 4)),
            new Edit(167, 4, F(brokerId, 4)),
            new Edit(45, 5, F(instrumentId, 5)),
            new Edit(112, 5, F(instrumentId, 5)),
            new Edit(191, 5, F(instrumentId, 5)),
            new Edit(201, 5, F(instrumentId, 5)),
            new Edit(207, 5, F(instrumentId, 5)),
            new Edit(272, 5, F(instrumentId, 5)),
            new Edit(126, 8, A(tradingDay)),
            new Edit(175, 8, A(tradingDay))
        );

    // ===== M2 帧 ⑳-4001（164B payload：OnRspOrderInsert 拒绝——C# 层不可见但真实，复刻） =====
    internal static readonly byte[] OrderInsertRspErrTemplate = H(
        "0103344CE101E24001E303E10201E0E5E307E355E3944354503ACEDED0A7B5C445786368616E67654944D7D6B6CEA3ACC7E0EBCCE0EEC8E0EBD5FDC8B7B5C445786368616E67654944EFEFE304E1018839393939E7323437313633E7616732353132EFEA303030303030303030303032E1323437313633EA323030E431E440BEDCE80133E931E30131E830EFEFEFEFEFEFEFEFE8616732353132EFEFEFEFEFEFEFEFE401"
    );

    /// <summary>⑳-4001 拒绝（payload；会话层与 F003 各自 BuildFrame 后拼接一次写）。</summary>
    internal static byte[] BuildOrderInsertRspErr(byte instance, byte reqId, int errorId, string errorMsg, string userId, string brokerId, string instrumentId, string orderRef, char priceType, char direction, char offsetFlag, double limitPrice, int volume, char timeCondition) =>
        Apply(OrderInsertRspErrTemplate,
            new Edit(10, 1, [instance]),
            new Edit(100, 12, A(orderRef)),
            new Edit(120, 3, A($"{priceType}{direction}{offsetFlag}")),
            new Edit(126, 3, TrimmedDouble(limitPrice)),
            new Edit(130, 2, Cat(MinimalInt(volume), A(timeCondition.ToString()))),
            new Edit(85, 6, F(userId, 6)),
            new Edit(113, 6, F(userId, 6)),
            new Edit(80, 4, F(brokerId, 4)),
            new Edit(92, 6, F(instrumentId, 6)),
            new Edit(148, 6, F(instrumentId, 6)),
            new Edit(17, 1, [reqId]),
            new Edit(20, 53, Cat(H("E3"), MinimalInt(errorId), EscapedGbk(errorMsg)))
        );

    // ===== M2 帧 ⑳-F003（163B payload：OnErrRtnOrderInsert——拒单在 C# 层唯一可见信号 → RspError） =====
    internal static readonly byte[] ErrRtnOrderInsertTemplate = H(
        "0103344CE102E2F003E301E10201E0E5E404E1018839393939E7323437313633E7616732353132EFEA303030303030303030303032E1323437313633EA323030E431E440BEDCE80133E931E30131E830EFEFEFEFEFEFEFEFE8616732353132EFEFEFEFEFEFEFEFE401E355E3944354503ACEDED0A7B5C445786368616E67654944D7D6B6CEA3ACC7E0EBCCE0EEC8E0EBD5FDC8B7B5C445786368616E67654944EFEFE3"
    );

    /// <summary>⑳-F003 报单错误回报（拒单 → RspError 事件）。</summary>
    internal static byte[] BuildErrRtnOrderInsert(byte instance, int errorId, string errorMsg, string userId, string brokerId, string instrumentId, string orderRef, char priceType, char direction, char offsetFlag, double limitPrice, int volume, char timeCondition) =>
        Apply(ErrRtnOrderInsertTemplate,
            new Edit(10, 1, [instance]),
            new Edit(41, 12, A(orderRef)),
            new Edit(61, 3, A($"{priceType}{direction}{offsetFlag}")),
            new Edit(67, 3, TrimmedDouble(limitPrice)),
            new Edit(71, 2, Cat(MinimalInt(volume), A(timeCondition.ToString()))),
            new Edit(26, 6, F(userId, 6)),
            new Edit(54, 6, F(userId, 6)),
            new Edit(21, 4, F(brokerId, 4)),
            new Edit(33, 6, F(instrumentId, 6)),
            new Edit(89, 6, F(instrumentId, 6)),
            new Edit(107, 53, Cat(H("E3"), MinimalInt(errorId), EscapedGbk(errorMsg)))
        );

    // ===== M2 帧 ㉒-4007（112B payload：OnRspOrderAction——OrderActionAsync 阻塞等它） =====
    internal static readonly byte[] OrderActionRspTemplate = H(
        "0103344CE101E24007E303E10201A5E30AE355E3194354503AB3B7B5A5D5D2B2BBB5BDCFE0E0D3A6B1A8B5A5EFEFEFEE0404014839393939E7323437313633EB303030303030303030303034E801C8E0E69C40444345EFEC30EC323437313633EFEFEFEFEFEFEFEFEFEFEFEFEFEFEF04"
    );

    /// <summary>㉒-4007 撤单应答·失败（RspInfo ← 核心错误码/消息）。</summary>
    internal static byte[] BuildOrderActionRspErr(byte instance, byte reqId, int errorId, string errorMsg, string userId, string brokerId, string orderRef) =>
        Apply(OrderActionRspTemplate,
            new Edit(10, 1, [instance]),
            new Edit(64, 12, A(orderRef)),
            new Edit(57, 6, F(userId, 6)),
            new Edit(90, 6, F(userId, 6)),
            new Edit(52, 4, F(brokerId, 4)),
            new Edit(16, 1, [reqId]),
            new Edit(19, 25, Cat(H("E3"), MinimalInt(errorId), EscapedGbk(errorMsg)))
        );

    /// <summary>㉒-4007 撤单应答·成功（RspInfo → E4 +「正确」——成功撤单不发 F004）。</summary>
    internal static byte[] BuildOrderActionRspOk(byte instance, byte reqId, string userId, string brokerId, string orderRef) =>
        Apply(OrderActionRspTemplate,
            new Edit(10, 1, [instance]),
            new Edit(64, 12, A(orderRef)),
            new Edit(57, 6, F(userId, 6)),
            new Edit(90, 6, F(userId, 6)),
            new Edit(52, 4, F(brokerId, 4)),
            new Edit(16, 1, [reqId]),
            new Edit(19, 25, Cat(H("E4"), A("D5FDC8B7")))
        );

    // ===== M2 帧 ㉒-F004（125B payload：OnErrRtnOrderAction 撤单错误回报） =====
    internal static readonly byte[] ErrRtnOrderActionTemplate = H(
        "0103344CE102E2F004E30DE1020270E40405021339393939E7323437313633EB303030303030303030303034E801C8E0E69C40444345EFEC30EFEFEFEFEFEFEFEFE5323437313633EFEFEFEFEFEFEFEFEFEFEFEFEFEFEFEFEFEFEFEFEF04E355E3194354503AB3B7B5A5D5D2B2BBB5BDCFE0E0D3A6B1A8B5A5EFEFEFEE"
    );

    /// <summary>㉒-F004 撤单错误回报（payload；会话层与 4007 拼接一次写）。</summary>
    internal static byte[] BuildErrRtnOrderAction(byte instance, int errorId, string errorMsg, string userId, string brokerId, string orderRef) =>
        Apply(ErrRtnOrderActionTemplate,
            new Edit(10, 1, [instance]),
            new Edit(32, 12, A(orderRef)),
            new Edit(25, 6, F(userId, 6)),
            new Edit(66, 6, F(userId, 6)),
            new Edit(20, 4, F(brokerId, 4)),
            new Edit(96, 25, Cat(H("E3"), MinimalInt(errorId), EscapedGbk(errorMsg)))
        );

    // ===== M3 帧 MdSubscribeRsp（183B payload：订阅应答——捕获 verbatim，回显块边界见注释） =====
    internal static readonly byte[] MdSubscribeRspTemplate = H(
        "0103344CE44402E5050229E755E44354503A4E6F204572726F72EFEFEFEFE92441E17088B26E0BE170E270B16E0BE170E2B0B16E0BE170E201E66D32373031EFEFEFEFEFE12441E17088B26E0BE170E270B16E0BE170E2B0B16E0BE170E201E66932373031EFEFEFEFEFE12441E17088B26E0BE170E270B16E0BE170E2B0B16E0BE170E201E6746132373031EFEFEFEFEF2441E17088B26E0BE170E270B16E0BE170E2B0B16E0BE170E201E6494632363132EFEFEFEFEF"
    );

    // ===== M3 帧 QryInstrumentRsp（421B payload：查合约 tag 应答——单行模板（InstrumentId/ExchangeId 等宽编辑；VolumeMultiple/PriceTick 等保留捕获 verbatim——板子只读 id/交易所） =====
    internal static readonly byte[] QryInstrumentRspTemplate = H(
        "0103344CE104E2802FE303E101025EE307E103025AEFEFE1494E45E173633237E1736332373036E1736332373036E17363E131E132EFEFEFEFE331E207E0EBE306E31EE301E201F4E301E203E0E83FB999999999999A3230323430353036E13230323430363033E13230323730353331E13230323730363031E13230323730363037E131E30132313FC5C28F5C28F5C33FC5C28F5C28F5C331EFEFE17FE0EFFFFFFFFFFFFFE13FF0E630736332373036E1494E45E1736332373036E1736332373036E17363E131E132303237E136E13330E131E1353030E131E131303030E1302E31E13230323430353036E13230323430363033E13230323730E1736332373036E17363E131E132303237E136E13330E131E1353030E131E131303030E1302E31E13230323430353036E13230323430363033E13230323730353331E13230323730363031E13230323730E17363E131E132303237E136E13330E131E1353030E131E131303030E1302E31E13230323430353036E13230323430363033E13230323730353331E13230323730363031E13230323730363037E131E131E17363EFEFEFEFEFE4"
    );

    /// <summary>查合约应答（板子数据源——instance/reqId 回显请求值；多合约=每合约一帧同写，首帧 verbatim isLast）。</summary>
    internal static byte[] BuildQryInstrumentRsp(byte instance, byte reqId, string instrumentId, string exchangeId) =>
        Apply(QryInstrumentRspTemplate,
            new Edit(10, 1, [instance]),
            new Edit(16, 1, [reqId]),
            new Edit(33, 6, F(instrumentId, 6)),
            new Edit(40, 6, F(instrumentId, 6)),
            new Edit(170, 6, F(instrumentId, 6)),
            new Edit(181, 6, F(instrumentId, 6)),
            new Edit(188, 6, F(instrumentId, 6)),
            new Edit(251, 6, F(instrumentId, 6)),
            new Edit(24, 3, F(exchangeId, 3)),
            new Edit(177, 3, F(exchangeId, 3))
        );

    // ===== M3 帧 MdLoginRsp（62B payload：MD 登录应答——交易日编辑） =====
    internal static readonly byte[] MdLoginRspTemplate = H(
        "0103344CE43001E5020181E755E44354503A4E6F204572726F72EFEFEFEFE9100301243230323630393239EFEFEFEFEFEFEFEFEFEFEFEFEFEFEFEFEFEFEE"
    );

    /// <summary>MD 登录应答（凭据不校验——行情订阅无账户语义）。</summary>
    internal static byte[] BuildMdLoginRsp(string tradingDay) =>
        Apply(MdLoginRspTemplate,
            new Edit(35, 8, A(tradingDay))
        );

    // ===== M3 帧 MdTick（109B payload：单合约 E4F103 流式 tick——价格三槽等宽填充，量串 verbatim） =====
    internal static readonly byte[] MdTickTemplate = H(
        "0103344CE4F103E504E1D2E42439E1866D32373031EFEB30323A32313A3139E53230323630393330E16D32373031EFEFEFEFEFE12433E11C40AA56E60C3DCE42192CC8E0E5E0E8E24142BF9B80E32434E11840AA54E7045140AA56E703282481E10840E0E073C994E0E15FE0E6"
    );

    /// <summary>tick 推送（E4F103 流式 op——E4F101 快照 op 重复推送触发 native 重投，勿改回）；last/bid/ask 等宽填充，量串 verbatim。</summary>
    internal static byte[] BuildMdTick(string instrumentId, string tradingDay, string updateTime, double lastPrice, double bidPrice, double askPrice) =>
        Apply(MdTickTemplate,
            new Edit(32, 8, A(tradingDay)),
            new Edit(16, 5, F(instrumentId, 5)),
            new Edit(41, 5, F(instrumentId, 5)),
            new Edit(23, 8, A(updateTime)),
            new Edit(56, 3, PadTrimmedDouble(lastPrice, 3)),
            new Edit(82, 3, PadTrimmedDouble(bidPrice, 3)),
            new Edit(88, 3, PadTrimmedDouble(askPrice, 3))
        );
}
