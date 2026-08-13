using System.Globalization;
using System.Text.RegularExpressions;

namespace Acme.Mobile.Shared.Converters;

/// <summary>
/// 预处理 Markdown 文本：把 pipe 表格中的空单元格填入不间断空格（ ）。
/// 修 Indiko.Maui.Controls.Markdown 的 RenderTable 跳过空单元格（continue）→ 该格位置露出
/// Grid 硬编码的 Colors.Gray 背景（显示为灰色色块）的 bug：填入内容后渲染器会为之创建带
/// 行/表头背景色的 Label，与有数据的单元格一致。仅处理「以 | 开头并以 | 结尾、且非分隔行」
/// 的行；非表格内容不动。对已在流式中的部分表格同样安全（不完整的行不匹配，原样放行）。
/// </summary>
public class MarkdownTableFillConverter : IValueConverter
{
    // 分隔行：|---|:--:|---|（只含空格/冒号/竖线/横线）
    private static readonly Regex SeparatorRow = new(@"^\s*\|[\s:|\-]+\|\s*$", RegexOptions.Compiled);
    // 表格行：以 | 开头并以 | 结尾，中间至少一个字符
    private static readonly Regex TableRow = new(@"^\s*\|.+\|\s*$", RegexOptions.Compiled);

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string text || string.IsNullOrEmpty(text) || !text.Contains('|'))
            return value;

        var lines = text.Split('\n');
        bool changed = false;
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Length == 0) continue;
            if (SeparatorRow.IsMatch(line)) continue;
            if (!TableRow.IsMatch(line)) continue;

            var filled = FillEmptyCells(line);
            if (!ReferenceEquals(filled, line))
            {
                lines[i] = filled;
                changed = true;
            }
        }
        return changed ? string.Join('\n', lines) : text;
    }

    private static string FillEmptyCells(string line)
    {
        var trimmed = line.Trim();
        var parts = trimmed.Split('|');
        if (parts.Length < 3) return line; // 至少 |x| → 3 段
        bool changed = false;
        // parts[0] 与 parts[last] 是首尾 | 外侧（通常空），只填内部单元格
        for (int i = 1; i < parts.Length - 1; i++)
        {
            if (string.IsNullOrWhiteSpace(parts[i]))
            {
                parts[i] = " ";
                changed = true;
            }
        }
        return changed ? string.Join('|', parts) : line;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>反转布尔值。</summary>
public class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b ? !b : false;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b ? !b : false;
}

/// <summary>非空 → true（用于可见性绑定）。字符串空串也算“空”。
/// 传 ConverterParameter="inverse" 则反转（空 → true），用于二者择一的互斥可见性。</summary>
public class NotNullConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool notEmpty = value is string s ? !string.IsNullOrEmpty(s) : value != null;
        return parameter is string p && p.Equals("inverse", StringComparison.OrdinalIgnoreCase) ? !notEmpty : notEmpty;
    }
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>非空非空串 → true。</summary>
public class StringToBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => !string.IsNullOrWhiteSpace(value as string);
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>bool → Visibility（MAUI 用 IsVisible，直接返回 bool；此转换器留作占位/反转组合）。</summary>
public class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b && b;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b && b;
}

/// <summary>取字符串首字符大写作为头像占位（空串返回 "?"）。</summary>
public class InitialConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var s = value as string;
        return string.IsNullOrEmpty(s) ? "?" : char.ToUpper(s[0]).ToString();
    }
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
