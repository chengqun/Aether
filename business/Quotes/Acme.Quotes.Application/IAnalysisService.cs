using Acme.Quotes.Domain;
using System.Threading.Tasks;

namespace Acme.Quotes.Application;

/// <summary>
/// Application 层技术分析服务接口（供 AI 工具注入，不直接依赖 Domain 的 <see cref="IAnalysisManager"/>）。
/// </summary>
public interface IAnalysisService
{
    Task<TechnicalAnalysisResult> CalculateAsync(
        string keyword, string freq, string indicators, int rowCount = 60);
}
