using System.Globalization;

namespace Acme.Desktop.Mvvm.Knowledge;

/// <summary>
/// 计算字段公式：解析「字段名 + - * /（括号）」表达式。字段名用 <c>[列名]</c> 包裹（支持含空格/中文/特殊字符的列名）。
/// 纯函数、零依赖，可单测。求值时每个字段取「聚合后」的值代入（<see cref="CsvPivot"/> 先对各字段求和再代入公式），
/// 保证比率（Σ分子/Σ分母）正确而非每行相除再求和。
/// </summary>
public static class CsvFormula
{
    /// <summary>公式 AST 节点（公开类型，供跨程序集单测与 pivot 求值）。</summary>
    public abstract record Node;
    public sealed record NumberNode(decimal Value) : Node;
    public sealed record FieldNode(string Name) : Node;
    public sealed record BinaryNode(char Op, Node Left, Node Right) : Node;

    /// <summary>解析公式为 AST。失败抛 <see cref="FormatException"/>。</summary>
    public static Node Parse(string expression)
    {
        var tokens = Tokenize(expression);
        if (tokens.Count == 0) throw new FormatException("空公式");
        var (node, pos) = ParseExpr(tokens, 0);
        if (pos != tokens.Count) throw new FormatException($"公式在位置 {pos} 有无法解析的内容");
        return node;
    }

    /// <summary>求值：字段名 → 值（缺失/null 返回空，导致整个表达式为空）。除零返回空。</summary>
    public static decimal? Evaluate(Node node, Func<string, decimal?> fieldValue)
    {
        switch (node)
        {
            case NumberNode n:
                return n.Value;
            case FieldNode f:
                return fieldValue(f.Name);
            case BinaryNode b:
                var l = Evaluate(b.Left, fieldValue);
                var r = Evaluate(b.Right, fieldValue);
                if (l is null || r is null) return null;
                try
                {
                    return b.Op switch
                    {
                        '+' => l + r,
                        '-' => l - r,
                        '*' => l * r,
                        '/' => r == 0 ? null : l / r,
                        _ => throw new FormatException($"未知运算符 {b.Op}"),
                    };
                }
                catch (OverflowException) { return null; }
            default:
                return null;
        }
    }

    /// <summary>公式里引用的字段名列表（按出现顺序，去重）。供 pivot 收集「需要聚合求和的列」。</summary>
    public static IReadOnlyList<string> ReferencedFields(Node node)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Visit(Node n)
        {
            if (n is FieldNode f && seen.Add(f.Name)) result.Add(f.Name);
            else if (n is BinaryNode b) { Visit(b.Left); Visit(b.Right); }
        }
        Visit(node);
        return result;
    }

    // —— 解析 ——

    private static List<string> Tokenize(string expression)
    {
        var tokens = new List<string>();
        var i = 0;
        while (i < expression.Length)
        {
            var c = expression[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c is '+' or '-' or '*' or '/' or '(' or ')')
            {
                tokens.Add(c.ToString());
                i++;
                continue;
            }
            // 字段名 [列名]
            if (c == '[')
            {
                var close = expression.IndexOf(']', i + 1);
                if (close < 0) throw new FormatException($"未闭合的 [ 在位置 {i}");
                tokens.Add(expression.Substring(i + 1, close - i - 1)); // 字段名（去括号）
                i = close + 1;
                continue;
            }
            // 数字（含小数点）
            if (char.IsDigit(c) || c == '.')
            {
                var start = i;
                while (i < expression.Length && (char.IsDigit(expression[i]) || expression[i] == '.')) i++;
                tokens.Add(expression.Substring(start, i - start));
                continue;
            }
            throw new FormatException($"无法识别的字符 '{c}' 在位置 {i}");
        }
        return tokens;
    }

    // 语法：expr = term (('+'|'-') term)*；term = factor (('*'|'/') factor)*；factor = number | field | '(' expr ')'
    private static (Node Node, int Pos) ParseExpr(List<string> tokens, int pos)
    {
        var (left, p) = ParseTerm(tokens, pos);
        while (p < tokens.Count && (tokens[p] == "+" || tokens[p] == "-"))
        {
            var op = tokens[p][0];
            var (right, p2) = ParseTerm(tokens, p + 1);
            left = new BinaryNode(op, left, right);
            p = p2;
        }
        return (left, p);
    }

    private static (Node Node, int Pos) ParseTerm(List<string> tokens, int pos)
    {
        var (left, p) = ParseFactor(tokens, pos);
        while (p < tokens.Count && (tokens[p] == "*" || tokens[p] == "/"))
        {
            var op = tokens[p][0];
            var (right, p2) = ParseFactor(tokens, p + 1);
            left = new BinaryNode(op, left, right);
            p = p2;
        }
        return (left, p);
    }

    private static (Node Node, int Pos) ParseFactor(List<string> tokens, int pos)
    {
        if (pos >= tokens.Count) throw new FormatException("公式意外结束");
        var t = tokens[pos];
        // 括号
        if (t == "(")
        {
            var (inner, p) = ParseExpr(tokens, pos + 1);
            if (p >= tokens.Count || tokens[p] != ")") throw new FormatException("缺少右括号 )");
            return (inner, p + 1);
        }
        if (t == ")" ) throw new FormatException("多余的右括号 )");
        // 数字
        if (decimal.TryParse(t, NumberStyles.Number, CultureInfo.InvariantCulture, out var num))
            return (new NumberNode(num), pos + 1);
        // 字段名（tokenize 时 [列名] 已去括号，直接作为字段名 token）
        return (new FieldNode(t), pos + 1);
    }
}
