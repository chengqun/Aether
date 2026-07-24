using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using SecurityEntity = Acme.Securities.Domain.Security;
using Acme.Securities.Domain;

namespace Acme.Securities.Application;

public class SecurityManager : ISecurityManager
{
    private readonly ISecurityRepository _securityRepository;

    public SecurityManager(ISecurityRepository securityRepository)
    {
        _securityRepository = securityRepository;
    }

    public async Task<SecurityEntity> AddSecurityAsync(SecurityEntity security)
    {
        if (security == null)
            throw new ArgumentNullException(nameof(security));

        ValidateSecurity(security);

        var existing = await _securityRepository.GetByCodeAsync(security.Code);
        if (existing != null)
            throw new InvalidOperationException($"证券代码 {security.Code} 已存在");

        await _securityRepository.AddAsync(security);
        return security;
    }

    public async Task<SecurityEntity> UpdateOrAddSecurityAsync(SecurityEntity security)
    {
        if (security == null)
            throw new ArgumentNullException(nameof(security));

        ValidateSecurity(security);

        var existing = await _securityRepository.GetByCodeAsync(security.Code);
        if (existing != null)
        {
            // 使用实体的 UpdateFrom 方法更新现有记录
            existing.UpdateFrom(security);
            await _securityRepository.UpdateAsync(existing);
            return existing;
        }

        await _securityRepository.AddAsync(security);
        return security;
    }

    public async Task AddSecuritiesAsync(IEnumerable<SecurityEntity> securities)
    {
        if (securities == null)
            throw new ArgumentNullException(nameof(securities));

        var securityList = securities.ToList();
        ValidateSecurities(securityList);

        await _securityRepository.AddRangeAsync(securityList);
    }

    public async Task AddSecuritiesAsync(IEnumerable<SecurityEntity> securities, int batchSize)
    {
        if (securities == null)
            throw new ArgumentNullException(nameof(securities));

        var securityList = securities.ToList();
        ValidateSecurities(securityList);

        await _securityRepository.AddRangeAsync(securityList, batchSize);
    }

    public async Task<List<SecurityEntity>> SearchSecuritiesAsync(string keyword)
    {
        if (string.IsNullOrWhiteSpace(keyword))
            return await _securityRepository.GetAllAsync();

        return await _securityRepository.SearchAsync(keyword);
    }

    public async Task<SecurityEntity?> GetSecurityByIdAsync(int id)
    {
        return await _securityRepository.GetByIdAsync(id);
    }

    public async Task<SecurityEntity?> GetSecurityByCodeAsync(string code)
    {
        if (string.IsNullOrWhiteSpace(code))
            throw new ArgumentNullException(nameof(code));

        return await _securityRepository.GetByCodeAsync(code);
    }

    public async Task<List<SecurityEntity>> GetSecuritiesByExchangeAsync(string exchange)
    {
        if (string.IsNullOrWhiteSpace(exchange))
            throw new ArgumentNullException(nameof(exchange));

        return await _securityRepository.GetByExchangeAsync(exchange);
    }

    public async Task<List<SecurityEntity>> GetSecuritiesByTypeAsync(string type)
    {
        if (string.IsNullOrWhiteSpace(type))
            throw new ArgumentNullException(nameof(type));

        return await _securityRepository.GetByTypeAsync(type);
    }

    public async Task<List<SecurityEntity>> GetMainFuturesAsync()
    {
        // 取全部期货，按品种（BaseCode）分组，每组取 Code 后缀 YYMM 数值最大的合约（主期货）。
        var futures = await _securityRepository.GetByTypeAsync("期货");

        return futures
            .Select(s => new
            {
                Security = s,
                Month = TryParseFutureMonth(s.Code, out var m) ? (int?)m : null,
                Prefix = ParseFuturePrefix(s.Code),
            })
            .Where(x => x.Month.HasValue)
            .GroupBy(x => !string.IsNullOrEmpty(x.Security.BaseCode) ? x.Security.BaseCode : x.Prefix)
            .Select(g => g.OrderByDescending(x => x.Month!.Value).First().Security)
            .OrderBy(s => !string.IsNullOrEmpty(s.BaseCode) ? s.BaseCode : s.Code)
            .ToList();
    }

    /// <summary>提取期货代码的字母前缀（品种），如 rb2610 → rb、IF2610 → IF。</summary>
    private static string ParseFuturePrefix(string code)
    {
        var match = System.Text.RegularExpressions.Regex.Match(code ?? string.Empty, @"^([A-Za-z]+)");
        return match.Success ? match.Groups[1].Value : (code ?? string.Empty);
    }

    /// <summary>解析期货代码后缀 YYMM 为整数，格式为 字母前缀+数字（如 rb2610 → 2610）。</summary>
    private static bool TryParseFutureMonth(string code, out int month)
    {
        month = 0;
        var match = System.Text.RegularExpressions.Regex.Match(code ?? string.Empty, @"^([A-Za-z]+)(\d+)$");
        if (!match.Success) return false;
        return int.TryParse(match.Groups[2].Value, out month);
    }

    public async Task<bool> ExistsAsync(string code)
    {
        if (string.IsNullOrWhiteSpace(code))
            throw new ArgumentNullException(nameof(code));

        return await _securityRepository.ExistsAsync(code);
    }

    public async Task UpdateSecurityAsync(SecurityEntity security)
    {
        if (security == null)
            throw new ArgumentNullException(nameof(security));

        ValidateSecurity(security);

        var existing = await _securityRepository.GetByIdAsync(security.Id);
        if (existing == null)
            throw new InvalidOperationException($"未找到ID为 {security.Id} 的证券");

        await _securityRepository.UpdateAsync(security);
    }

    public async Task DeleteSecurityAsync(int id)
    {
        var security = await _securityRepository.GetByIdAsync(id);
        if (security == null)
            throw new InvalidOperationException($"未找到ID为 {id} 的证券");

        await _securityRepository.DeleteAsync(id);
    }

    private void ValidateSecurity(SecurityEntity security)
    {
        if (string.IsNullOrWhiteSpace(security.Code))
            throw new ArgumentException("证券代码不能为空", nameof(security.Code));

        if (string.IsNullOrWhiteSpace(security.Name))
            throw new ArgumentException("证券名称不能为空", nameof(security.Name));

        if (string.IsNullOrWhiteSpace(security.Type))
            throw new ArgumentException("证券类型不能为空", nameof(security.Type));

        if (string.IsNullOrWhiteSpace(security.Exchange))
            throw new ArgumentException("交易所不能为空", nameof(security.Exchange));

        if (!IsValidSecurityCode(security.Code, security.Exchange))
            throw new ArgumentException($"无效的证券代码格式: {security.Code}", nameof(security.Code));
    }

    private void ValidateSecurities(List<SecurityEntity> securities)
    {
        foreach (var security in securities)
        {
            ValidateSecurity(security);
        }
    }

    private bool IsValidSecurityCode(string code, string exchange)
    {
        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(exchange))
            return false;

        code = code.Trim();

        // 允许纯数字（股票）或字母+数字组合（期货）
        if (long.TryParse(code, out _))
            return true;

        // 期货代码格式：字母前缀 + 数字（如 lh2607, IF2607）
        return code.Length >= 4 && code.Length <= 10 &&
               code.All(c => char.IsLetterOrDigit(c));
    }
}
