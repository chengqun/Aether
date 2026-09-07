using Acme.Desktop.Mvvm.Knowledge;
using Xunit;

namespace Acme.Desktop.Mvvm.Tests;

public class CsvFormulaTests
{
    private static decimal? Eval(string expr, Func<string, decimal?> field)
    {
        var node = CsvFormula.Parse(expr);
        return CsvFormula.Evaluate(node, field);
    }

    [Fact]
    public void Parse_DivideTwoFields()
    {
        var v = Eval("[od_yi] / [obs_yi]", f => f == "od_yi" ? 0.01497m : f == "obs_yi" ? 1m : null);
        Assert.Equal(0.01497m, v);
    }

    [Fact]
    public void Parse_ParenthesesAndPrecedence()
    {
        // (1 + 2) * 3 = 9
        var v = Eval("(1 + 2) * 3", _ => null);
        Assert.Equal(9m, v);
        // 1 + 2 * 3 = 7（乘除优先）
        var v2 = Eval("1 + 2 * 3", _ => null);
        Assert.Equal(7m, v2);
    }

    [Fact]
    public void Parse_FieldNameWithChineseAndSpaces()
    {
        var v = Eval("[安全表 逾期金额] / [观测金额]", f => f == "安全表 逾期金额" ? 5m : f == "观测金额" ? 2m : null);
        Assert.Equal(2.5m, v);
    }

    [Fact]
    public void Parse_DivideByZero_ReturnsNull()
    {
        var v = Eval("[a] / [b]", f => f == "a" ? 1m : 0m);
        Assert.Null(v);
    }

    [Fact]
    public void Parse_MissingField_ReturnsNull()
    {
        var v = Eval("[a] + [b]", f => f == "a" ? 1m : null);
        Assert.Null(v);
    }

    [Fact]
    public void ReferencedFields_ListsFieldsInOrder()
    {
        var node = CsvFormula.Parse("[od_yi] / [obs_yi] + [amt_yi]");
        Assert.Equal(new[] { "od_yi", "obs_yi", "amt_yi" }, CsvFormula.ReferencedFields(node));
    }

    [Fact]
    public void Parse_InvalidExpression_Throws()
    {
        Assert.Throws<FormatException>(() => CsvFormula.Parse("[a] +"));
        Assert.Throws<FormatException>(() => CsvFormula.Parse("(1 + 2"));
        Assert.Throws<FormatException>(() => CsvFormula.Parse(""));
    }
}
