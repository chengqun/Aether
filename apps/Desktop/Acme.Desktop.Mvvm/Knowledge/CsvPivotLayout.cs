using System.Text.Json;

namespace Acme.Desktop.Mvvm.Knowledge;

/// <summary>值字段布局项（序列化）——普通字段按 <see cref="Name"/>（列名）引用，计算字段带 <see cref="Formula"/>。</summary>
public sealed class CsvValueLayout
{
    public string Name { get; set; } = string.Empty;
    public string? Formula { get; set; }
    public bool AsPercent { get; set; }
}

/// <summary>筛选字段布局项——字段名 + 勾选的值列表（null/空 = 全选）。</summary>
public sealed class CsvFilterLayout
{
    public string Field { get; set; } = string.Empty;
    public List<string>? Values { get; set; }
}

/// <summary>计算字段布局项（字段列表里、尚未拖进值区的）。</summary>
public sealed class CsvCalcLayout
{
    public string Name { get; set; } = string.Empty;
    public string Formula { get; set; } = string.Empty;
}

/// <summary>
/// 透视表布局快照（可序列化为 JSON，按 CSV 文件持久化，跨打开恢复）。字段用「名字」引用（普通字段按列名，
/// 恢复时现查列索引——跨文件/列位置变化仍能匹配）；计算字段带公式；筛选字段带勾选值。
/// 纯数据 + <see cref="Serialize"/>/<see cref="Deserialize"/>（System.Text.Json），零 Avalonia，可单测。
/// </summary>
public sealed class CsvPivotLayout
{
    public List<string> Rows { get; set; } = new();
    public string? Column { get; set; }
    public List<CsvValueLayout> Values { get; set; } = new();
    public List<CsvFilterLayout> Filters { get; set; } = new();
    public List<CsvCalcLayout> Calculated { get; set; } = new();

    public string Serialize() => JsonSerializer.Serialize(this);

    public static CsvPivotLayout? Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<CsvPivotLayout>(json); }
        catch { return null; }
    }
}
