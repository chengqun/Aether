using System.IO;
using System.Linq;
using System.Reflection;
using Acme.Trading.Domain;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Acme.Trading.Scripting;

/// <summary>Roslyn 动态编译策略源码 → IStrategy 实例。编译失败返回错误（前馈传感器信号）。</summary>
public static class StrategyScriptCompiler
{
    /// <summary>编译源码并创建 IStrategy 实例。成功 out instance；失败 out error。</summary>
    public static bool TryCompile(string source, out IStrategy? instance, out string? error)
    {
        instance = null;
        error = null;

        // 引用当前 AppDomain 已加载程序集（含 Acme.Trading.Domain.dll + System.Runtime/Linq 等）
        var references = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
            .Select(a => MetadataReference.CreateFromFile(a.Location))
            .Distinct()
            .ToArray();

        var compilation = CSharpCompilation.Create(
            "StrategyScript_" + System.Guid.NewGuid().ToString("N"),
            new[] { CSharpSyntaxTree.ParseText(source) },
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        using var ms = new MemoryStream();
        var result = compilation.Emit(ms);
        if (!result.Success)
        {
            error = string.Join("\n", result.Diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => d.ToString()));
            return false;
        }

        ms.Position = 0;
        var asm = Assembly.Load(ms.ToArray());
        var strategyType = asm.GetTypes().FirstOrDefault(t =>
            typeof(IStrategy).IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface);
        if (strategyType is null)
        {
            error = "源码中未找到实现 IStrategy 的非抽象类";
            return false;
        }

        instance = (IStrategy)Activator.CreateInstance(strategyType)!;
        return true;
    }
}
