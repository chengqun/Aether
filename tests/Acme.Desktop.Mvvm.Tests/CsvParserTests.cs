using Acme.Desktop.Mvvm.Knowledge;
using Xunit;

namespace Acme.Desktop.Mvvm.Tests;

public class CsvParserTests
{
    [Fact]
    public void Parse_SimpleHeaderAndRows()
    {
        var table = CsvParser.Parse("a,b,c\n1,2,3\n4,5,6\n");
        Assert.Equal(new[] { "a", "b", "c" }, table.Headers);
        Assert.Equal(2, table.RowCount);
        Assert.Equal("1", table.Rows[0][0]);
        Assert.Equal("6", table.Rows[1][2]);
    }

    [Fact]
    public void Parse_QuotedCommaAndNewline()
    {
        var table = CsvParser.Parse("a,b\n\"x,y\",\"line1\nline2\"\n");
        Assert.Equal(1, table.RowCount);
        Assert.Equal("x,y", table.Rows[0][0]);
        Assert.Equal("line1\nline2", table.Rows[0][1]);
    }

    [Fact]
    public void Parse_EscapedDoubleQuote()
    {
        var table = CsvParser.Parse("a\n\"say \"\"hi\"\"\"\n");
        Assert.Equal(1, table.RowCount);
        Assert.Equal("say \"hi\"", table.Rows[0][0]);
    }

    [Fact]
    public void Parse_CrlfLineEndings()
    {
        var table = CsvParser.Parse("a,b\r\n1,2\r\n3,4\r\n");
        Assert.Equal(2, table.RowCount);
        Assert.Equal("4", table.Rows[1][1]);
    }

    [Fact]
    public void Parse_NoTrailingNewline()
    {
        var table = CsvParser.Parse("a,b\n1,2");
        Assert.Equal(1, table.RowCount);
        Assert.Equal("2", table.Rows[0][1]);
    }

    [Fact]
    public void Parse_MissingCells_NormalizedToEmpty()
    {
        var table = CsvParser.Parse("a,b,c\n1\n");
        Assert.Equal(3, table.RowCount == 1 ? table.Rows[0].Count : -1);
        Assert.Equal("1", table.Rows[0][0]);
        Assert.Equal("", table.Rows[0][1]);
        Assert.Equal("", table.Rows[0][2]);
    }

    [Fact]
    public void Parse_ExtraCells_TruncatedToHeaderCount()
    {
        var table = CsvParser.Parse("a,b\n1,2,3,4\n");
        Assert.Equal(1, table.RowCount);
        Assert.Equal(2, table.Rows[0].Count);
        Assert.Equal("1", table.Rows[0][0]);
        Assert.Equal("2", table.Rows[0][1]);
    }

    [Fact]
    public void Parse_Empty_ReturnsEmptyTable()
    {
        var table = CsvParser.Parse("");
        Assert.Empty(table.Headers);
        Assert.Equal(0, table.RowCount);
    }

    [Fact]
    public void Parse_HeaderOnly_NoRows()
    {
        var table = CsvParser.Parse("a,b,c\n");
        Assert.Equal(new[] { "a", "b", "c" }, table.Headers);
        Assert.Equal(0, table.RowCount);
    }
}
