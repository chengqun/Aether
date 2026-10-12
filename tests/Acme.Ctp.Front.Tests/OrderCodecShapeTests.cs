using Acme.Ctp.Front;

namespace Acme.Ctp.Front.Tests;

/// <summary>
/// ⑲ 报单 wire 解析的**形状锚定回归钉**（2026-10-12 服务器三轮丢单实测帧——tag 是动态值，
/// 随用户名长度/字段值宽联动漂移；按固定 tag 解析对非 6 字符用户/低价/不同量形全灭）。
/// 五个帧全部来自服务器前置日志的 body hex（真实 SDK 编码），字段语义已知——防 codec 回退。
/// </summary>
public class OrderCodecShapeTests
{
    // user=e2eusr（6字符）价格 2998 量 5 GFD 买开——M2 形状（orderRef@E8、枚举@EA、量@E8）
    private const string FrameE2Eusr =
        "E101E240E404E101017BE30404E1017739393939E7653265757372EFEFE8303030303030303030303031E1653265757372EA323030E431E440A76CE80533E931E30131E830EFEFEFEFEFEFEFEFE86178303031EFEFEFEFEFEFEFE4";

    // user=mm1（3字符）价格 50 量 1 GFD 买开——orderRef@EB、枚举@ED、量@E9（低价 <1024 场景）
    private const string FrameMm1 =
        "E101E240E404E101017BE30404E1017739393939E76D6D31EFEFEB303030303030303030303031E16D6D31ED323030E431E44049E90133E931E30131E830EFEFEFEFEFEFEFEFE86178303031EFEFEFEFEFEFEFE4";

    // user=botmm（5字符）价格 99.94 量 5 GFD——orderRef@E9、枚举@EB、量@**E3**（2026-10-12 服务器实测
    // 做市商挂单形状——量 tag E3 是第三轮丢单的根因，固定 E8/E7/E9 链救不了）
    private const string FrameBotmmLadder =
        "E101E240E412E101017BE31604E1017739393939E7626F746D6DEFEFE9303030303030303030303231E1626F746D6DEB323030E431E44058FC28F5C28F5CE30533E931E30131E830EFEFEFEFEFEFEFEFE86178303031EFEFEFEFEFEFEFE4";

    [Theory]
    [InlineData(FrameE2Eusr,   "000000000001", "ax001", '0', '0', '2', 2998.0, 5, '3')]
    [InlineData(FrameMm1,      "000000000001", "ax001", '0', '0', '2', 50.0,   1, '3')]
    [InlineData(FrameBotmmLadder, "000000000021", "ax001", '0', '0', '2', 99.94, 5, '3')]
    public void 实测帧_形状锚定解析_各字段精确还原(string hex, string refId, string inst,
        char dir, char off, char pt, double price, int vol, char tc)
    {
        var o = CtpOrderWireCodec.ParseOrderInsert(Convert.FromHexString(hex));
        Assert.NotNull(o);
        Assert.Equal(refId, o.OrderRef);
        Assert.Equal(inst, o.InstrumentId);
        Assert.Equal(dir, o.Direction);
        Assert.Equal(off, o.OffsetFlag);
        Assert.Equal(pt, o.PriceType);
        Assert.Equal(price, o.LimitPrice, 2);
        Assert.Equal(vol, o.Volume);
        Assert.Equal(tc, (char)o.TimeCondition);
    }
}
