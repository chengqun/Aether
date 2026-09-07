using Acme.Desktop.Mvvm.Knowledge;
using Xunit;

namespace Acme.Desktop.Mvvm.Tests;

public class CsvPivotLayoutTests
{
    private const string Csv = "m,grp,obs_point,od_yi,obs_yi\n" +
        "202501,A,fpd1,0.01,1.0\n202501,B,fpd10,0.02,2.0\n";

    [Fact]
    public void Serialize_RoundTrip()
    {
        var layout = new CsvPivotLayout
        {
            Rows = new() { "m", "grp" },
            Column = "obs_point",
            Values = new()
            {
                new CsvValueLayout { Name = "逾期率", Formula = "[od_yi] / [obs_yi]", AsPercent = true },
                new CsvValueLayout { Name = "od_yi", AsPercent = false },
            },
            Filters = new() { new CsvFilterLayout { Field = "grp", Values = new() { "A" } } },
            Calculated = new() { new CsvCalcLayout { Name = "rate2", Formula = "[od_yi] * 2" } },
        };
        var json = layout.Serialize();
        var back = CsvPivotLayout.Deserialize(json)!;

        Assert.Equal(new[] { "m", "grp" }, back.Rows);
        Assert.Equal("obs_point", back.Column);
        Assert.Equal(2, back.Values.Count);
        Assert.True(back.Values[0].AsPercent);
        Assert.Equal("[od_yi] / [obs_yi]", back.Values[0].Formula);
        Assert.Equal("od_yi", back.Values[1].Name);
        Assert.Equal("grp", back.Filters[0].Field);
        Assert.Equal(new[] { "A" }, back.Filters[0].Values);
        Assert.Equal("rate2", back.Calculated[0].Name);
    }

    [Fact]
    public void Deserialize_NullOrEmpty_ReturnsNull()
    {
        Assert.Null(CsvPivotLayout.Deserialize(null));
        Assert.Null(CsvPivotLayout.Deserialize(""));
        Assert.Null(CsvPivotLayout.Deserialize("   "));
    }

    [Fact]
    public void Deserialize_InvalidJson_ReturnsNull()
    {
        Assert.Null(CsvPivotLayout.Deserialize("not json {"));
    }

    [Fact]
    public void CaptureLayout_ReflectsVmState()
    {
        var vm = new CsvTableViewModel(Csv);
        // 默认：行维度 = m,grp；列维度 = obs_point；值区空。
        var layout = vm.CaptureLayout();
        Assert.Equal(new[] { "m", "grp" }, layout.Rows);
        Assert.Equal("obs_point", layout.Column);
        Assert.Empty(layout.Values);
    }

    [Fact]
    public void ApplyLayout_RestoresRowsColumnValues()
    {
        var vm = new CsvTableViewModel(Csv);
        var layout = new CsvPivotLayout
        {
            Rows = new() { "grp" },
            Column = "obs_point",
            Values = new() { new CsvValueLayout { Name = "逾期率", Formula = "[od_yi] / [obs_yi]", AsPercent = true } },
        };
        vm.ApplyLayout(layout);

        var back = vm.CaptureLayout();
        Assert.Equal(new[] { "grp" }, back.Rows);
        Assert.Equal("obs_point", back.Column);
        Assert.Single(back.Values);
        Assert.Equal("逾期率", back.Values[0].Name);
        Assert.True(back.Values[0].AsPercent);
    }

    [Fact]
    public void ApplyLayout_UnknownFieldName_Skipped()
    {
        var vm = new CsvTableViewModel(Csv);
        var layout = new CsvPivotLayout
        {
            Rows = new() { "nonexistent", "grp" },
            Column = "no_such_column",
        };
        // 不抛异常。
        vm.ApplyLayout(layout);
        var back = vm.CaptureLayout();
        Assert.Equal(new[] { "grp" }, back.Rows);
        Assert.Null(back.Column);
    }

    [Fact]
    public void ApplyLayout_FilterWithValues()
    {
        var vm = new CsvTableViewModel(Csv);
        var layout = new CsvPivotLayout
        {
            Rows = new() { "m" },
            Column = "obs_point",
            Values = new() { new CsvValueLayout { Name = "od_yi" } },
            Filters = new() { new CsvFilterLayout { Field = "grp", Values = new() { "A" } } },
        };
        vm.ApplyLayout(layout);

        var back = vm.CaptureLayout();
        Assert.Single(back.Filters);
        Assert.Equal("grp", back.Filters[0].Field);
        Assert.Equal(new[] { "A" }, back.Filters[0].Values);
    }

    [Fact]
    public void CaptureLayout_CalculatedInFieldList()
    {
        var vm = new CsvTableViewModel(Csv);
        vm.AddCalculatedField("rate2", "[od_yi] * 2");
        var layout = vm.CaptureLayout();
        Assert.Single(layout.Calculated);
        Assert.Equal("rate2", layout.Calculated[0].Name);
    }
}
