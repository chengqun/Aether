using Acme.AppAutomation.StockTradingBridge;
using Xunit;

namespace Acme.AppAutomation.Tests;

public class ThsPositionsParserTests
{
    // 列序：0 代码 / 1 名称 / 2 余额 / 3 可用 / 4 冻结 / 5 成本价 / 6 市价 / 7 盈亏 / 8 盈亏比 / 9 市值 / 10 持股天数 / 11 市场
    private const string Sample =
        "证券代码\t证券名称\t股票余额\t可用余额\t冻结数量\t成本价\t市价\t盈亏\t盈亏比例(%.2f)\t市值\t持股天数\t市场\r\n" +
        "600000\t浦发银行\t1000\t1000\t0\t10.50\t11.20\t700.00\t6.67\t11200.00\t5\tSH\r\n" +
        "000001\t平安银行\t500\t500\t0\t13.00\t12.50\t-250.00\t-3.85\t6250.00\t3\tSZ";

    [Fact]
    public void Parse_returns_empty_for_blank_input()
    {
        Assert.Empty(ThsPositionsParser.Parse(""));
        Assert.Empty(ThsPositionsParser.Parse("   "));
    }

    [Fact]
    public void Parse_returns_empty_when_only_header()
    {
        Assert.Empty(ThsPositionsParser.Parse("代码\t名称\t余额"));
    }

    [Fact]
    public void Parse_maps_all_twelve_columns()
    {
        var list = ThsPositionsParser.Parse(Sample);

        Assert.Equal(2, list.Count);
        var first = list[0];
        Assert.Equal("600000", first.Symbol);
        Assert.Equal("浦发银行", first.Name);
        Assert.Equal(1000, first.Quantity);
        Assert.Equal(1000, first.AvailableQuantity);
        Assert.Equal(0, first.FrozenQuantity);
        Assert.Equal(10.50m, first.CostPrice);
        Assert.Equal(11.20m, first.CurrentPrice);
        Assert.Equal(700.00m, first.ProfitLoss);
        Assert.Equal(6.67m, first.ProfitLossRatio);
        Assert.Equal(11200.00m, first.MarketValue);
        Assert.Equal(5, first.HoldingDays);
        Assert.Equal("SH", first.ExchangeMarket);

        var second = list[1];
        Assert.Equal("000001", second.Symbol);
        Assert.Equal(-250.00m, second.ProfitLoss);
        Assert.Equal("SZ", second.ExchangeMarket);
    }

    [Fact]
    public void Parse_skips_rows_with_too_few_columns()
    {
        var text = "证券代码\t证券名称\r\n600000\t浦发银行\r\n短行";
        Assert.Empty(ThsPositionsParser.Parse(text));
    }

    [Fact]
    public void Parse_handles_unix_newlines()
    {
        var text = "代码\t名\t余\t可\t冻\t成本\t市\t盈亏\t比\t市值\t天\t市\n600000\t浦发\t100\t100\t0\t1\t2\t3\t4\t5\t6\tSH";
        var list = ThsPositionsParser.Parse(text);
        Assert.Single(list);
        Assert.Equal("600000", list[0].Symbol);
        Assert.Equal(6, list[0].HoldingDays);
    }
}
