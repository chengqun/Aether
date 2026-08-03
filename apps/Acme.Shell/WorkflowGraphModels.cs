using Acme.Notes.Domain.Knowledge;

namespace Acme.Shell;

/// <summary>工作流转交图的不可变图模型（UI 无关）。</summary>
public sealed record WorkflowGraphModel(IReadOnlyList<GraphNode> Nodes, IReadOnlyList<GraphEdge> Edges)
{
    public static WorkflowGraphModel Empty { get; } = new(Array.Empty<GraphNode>(), Array.Empty<GraphEdge>());
}

public sealed record GraphNode(Guid Id, string Name, bool IsEntry);

public sealed record GraphEdge(Guid FromId, Guid ToId, string? Label);

/// <summary>布局后的图（含像素坐标，用于画布渲染）。</summary>
public sealed record LaidOutGraph(IReadOnlyList<LaidOutNode> Nodes, IReadOnlyList<LaidOutEdge> Edges, double Width, double Height)
{
    public static LaidOutGraph Empty { get; } = new(Array.Empty<LaidOutNode>(), Array.Empty<LaidOutEdge>(), 0, 0);
}

public sealed record LaidOutNode(Guid Id, string Name, bool IsEntry, double X, double Y, double W, double H);

/// <summary>贝塞尔边：P0=from, P3=to, P1/P2 控制点；LabelX/LabelY 为 t=0.5 处。</summary>
public sealed record LaidOutEdge(
    Guid FromId, Guid ToId, string? Label,
    double FromX, double FromY, double ToX, double ToY,
    double C1X, double C1Y, double C2X, double C2Y,
    double LabelX, double LabelY);

/// <summary>从编辑器定义数据构建图模型 + 分层布局 + Mermaid 生成。</summary>
public static class WorkflowGraphBuilder
{
    private const double HSpacing = 250;
    private const double VSpacing = 170;
    private const double NodeW = 184;
    private const double NodeH = 50;
    private const double Margin = 44;

    /// <summary>从入口/参与者/转交边构建图模型。</summary>
    public static WorkflowGraphModel Build(
        Guid entryAgentId,
        IEnumerable<ParticipantOption> participants,
        IEnumerable<HandoffRow> handoffs)
    {
        var nodes = (participants ?? Enumerable.Empty<ParticipantOption>())
            .Where(p => p.IsSelected)
            .Select(p => new GraphNode(p.Id, p.Name, p.Id == entryAgentId))
            .ToList();

        // 入口若未勾选也补进图（保证有根）
        if (entryAgentId != Guid.Empty && nodes.All(n => n.Id != entryAgentId))
        {
            var entryName = participants?.FirstOrDefault(p => p.Id == entryAgentId)?.Name ?? "入口";
            nodes.Add(new GraphNode(entryAgentId, entryName, true));
        }

        var nodeIds = nodes.Select(n => n.Id).ToHashSet();
        var edges = (handoffs ?? Enumerable.Empty<HandoffRow>())
            .Where(h => h.FromId != Guid.Empty && h.ToId != Guid.Empty
                        && nodeIds.Contains(h.FromId) && nodeIds.Contains(h.ToId))
            .Select(h => new GraphEdge(h.FromId, h.ToId, h.Reason))
            .ToList();

        return nodes.Count == 0 ? WorkflowGraphModel.Empty : new WorkflowGraphModel(nodes, edges);
    }

    /// <summary>分层布局：从入口 BFS 定层，同层横向居中。</summary>
    public static LaidOutGraph Layout(WorkflowGraphModel model)
    {
        if (model.Nodes.Count == 0) return LaidOutGraph.Empty;

        var byId = model.Nodes.ToDictionary(n => n.Id);
        var entry = model.Nodes.FirstOrDefault(n => n.IsEntry) ?? model.Nodes[0];

        // BFS 出边定层
        var level = new Dictionary<Guid, int>();
        var queue = new Queue<Guid>();
        level[entry.Id] = 0;
        queue.Enqueue(entry.Id);
        while (queue.Count > 0)
        {
            var u = queue.Dequeue();
            var ul = level[u];
            foreach (var e in model.Edges.Where(x => x.FromId == u))
            {
                if (!level.ContainsKey(e.ToId)) { level[e.ToId] = ul + 1; queue.Enqueue(e.ToId); }
                else if (level[e.ToId] > ul + 1) level[e.ToId] = ul + 1;
            }
        }
        var maxLevel = level.Values.DefaultIfEmpty(0).Max();
        foreach (var n in model.Nodes)
            if (!level.ContainsKey(n.Id)) level[n.Id] = maxLevel + 1;

        // 分组、横向居中
        var levels = model.Nodes.GroupBy(n => level[n.Id]).OrderBy(g => g.Key).ToList();
        var maxCount = levels.Max(g => g.Count());

        var pos = new Dictionary<Guid, (double X, double Y)>();
        foreach (var grp in levels)
        {
            var ordered = grp.OrderBy(n => n.Name).ToList();
            var count = ordered.Count;
            for (var i = 0; i < count; i++)
            {
                var x = (i - (count - 1) / 2.0) * HSpacing;
                var y = grp.Key * VSpacing;
                pos[ordered[i].Id] = (x, y);
            }
        }

        var width = Math.Max((maxCount - 1) * HSpacing + NodeW, NodeW) + Margin * 2;
        var height = (levels.Max(g => g.Key) * VSpacing) + NodeH + Margin * 2;
        var offsetX = width / 2;
        var offsetY = Margin;

        var laidNodes = model.Nodes.Select(n =>
        {
            var (x, y) = pos[n.Id];
            return new LaidOutNode(n.Id, n.Name, n.IsEntry, x + offsetX - NodeW / 2, y + offsetY, NodeW, NodeH);
        }).ToList();

        var laidEdges = model.Edges.Select(e =>
        {
            var f = laidNodes.First(n => n.Id == e.FromId);
            var t = laidNodes.First(n => n.Id == e.ToId);
            double fx, fy, tx, ty, c1x, c1y, c2x, c2y, lx, ly;
            var sameLevel = Math.Abs(f.Y - t.Y) < 1;
            if (sameLevel)
            {
                // 同层：弧线向下凸出（避开节点文字），标注置于节点行下方
                var fLeft = f.X <= t.X;
                var dir = fLeft ? 1.0 : -1.0;
                fx = fLeft ? f.X + f.W : f.X;
                fy = f.Y + f.H / 2;
                tx = fLeft ? t.X : t.X + t.W;
                ty = t.Y + t.H / 2;
                var bow = 58.0;
                c1x = fx + 32 * dir; c1y = fy + bow;
                c2x = tx - 32 * dir; c2y = ty + bow;
                lx = (fx + tx) / 2;
                ly = f.Y + f.H + 16;   // 标注放节点行下方，不压文字
            }
            else
            {
                // 跨层：下中 → 上中
                fx = f.X + f.W / 2; fy = f.Y + f.H;
                tx = t.X + t.W / 2; ty = t.Y;
                c1x = fx; c1y = fy + VSpacing * 0.4;
                c2x = tx; c2y = ty - VSpacing * 0.4;
                lx = 0.125 * fx + 0.375 * c1x + 0.375 * c2x + 0.125 * tx;
                ly = 0.125 * fy + 0.375 * c1y + 0.375 * c2y + 0.125 * ty;
            }
            return new LaidOutEdge(e.FromId, e.ToId, e.Label, fx, fy, tx, ty, c1x, c1y, c2x, c2y, lx, ly);
        }).ToList();

        return new LaidOutGraph(laidNodes, laidEdges, width, height);
    }

    /// <summary>从图模型生成 Mermaid flowchart 字符串。</summary>
    public static string ToMermaid(WorkflowGraphModel model)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("flowchart TD");
        var idMap = new Dictionary<Guid, string>();
        var i = 0;
        foreach (var n in model.Nodes)
        {
            var nid = $"N{i++}";
            idMap[n.Id] = nid;
            sb.AppendLine($"  {nid}[\"{Escape(n.Name)}\"]");
        }
        foreach (var e in model.Edges)
        {
            var from = idMap[e.FromId];
            var to = idMap[e.ToId];
            if (string.IsNullOrWhiteSpace(e.Label))
                sb.AppendLine($"  {from} --> {to}");
            else
                sb.AppendLine($"  {from} -- \"{Escape(e.Label)}\" --> {to}");
        }
        if (model.Nodes.FirstOrDefault(n => n.IsEntry) is { } entry && idMap.TryGetValue(entry.Id, out var eid))
        {
            sb.AppendLine($"  class {eid} entry");
            sb.AppendLine("  classDef entry fill:#0891B2,color:#fff,stroke:#0e7490,stroke-width:2px");
        }
        return sb.ToString();
    }

    private static string Escape(string? s) => (s ?? string.Empty).Replace("\"", "'");
}

/// <summary>知识对象图构建器：从焦点节点出发只跟出边做闭包（不串业务）+ 闭包内所有边。</summary>
public static class KnowledgeGraphBuilder
{
    /// <summary>从焦点知识对象出发，沿出边（typed link）做传递闭包，返回闭包子图。
    /// 只跟出边、不回溯入边，配合资产叶子（links:[]）天然不跨业务。focus 高亮。</summary>
    public static WorkflowGraphModel BuildClosure(
        string focalTitle,
        KnowledgeObjectDefinition focalDef,
        Func<string?, (string Title, KnowledgeObjectDefinition? Def)?> resolveTarget)
    {
        if (focalDef is null || string.IsNullOrWhiteSpace(focalTitle)) return WorkflowGraphModel.Empty;

        var nodes = new Dictionary<string, GraphNode>(StringComparer.Ordinal);
        var edges = new List<GraphEdge>();
        var queue = new Queue<(string Title, KnowledgeObjectDefinition Def)>();

        nodes[focalTitle] = new GraphNode(Guid.NewGuid(), focalTitle, true);
        queue.Enqueue((focalTitle, focalDef));

        while (queue.Count > 0)
        {
            var (curTitle, curDef) = queue.Dequeue();
            if (!nodes.TryGetValue(curTitle, out var curNode)) continue;
            foreach (var l in curDef.Links)
            {
                var tgt = l.Target;
                if (string.IsNullOrWhiteSpace(tgt)) continue;
                var resolved = resolveTarget(tgt);
                var nodeTitle = resolved?.Title ?? tgt!;
                if (!nodes.ContainsKey(nodeTitle))
                {
                    nodes[nodeTitle] = new GraphNode(Guid.NewGuid(), nodeTitle, false);
                    if (resolved?.Def is { } td) queue.Enqueue((nodeTitle, td));
                    // 解析不到（目标被删/未保存）→ 仍画节点但不跟其出边
                }
                edges.Add(new GraphEdge(curNode.Id, nodes[nodeTitle].Id, l.Role));
            }
        }
        return new WorkflowGraphModel(nodes.Values.ToList(), edges);
    }
}
