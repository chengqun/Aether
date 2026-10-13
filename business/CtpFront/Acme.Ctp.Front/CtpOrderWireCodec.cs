using System.Buffers.Binary;
using System.Text;

namespace Acme.Ctp.Front;

/// <summary>
/// ⑲ 报单 / ㉑ 撤单请求的 wire 解析 + 数值解码（M2）。
/// 按 run 形状分类而非固定偏移（SDK 构造的请求形状稳定；oracle = 20260929-031656 + 20261001-024350/024121 抓包，
/// 形状规则经 FrontGen 同款查找器验证）。数值解码是模板 <c>TrimmedDouble/MinimalInt</c> 的逆操作。
/// </summary>
internal static class CtpOrderWireCodec
{
    internal sealed record ParsedOrderInsert(
        byte ReqId, string OrderRef, string InstrumentId,
        char PriceType, char Direction, char OffsetFlag,
        double LimitPrice, int Volume, char TimeCondition);

    internal sealed record ParsedOrderAction(byte ReqId, string OrderRef);

    private static bool IsTag(byte b) => b is >= 0xE1 and <= 0xEF or 0xFE;
    private static bool IsDigit(byte b) => b is >= (byte)'0' and <= (byte)'9';

    internal static ParsedOrderInsert? ParseOrderInsert(ReadOnlySpan<byte> body)
    {
        var reqId = ByteAfter(body, "E101017BE3");
        // **全形状锚定**（2026-10-12 三轮探针定案）：SDK 的 tag 是动态值（实测随用户名长度/字段值宽联动漂移——
        // orderRef 的 tag = 0xEE−len(user)（6字符⇒E8、5⇒E9、3⇒EB）；枚举 tag = orderRef tag+2；量的 tag 实测
        // 出现 E3/E8/E9 三种；价格的 tag 实测恒 E4 但不保证）。按固定 tag 找字段天然脆弱——除 reqId 外全部
        // 改为「任意 tag + 形状谓词」锚定（形状在全帧内的唯一性见各谓词注释）。
        var orderRef = RunLast(body, 0, OrderRefShape);
        // 枚举三元组（PriceType/Direction/OffsetFlag 的 ASCII '0'-'4'）——3 字节纯 [0-4] run 唯一
        //（orderRef 12 位、量 run 含低字节、价格 run 含非数字）。
        var enums = RunLast(body, 0, r => r.Length == 3 && r.ToArray().All(b => b is >= (byte)'0' and <= (byte)'4'));
        // 量：VolShape（前缀含 <0x20 低字节 + 尾字节 TimeCondition '1'-'4'）——任意 tag（实测 E3/E8/E9
        // 均出现过；帧内无其它「低字节+尾 TimeCondition」形状的 run）。先定位它的 tag 索引——价格锚点用。
        var volTagIdx = FindRunIndex(body, VolShape);
        if (volTagIdx < 0) return null;
        var volRun = ExtractRunAt(body, volTagIdx);
        // 价格：尾剥零大端 double——解码后 2-8B、非纯 ASCII 数字（4049='@I' 之类）、解析值在 (0,1e9) 合理区间。
        // **位置锚定**：价格紧跟在量 run 之前（帧序：…枚举 → "1" → 价格 → 量 → …）——在量的前缀里取
        // 最后一个合格 run（防帧头 reqId 族 01017B / 量后 0131 等小字节垃圾 run 误中——形状谓词对
        // 1e-303 量级的小值不设防，位置才是硬闸）。低价（<1024，如 ax001 的 100.x）的 double 全字节
        // 可 <0x80——旧谓词 Any(b≥0x80) 对其失效是首轮丢单根因。
        // **E0 转义感知（2026-10-13）**：SDK 对含 tag 字母表字节（E0..EF/FE）的价格值做 E0 前插转义
        // （实测 92.03 → 40 57 01 [E0] EB 85 1E B8 52，线上 run 比逻辑值长 1-8B）——谓词按**去转义后**
        // 的长度/值判形状（旧版按裸 run 判 ⇒ 9B 转义 run 被长度帽 ≤8 拒收 ⇒ 价格误锚到 broker 字段
        // 解出 1.35E-301 垃圾价入簿）。
        var price = volTagIdx > 0
            ? RunLast(body[..volTagIdx], 0, r => UnescapeRun(r).Length is >= 2 and <= 8
                && !r.ToArray().All(b => b is >= (byte)'0' and <= (byte)'9')
                && ParseTrimmedDouble(r) is > 0 and < 1e9)
            : null;
        // 合约形状 ^[A-Za-z]{1,2}\d{3,4}$（官方命名——1-2 字母；本地品种已改名 zx 对齐，排除用户名/交易所码）
        // 取**最后一个**匹配（合约近帧尾、用户近帧头——避免 3 字母+3 数字的用户名误中）
        var inst = RunLast(body, 0, r =>
        {
            var letters = 0;
            while (letters < r.Length && char.IsAsciiLetter((char)r[letters])) letters++;
            return letters is >= 1 and <= 2 && r.Length - letters is >= 3 and <= 4
                && r[letters..].ToArray().All(IsDigit);
        });
        if (reqId is null || orderRef is null || enums is null || price is null || volRun is null || inst is null) return null;
        return new ParsedOrderInsert(reqId.Value, Encoding.ASCII.GetString(orderRef), Encoding.ASCII.GetString(inst),
            (char)enums[0], (char)enums[1], (char)enums[2],
            ParseTrimmedDouble(price), (int)ParseMinimalInt(volRun[..^1]), (char)volRun[^1]);
    }

    /// <summary>定位第一个满足形状谓词的 run 的 tag 起始索引（找不到 −1）——量字段定位 + 价格位置锚点。</summary>
    private static int FindRunIndex(ReadOnlySpan<byte> body, Func<ReadOnlySpan<byte>, bool> match)
    {
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
            if (j > i + 1 && match(body[(i + 1)..j])) return i;
        }
        return -1;
    }

    /// <summary>提取指定 tag 索引处的 run 值（E0 转义感知）。</summary>
    private static byte[]? ExtractRunAt(ReadOnlySpan<byte> body, int tagIdx)
    {
        var j = tagIdx + 1;
        while (j < body.Length)
        {
            if (body[j] == 0xE0 && j + 1 < body.Length) { j += 2; continue; }
            if (IsTag(body[j])) break;
            j++;
        }
        return j > tagIdx + 1 ? body[(tagIdx + 1)..j].ToArray() : null;
    }

    internal static ParsedOrderAction? ParseOrderAction(ReadOnlySpan<byte> body)
    {
        var reqId = ByteAfter(body, "E101013BE3");
        var orderRef = RunAfter(body, 0xEB, r => r.Length == 12 && r.ToArray().All(IsDigit));
        if (reqId is null || orderRef is null) return null;
        return new ParsedOrderAction(reqId.Value, Encoding.ASCII.GetString(orderRef));
    }

    /// <summary>探针用：逐字段解析结果（每行一个字段，null 即缺口）——CodecProbeTests 消费。</summary>
    internal static List<string> DebugFields(ReadOnlySpan<byte> body)
    {
        var lines = new List<string>();
        var reqId = ByteAfter(body, "E101017BE3");
        lines.Add($"reqId={(reqId is { } r ? r.ToString() : "null")}");
        var orderRef = RunLast(body, 0, OrderRefShape);
        lines.Add($"orderRef={(orderRef is null ? "null" : Encoding.ASCII.GetString(orderRef))}");
        var enums = RunAfter(body, 0xEA, r => r.Length == 3 && r.ToArray().All(b => b is >= (byte)'0' and <= (byte)'4'));
        lines.Add($"enums(EA)={(enums is null ? "null" : Encoding.ASCII.GetString(enums))}");
        var price = RunAfter(body, 0xE4, r => r.Length is >= 2 and <= 8 && !r.ToArray().All(b => b is >= (byte)'0' and <= (byte)'9'));
        lines.Add($"price(E4)={(price is null ? "null" : $"{ParseTrimmedDouble(price)} (hex {Convert.ToHexString(price)})")}");
        var vol = RunAfter(body, 0xE8, VolShape) ?? RunAfter(body, 0xE7, VolShape) ?? RunAfter(body, 0xE9, VolShape);
        lines.Add($"vol(E8/E7/E9)={(vol is null ? "null" : $"{ParseMinimalInt(vol[..^1])} tc={(char)vol[^1]} (hex {Convert.ToHexString(vol)})")}");
        var inst = RunLast(body, 0, r =>
        {
            var letters = 0;
            while (letters < r.Length && char.IsAsciiLetter((char)r[letters])) letters++;
            return letters is >= 1 and <= 2 && r.Length - letters is >= 3 and <= 4
                && r[letters..].ToArray().All(IsDigit);
        });
        lines.Add($"inst={(inst is null ? "null" : Encoding.ASCII.GetString(inst))}");
        return lines;
    }

    /// <summary>量字段 run 形状：len 2-5、尾字节 = TimeCondition（'1' IOC / '2' FOK / '3' GFD / '4' PostOnly——本地协议扩展），
    /// 前缀含低字节。</summary>
    private static bool VolShape(ReadOnlySpan<byte> r)
        => r.Length is >= 2 and <= 5
           && r[^1] is (byte)'1' or (byte)'2' or (byte)'3' or (byte)'4'
           && r[..^1].ToArray().Any(b => b < 0x20);

    /// <summary>orderRef run 形状：恒 12 位零填充数字（D12）。</summary>
    private static bool OrderRefShape(ReadOnlySpan<byte> r)
        => r.Length == 12 && r.ToArray().All(IsDigit);

    /// <summary>needle（hex）后紧跟的 1 字节。</summary>
    private static byte? ByteAfter(ReadOnlySpan<byte> body, string needleHex)
    {
        var needle = Convert.FromHexString(needleHex);
        var hit = body.IndexOf(needle);
        return hit >= 0 && hit + needle.Length < body.Length ? body[hit + needle.Length] : null;
    }

    /// <summary>任意 tag 下满足形状谓词的**最后一个** run（合约字段近帧尾——用户字段近帧头的形状冲突防误中）。</summary>
    private static byte[]? RunLast(ReadOnlySpan<byte> body, byte tag, Func<ReadOnlySpan<byte>, bool> match)
    {
        byte[]? last = null;
        for (var i = 0; i < body.Length; i++)
        {
            if (tag != 0 ? body[i] != tag : !IsTag(body[i])) continue;
            var j = i + 1;
            while (j < body.Length)
            {
                if (body[j] == 0xE0 && j + 1 < body.Length) { j += 2; continue; }
                if (IsTag(body[j])) break;
                j++;
            }
            if (j > i + 1 && match(body[(i + 1)..j])) last = body[(i + 1)..j].ToArray();
            i = j - 1;
        }
        return last;
    }

    /// <summary>tag（0 = 任意 tag）后满足形状谓词的第一个 run（E0 转义感知；值长 = tag 到下一 tag）。</summary>
    private static byte[]? RunAfter(ReadOnlySpan<byte> body, byte tag, Func<ReadOnlySpan<byte>, bool> match, string what = "")
    {
        for (var i = 0; i < body.Length; i++)
        {
            if (tag != 0 ? body[i] != tag : !IsTag(body[i])) continue;
            var j = i + 1;
            while (j < body.Length)
            {
                if (body[j] == 0xE0 && j + 1 < body.Length) { j += 2; continue; }
                if (IsTag(body[j])) break;
                j++;
            }
            if (j > i + 1 && match(body[(i + 1)..j])) return body[(i + 1)..j].ToArray();
            i = j - 1;
        }
        return null;
    }

    /// <summary>尾剥零大端 double 的解码（右补零到 8B）——模板 TrimmedDouble 的逆。
    /// E0 转义感知：值内 {E0..EF,FE} 字节前插 E0（SDK 请求侧实证 92.03 → 40 57 01 [E0] EB 85 1E B8 52；
    /// 旧版直读转义字节 ⇒ 非二进小数价被解成 1.35E-301 量级垃圾价入簿——修前实测踩中）。</summary>
    internal static double ParseTrimmedDouble(ReadOnlySpan<byte> wire)
    {
        var un = UnescapeRun(wire);
        var b = new byte[8];
        un.AsSpan(0, Math.Min(un.Length, 8)).CopyTo(b);
        return BinaryPrimitives.ReadDoubleBigEndian(b);
    }

    /// <summary>剥离 run 值内的 E0 转义（E0 + 下一字节 = 一个字面值字节；E0 自身亦被转义为 E0 E0）。</summary>
    private static byte[] UnescapeRun(ReadOnlySpan<byte> wire)
    {
        var r = new byte[wire.Length];
        var n = 0;
        for (var i = 0; i < wire.Length; i++)
        {
            if (wire[i] == 0xE0 && i + 1 < wire.Length) i++;   // 丢弃转义前缀，保留被转义的字面字节
            r[n++] = wire[i];
        }
        return r[..n];
    }

    /// <summary>去前导零大端 int 的解码（左补零到 8B）——模板 MinimalInt 的逆。</summary>
    internal static long ParseMinimalInt(ReadOnlySpan<byte> wire)
    {
        var b = new byte[8];
        wire.CopyTo(b.AsSpan(8 - wire.Length));
        return BinaryPrimitives.ReadInt64BigEndian(b);
    }
}
