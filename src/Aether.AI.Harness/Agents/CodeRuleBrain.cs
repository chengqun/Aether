using System.Globalization;
using Microsoft.Extensions.AI;

namespace Aether.AI.Harness.Agents;

/// <summary>
/// 代码规则大脑的抽象基类：作者用 C# 实现一个确定性决策器，代替 YAML <see cref="RuleProgramBrain"/>。
/// 镜像 Trading 策略脚本管线——笔记里写 C# 源码，Roslyn 即时编译成 <see cref="CodeRuleBrain"/> 子类，
/// 注册成工厂（每次召唤 fresh instance，因代码大脑可有状态）。
/// <para>
/// 作者模型为状态机式（MVP）：每次 agent 循环 <see cref="Decide"/> 被调一次，返回本步要流出的
/// <see cref="AIContent"/>——<see cref="Call"/> 产 <see cref="FunctionCallContent"/>（循环继续，MAF 执行工具
/// 后回填结果再调本大脑）、<see cref="Reply"/> 产 <see cref="TextContent"/>（循环终止）。状态靠
/// <see cref="Step"/>（本轮工具调用计数）+ <see cref="LastResult"/> + 作者自有字段在多次 <see cref="Decide"/>
/// 间累积（实例在同一轮内复用）。
/// </para>
/// <para>
/// 对称于 <see cref="RuleProgramBrain"/>：同样实现 <see cref="IRuleBrain"/>，经 <see cref="RuleChatClient"/>
/// 接入 MAF HarnessAgent，工具执行/回填/循环终止全归框架。区别仅在「决定下一步」用 C# 而非声明式规则程序。
/// </para>
/// </summary>
public abstract class CodeRuleBrain : IRuleBrain
{
    /// <summary>当前求值上下文（<see cref="DecideAsync"/> 调用前由基类设置）。</summary>
    protected RuleContext Context { get; private set; } = null!;

    /// <summary>本轮最新 user 消息文本（无则空串）。</summary>
    protected string UserText => Context.LatestUser?.Text ?? string.Empty;

    /// <summary>本轮已调工具次数（最后一条 user 消息之后的 FunctionCallContent 计数）。</summary>
    protected int Step => Context.ToolCallsThisTurn;

    /// <summary>历史里最新一条工具结果文本（无则空串）。</summary>
    protected string LastResult => Context.LatestToolResult?.Result?.ToString() ?? string.Empty;

    /// <summary>本轮可用的工具（来自 ChatOptions.Tools）。</summary>
    protected IReadOnlyList<AIFunction> AvailableTools => Context.AvailableTools;

    /// <summary>完整对话历史（含 system / 历史 turn / 本轮工具调用与结果）。</summary>
    protected IReadOnlyList<ChatMessage> History => Context.History;

    /// <summary>
    /// 作者实现：依据 <see cref="Step"/>/<see cref="UserText"/>/<see cref="LastResult"/> 返回本步要流出的
    /// <see cref="AIContent"/>（<see cref="Call"/> 或 <see cref="Reply"/> 的结果）。须保证收敛
    /// （最终返回 <see cref="Reply"/>），否则 MAF 循环不结束。
    /// </summary>
    protected abstract IReadOnlyList<AIContent> Decide();

    /// <inheritdoc/>
    public ValueTask<IReadOnlyList<AIContent>> DecideAsync(RuleContext context, CancellationToken cancellationToken)
    {
        Context = context ?? throw new ArgumentNullException(nameof(context));
        return new(Decide());
    }

    /// <summary>
    /// 产一个工具调用：<paramref name="tool"/> 名 + 参数对象。<paramref name="args"/> 为匿名对象/字典，
    /// 内部转 <see cref="Dictionary{TKey, TValue}"/> 供 <see cref="FunctionCallContent"/>。返回后循环继续。
    /// </summary>
    protected static IReadOnlyList<AIContent> Call(string tool, object? args = null)
    {
        if (string.IsNullOrEmpty(tool)) throw new ArgumentException("工具名不能为空", nameof(tool));
        var dict = ToArgs(args);
        var callId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        return new[] { new FunctionCallContent(callId, tool, dict) };
    }

    /// <summary>产终答文本，终止 agent 循环。</summary>
    protected static IReadOnlyList<AIContent> Reply(string text)
        => new[] { new TextContent(text ?? string.Empty) };

    /// <summary>把匿名对象/字典/空转成 FunctionCallContent 接受的参数字典。</summary>
    private static Dictionary<string, object?> ToArgs(object? args)
    {
        if (args is null) return new Dictionary<string, object?>();
        if (args is IDictionary<string, object?> already) return new Dictionary<string, object?>(already, StringComparer.Ordinal);
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var p in args.GetType().GetProperties())
        {
            result[p.Name] = p.GetValue(args);
        }
        return result;
    }
}
