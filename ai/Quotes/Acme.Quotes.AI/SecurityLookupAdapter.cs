using Acme.Quotes.Domain;
using Acme.Securities.Domain;       // ISecurityManager + Security (mapped from)

namespace Acme.Quotes.AI;

/// <summary>
/// Anti-corruption adapter: implements Quotes' <see cref="ISecurityLookup"/> by
/// delegating to the Securities context's <see cref="ISecurityManager"/> and
/// mapping its <c>Security</c> entity into Quotes' <see cref="SecurityRef"/> DTO.
/// Lives in the AI adapter (where the two contexts meet) so the Quotes bounded
/// context itself stays free of any Securities dependency.
/// </summary>
public sealed class SecurityLookupAdapter : ISecurityLookup
{
    private readonly ISecurityManager _securityManager;

    public SecurityLookupAdapter(ISecurityManager securityManager)
        => _securityManager = securityManager ?? throw new ArgumentNullException(nameof(securityManager));

    /// <inheritdoc/>
    public async Task<List<SecurityRef>> SearchAsync(string keyword)
    {
        var securities = await _securityManager.SearchSecuritiesAsync(keyword);
        return securities.Select(s => new SecurityRef
        {
            Id = s.Id,
            Code = s.Code,
            Name = s.Name,
            Type = s.Type,
            Exchange = s.Exchange,
            BaseCode = s.BaseCode
        }).ToList();
    }
}
