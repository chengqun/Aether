namespace Acme.Notes.Domain.Knowledge;

/// <summary>校验输入单元：相对路径（知识库根相对）+ 解析后的定义。<see cref="Definition"/> null = 文件解析失败
/// （仍入列表——参与断链解析索引，但自身不做边检查——links 不可信）。</summary>
public sealed record KnowledgeItem(string Path, KnowledgeObjectDefinition? Definition);

/// <summary>一条体检诊断。唯一级别 error=断链——诊断不阻断写入（人和 agent 共写知识库必有中间态，体检做提示不做门禁）。</summary>
public sealed record KnowledgeDiagnostic(string Severity, string Category, string File, string Message);

/// <summary>
/// 知识对象图体检器（纯函数，零 I/O——目标解析经注入委托，测试可换 fake）。检查**断链**：link targetId
/// 解析不到任何知识对象文件 → error（改名/移动/删除目标后遗留的坏引用）。
/// <para>词表/边方向矩阵/按 kind 必填字段的 <c>knowledge-manifest.yaml</c> 机制已于 2026-09-22 移除：
/// 三项均为软警告且维护成本高于收益（语料演化快于规则维护），语料规范改由各项目 README 散文承载
/// （agent 可 read，天然按目录一份、各目录不同）。唯一 error 级检查（断链）与清单无关，保留。</para>
/// </summary>
public static class KnowledgeGraphValidator
{
    /// <summary>校验全部知识对象。</summary>
    /// <param name="items">全量知识对象（含解析失败项）。</param>
    /// <param name="resolveTarget">(linkTarget, 源对象相对路径) → 目标项。返回 null = 断链；
    /// 返回 <see cref="KnowledgeItem.Definition"/> null 的项 = 文件存在但解析失败/非知识对象（算解析到，不断链）。</param>
    public static IReadOnlyList<KnowledgeDiagnostic> Validate(
        IReadOnlyList<KnowledgeItem> items,
        Func<string, string, KnowledgeItem?> resolveTarget)
    {
        var result = new List<KnowledgeDiagnostic>();
        foreach (var item in items)
        {
            var def = item.Definition;
            if (def is null) continue; // 解析失败的文件：links 不可信，跳过（仍被别的文件当解析目标）

            foreach (var link in def.Links)
            {
                if (string.IsNullOrWhiteSpace(link.Target)) continue;
                var target = resolveTarget(link.Target, item.Path);
                if (target is null)
                    result.Add(new KnowledgeDiagnostic("error", "断链", item.Path,
                        $"link [{link.Role}] → 「{link.Target}」解析不到任何知识对象文件"));
            }
        }
        return result;
    }
}
