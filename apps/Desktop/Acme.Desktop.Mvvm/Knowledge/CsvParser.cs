using System.Text;

namespace Acme.Desktop.Mvvm.Knowledge;

/// <summary>
/// CSV 解析结果：表头（<see cref="Headers"/>）+ 数据行（<see cref="Rows"/>，每行与表头等长的字符串数组）。
/// 纯数据容器（零 Avalonia）。由 <see cref="CsvParser"/> 产出。
/// </summary>
public sealed class CsvTable
{
    /// <summary>表头列名（去引号后原文）。空文件 → 空数组。</summary>
    public IReadOnlyList<string> Headers { get; }

    /// <summary>数据行（每行长度 = <see cref="Headers"/>.Count；缺失单元格填空串归一化）。</summary>
    public IReadOnlyList<IReadOnlyList<string>> Rows { get; }

    public CsvTable(IReadOnlyList<string> headers, IReadOnlyList<IReadOnlyList<string>> rows)
    {
        Headers = headers;
        Rows = rows;
    }

    public int ColumnCount => Headers.Count;
    public int RowCount => Rows.Count;
}

/// <summary>
/// 轻量 CSV 解析器（RFC 4180：引号内逗号/换行、<c>""</c> 转义为 <c>"</c>）。纯函数、无外部依赖，可单测。
/// 用于 labs 知识对象页的 csv 产物体文件「上栏表格预览」——只需读取展示，不需写回。
/// 首行视为表头；数据行缺列/多列做归一化（缺则补空、多则截断到表头数）。空文件/全空返回空表。
/// </summary>
public static class CsvParser
{
    public static CsvTable Parse(string text)
    {
        if (string.IsNullOrEmpty(text))
            return new CsvTable(Array.Empty<string>(), Array.Empty<IReadOnlyList<string>>());

        var rows = ParseRows(text);
        if (rows.Count == 0)
            return new CsvTable(Array.Empty<string>(), Array.Empty<IReadOnlyList<string>>());

        var headers = rows[0];
        var data = new List<IReadOnlyList<string>>(rows.Count - 1);
        for (var i = 1; i < rows.Count; i++)
            data.Add(Normalize(rows[i], headers.Count));

        return new CsvTable(headers, data);
    }

    /// <summary>把一条记录归一化到 targetCount 列：缺列补空、多列截断。</summary>
    private static IReadOnlyList<string> Normalize(IReadOnlyList<string> row, int targetCount)
    {
        if (row.Count == targetCount) return row;
        var result = new string[targetCount];
        for (var i = 0; i < targetCount; i++)
            result[i] = i < row.Count ? row[i] : string.Empty;
        return result;
    }

    /// <summary>按 RFC 4180 解析出所有记录（首行即表头；每条记录是去引号后的字段列表）。</summary>
    private static List<IReadOnlyList<string>> ParseRows(string text)
    {
        var result = new List<IReadOnlyList<string>>();
        var fields = new List<string>();
        var sb = new StringBuilder();
        var inQuotes = false;
        var i = 0;

        while (i < text.Length)
        {
            var c = text[i];

            if (inQuotes)
            {
                if (c == '"')
                {
                    // "" 转义为 "
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        sb.Append('"');
                        i += 2;
                        continue;
                    }
                    inQuotes = false;
                    i++;
                    continue;
                }
                sb.Append(c);
                i++;
                continue;
            }

            // 非引号态
            if (c == '"')
            {
                inQuotes = true;
                i++;
                continue;
            }
            if (c == ',')
            {
                fields.Add(sb.ToString());
                sb.Clear();
                i++;
                continue;
            }
            if (c == '\r' || c == '\n')
            {
                // 处理 \r\n 与单独的 \n/\r
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                i++;
                fields.Add(sb.ToString());
                sb.Clear();
                result.Add(fields.ToArray());
                fields.Clear();
                continue;
            }
            sb.Append(c);
            i++;
        }

        // 尾部残留字段（无结尾换行时）
        if (sb.Length > 0 || fields.Count > 0)
        {
            fields.Add(sb.ToString());
            result.Add(fields.ToArray());
        }

        return result;
    }
}
