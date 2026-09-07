using Acme.Desktop.Mvvm.Knowledge;
using Xunit;

namespace Acme.Desktop.Mvvm.Tests;

public class CsvPivotTests
{
    // 长表：维度(m,grp) + 观测点(obs_point) + 值(od_yi)。透视 obs_point 成列头。
    private static CsvTable SampleTable() => CsvParser.Parse(
        "m,grp,obs_point,od_yi\n" +
        "202501,A,fpd1,0.10\n" +
        "202501,A,fpd10,0.20\n" +
        "202501,B,fpd1,0.30\n" +
        "202501,B,fpd10,0.40\n");

    private static CsvValueSpec Single(int col, string name) => CsvValueSpec.Single(col, name);

    [Fact]
    public void Pivot_Basic_TransposesObsPointToHeaders()
    {
        var table = SampleTable();
        var result = CsvPivot.Pivot(table, new[] { 0, 1 }, 2, new[] { Single(3, "od_yi") }, asPercent: false);

        Assert.Equal(new[] { "m", "grp" }, result.RowHeaders);
        Assert.Equal(new[] { "fpd1", "fpd10" }, result.PivotHeaders);
        Assert.Equal(2, result.Rows.Count);
        var rowA = result.Rows[0]; // A 行：fpd1=0.10, fpd10=0.20
        Assert.Equal("A", rowA[1]);
        Assert.Equal("0.1", rowA[2]);
        Assert.Equal("0.2", rowA[3]);
    }

    [Fact]
    public void Pivot_SumsNumericDuplicates()
    {
        var table = CsvParser.Parse(
            "grp,obs,val\nA,x,1\nA,x,2\nA,y,5\n");
        var result = CsvPivot.Pivot(table, new[] { 0 }, 1, new[] { Single(2, "val") }, asPercent: false);
        Assert.Single(result.Rows);
        var row = result.Rows[0];
        Assert.Equal("3", row[1]);  // x: 1+2
        Assert.Equal("5", row[2]);  // y: 5
    }

    [Fact]
    public void Pivot_MissingCell_EmptyString()
    {
        var table = CsvParser.Parse(
            "grp,obs,val\nA,x,1\nB,y,2\n");
        var result = CsvPivot.Pivot(table, new[] { 0 }, 1, new[] { Single(2, "val") }, asPercent: false);
        var rowA = result.Rows[0]; // A: x=1, y 缺失 → ""
        Assert.Equal("", rowA[2]);
    }

    [Fact]
    public void Pivot_ExcludesPivotAndValueFromRowDimensions()
    {
        var table = SampleTable();
        var result = CsvPivot.Pivot(table, new[] { 0, 1, 2, 3 }, 2, new[] { Single(3, "od_yi") }, asPercent: false);
        Assert.Equal(new[] { "m", "grp" }, result.RowHeaders);
    }

    [Fact]
    public void Pivot_SubsetRowDimensions_OnlySelectedGroup()
    {
        var table = SampleTable();
        var result = CsvPivot.Pivot(table, new[] { 1 }, 2, new[] { Single(3, "od_yi") }, asPercent: false);
        Assert.Equal(new[] { "grp" }, result.RowHeaders);
        Assert.Equal(2, result.Rows.Count);
    }

    [Fact]
    public void Pivot_FormulaValue_DividesAfterSumming()
    {
        // 公式 [od]/[obs_yi]：聚合后相除（Σod ÷ Σobs），默认原值（非百分比）。4/30=0.133333。
        var table = CsvParser.Parse(
            "grp,obs,od,obs_yi\nA,x,1,10\nA,x,3,20\n");
        var result = CsvPivot.Pivot(table, new[] { 0 }, 1,
            new[] { CsvValueSpec.Formula_("逾期率", "[od] / [obs_yi]") }, asPercent: false);
        Assert.Equal(new[] { "逾期率" }, result.ValueHeaders);
        Assert.Single(result.Rows);
        Assert.Equal("0.133333", result.Rows[0][1]);
    }

    [Fact]
    public void Pivot_FormulaValue_AsPercent()
    {
        // 百分比开关：0.133333 → 13.333%。
        var table = CsvParser.Parse(
            "grp,obs,od,obs_yi\nA,x,1,10\nA,x,3,20\n");
        var result = CsvPivot.Pivot(table, new[] { 0 }, 1,
            new[] { CsvValueSpec.Formula_("逾期率", "[od] / [obs_yi]") }, asPercent: true);
        Assert.Equal("13.333%", result.Rows[0][1]);
    }

    [Fact]
    public void Pivot_MultipleValueFields_ProduceMultipleColumns()
    {
        // 两个值字段：单列 od + 公式 [od]/[obs_yi] → 每个观测点两组列。
        var table = CsvParser.Parse(
            "grp,obs,od,obs_yi\nA,x,1,10\nA,y,2,20\n");
        var result = CsvPivot.Pivot(table, new[] { 0 }, 1,
            new[] { Single(2, "od"), CsvValueSpec.Formula_("rate", "[od] / [obs_yi]") },
            asPercent: false);
        // 多观测点(x,y)：值头 = 名 · 观测点
        Assert.Equal(new[] { "od · x", "od · y", "rate · x", "rate · y" }, result.ValueHeaders);
    }

    [Fact]
    public void Pivot_NaturalSort_PivotHeaders_ByNumber()
    {
        // fpd1 < fpd10 < fpd3 < fpd30 的字母序应被数值序取代：fpd1 < fpd3 < fpd10 < fpd30，mob2 < mob10。
        var table = CsvParser.Parse(
            "obs,val\nfpd1,1\nfpd10,2\nfpd3,3\nfpd30,4\nmob10,5\nmob2,6\n");
        var result = CsvPivot.Pivot(table, Array.Empty<int>(), 0, new[] { Single(1, "val") }, asPercent: false);
        Assert.Equal(new[] { "fpd1", "fpd3", "fpd10", "fpd30", "mob2", "mob10" }, result.PivotHeaders);
    }

    [Fact]
    public void Pivot_Filter_RemovesNonAllowedRows()
    {
        // 筛选 grp 只保留 "A"：B 行被过滤。
        var table = CsvParser.Parse(
            "grp,obs,val\nA,x,10\nB,x,20\n");
        var filters = new[] { (ColumnIndex: 0, (IReadOnlySet<string>)new HashSet<string> { "A" }) };
        var result = CsvPivot.Pivot(table, new[] { 0 }, 1, new[] { Single(2, "val") }, asPercent: false, filters);
        // 只有 A 一行，值 = 10。
        Assert.Single(result.Rows);
        Assert.Equal("10", result.Rows[0][1]);
    }

    [Fact]
    public void Pivot_Filter_EmptyAllowed_NoFilter()
    {
        // 空集合 = 全选不过滤。
        var table = CsvParser.Parse(
            "grp,obs,val\nA,x,10\nB,x,20\n");
        var filters = new[] { (ColumnIndex: 0, (IReadOnlySet<string>)new HashSet<string>()) };
        var result = CsvPivot.Pivot(table, new[] { 0 }, 1, new[] { Single(2, "val") }, asPercent: false, filters);
        Assert.Equal(2, result.Rows.Count);
    }
}

public class CsvFieldClassifierTests
{
    private static CsvTable VintageTable() => CsvParser.Parse(
        "m,grp,product_flag3,loan_org_label,obs_point,putout_cnt,amt_yi,obs_yi,od_yi\n" +
        "202501,sec_事件前借款,优质户_mob6+,01.借条,fpd1,508,0.079,0.079044,\n" +
        "202501,sec_事件前借款,优质户_mob6+,01.借条,fpd10,508,0.079,0.079044,\n" +
        "202501,sec_事件前借款,优质户_mob6+,01.借条,fpd30,508,0.079,0.079044,\n");

    [Fact]
    public void Classify_ObsPoint_IsPivot()
    {
        var fields = CsvPivot.Fields(VintageTable());
        var obsPoint = fields.First(f => f.Header == "obs_point");
        Assert.Equal(CsvFieldKind.Pivot, obsPoint.Kind);
    }

    [Fact]
    public void Classify_OdYi_IsMeasure()
    {
        var fields = CsvPivot.Fields(VintageTable());
        var odYi = fields.First(f => f.Header == "od_yi");
        Assert.Equal(CsvFieldKind.Measure, odYi.Kind);
    }

    [Fact]
    public void Classify_Grp_IsDimension()
    {
        var fields = CsvPivot.Fields(VintageTable());
        var grp = fields.First(f => f.Header == "grp");
        Assert.Equal(CsvFieldKind.Dimension, grp.Kind);
    }

    [Fact]
    public void Classify_M_IsDimension_NotMeasure()
    {
        // m（月份 202501）是整数值列但应判为维度，不能误判为指标。
        var fields = CsvPivot.Fields(VintageTable());
        var m = fields.First(f => f.Header == "m");
        Assert.Equal(CsvFieldKind.Dimension, m.Kind);
    }

    [Fact]
    public void Classify_BomHeader_M_IsDimension()
    {
        // 真实 CSV 首表头带 UTF-8 BOM（﻿m）。验证 BOM 不影响分类：﻿m 仍判为维度。
        var table = CsvParser.Parse(
            "﻿m,grp,obs_point,od_yi\n" +
            "202501,A,fpd1,0.01\n");
        var fields = CsvPivot.Fields(table);
        var m = fields.First(f => f.Header.StartsWith("m"));
        Assert.Equal(CsvFieldKind.Dimension, m.Kind);
    }

    [Fact]
    public void Classify_PutoutCnt_IsMeasure()
    {
        // putout_cnt 含 cnt 信号 + 整数值 → 指标（不是维度）。
        var fields = CsvPivot.Fields(VintageTable());
        var cnt = fields.First(f => f.Header == "putout_cnt");
        Assert.Equal(CsvFieldKind.Measure, cnt.Kind);
    }
}
