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
        // orderRef：**形状锚定**（2026-10-12 探针定案）——tag 是动态值（实测 orderRef 的 tag = 0xEE − len(用户名)：
        // 6字符⇒E8、7⇒E7、8⇒E6、3⇒EB——随用户名长度反比漂移），按固定 tag 找天然脆弱。12 位纯数字 run
        // 全帧唯一（枚举"200" 3 位、量 run 含低字节、价格 run 含非数字），任意 tag 扫描取之。
        var orderRef = RunLast(body, 0, OrderRefShape);
        // 枚举三元组（PriceType/Direction/OffsetFlag 的 ASCII '0'-'4'）——**形状锚定**（2026-10-12 探针定案）：
        // tag 动态（实测 = orderRef 的 tag + 2：E8⇒EA、E7⇒E9、EB⇒ED——随用户名长度联动漂移）。
        // 3 字节纯 [0-4] 数字 run 全帧唯一（orderRef 12 位、量 run 含低字节、价格 run 含非数字）。
        var enums = RunLast(body, 0, r => r.Length == 3 && r.ToArray().All(b => b is >= (byte)'0' and <= (byte)'4'));
        // 价格谓词（2026-10-12 修正）：尾剥零大端 double——**不能要求值内含 ≥0x80 字节**（旧谓词对
        // 抓包价 2999=0x40A7… 恰好成立，而 ax001 价 100=0x4059… 全字节 <0x80 ⇒ 价格 <1024 的单全被
        // 解析失败静默丢弃——服务器实测 bot 全部丢单的根因）。改为「非纯 ASCII 数字」：二进制价格 run
        // 必含非数字字节（0x40='@' 等），而帧内纯数字的 E4 run（计数器 04/"1"）被排除。
        var price = RunAfter(body, 0xE4, r => r.Length is >= 2 and <= 8 && !r.ToArray().All(b => b is >= (byte)'0' and <= (byte)'9'));
        // 量的 tag 随值宽/形状变：1 字节量 ⇒ E8 或 E9（实测量1+GFD 编码 E9 01 33——2026-10-12 服务器
        // 丢单实测；M2 抓包 oracle 曾实证 E8 2B run），2 字节量（≥256 手）⇒ E7（3B run）——先 E8 后 E7 后 E9，
        // VolShape 谓词（前缀含 <0x20 低字节 + 尾字节 TimeCondition）闸住误中。
        var vol = RunAfter(body, 0xE8, VolShape) ?? RunAfter(body, 0xE7, VolShape) ?? RunAfter(body, 0xE9, VolShape);
        // 合约形状 ^[A-Za-z]{1,2}\d{3,4}$（官方命名——1-2 字母；本地品种已改名 zx 对齐，排除用户名/交易所码）
        // 取**最后一个**匹配（合约近帧尾、用户近帧头——避免 3 字母+3 数字的用户名误中）
        var inst = RunLast(body, 0, r =>
        {
            var letters = 0;
            while (letters < r.Length && char.IsAsciiLetter((char)r[letters])) letters++;
            return letters is >= 1 and <= 2 && r.Length - letters is >= 3 and <= 4
                && r[letters..].ToArray().All(IsDigit);
        });
        if (reqId is null || orderRef is null || enums is null || price is null || vol is null || inst is null) return null;
        return new ParsedOrderInsert(reqId.Value, Encoding.ASCII.GetString(orderRef), Encoding.ASCII.GetString(inst),
            (char)enums[0], (char)enums[1], (char)enums[2],
            ParseTrimmedDouble(price), (int)ParseMinimalInt(vol[..^1]), (char)vol[^1]);
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

    /// <summary>尾剥零大端 double 的解码（右补零到 8B）——模板 TrimmedDouble 的逆。</summary>
    internal static double ParseTrimmedDouble(ReadOnlySpan<byte> wire)
    {
        var b = new byte[8];
        wire.CopyTo(b);
        return BinaryPrimitives.ReadDoubleBigEndian(b);
    }

    /// <summary>去前导零大端 int 的解码（左补零到 8B）——模板 MinimalInt 的逆。</summary>
    internal static long ParseMinimalInt(ReadOnlySpan<byte> wire)
    {
        var b = new byte[8];
        wire.CopyTo(b.AsSpan(8 - wire.Length));
        return BinaryPrimitives.ReadInt64BigEndian(b);
    }
}
