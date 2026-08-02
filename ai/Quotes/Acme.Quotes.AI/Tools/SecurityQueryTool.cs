using Acme.Securities.Application;
using System;
using System.ComponentModel;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Acme.Quotes.AI.Tools;

public class SecurityQueryTool
{
    public class Output
    {
        public string Result { get; set; } = string.Empty;
    }

    private readonly ISecurityService _securityService;

    public SecurityQueryTool(ISecurityService securityService)
    {
        _securityService = securityService ?? throw new ArgumentNullException(nameof(securityService));
    }

    [Description("查询证券信息，根据关键词搜索股票/期货代码和名称。当用户询问某只股票或期货的信息时调用此工具。")]
    public async Task<Output> QuerySecurity([Description("股票/期货代码或名称关键词，如 600000、浦发、IF")] string keyword, CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(keyword))
            {
                return new Output { Result = "请提供搜索关键词" };
            }

            var results = await _securityService.SearchSecuritiesAsync(keyword);

            if (results.Count == 0)
            {
                return new Output { Result = $"未找到与\"{keyword}\"相关的证券" };
            }

            var sb = new StringBuilder();
            sb.AppendLine($"找到{results.Count}条结果：");
            foreach (var s in results)
            {
                sb.AppendLine($"- {s.Code} {s.Name} [{s.Type}] 交易所:{s.Exchange} 基础代码:{s.BaseCode}");
            }

            return new Output { Result = sb.ToString() };
        }
        catch (Exception ex)
        {
            return new Output { Result = $"查询证券失败: {ex.Message}" };
        }
    }
}
