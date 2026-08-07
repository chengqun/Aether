using System.IO;
using System.Reflection;
using Aether.AI.Harness.Agents;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Aether.AI.Harness.Scripting;

/// <summary>
/// Roslyn 动态编译代码规则源码 → <see cref="CodeRuleBrain"/> 实例。镜像 Trading 的
/// <c>StrategyScriptCompiler</c>：批量引用 AppDomain 已加载程序集 → 内存 emit → Assembly.Load
/// → 找 <see cref="CodeRuleBrain"/> 子类 → <see cref="Activator.CreateInstance"/>。编译失败返回错误。
/// <para>本类不依赖 <c>Acme.Trading.Scripting</c>（platform 不引 business）——复用模式，不复用代码。</para>
/// </summary>
public static class RuleScriptCompiler
{
    /// <summary>编译源码并创建 <see cref="CodeRuleBrain"/> 实例。成功 out instance；失败 out error。</summary>
    public static bool TryCompile(string source, out CodeRuleBrain? instance, out string? error)
    {
        instance = null;
        error = null;

        // 引用当前 AppDomain 已加载程序集（含 Aether.AI.Harness.dll 的 CodeRuleBrain + System.Runtime/Linq 等）
        var references = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
            .Select(a => MetadataReference.CreateFromFile(a.Location))
            .Distinct()
            .ToArray();

        var compilation = CSharpCompilation.Create(
            "CodeRuleScript_" + Guid.NewGuid().ToString("N"),
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
        var brainType = asm.GetTypes().FirstOrDefault(t =>
            typeof(CodeRuleBrain).IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface);
        if (brainType is null)
        {
            error = "源码中未找到继承 CodeRuleBrain 的非抽象类";
            return false;
        }

        instance = (CodeRuleBrain)Activator.CreateInstance(brainType)!;
        return true;
    }
}
