using Acme.Notes.Domain.Agents;

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
    private const double HSpacing = 210;
    private const double VSpacing = 140;
    private const double NodeW = 168;
    private const double NodeH = 48;
    private const double Margin = 40;

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
            double fx, fy, tx, ty, c1x, c1y, c2x, c2y;
            var sameLevel = Math.Abs(f.Y - t.Y) < 1;
            if (sameLevel)
            {
                // 同层：右中 → 左中
                var (fromN, toN, dir) = f.X < t.X ? (f, t, 1.0) : (t, f, -1.0);
                fx = fromN.X + fromN.W; fy = fromN.Y + fromN.H / 2;
                tx = toN.X; ty = toN.Y + toN.H / 2;
                c1x = fx + 40 * dir; c1y = fy;
                c2x = tx - 40 * dir; c2y = ty;
                if (f.X >= t.X) { (fx, tx) = (tx, fx); (fy, ty) = (ty, fy); (c1x, c2x) = (c2x, c1x); (c1y, c2y) = (c2y, c1y); }
            }
            else
            {
                // 跨层：下中 → 上中
                fx = f.X + f.W / 2; fy = f.Y + f.H;
                tx = t.X + t.W / 2; ty = t.Y;
                c1x = fx; c1y = fy + VSpacing * 0.4;
                c2x = tx; c2y = ty - VSpacing * 0.4;
            }
            // 贝塞尔 t=0.5 点
            var lx = 0.125 * fx + 0.375 * c1x + 0.375 * c2x + 0.125 * tx;
            var ly = 0.125 * fy + 0.375 * c1y + 0.375 * c2y + 0.125 * ty;
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
