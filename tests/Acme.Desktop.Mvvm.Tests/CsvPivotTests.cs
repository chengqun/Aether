using Acme.Desktop.Mvvm.Knowledge;
using Xunit;

namespace Acme.Desktop.Mvvm.Tests;

public class CsvPivotTests
{
    // 长表：维度(grp) + 指标维度(obs_point) + 值(od_yi)。透视 obs_point 成列头。
    private static CsvTable SampleTable() => CsvParser.Parse(
        "m,grp,obs_point,od_yi\n" +
        "202501,A,fpd1,0.10\n" +
        "202501,A,fpd10,0.20\n" +
        "202501,B,fpd1,0.30\n" +
        "202501,B,fpd10,0.40\n");

    [Fact]
    public void Pivot_Basic_TransposesObsPointToHeaders()
    {
        var table = SampleTable();
        var result = CsvPivot.Pivot(table, pivotColumnIndex: 2, valueColumnIndex: 3); // obs_point, od_yi

        Assert.Equal(new[] { "m", "grp" }, result.RowHeaders);
        Assert.Equal(new[] { "fpd1", "fpd10" }, result.PivotHeaders);
        Assert.Equal("od_yi", result.ValueHeader);
        // 2 个行维度组合 × 2 个 pivot 头 = 4 格
        Assert.Equal(2, result.Rows.Count);
        // 行 A：fpd1=0.10, fpd10=0.20
        var rowA = result.Rows[0];
        Assert.Equal("A", rowA[1]);
        Assert.Equal("0.1", rowA[2]);
        Assert.Equal("0.2", rowA[3]);
    }

    [Fact]
    public void Pivot_SumsNumericDuplicates()
    {
        var table = CsvParser.Parse(
            "grp,obs,val\nA,x,1\nA,x,2\nA,y,5\n");
        var result = CsvPivot.Pivot(table, pivotColumnIndex: 1, valueColumnIndex: 2);
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
        var result = CsvPivot.Pivot(table, pivotColumnIndex: 1, valueColumnIndex: 2);
        var rowA = result.Rows[0]; // A: x=1, y 缺失 → ""
        Assert.Equal("", rowA[2]);
    }

    [Fact]
    public void RecommendPivotColumn_PicksLowestDistinct()
    {
        var table = SampleTable();
        // 列：m(1 去重), grp(2 去重), obs_point(2 去重 fpd1/fpd10), od_yi(4 去重)
        // 最小去重 = m(1)。但 m 全是 202501（1 个值），客观上 m 是「低基数」列。
        // 这里验证的是排序正确与否的机制，用 od_yi 那列 4 个不同值不应被选。
        var idx = CsvPivot.RecommendPivotColumn(table);
        Assert.Equal(0, idx); // m 列（1 个去重值）被推荐
    }

    [Fact]
    public void RecommendValueColumn_PicksFirstNumericColumn()
    {
        var table = SampleTable();
        // od_yi 是唯一纯数值列（index 3）。m/grp/obs_point 非数值。
        Assert.Equal(3, CsvPivot.RecommendValueColumn(table));
    }
}
