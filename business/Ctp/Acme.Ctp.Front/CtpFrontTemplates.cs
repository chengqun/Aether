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
    internal static byte[] H(string hex) => Convert.FromHexString(hex);

    /// <summary>ASCII 字符串值 → 字节（协议字段值为 ASCII）。</summary>
    internal static byte[] A(string s) => Encoding.ASCII.GetBytes(s);

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
            new Edit(17, 4, A(brokerId)),
            new Edit(22, 6, A(userId)),
            new Edit(159, 18, A(appId))
        );

    // ===== 帧 ⑧ 登录预响应（77B payload：broker/user 替换） =====
    internal static readonly byte[] LoginRspTemplate = H(
        "0103344CE43011E502015BE301E355E4D5FDC8B7EFEFEFEFEFE21012E1FE39393939E7323437313633EFEFEFEFEFEFEFEFEFEFE30173696D6E6F775F636C69656E745F74657374EF31EFEFEFE4"
    );

    /// <summary>⑧ 登录预响应（SDK 收 ⑧ 后自动发 ⑨ 系统信息请求）。</summary>
    internal static byte[] BuildLoginRsp(string brokerId, string userId) =>
        Apply(LoginRspTemplate,
            new Edit(30, 4, A(brokerId)),
            new Edit(35, 6, A(userId))
        );

    // ===== 帧 ⑩ 登录结果主帧（206B payload：loginReqId/broker/user/交易日×2 替换） =====
    internal static readonly byte[] SysInfoRspTemplate = H(
        "0103344CE43001E503018DE302E355E4D5FDC8B7EFEFEFEFEFE24036E1087FFFFFFFE22710100301243230323630393239E131313A31363A3536E139393939E7323437313633EA54726164696E67486F7374696E67EFEF012DAF7B0231EC31313A31363A3535E131313A31363A3535E131313A31363A3535E131313A31323A3034E131313A31363A3535E176362E372E31335F58435F32303236303431332E31323336352E746B65726E656CE831313A31363A3535E401E301323032363039323931313A31333A3232EFEFEFEFE6"
    );

    /// <summary>⑩ 登录结果主帧（交易日/FrontID/SessionID/MaxOrderRef——⑩ 到达即完成 OnRspUserLogin）。</summary>
    internal static byte[] BuildSysInfoRsp(string brokerId, string userId, byte loginReqId, string tradingDay) =>
        Apply(SysInfoRspTemplate,
            new Edit(12, 1, [loginReqId]),
            new Edit(59, 4, A(brokerId)),
            new Edit(64, 6, A(userId)),
            new Edit(41, 8, A(tradingDay)),
            new Edit(185, 8, A(tradingDay))
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
            new Edit(34, 4, A(brokerId)),
            new Edit(39, 6, A(userId)),
            new Edit(46, 8, A(tradingDay))
        );

    // ===== 帧 ⑯ 资金应答（134B payload：reqId/broker/账户×2/交易日 + 6 个 E0 转义大端余额字段） =====
    internal static readonly byte[] QryAccountRspTemplate = H(
        "0103344CE104E28007E301E101018EE305E10C018A39393939E13234373136E1323437313633E130E130E139EFE2412E83E0EF6147AE14412E83E0EF6147AE14EFEFEFEFEFEFEE412E83E0EF6147AE14412E83E0EF6147AE14412E83E0EF6147AE14E83230323630393239E401EFEFEFE3434E59EFEFEB412E83E0EF6147AE14EFEFEFEFEFEE"
    );

    /// <summary>⑯ 资金应答（6 个 E0 转义余额字段全部 ← Balance——整槽替换，线上宽度随值内 tag 字节数可变）。</summary>
    internal static byte[] BuildQryAccountRsp(byte reqId, string brokerId, string accountId, string userId, string tradingDay, double balance) =>
        Apply(QryAccountRspTemplate,
            new Edit(16, 1, [reqId]),
            new Edit(21, 4, A(brokerId)),
            new Edit(26, 5, A(accountId.Length >= 5 ? accountId[..5] : accountId.PadLeft(5, '0'))),
            new Edit(32, 6, A(accountId)),
            new Edit(99, 8, A(tradingDay)),
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
}
