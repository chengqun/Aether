namespace Acme.Notes.Domain.Knowledge;

/// <summary>
/// 知识库规则清单（机器可读的语料自治规范）——由知识库根的 <c>knowledge-manifest.yaml</c> 装载
/// （<c>KnowledgeManifestLoader</c>）。三类规则：kind 词表 / 边方向矩阵 / 按 kind 必填 front-matter 字段。
/// 各项 null = 不检查（无清单时校验器只做断链检查）。规则归语料所有（数据文件可编辑），
/// 校验器归平台（代码一次写好通用）——换知识库零代码。
/// </summary>
public sealed record KnowledgeManifest(
    IReadOnlyList<string>? Kinds = null,
    IReadOnlyDictionary<string, KnowledgeEdgeRule>? Edges = null,
    IReadOnlyDictionary<string, IReadOnlyList<string>>? RequiredFields = null);

/// <summary>一条边规则：role 允许的源 kind 列表（<see cref="From"/>）与目标 kind 列表（<see cref="To"/>）。
/// <c>"*"</c> = 任意 kind；null = 不检查该侧。</summary>
public sealed record KnowledgeEdgeRule(IReadOnlyList<string>? From = null, IReadOnlyList<string>? To = null);

/// <summary>校验输入单元：相对路径（知识库根相对）+ 解析后的定义。<see cref="Definition"/> null = 文件解析失败
/// （仍入列表——参与断链解析索引，但不做边/字段检查）。</summary>
public sealed record KnowledgeItem(string Path, KnowledgeObjectDefinition? Definition);

/// <summary>一条体检诊断。<see cref="Severity"/>：error=断链（优先处理）；warning=软约束提示（词表/方向/必填字段）——
/// 一律不阻断写入（人和 agent 共写知识库必有中间态，约束做体检不做门禁）。</summary>
public sealed record KnowledgeDiagnostic(string Severity, string Category, string File, string Message);

/// <summary>
/// 知识对象图体检器（纯函数，零 I/O——目标解析经注入委托，测试可换 fake）。检查四类：
/// ①断链（link targetId 解析不到任何文件）②kind 词表 ③边方向矩阵（role 的 from/to kind）④按 kind 必填
/// front-matter 字段（查 <see cref="KnowledgeObjectDefinition.ExtraFrontMatter"/>）。
/// 无清单（manifest=null）时只做断链检查（全语料通用）。
/// </summary>
public static class KnowledgeGraphValidator
{
    /// <summary>校验全部知识对象。</summary>
    /// <param name="items">全量知识对象（含解析失败项）。</param>
    /// <param name="manifest">规则清单；null = 仅断链检查。</param>
    /// <param name="resolveTarget">(linkTarget, 源对象相对路径) → 目标项。返回 null = 断链；
    /// 返回 <see cref="KnowledgeItem.Definition"/> null 的项 = 文件存在但解析失败/非知识对象（算解析到，跳过目标 kind 检查）。</param>
    public static IReadOnlyList<KnowledgeDiagnostic> Validate(
        IReadOnlyList<KnowledgeItem> items,
        KnowledgeManifest? manifest,
        Func<string, string, KnowledgeItem?> resolveTarget)
    {
        var result = new List<KnowledgeDiagnostic>();
        foreach (var item in items)
        {
            var def = item.Definition;

            // ① kind 词表（软）
            if (manifest?.Kinds is { Count: > 0 } kinds)
            {
                if (string.IsNullOrWhiteSpace(def?.Kind))
                    result.Add(new KnowledgeDiagnostic("warning", "kind", item.Path, "缺少 kind（front-matter 无 kind 字段）"));
                else if (!kinds.Contains(def.Kind, StringComparer.Ordinal))
                    result.Add(new KnowledgeDiagnostic("warning", "kind", item.Path,
                        $"词表外 kind「{def.Kind}」——不在 knowledge-manifest.yaml 的 kinds 清单内"));
            }

            if (def is null) continue; // 解析失败的文件：不再做边/字段检查（kind 缺失已提示）

            // ② 按 kind 必填 front-matter 字段（软）
            if (manifest?.RequiredFields is { Count: > 0 } required
                && !string.IsNullOrEmpty(def.Kind)
                && required.TryGetValue(def.Kind, out var fields))
            {
                foreach (var field in fields)
                    if (def.ExtraFrontMatter is null || !def.ExtraFrontMatter.ContainsKey(field))
                        result.Add(new KnowledgeDiagnostic("warning", "必填字段", item.Path,
                            $"kind「{def.Kind}」缺必填 front-matter 字段「{field}」"));
            }

            // ③ 边：断链（error）+ role 词表/方向矩阵（软）
            foreach (var link in def.Links)
            {
                if (string.IsNullOrWhiteSpace(link.Target)) continue;
                var target = resolveTarget(link.Target, item.Path);
                if (target is null)
                {
                    result.Add(new KnowledgeDiagnostic("error", "断链", item.Path,
                        $"link [{link.Role}] → 「{link.Target}」解析不到任何知识对象文件"));
                    continue;
                }
                if (manifest?.Edges is not { Count: > 0 } edges) continue;
                if (!edges.TryGetValue(link.Role, out var rule))
                {
                    result.Add(new KnowledgeDiagnostic("warning", "边类型", item.Path,
                        $"词表外 role「{link.Role}」——不在 knowledge-manifest.yaml 的 edges 清单内"));
                    continue;
                }
                if (rule.From is { Count: > 0 } from && !ContainsKind(from, def.Kind))
                    result.Add(new KnowledgeDiagnostic("warning", "边方向", item.Path,
                        $"role「{link.Role}」不允许从 kind「{def.Kind}」发出（from 列表：{string.Join("/", from)}）"));
                if (rule.To is { Count: > 0 } to
                    && !string.IsNullOrEmpty(target.Definition?.Kind)
                    && !ContainsKind(to, target.Definition!.Kind))
                    result.Add(new KnowledgeDiagnostic("warning", "边方向", item.Path,
                        $"role「{link.Role}」的目标「{link.Target}」kind 是「{target.Definition!.Kind}」，不在 to 列表（{string.Join("/", to)}）"));
            }
        }
        return result;
    }

    private static bool ContainsKind(IReadOnlyList<string> list, string? kind)
        => list.Contains("*") || (kind is not null && list.Contains(kind, StringComparer.Ordinal));
}
